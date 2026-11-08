using System.ComponentModel;
using System.IO;
using ExHyperV.Tools;
using Microsoft.Win32;

namespace ExHyperV.Services;

/// <summary>实验性系统产品类型切换的提交状态。</summary>
public enum HostProductTypeSwitchState
{
    /// <summary>本次替换已提交，关联步骤也完成，等待宿主重启。</summary>
    SubmittedAwaitingRestart,
    /// <summary>已发现此前成功提交的待重启替换，本次未再次提交。</summary>
    AlreadyPending,
    /// <summary>本次未确认替换提交成功。</summary>
    Failed,
    /// <summary>挂载清理或提交后的步骤未完全完成，须保留并核对恢复材料。</summary>
    CleanupRequired
}

/// <summary>此前产品类型切换的挂起记录。</summary>
/// <param name="TargetType">已知目标 WinNT/ServerNT；未知时为空字符串。</param>
/// <param name="IsConfirmed">是否有成功替换后写入的挂起标记；仅文件占用不能确认。</param>
/// <param name="RecoveryPaths">已记录或检测到的恢复材料路径。</param>
public sealed record HostProductTypePendingSwitch(string TargetType, bool IsConfirmed, IReadOnlyList<string> RecoveryPaths);

/// <summary>产品类型切换结果；即使失败也保留恢复材料和未卸载注册表键。</summary>
public sealed class HostProductTypeSwitchResult
{
    /// <summary>产品类型切换的提交与清理状态。</summary>
    /// <fielddoc>
    /// <meaning>产品类型切换的提交与清理状态。</meaning>
    /// </fielddoc>
    public HostProductTypeSwitchState State { get; }
    /// <summary>切换操作的结果及错误信息。</summary>
    /// <fielddoc>
    /// <meaning>切换操作的结果及错误信息。</meaning>
    /// </fielddoc>
    public ApiResponse Response { get; }
    /// <summary>本次切换的目标产品类型；已有挂起替换时为其目标类型。</summary>
    /// <fielddoc>
    /// <meaning>本次切换的目标产品类型；已有挂起替换时为其目标类型。</meaning>
    /// <values kind="options">WinNT：工作站；ServerNT：服务器。</values>
    /// </fielddoc>
    public string TargetType { get; }
    /// <summary>切换使用的恢复目录、工作注册表文件和备份路径。</summary>
    /// <fielddoc>
    /// <meaning>切换使用的恢复目录、工作注册表文件和备份路径。</meaning>
    /// <values kind="format">宿主路径列表；在早期失败时，部分路径对应的文件可能尚未创建。</values>
    /// </fielddoc>
    public IReadOnlyList<string> RecoveryPaths { get; }
    /// <summary>清理时未能卸载的临时注册表键。</summary>
    /// <fielddoc>
    /// <meaning>清理时未能卸载的临时注册表键。</meaning>
    /// <values kind="format">HKLM 下的键名；无已知残留时为 null。</values>
    /// </fielddoc>
    public string? MountedHiveKey { get; }
    /// <summary>本次调用是否已成功提交系统注册表替换。后续步骤失败时仍保留提交结果。</summary>
    /// <fielddoc>
    /// <meaning>本次调用是否已成功提交系统注册表替换。后续步骤失败时仍保留提交结果。</meaning>
    /// <values kind="options">true：已提交；false：未确认提交成功。</values>
    /// </fielddoc>
    public bool ReplacementSubmitted { get; }
    /// <summary>是否已有等待宿主重启的产品类型替换。</summary>
    /// <fielddoc>
    /// <meaning>是否已有等待宿主重启的产品类型替换。</meaning>
    /// <values kind="options">true：本次或此前已提交替换；false：尚未确认提交。</values>
    /// </fielddoc>
    public bool RequiresRestart => ReplacementSubmitted || State == HostProductTypeSwitchState.AlreadyPending;
    /// <summary>产品类型替换是否完整提交，或已确认此前有待重启的替换。</summary>
    /// <fielddoc>
    /// <meaning>产品类型替换是否完整提交，或已确认此前有待重启的替换。</meaning>
    /// <values kind="options">true：完整提交或此前已挂起；false：提交失败或仍有清理事项。</values>
    /// </fielddoc>
    public bool Success => Response.Success && State is HostProductTypeSwitchState.SubmittedAwaitingRestart or HostProductTypeSwitchState.AlreadyPending;

    internal HostProductTypeSwitchResult(HostProductTypeSwitchState state, ApiResponse response, string target,
        IEnumerable<string> recoveryPaths, string? mountedHiveKey, bool submitted)
    {
        State = state; Response = response; TargetType = target;
        RecoveryPaths = Array.AsReadOnly(recoveryPaths.ToArray());
        MountedHiveKey = mountedHiveKey; ReplacementSubmitted = submitted;
    }
}

/// <summary>读取和实验性修改宿主 Windows 产品类型，通过 SYSTEM hive 替换安排重启后生效。</summary>
/// <remarks>此功能为实验性 Windows 产品类型修改，需要管理员及备份、还原特权。每次使用独立工作目录，恢复材料保存在当前用户的 LocalApplicationData/ExHyperV/Recovery 下。挂起替换所用文件需保留到重启完成并核对结果；同一宿主禁止并行提交。</remarks>
public static class HostProductTypeService
{
    internal const string PendingKeyPath = @"SOFTWARE\ExHyperV\PendingSystemTypeSwitch";
    private static readonly IHostProductTypeBackend Backend = new HostProductTypeBackend();

    /// <summary>读取宿主是否属于服务器产品类型。</summary>
    /// <returns>ProductType 为非 WinNT 值时返回 true；工作站、未取得或读取失败时返回 false。</returns>
    public static bool IsServerSystem()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\ProductOptions");
            string? type = key?.GetValue("ProductType")?.ToString();
            return type != null && !type.Equals("WinNT", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>读取此前提交的挂起标记，并检查遗留恢复文件是否仍被占用。</summary>
    /// <returns>成功空结果表示未发现挂起线索；有数据时需检查 IsConfirmed。读取错误单独返回失败。</returns>
    public static ApiResponse<HostProductTypePendingSwitch> GetPendingSwitch() => Backend.ReadPending();

    /// <summary>仅根据已经成功读取的挂起信息判断是否允许尝试提交；执行仍会重新检查并获取跨进程锁。</summary>
    /// <param name="pending">成功查询得到的挂起信息；null 表示没有发现挂起线索。</param>
    /// <returns>不存在挂起线索时为 true；查询失败时不应调用此判断。</returns>
    public static bool CanApplySwitch(HostProductTypePendingSwitch? pending) => pending == null;

    internal static string? GetPendingTarget()
    {
        var result = GetPendingSwitch();
        return !result.Success ? string.Empty : result.HasData ? result.Data.TargetType : null;
    }

    /// <summary>准备并提交实验性 SYSTEM hive 产品类型替换，不重启宿主。</summary>
    /// <param name="toServer">true 请求 ServerNT，false 请求 WinNT。</param>
    /// <returns>结构化提交状态；失败仍携带恢复路径和已知挂载残留，不删除旧备份。</returns>
    /// <remarks>其他进程正在提交时立即失败。已有确认的挂起替换时返回其目标，保留原请求。切换为 ServerNT 时同时禁用关机事件跟踪，切换为 WinNT 时删除该策略覆盖。替换提交、策略写入和挂起标记全部完成才报告成功；部分完成或清理失败时，结果保留已提交状态和恢复材料。</remarks>
    public static HostProductTypeSwitchResult ApplySwitch(bool toServer) => ApplySwitchCore(toServer, Backend);

    internal static HostProductTypeSwitchResult ApplySwitchCore(bool toServer, IHostProductTypeBackend backend)
    {
        string target = toServer ? "ServerNT" : "WinNT";
        HostProductTypeWork? work = null;
        string? mounted = null;
        bool submitted = false;
        IDisposable? lease = null;
        HostProductTypeSwitchResult? operationResult = null;
        HostProductTypeSwitchResult Result(HostProductTypeSwitchState state, ApiResponse response, IEnumerable<string>? paths = null)
            => new(state, response, target, paths ?? work?.RecoveryPaths ?? [], mounted, submitted);
        ApiResponse Invoke(Func<ApiResponse> operation)
        {
            try { return operation(); }
            catch (Exception error) { return FailureFromException(error); }
        }
        HostProductTypeSwitchResult Fail(ApiResponse failure)
        {
            if (mounted != null)
            {
                var unload = Invoke(() => backend.UnloadHive(mounted));
                if (unload.Success) mounted = null;
                else failure = ApiResponse.Fail(failure.Error + " 临时 hive 卸载失败：" + unload.Error,
                    unload.Code, unload.ErrorSource, unload.Exception, ApiErrorKind.CleanupRequired);
            }
            if (submitted || mounted != null || failure.ErrorKind == ApiErrorKind.CleanupRequired)
                return Result(HostProductTypeSwitchState.CleanupRequired, ApiResponse.Fail(failure.Error,
                    failure.Code, failure.ErrorSource, failure.Exception, ApiErrorKind.CleanupRequired));
            return Result(HostProductTypeSwitchState.Failed, failure);
        }
        try
        {
            var acquired = backend.TryAcquire();
            if (!acquired.HasData) return Fail(ApiResponse.Fail(acquired.Error, acquired.Code, acquired.ErrorSource, acquired.Exception, acquired.ErrorKind));
            lease = acquired.Data;
            var pending = backend.ReadPending();
            if (!pending.Success) return Fail(ApiResponse.Fail(pending.Error, pending.Code, pending.ErrorSource, pending.Exception, pending.ErrorKind));
            if (pending.HasData)
            {
                if (!pending.Data.IsConfirmed)
                    return Result(HostProductTypeSwitchState.Failed, ApiResponse.Fail("恢复文件仍被占用，无法确认是否存在待重启替换，请先核对。", kind: ApiErrorKind.Conflict), pending.Data.RecoveryPaths);
                target = pending.Data.TargetType;
                return Result(HostProductTypeSwitchState.AlreadyPending, ApiResponse.Ok(), pending.Data.RecoveryPaths);
            }
            return backend.RunWithPrivileges(() =>
            {
                // 写入和失败卸载都在临时身份内结束，再恢复线程身份。
                try
                {
                    work = backend.CreateWork();
                    var saved = Invoke(() => backend.SaveHive(work.HivePath));
                    if (!saved.Success) return operationResult = Fail(saved);
                    var loaded = Invoke(() => backend.LoadHive(work.MountKey, work.HivePath));
                    if (!loaded.Success) return operationResult = Fail(loaded);
                    mounted = work.MountKey;
                    var current = backend.ReadCurrentControlSet(work.MountKey);
                    if (!current.HasData || current.Data <= 0 || current.Data > 999)
                        return operationResult = Fail(ApiResponse.Fail(current.Success ? "无法确认离线 SYSTEM 的当前 ControlSet，已停止修改。" : current.Error,
                            current.Code, current.ErrorSource, current.Exception, current.Success ? ApiErrorKind.InvalidState : current.ErrorKind));
                    var patched = Invoke(() => backend.WriteProductType(work.MountKey, current.Data, target));
                    if (!patched.Success) return operationResult = Fail(patched);
                    var unloaded = Invoke(() => backend.UnloadHive(work.MountKey));
                    if (!unloaded.Success)
                        return operationResult = Result(HostProductTypeSwitchState.CleanupRequired, ApiResponse.Fail("临时 hive 卸载失败，未提交替换：" + unloaded.Error,
                            unloaded.Code, unloaded.ErrorSource, unloaded.Exception, ApiErrorKind.CleanupRequired));
                    mounted = null;
                    var replaced = Invoke(() => backend.ReplaceHive(work.HivePath, work.BackupPath));
                    if (!replaced.Success)
                        return operationResult = Fail(ApiResponse.Fail(replaced.Error, replaced.Code, replaced.ErrorSource, replaced.Exception,
                            replaced.Code == 5 ? ApiErrorKind.AccessDenied : replaced.ErrorKind));
                    submitted = true;
                    var policy = Invoke(() => backend.ApplyShutdownEventTracker(toServer));
                    var marker = Invoke(() => backend.MarkPending(target, work));
                    if (!policy.Success || !marker.Success)
                        return operationResult = Fail(ApiResponse.Fail($"SYSTEM 替换已提交，但后续步骤未完成。策略：{policy.Error} 挂起记录：{marker.Error}", kind: ApiErrorKind.CleanupRequired));
                    return operationResult = Result(HostProductTypeSwitchState.SubmittedAwaitingRestart, ApiResponse.Ok());
                }
                catch (Exception error) { return operationResult = Fail(FailureFromException(error)); }
            });
        }
        catch (Exception error)
        {
            if (operationResult == null) return Fail(FailureFromException(error));

            // 回调已经结束，临时身份清理失败不能抹去实际提交或原始失败。
            var cleanup = FailureFromException(error);
            var original = operationResult.Response;
            Exception combined = original.Exception is { } cause ? new AggregateException(cause, error) : error;
            return Result(HostProductTypeSwitchState.CleanupRequired, ApiResponse.Fail(
                (original.Success ? string.Empty : original.Error + " ") + "临时特权清理失败：" + cleanup.Error,
                original.Success ? cleanup.Code : original.Code,
                original.Success ? cleanup.ErrorSource : original.ErrorSource,
                combined, ApiErrorKind.CleanupRequired));
        }
        finally { lease?.Dispose(); }
    }

    private static ApiResponse FailureFromException(Exception error)
    {
        var native = error as Win32Exception;
        return ApiResponse.Fail(error.Message, native?.NativeErrorCode ?? -1,
            native == null ? ApiErrorSource.None : ApiErrorSource.Win32, error,
            error is UnauthorizedAccessException or System.Security.SecurityException || native?.NativeErrorCode is 5 or 1300 or 1314
                ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);
    }
}

internal sealed record HostProductTypeWork(string DirectoryPath, string HivePath, string BackupPath, string MountKey)
{
    internal IReadOnlyList<string> RecoveryPaths => [DirectoryPath, HivePath, BackupPath];
}

internal interface IHostProductTypeBackend
{
    ApiResponse<IDisposable> TryAcquire();
    ApiResponse<HostProductTypePendingSwitch> ReadPending();
    HostProductTypeSwitchResult RunWithPrivileges(Func<HostProductTypeSwitchResult> operation);
    HostProductTypeWork CreateWork();
    ApiResponse SaveHive(string hivePath);
    ApiResponse LoadHive(string mountKey, string hivePath);
    ApiResponse<int> ReadCurrentControlSet(string mountKey);
    ApiResponse WriteProductType(string mountKey, int controlSet, string target);
    ApiResponse UnloadHive(string mountKey);
    ApiResponse ReplaceHive(string hivePath, string backupPath);
    ApiResponse ApplyShutdownEventTracker(bool toServer);
    ApiResponse MarkPending(string target, HostProductTypeWork work);
}

internal sealed class HostProductTypeBackend : IHostProductTypeBackend
{
    private static string WorkRoot
    {
        get
        {
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localData)) throw new IOException("无法确定持久恢复材料目录。");
            return Path.Combine(localData, "ExHyperV", "Recovery", "ProductTypeSwitch");
        }
    }
    public ApiResponse<IDisposable> TryAcquire()
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, @"Global\ExHyperV.ProductTypeSwitch");
            bool entered;
            try { entered = mutex.WaitOne(0); } catch (AbandonedMutexException) { entered = true; }
            if (!entered) { mutex.Dispose(); return ApiResponse<IDisposable>.Fail("另一个进程正在处理系统产品类型切换。", kind: ApiErrorKind.Conflict); }
            return ApiResponse<IDisposable>.Ok(new MutexLease(mutex));
        }
        catch (Exception error)
        {
            mutex?.Dispose();
            return ApiResponse<IDisposable>.Fail(error.Message, exception: error,
                kind: error is UnauthorizedAccessException ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);
        }
    }
    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;
        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _mutex, null);
            if (value == null) return;
            try { value.ReleaseMutex(); } finally { value.Dispose(); }
        }
    }
    public ApiResponse<HostProductTypePendingSwitch> ReadPending()
    {
        try
        {
            using (var key = Registry.LocalMachine.OpenSubKey(HostProductTypeService.PendingKeyPath))
                if (key?.GetValue("Target") is string target && target is "ServerNT" or "WinNT")
                {
                    var paths = key.GetValue("RecoveryPaths") as string[] ?? [];
                    return ApiResponse<HostProductTypePendingSwitch>.Ok(new(target, true, Array.AsReadOnly(paths)));
                }
            var backups = new List<string> { @"C:\temp\sys_bak_exec.hiv" };
            if (Directory.Exists(WorkRoot)) backups.AddRange(Directory.EnumerateFiles(WorkRoot, "SYSTEM.backup.hiv", SearchOption.AllDirectories));
            foreach (string backup in backups)
            {
                if (!File.Exists(backup)) continue;
                try { using var stream = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None); }
                catch (IOException) { return ApiResponse<HostProductTypePendingSwitch>.Ok(new(string.Empty, false, Array.AsReadOnly(new[] { backup }))); }
            }
            return ApiResponse<HostProductTypePendingSwitch>.Empty();
        }
        catch (Exception error)
        {
            return ApiResponse<HostProductTypePendingSwitch>.Fail(error.Message, exception: error,
                kind: error is UnauthorizedAccessException ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);
        }
    }
    public HostProductTypeSwitchResult RunWithPrivileges(Func<HostProductTypeSwitchResult> operation)
        => TemporaryPrivileges.Run(["SeBackupPrivilege", "SeRestorePrivilege"], operation);
    public HostProductTypeWork CreateWork()
    {
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(WorkRoot, id);
        Directory.CreateDirectory(directory);
        return new(directory, Path.Combine(directory, "SYSTEM.modified.hiv"), Path.Combine(directory, "SYSTEM.backup.hiv"), "ExHyperV_ProductType_" + id);
    }
    public ApiResponse SaveHive(string path) => Win32Api.SaveHive("SYSTEM", path);
    public ApiResponse LoadHive(string key, string path) => Win32Api.LoadHive(key, path);
    public ApiResponse<int> ReadCurrentControlSet(string key) => Win32Api.GetHiveDwordValue(key + @"\Select", "Current");
    public ApiResponse WriteProductType(string key, int controlSet, string target)
        => Win32Api.SetHiveStringValue($"{key}\\ControlSet{controlSet:D3}\\Control\\ProductOptions", "ProductType", target);
    public ApiResponse UnloadHive(string key) => Win32Api.UnloadHive(key);
    public ApiResponse ReplaceHive(string hive, string backup) => Win32Api.ReplaceHive("SYSTEM", hive, backup);
    public ApiResponse ApplyShutdownEventTracker(bool toServer)
    {
        try
        {
            const string path = @"SOFTWARE\Policies\Microsoft\Windows NT\Reliability";
            if (toServer)
            {
                using var key = Registry.LocalMachine.CreateSubKey(path);
                if (key == null) return ApiResponse.Fail("无法打开关机事件跟踪策略。", kind: ApiErrorKind.AccessDenied);
                key.SetValue("ShutdownReasonOn", 0, RegistryValueKind.DWord);
            }
            else
            {
                using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
                key?.DeleteValue("ShutdownReasonOn", throwOnMissingValue: false);
            }
            return ApiResponse.Ok();
        }
        catch (Exception error) { return ApiResponse.Fail(error.Message, exception: error, kind: ApiErrorKind.OperationFailed); }
    }
    public ApiResponse MarkPending(string target, HostProductTypeWork work)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(HostProductTypeService.PendingKeyPath, true, RegistryOptions.Volatile);
            if (key == null) return ApiResponse.Fail("无法写入待重启记录。", kind: ApiErrorKind.AccessDenied);
            key.SetValue("RecoveryPaths", work.RecoveryPaths.ToArray(), RegistryValueKind.MultiString);
            key.SetValue("Target", target);
            return ApiResponse.Ok();
        }
        catch (Exception error) { return ApiResponse.Fail(error.Message, exception: error, kind: ApiErrorKind.OperationFailed); }
    }
}
