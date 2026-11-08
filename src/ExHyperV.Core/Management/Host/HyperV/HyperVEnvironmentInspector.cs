using System.ComponentModel;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using ExHyperV.Tools;

namespace ExHyperV.Services;

// 查询串行判定、分别有界等待。一次读取超时后仍保留正在执行的原生任务，
// 后续调用共用该任务直至真正结束；不能把放弃等待描述为已取消 WMI。
internal static class HyperVEnvironmentInspector
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly SingleFlight<bool?> HypervisorFlight = new();
    private static readonly SingleFlight<VmmsServiceInfo?> VmmsFlight = new();
    private static readonly SingleFlight<bool> WmiFlight = new();

    internal interface IBackend
    {
        Task<bool?> ReadHypervisorAsync();
        Task<VmmsServiceInfo?> ReadVmmsAsync();
        Task<bool> ReadWmiAsync();
    }

    internal static Task<HyperVEnvironmentStatus> GetAsync(Func<bool?> hypervisor)
        => GetAsync(new WindowsBackend(hypervisor), DefaultTimeout);

    internal static async Task<HyperVEnvironmentStatus> GetAsync(IBackend backend, TimeSpan timeout)
    {
        ValidateTimeout(timeout);
        var hypervisor = await GetHypervisorAsync(backend, timeout).ConfigureAwait(false);
        if (hypervisor.State != HyperVCheckState.Passed)
            return new(hypervisor, NotChecked(HyperVCheckStage.Vmms), NotChecked(HyperVCheckStage.Wmi), null);
        var (vmms, service) = await GetVmmsAsync(backend, timeout).ConfigureAwait(false);
        if (vmms.State != HyperVCheckState.Passed)
            return new(hypervisor, vmms, NotChecked(HyperVCheckStage.Wmi), service);
        var wmi = await GetWmiAsync(backend, timeout).ConfigureAwait(false);
        return new(hypervisor, vmms, wmi, service);
    }

    internal static Task<(HyperVCheckResult Check, VmmsServiceInfo? Service)> GetVmmsAsync()
        => GetVmmsAsync(new WindowsBackend(() => null), DefaultTimeout);

    internal static Task<HyperVCheckResult> GetWmiAsync()
        => GetWmiAsync(new WindowsBackend(() => null), DefaultTimeout);

    private static async Task<HyperVCheckResult> GetHypervisorAsync(IBackend backend, TimeSpan timeout)
    {
        try
        {
            bool? active = await backend.ReadHypervisorAsync().WaitAsync(timeout).ConfigureAwait(false);
            return active switch
            {
                true => Passed(HyperVCheckStage.Hypervisor),
                false => new(HyperVCheckStage.Hypervisor, HyperVCheckState.NotReady, HyperVDiagnosticKind.NotRunning,
                    Properties.Resources.HostPageViewModel_HypervisorInactive),
                null => new(HyperVCheckStage.Hypervisor, HyperVCheckState.Unknown, HyperVDiagnosticKind.InvalidData,
                    "无法确认本系统监控程序状态。")
            };
        }
        catch (Exception error) { return ClassifyFailure(error, HyperVCheckStage.Hypervisor); }
    }

    internal static async Task<(HyperVCheckResult Check, VmmsServiceInfo? Service)> GetVmmsAsync(IBackend backend, TimeSpan timeout)
    {
        ValidateTimeout(timeout);
        try
        {
            var service = await backend.ReadVmmsAsync().WaitAsync(timeout).ConfigureAwait(false);
            if (service == null)
                return (new(HyperVCheckStage.Vmms, HyperVCheckState.NotReady, HyperVDiagnosticKind.NotFound,
                    Properties.Resources.HostPageViewModel_VmmsMissing), null);
            if (string.IsNullOrWhiteSpace(service.State))
                return (new(HyperVCheckStage.Vmms, HyperVCheckState.Unknown, HyperVDiagnosticKind.InvalidData,
                    "已找到 VMMS 服务，但未返回有效运行状态。"), service);
            if (service.State.Equals("Running", StringComparison.OrdinalIgnoreCase))
                return (Passed(HyperVCheckStage.Vmms), service);

            var kind = HyperVDiagnosticKind.NotRunning;
            string message = $"VMMS 服务未运行，当前状态：{service.State}。";
            if (service.StartMode?.Equals("Disabled", StringComparison.OrdinalIgnoreCase) == true)
            {
                kind = HyperVDiagnosticKind.ServiceDisabled;
                message += "服务启动类型已禁用。";
            }
            if (service.Image.State == VmmsImageState.Missing)
            {
                kind = HyperVDiagnosticKind.ExecutableMissing;
                message += $"已确认服务程序文件不存在：{service.Image.ResolvedPath}。";
            }
            if (service.ExitCode is > 0) message += $"退出码：{service.ExitCode}。";
            if (service.ServiceSpecificExitCode is > 0) message += $"服务退出码：{service.ServiceSpecificExitCode}。";
            return (new(HyperVCheckStage.Vmms, HyperVCheckState.NotReady, kind, message), service);
        }
        catch (Exception error) { return (ClassifyFailure(error, HyperVCheckStage.Vmms), null); }
    }

    internal static async Task<HyperVCheckResult> GetWmiAsync(IBackend backend, TimeSpan timeout)
    {
        ValidateTimeout(timeout);
        try
        {
            bool present = await backend.ReadWmiAsync().WaitAsync(timeout).ConfigureAwait(false);
            return present ? Passed(HyperVCheckStage.Wmi)
                : new(HyperVCheckStage.Wmi, HyperVCheckState.NotReady, HyperVDiagnosticKind.NotFound,
                    "Hyper-V WMI 查询成功，但未找到管理服务实例。");
        }
        catch (Exception error) { return ClassifyFailure(error, HyperVCheckStage.Wmi); }
    }

    internal static HyperVCheckResult ClassifyFailure(Exception error, HyperVCheckStage stage)
    {
        int code = error is ManagementException management ? (int)management.ErrorCode
            : error is Win32Exception native ? native.NativeErrorCode : error.HResult;
        uint raw = unchecked((uint)code);
        HyperVDiagnosticKind kind = error is TimeoutException || raw is 0x00040004u or 0x80043001u or 0x800705B4u or 1460u
            ? HyperVDiagnosticKind.Timeout
            : error is UnauthorizedAccessException || raw is 5u or 1314u or 0x80070005u or 0x80070522u or 0x80041003u
                ? HyperVDiagnosticKind.AccessDenied
            : raw == 0x8004100Eu ? HyperVDiagnosticKind.InvalidNamespace
            : raw == 0x80041010u ? HyperVDiagnosticKind.InvalidClass
            : raw is 0x80041004u or 0x80041011u or 0x80041012u or 0x80041013u or 0x80041014u
                ? HyperVDiagnosticKind.ProviderFailure
            : raw is 1722u or 1726u or 1753u or 0x800706BAu or 0x800706BEu or 0x800706D9u or 0x80010108u or 0x80040154u or 0x80041015u
                ? HyperVDiagnosticKind.ConnectionFailure
            : error is InvalidDataException or ArgumentException ? HyperVDiagnosticKind.InvalidData
            : HyperVDiagnosticKind.QueryFailed;
        var source = error is ManagementException ? ApiErrorSource.Wmi
            : error is COMException ? ApiErrorSource.Com
            : error is Win32Exception or UnauthorizedAccessException ? ApiErrorSource.Win32 : ApiErrorSource.None;
        string subject = stage switch
        {
            HyperVCheckStage.Hypervisor => "本系统监控程序",
            HyperVCheckStage.Vmms => "VMMS 服务",
            _ => "Hyper-V WMI"
        };
        string message = kind == HyperVDiagnosticKind.Timeout
            ? $"查询{subject}超时，无法确认当前状态；底层读取可能仍在执行。"
            : $"无法查询{subject}：{error.Message}";
        var failure = new ApiFailure(kind == HyperVDiagnosticKind.AccessDenied ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed,
            code, source, error.Message);
        return new(stage, HyperVCheckState.Unknown, kind, message, failure);
    }

    internal static VmmsImageInfo InspectImage(string? commandLine)
        => InspectImage(commandLine, File.GetAttributes);

    internal static VmmsImageInfo InspectImage(string? commandLine, Func<string, FileAttributes> getAttributes)
    {
        string? path = ResolveImagePath(commandLine);
        if (path == null) return new(VmmsImageState.Unknown, null);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (drive.DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory || !drive.IsReady)
                return new(VmmsImageState.Unknown, path);
            FileAttributes attributes = getAttributes(path);
            return new((attributes & FileAttributes.Directory) == 0 ? VmmsImageState.Exists : VmmsImageState.Unknown, path);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(VmmsImageState.Missing, path, FileFailure(error));
        }
        catch (Exception error)
        {
            return new(VmmsImageState.Unknown, path, FileFailure(error));
        }
    }

    internal static string? ResolveImagePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        try
        {
            string value = Environment.ExpandEnvironmentVariables(commandLine.Trim());
            if (value.Contains('%')) return null;
            string path;
            if (value[0] == '"')
            {
                int end = value.IndexOf('"', 1);
                if (end <= 1 || end + 1 < value.Length && !char.IsWhiteSpace(value[end + 1])) return null;
                path = value[1..end];
            }
            else
            {
                int end = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (end < 0) return null;
                end += 4;
                if (end < value.Length && !char.IsWhiteSpace(value[end])) return null;
                path = value[..end];
                // 未加引号且含空格的路径存在启动解释歧义，不能猜哪个文件会被执行。
                if (path.Any(char.IsWhiteSpace)) return null;
            }
            if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[12..]);
            if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
            // 不探测 UNC/网络路径；只检查可以明确解析的本机盘符绝对路径。
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\'
                || !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return null;
            return Path.GetFullPath(path);
        }
        catch { return null; }
    }

    private static ApiFailure FileFailure(Exception error) => new(error is UnauthorizedAccessException
        ? ApiErrorKind.AccessDenied : error is FileNotFoundException or DirectoryNotFoundException ? ApiErrorKind.NotFound : ApiErrorKind.OperationFailed,
        error.HResult, ApiErrorSource.Win32, error.Message);

    private static HyperVCheckResult Passed(HyperVCheckStage stage) => new(stage, HyperVCheckState.Passed, HyperVDiagnosticKind.None, string.Empty);
    private static HyperVCheckResult NotChecked(HyperVCheckStage stage) => new(stage, HyperVCheckState.NotChecked, HyperVDiagnosticKind.None, string.Empty);
    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    // 只共享仍在运行的读取任务，不缓存已完成结果，不增长无界的排队任务。
    internal sealed class SingleFlight<T>
    {
        private readonly object _gate = new();
        private Task<T>? _pending;
        internal Task<T> Run(Func<Task<T>> read)
        {
            lock (_gate)
            {
                if (_pending == null || _pending.IsCompleted)
                {
                    _pending = Task.Run(read);
                    _ = _pending.ContinueWith(completed => _ = completed.Exception,
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                return _pending;
            }
        }
    }

    private sealed class WindowsBackend(Func<bool?> hypervisor) : IBackend
    {
        public Task<bool?> ReadHypervisorAsync() => HypervisorFlight.Run(() => Task.FromResult(hypervisor()));
        public Task<VmmsServiceInfo?> ReadVmmsAsync() => VmmsFlight.Run(() => Task.FromResult(ReadVmms()));
        public Task<bool> ReadWmiAsync() => WmiFlight.Run(() => Task.FromResult(ReadWmi()));

        private static ManagementScope Connect(string path)
        {
            var scope = new ManagementScope(path, new ConnectionOptions { Timeout = DefaultTimeout });
            scope.Connect();
            return scope;
        }

        private static ManagementObjectSearcher Search(ManagementScope scope, string query)
            => new(scope, new ObjectQuery(query), new System.Management.EnumerationOptions
            {
                ReturnImmediately = true, Rewindable = false, Timeout = DefaultTimeout
            });

        private static VmmsServiceInfo? ReadVmms()
        {
            var scope = Connect(@"\\.\root\cimv2");
            using var searcher = Search(scope,
                "SELECT State, StartMode, ExitCode, ServiceSpecificExitCode, ProcessId, PathName FROM Win32_Service WHERE Name = 'vmms'");
            using var collection = searcher.Get();
            foreach (ManagementBaseObject service in collection)
            {
                using (service)
                {
                    string? path = service["PathName"] as string;
                    return new(service["State"] as string, service["StartMode"] as string,
                        service["ExitCode"] as uint?, service["ServiceSpecificExitCode"] as uint?,
                        service["ProcessId"] as uint?, path, InspectImage(path));
                }
            }
            return null;
        }

        private static bool ReadWmi()
        {
            var scope = Connect(@"\\.\root\virtualization\v2");
            using var searcher = Search(scope, "SELECT Name FROM Msvm_VirtualSystemManagementService");
            using var collection = searcher.Get();
            foreach (ManagementBaseObject instance in collection)
            {
                using (instance) { return true; }
            }
            return false;
        }
    }
}
