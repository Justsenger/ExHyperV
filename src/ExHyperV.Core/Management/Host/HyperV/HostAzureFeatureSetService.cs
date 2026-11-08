using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using ExHyperV.Tools;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Services;

/// <summary>在业务操作期间设置或移除主机 AzureFeatureSet 开关，并在结束时恢复原值。</summary>
/// <remarks>修改 HKLM 64 位视图，需要注册表写权限。跨进程锁覆盖原值读取、业务执行及恢复；恢复包含原值的存在性、类型和内容，并覆盖期间的外部修改。强制终止进程或断电不能保证恢复，不提供启动/退出时的全局兜底。</remarks>
internal static class HostAzureFeatureSetService
{
    private const string VirtualizationKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization";
    private const string AzureFeatureSetValue = "AzureFeatureSet";
    private const string ResourceName = "host:azure-feature-set";

    /// <summary>临时启用开关并执行返回响应的业务；锁、权限及恢复失败转换为失败响应。</summary>
    public static Task<ApiResponse> RunTemporarilyEnabledAsync(Func<Task<ApiResponse>> action) =>
        RunResponseAsync(() => RunTemporarilyEnabledAsync<ApiResponse>(action));

    /// <summary>临时移除开关并执行返回响应的业务；恢复失败明确标记为 CleanupRequired。</summary>
    public static Task<ApiResponse> RunTemporarilyDisabledAsync(Func<Task<ApiResponse>> action) =>
        RunResponseAsync(() => RunTemporarilyDisabledAsync<ApiResponse>(action));

    internal static async Task<ApiResponse> RunResponseAsync(Func<Task<ApiResponse>> action)
    {
        try { return await action().ConfigureAwait(false); }
        catch (Exception error)
        {
            var unknown = FindUnknownResult(error);
            if (unknown is not null)
                return ApiResponse.Fail(error.Message, unknown.Code, unknown.ErrorSource, error, ApiErrorKind.OutcomeUnknown);
            ApiErrorKind kind = ContainsException<RestoreFailureException>(error) || ContainsException<AbandonedMutexException>(error)
                ? ApiErrorKind.CleanupRequired
                : ContainsException<UnauthorizedAccessException>(error) || ContainsException<SecurityException>(error)
                    || FindNativeAccessFailure(error) is not null
                    ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed;
            return ApiResponse.Fail(error.Message,
                error is Win32Exception native ? native.NativeErrorCode : error.HResult,
                error is Win32Exception ? ApiErrorSource.Win32 : ApiErrorSource.None, error, kind);
        }
    }

    private static ApiResponse? FindUnknownResult(Exception error)
    {
        if (error is CoreOperationException { Result.ErrorKind: ApiErrorKind.OutcomeUnknown } operation)
            return operation.Result;
        if (error is RestoreFailureException { OperationResult.ErrorKind: ApiErrorKind.OutcomeUnknown } restore)
            return restore.OperationResult;
        if (error is AggregateException aggregate)
            return aggregate.InnerExceptions.Select(FindUnknownResult).FirstOrDefault(result => result is not null);
        return error.InnerException is null ? null : FindUnknownResult(error.InnerException);
    }

    private static bool ContainsException<T>(Exception error) where T : Exception => error is T
        || (error is AggregateException aggregate ? aggregate.InnerExceptions.Any(ContainsException<T>)
            : error.InnerException is not null && ContainsException<T>(error.InnerException));

    private static Win32Exception? FindNativeAccessFailure(Exception error)
    {
        if (error is Win32Exception { NativeErrorCode: 5 or 1314 } denied) return denied;
        if (error is AggregateException aggregate)
            return aggregate.InnerExceptions.Select(FindNativeAccessFailure).FirstOrDefault(value => value is not null);
        return error.InnerException is null ? null : FindNativeAccessFailure(error.InnerException);
    }

    /// <summary>临时启用 AzureFeatureSet 后执行业务，并在释放跨进程锁前恢复原值。</summary>
    /// <remarks>业务失败仍尝试恢复；业务与恢复均失败时保留两份错误。不得嵌套调用本服务。</remarks>
    /// <returns>业务回调返回的结果；获取锁、读取原值、业务或恢复异常会传播。</returns>
    /// <param name="action">在开关状态生效期间完成的异步业务。</param>
    /// <typeparam name="T">业务回调的结果类型。</typeparam>
    public static Task<T> RunTemporarilyEnabledAsync<T>(Func<Task<T>> action) =>
        RunWithTemporaryStateAsync(enabled: true, action);

    /// <summary>临时移除 AzureFeatureSet 后执行业务，并在释放跨进程锁前恢复原值。</summary>
    /// <remarks>原值不存在时结束后删除；原值存在时按原始类型及字节恢复，包括期间的外部改写。不得嵌套调用本服务。</remarks>
    /// <returns>业务回调返回的结果；获取锁、读取原值、业务或恢复异常会传播。</returns>
    /// <param name="action">在开关状态生效期间完成的异步业务。</param>
    /// <typeparam name="T">业务回调的结果类型。</typeparam>
    public static Task<T> RunTemporarilyDisabledAsync<T>(Func<Task<T>> action) =>
        RunWithTemporaryStateAsync(enabled: false, action);

    private static Task<T> RunWithTemporaryStateAsync<T>(bool enabled, Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return HostResourceLock.RunAsync(ResourceName, () =>
            RunWithTemporaryStateCoreAsync(enabled, action, CaptureOriginal, ApplyTemporary, RestoreOriginal));
    }

    // 原值读取不放入恢复区间；读取失败或尚未提交写入时，不以清理名义修改注册表。
    internal static async Task<T> RunWithTemporaryStateCoreAsync<T>(
        bool enabled, Func<Task<T>> action, Func<ValueSnapshot> capture,
        Action<bool, Action> apply, Action<ValueSnapshot> restore)
    {
        ValueSnapshot original = capture();
        bool mutationStarted = false;
        T result = default!;
        Exception? operationError = null;
        try
        {
            apply(enabled, () => mutationStarted = true);
            result = await action().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            operationError = error;
        }

        Exception? restoreError = null;
        if (mutationStarted)
        {
            try { restore(original); }
            catch (Exception error) { restoreError = error; }
        }

        if (restoreError is not null)
            throw new RestoreFailureException(operationError, result is ApiResponse response ? response : null, restoreError);

        if (operationError is not null) ExceptionDispatchInfo.Capture(operationError).Throw();
        return result;
    }

    // 用类型标识恢复失败，响应适配不能根据异常文案或任意 AggregateException 猜测副作用。
    internal sealed class RestoreFailureException : AggregateException
    {
        internal ApiResponse? OperationResult { get; }

        internal RestoreFailureException(Exception? operationError, ApiResponse? operationResult, Exception restoreError)
            : base("未能恢复 AzureFeatureSet 操作前的原值。", GetErrors(operationError, operationResult, restoreError))
        {
            OperationResult = operationResult;
        }

        private static IEnumerable<Exception> GetErrors(Exception? operationError, ApiResponse? operationResult, Exception restoreError)
        {
            if (operationError is not null) yield return operationError;
            else if (operationResult is { Success: false })
            {
                var failure = new InvalidOperationException(operationResult.Error, operationResult.Exception);
                failure.Data[nameof(ApiResponse)] = operationResult;
                yield return failure;
            }
            yield return restoreError;
        }
    }

    private static ValueSnapshot CaptureOriginal()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(VirtualizationKey, writable: false);
        return key is null ? ValueSnapshot.Missing : ReadValue(key);
    }

    private static void ApplyTemporary(bool enabled, Action beforeWrite)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(VirtualizationKey, writable: true)
            ?? throw new InvalidOperationException(Properties.Resources.Error_Host_AzureFeatureSetRegistryUnavailable);
        beforeWrite();
        if (enabled) key.SetValue(AzureFeatureSetValue, 1, RegistryValueKind.DWord);
        else key.DeleteValue(AzureFeatureSetValue, throwOnMissingValue: false);
        key.Flush();
    }

    private static void RestoreOriginal(ValueSnapshot original)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.CreateSubKey(VirtualizationKey, writable: true)
            ?? throw new InvalidOperationException(Properties.Resources.Error_Host_AzureFeatureSetRegistryUnavailable);
        RestoreValue(key, original);
    }

    internal sealed record ValueSnapshot(bool Exists, uint Type, byte[] Data)
    {
        internal static ValueSnapshot Missing { get; } = new(false, 0, []);
        internal bool SameValue(ValueSnapshot other) =>
            Exists == other.Exists && (!Exists || (Type == other.Type && Data.AsSpan().SequenceEqual(other.Data)));
    }

    // RegQueryValueExW 在同一次成功读取中返回类型和原始字节，不展开 REG_EXPAND_SZ，
    // 也不丢失字符串的终止符或 REG_NONE 数据。
    // https://learn.microsoft.com/windows/win32/api/winreg/nf-winreg-regqueryvalueexw
    internal static ValueSnapshot ReadValue(RegistryKey key)
    {
        byte[] data = new byte[256];
        for (int attempt = 0; attempt < 8; attempt++)
        {
            uint length = checked((uint)data.Length);
            int status = RegQueryValueExW(key.Handle, AzureFeatureSetValue, 0, out uint type, data, ref length);
            if (status == 2) return ValueSnapshot.Missing; // ERROR_FILE_NOT_FOUND
            if (status == 234) // ERROR_MORE_DATA：外部写入可能令值在读取期间增长。
            {
                data = new byte[checked((int)length)];
                continue;
            }
            if (status != 0) throw new Win32Exception(status);
            Array.Resize(ref data, checked((int)length));
            return new(true, type, data);
        }
        throw new IOException("AzureFeatureSet 在读取期间持续变化，未修改该值。");
    }

    // 恢复只写目标值，不删除或回滚注册表键及其其他值。
    internal static void RestoreValue(RegistryKey key, ValueSnapshot original)
    {
        if (original.Exists)
        {
            int status = RegSetValueExW(key.Handle, AzureFeatureSetValue, 0, original.Type,
                original.Data, checked((uint)original.Data.Length));
            if (status != 0) throw new Win32Exception(status);
        }
        else key.DeleteValue(AzureFeatureSetValue, throwOnMissingValue: false);
        key.Flush();
        if (!original.SameValue(ReadValue(key)))
            throw new IOException("AzureFeatureSet 恢复后的原值核对失败。");
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegQueryValueExW(SafeRegistryHandle key, string valueName, nint reserved,
        out uint type, [Out] byte[] data, ref uint dataLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegSetValueExW(SafeRegistryHandle key, string valueName, uint reserved,
        uint type, byte[] data, uint dataLength);
}
