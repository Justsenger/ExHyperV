using System.Runtime.InteropServices;

namespace ExHyperV.Tools;

internal enum DismFeatureState
{
    NotPresent = 0, UninstallPending = 1, Staged = 2, Removed = 3,
    Installed = 4, InstallPending = 5, Superseded = 6, PartiallyInstalled = 7
}

// 操作码与关闭、清理码分别保留；清理失败不能抹掉已经完成的修改。
internal sealed record DismFeatureOperation(ApiResponse Response, bool ReloadRequired, int NativeCode)
{
    internal int? CloseCode { get; init; }
    internal int? ShutdownCode { get; init; }
}

/// <summary>使用本机 DISM 修改在线 Windows 映像的可选功能，并在本进程串行化 DISM 生命周期。</summary>
/// <remarks>需要管理员权限和系统 DISM 支持。没有公开取消或进度入口；成功可能仍要求重启。</remarks>
internal static class DismApi
{
    // 仅协调本封装的初始化、操作及清理；不能代替同进程其他库的 DISM 协调。
    // 当前产品入口是启用 Hyper-V，禁用 Hyper-V 只修改 BCD。
    private static readonly SemaphoreSlim _gate = new(1, 1);

    internal const int RebootRequired = 3010;
    internal const int ReloadImageSessionRequired = 1;
    private const int UnknownFeature = unchecked((int)0x800F080C);

    // Empty 只表示 CBS_E_UNKNOWN_UPDATE；查询失败不能触发组件登记。
    internal static Task<ApiResponse<DismFeatureState>> GetFeatureStateAsync(string featureName)
        => GetFeatureStateAsync(featureName, new NativeApi());

    internal static async Task<ApiResponse<DismFeatureState>> GetFeatureStateAsync(string featureName, INativeApi native)
    {
        DismFeatureState? state = null;
        ApiResponse? bufferCleanupFailure = null;
        var operation = await RunFeatureAsync("DismGetFeatureInfo", session =>
        {
            nint info = 0;
            try
            {
                int hr = native.GetFeatureInfo(session, featureName, out info);
                if (hr == UnknownFeature) return 0;
                if (hr != 0) return hr;
                if (info == 0) throw new InvalidOperationException("DismGetFeatureInfo 未返回功能信息。");
                // 必须在 DismDelete 前复制数据，指针布局由平台默认对齐决定。
                state = ReadFeatureState(info);
                return 0;
            }
            finally
            {
                if (info != 0)
                {
                    try
                    {
                        int hr = native.Delete(info);
                        if (hr != 0) bufferCleanupFailure = NativeFailure("DismDelete", hr);
                    }
                    catch (Exception error)
                    {
                        bufferCleanupFailure = ApiResponse.Fail($"DismDelete 未确认完成：{error.Message}",
                            error.HResult, ApiErrorSource.Win32, error, ApiErrorKind.CleanupRequired);
                    }
                }
            }
        }, native, modifying: false).ConfigureAwait(false);
        ApiResponse result = bufferCleanupFailure is null ? operation.Response
            : CombineCleanupFailure(operation.Response, bufferCleanupFailure);
        return !result.Success
            ? ApiResponse<DismFeatureState>.Fail(result.Error, result.Code, result.ErrorSource, result.Exception, result.ErrorKind)
            : state.HasValue ? ApiResponse<DismFeatureState>.Ok(state.Value) : ApiResponse<DismFeatureState>.Empty();
    }

    internal static DismFeatureState ReadFeatureState(nint featureInfo)
    {
        var info = Marshal.PtrToStructure<DismNativeMethods.DismFeatureInfo>(featureInfo);
        if (!Enum.IsDefined(info.FeatureState))
            throw new InvalidOperationException($"DISM 返回无法识别的功能状态：{(int)info.FeatureState}。");
        // RestartRequired 是该功能安装时的重启特性，不能据此推断当前有待重启变更。
        return info.FeatureState;
    }

    internal static async Task<ApiResponse<bool>> FeatureExistsAsync(string featureName)
    {
        var result = await GetFeatureStateAsync(featureName).ConfigureAwait(false);
        return result.Success ? ApiResponse<bool>.Ok(result.HasData)
            : ApiResponse<bool>.Fail(result.Error, result.Code, result.ErrorSource, result.Exception, result.ErrorKind);
    }

    // ── 公开接口 ──────────────────────────────────────────────────

    /// <summary>将给定功能名称原样提交给 DISM 启用接口。</summary>
    /// <remarks>修改宿主在线映像；不自动重启。LimitAccess 在 Windows 8.1／Server 2012 R2 起被忽略，安装来源由 sourcePath 和系统修复策略决定。</remarks>
    /// <returns>功能请求成功（包括需要重启）且清理完成时为成功；清理失败返回 CleanupRequired，并保留功能请求结果。</returns>
    /// <param name="featureNames">Windows 可选功能的单个名称；参数名为历史命名，本封装不解析分号批量列表。</param>
    /// <param name="enableAll">是否同时启用该功能的父级依赖。</param>
    public static async Task<ApiResponse> EnableFeatureAsync(string featureNames, bool enableAll = true)
        => (await EnableFeatureWithResultAsync(featureNames, enableAll).ConfigureAwait(false)).Response;

    internal static Task<DismFeatureOperation> EnableFeatureWithResultAsync(string featureName, bool enableAll = true)
        => EnableFeatureWithResultAsync(featureName, enableAll, new NativeApi());

    internal static Task<DismFeatureOperation> EnableFeatureWithResultAsync(string featureName, bool enableAll, INativeApi native)
        => RunFeatureAsync("DismEnableFeature", session => native.EnableFeature(session, featureName, enableAll), native);

    /// <summary>将给定功能名称原样提交给 DISM 禁用接口。</summary>
    /// <remarks>修改宿主在线映像，需要管理权限；不会自动重启。</remarks>
    /// <returns>功能请求成功（包括需要重启）且清理完成时为成功；清理失败返回 CleanupRequired，并保留功能请求结果。</returns>
    /// <param name="featureNames">Windows 可选功能的单个名称；本封装不拆分名称列表。</param>
    /// <param name="removePayload">是否移除功能的组件负载；false 保留组件文件。</param>
    public static async Task<ApiResponse> DisableFeatureAsync(string featureNames, bool removePayload = false)
    {
        var native = new NativeApi();
        return (await RunFeatureAsync("DismDisableFeature",
            session => native.DisableFeature(session, featureNames, removePayload), native).ConfigureAwait(false)).Response;
    }

    // ── 内部实现 ──────────────────────────────────────────────────

    private static async Task<DismFeatureOperation> RunFeatureAsync(string operationName, Func<uint, int> operation,
        INativeApi native, bool modifying = true)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { return await Task.Run(() => ExecuteFeature(operationName, operation, native, modifying)).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private static DismFeatureOperation ExecuteFeature(string operationName, Func<uint, int> operation,
        INativeApi native, bool modifying)
    {
        bool initialized = false;
        bool sessionOpened = false;
        uint session = 0;
        string stage = "DismInitialize";
        int nativeCode = -1;
        int? closeCode = null;
        int? shutdownCode = null;
        bool restart = false;
        bool reload = false;
        var steps = new List<OperationStepResult>();
        ApiResponse result;
        ApiResponse? cleanupFailure = null;
        try
        {
            result = Run();
        }
        catch (Exception ex)
        {
            // 功能调用抛异常时不能证明请求未提交；清理不撤销已完成的功能修改。
            result = ApiResponse.Fail($"{stage} 未能确认完成：{ex.Message}", ex.HResult,
                ApiErrorSource.Win32, ex,
                modifying && stage == operationName ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.OperationFailed);
            steps.Add(OperationStepResult.From(stage, result));
        }
        finally
        {
            if (sessionOpened)
                cleanupFailure = Cleanup("DismCloseSession", native.CloseSession, session, out closeCode);

            // 初始化失败（包括其他库已经初始化）时没有清理归属；
            // 关闭会话失败时，也未满足 Shutdown 必须先关闭会话的前提。
            if (initialized && cleanupFailure is null)
                cleanupFailure = Cleanup("DismShutdown", _ => native.Shutdown(), 0, out shutdownCode);
        }

        if (cleanupFailure is not null) result = CombineCleanupFailure(result, cleanupFailure);
        return new(result.WithRestartRequired(restart).WithSteps(steps), reload, nativeCode)
        {
            CloseCode = closeCode,
            ShutdownCode = shutdownCode
        };

        ApiResponse? Cleanup(string name, Func<uint, int> cleanup, uint handle, out int? code)
        {
            code = null;
            ApiResponse response;
            try
            {
                int hr = cleanup(handle);
                code = hr;
                // 在线变更后的 CloseSession 实测会返回 3010；继续 Shutdown，保留重启要求。
                // 不将任意非负值一概作为完成，其他状态仍须明确处理。
                restart |= hr == RebootRequired;
                response = hr is 0 or RebootRequired ? ApiResponse.Ok() : NativeFailure(name, hr);
                if (hr == RebootRequired) response = response.WithRestartReason(RestartReason.WindowsFeatureChange);
            }
            catch (Exception error)
            {
                response = ApiResponse.Fail($"{name} 未能确认完成：{error.Message}", error.HResult,
                    ApiErrorSource.Win32, error, ApiErrorKind.CleanupRequired);
            }
            steps.Add(OperationStepResult.From(name, response));
            return response.Success ? null : response;
        }

        ApiResponse Run()
        {
            int hr = native.Initialize();
            if (hr != 0) return Record(NativeFailure(stage, hr));
            initialized = true;

            stage = "DismOpenSession";
            hr = native.OpenSession(out session);
            if (hr != 0) return Record(NativeFailure(stage, hr));
            if (session == 0)
                return Record(ApiResponse.Fail("DismOpenSession 未返回有效会话，未提交功能操作。",
                    source: ApiErrorSource.Win32, kind: ApiErrorKind.InvalidState));
            sessionOpened = true;

            stage = operationName;
            hr = operation(session);
            nativeCode = hr;
            restart |= hr == RebootRequired;
            reload = modifying && hr == ReloadImageSessionRequired;
            var response = hr == 0 || modifying && (hr == RebootRequired || reload)
                ? ApiResponse.Ok() : NativeFailure(stage, hr);
            if (hr == RebootRequired) response = response.WithRestartReason(RestartReason.WindowsFeatureChange);
            return Record(response);

            ApiResponse Record(ApiResponse response)
            {
                steps.Add(OperationStepResult.From(stage, response));
                return response;
            }
        }
    }

    private static ApiResponse CombineCleanupFailure(ApiResponse result, ApiResponse cleanupFailure)
    {
        string operationState = result.Success ? "DISM 操作已返回成功。" : $"DISM 操作未确认完成：{result.Error}";
        Exception? error = result.Exception is { } original && cleanupFailure.Exception is { } cleanup
            ? new AggregateException(original, cleanup) : result.Exception ?? cleanupFailure.Exception;
        return ApiResponse.Fail($"{operationState} DISM 资源清理失败：{cleanupFailure.Error} " +
            "资源未确认释放，不应据此自动重试功能操作。",
            result.Success ? cleanupFailure.Code : result.Code,
            result.Success ? cleanupFailure.ErrorSource : result.ErrorSource, error, ApiErrorKind.CleanupRequired)
            .WithRestartRequired(result.RequiresRestart).WithRestartReasons(result.RestartReasons).WithSteps(result.Steps);
    }

    private static ApiResponse NativeFailure(string stage, int hr)
        => ApiResponse.Fail($"{stage} 失败：0x{hr:X8}", hr, ApiErrorSource.Win32,
            kind: hr is 5 or unchecked((int)0x80070005) ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);

    internal interface INativeApi
    {
        int Initialize();
        int OpenSession(out uint session);
        int GetFeatureInfo(uint session, string feature, out nint info);
        int EnableFeature(uint session, string feature, bool enableAll);
        int DisableFeature(uint session, string feature, bool removePayload);
        int Delete(nint info);
        int CloseSession(uint session);
        int Shutdown();
    }

    private sealed class NativeApi : INativeApi
    {
        public int Initialize() => DismNativeMethods.DismInitialize(DismNativeMethods.DismLogLevel.DismLogErrorsWarningsInfo, null, null);
        public int OpenSession(out uint session) => DismNativeMethods.DismOpenSession(DismNativeMethods.DISM_ONLINE_IMAGE, null, null, out session);
        public int GetFeatureInfo(uint session, string feature, out nint info) => DismNativeMethods.DismGetFeatureInfo(
            session, feature, null, DismNativeMethods.DismPackageIdentifier.DismPackageNone, out info);
        public int EnableFeature(uint session, string feature, bool enableAll) => DismNativeMethods.DismEnableFeature(
            session, feature, null, DismNativeMethods.DismPackageIdentifier.DismPackageNone,
            false, null, 0, enableAll, nint.Zero, nint.Zero, nint.Zero);
        public int DisableFeature(uint session, string feature, bool removePayload) => DismNativeMethods.DismDisableFeature(
            session, feature, null, removePayload, nint.Zero, nint.Zero, nint.Zero);
        public int Delete(nint info) => DismNativeMethods.DismDelete(info);
        public int CloseSession(uint session) => DismNativeMethods.DismCloseSession(session);
        public int Shutdown() => DismNativeMethods.DismShutdown();
    }
}

// ══════════════════════════════════════════════════════════════════
//  NativeMethods — dismapi.dll P/Invoke 声明
// ══════════════════════════════════════════════════════════════════
internal static class DismNativeMethods
{
    public const string DISM_ONLINE_IMAGE = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}";

    public enum DismLogLevel { DismLogErrorsWarningsInfo = 2 }
    public enum DismPackageIdentifier { DismPackageNone = 0, DismPackageName = 1, DismPackagePath = 2 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DismFeatureInfo
    {
        internal nint FeatureName;
        internal DismFeatureState FeatureState;
        internal nint DisplayName;
        internal nint Description;
        internal int RestartRequired;
        internal nint CustomProperty;
        internal uint CustomPropertyCount;
    }

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    public static extern int DismInitialize(
        DismLogLevel logLevel,
        [MarshalAs(UnmanagedType.LPWStr)] string? logFilePath,
        [MarshalAs(UnmanagedType.LPWStr)] string? scratchDirectory);

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    public static extern int DismOpenSession(
        [MarshalAs(UnmanagedType.LPWStr)] string imagePath,
        [MarshalAs(UnmanagedType.LPWStr)] string? windowsDirectory,
        [MarshalAs(UnmanagedType.LPWStr)] string? systemDrive,
        out uint session);

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    internal static extern int DismGetFeatureInfo(uint session, string featureName,
        string? identifier, DismPackageIdentifier packageIdentifier, out nint featureInfo);

    [DllImport("dismapi.dll")]
    internal static extern int DismDelete(nint structure);

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    public static extern int DismEnableFeature(
        uint session,
        [MarshalAs(UnmanagedType.LPWStr)] string featureName,
        [MarshalAs(UnmanagedType.LPWStr)] string? identifier,
        DismPackageIdentifier packageIdentifier,
        [MarshalAs(UnmanagedType.Bool)] bool limitAccess,
        string[]? sourcePaths,
        uint sourcePathCount,
        [MarshalAs(UnmanagedType.Bool)] bool enableAll,
        nint cancelEvent,
        nint progress,
        nint userData);

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    public static extern int DismDisableFeature(
        uint session,
        [MarshalAs(UnmanagedType.LPWStr)] string featureName,
        [MarshalAs(UnmanagedType.LPWStr)] string? packageName,
        [MarshalAs(UnmanagedType.Bool)] bool removePayload,
        nint cancelEvent,
        nint progress,
        nint userData);

    [DllImport("dismapi.dll")]
    public static extern int DismCloseSession(uint session);

    [DllImport("dismapi.dll")]
    public static extern int DismShutdown();
}
