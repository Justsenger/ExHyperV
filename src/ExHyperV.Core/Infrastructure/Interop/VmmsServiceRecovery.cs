using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;

namespace ExHyperV.Tools;

internal enum VmmsServiceState : uint
{
    Unknown = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7
}

internal readonly record struct VmmsRecoveryResult(bool Attempted, bool Recovered, string? Error);

/// <summary>在 Hyper-V 操作期间检测到 vmms 意外崩溃或重连中断时等待/拉起服务并重置 WMI 连接缓存。</summary>
internal static class VmmsServiceRecovery
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const int ScStatusProcessInfo = 0;
    private const int ErrorServiceAlreadyRunning = 1056;

    internal sealed class Backend
    {
        public required Func<VmmsServiceState> QueryState { get; init; }
        public required Func<int> StartService { get; init; }
        public required Action ClearWmiCache { get; init; }
        public required Func<bool> ProbeHyperVWmi { get; init; }
        public Action<int> SleepMs { get; init; } = Thread.Sleep;
    }

    private static readonly Backend DefaultBackend = new()
    {
        QueryState = QueryVmmsStateNative,
        StartService = StartVmmsNative,
        ClearWmiCache = WmiConnectionCache.Clear,
        ProbeHyperVWmi = ProbeHyperVWmiNative
    };

    internal static async Task<VmmsRecoveryResult> TryRecoverIfStoppedAsync(
        bool recoverWhenAlreadyRunning = false,
        CancellationToken cancellationToken = default,
        Backend? backend = null)
    {
        var active = backend ?? DefaultBackend;
        return await Task.Run(() => TryRecoverCore(active, recoverWhenAlreadyRunning, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    internal static VmmsRecoveryResult TryRecoverCore(
        Backend backend,
        bool recoverWhenAlreadyRunning,
        CancellationToken cancellationToken)
    {
        VmmsServiceState state;
        try
        {
            state = backend.QueryState();
        }
        catch (Exception ex)
        {
            return new(false, false, $"查询 vmms 服务状态失败：{ex.Message}");
        }

        if (state == VmmsServiceState.Running && !recoverWhenAlreadyRunning)
            return new(false, true, null);

        // 等待正在退出的进程彻底停稳，再执行启动。
        if (state == VmmsServiceState.StopPending)
        {
            for (int i = 0; i < 50; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                backend.SleepMs(100);
                state = backend.QueryState();
                if (state != VmmsServiceState.StopPending) break;
            }
        }

        if (state is VmmsServiceState.Stopped or VmmsServiceState.Paused)
        {
            int err = backend.StartService();
            if (err != 0 && err != ErrorServiceAlreadyRunning)
                return new(true, false, $"拉起 vmms 服务失败：{new Win32Exception(err).Message} (Win32={err})");
            state = VmmsServiceState.StartPending;
        }

        if (state != VmmsServiceState.Running)
        {
            for (int i = 0; i < 150; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                backend.SleepMs(100);
                state = backend.QueryState();
                if (state == VmmsServiceState.Running) break;
                if (state == VmmsServiceState.Stopped)
                    return new(true, false, "vmms 服务启动后再次停止。");
            }
        }

        if (state != VmmsServiceState.Running)
            return new(true, false, $"等待 vmms 恢复运行超时，当前状态：{state}。");

        backend.ClearWmiCache();

        for (int i = 0; i < 25; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (backend.ProbeHyperVWmi())
                return new(true, true, null);
            backend.SleepMs(200);
        }

        return new(true, false, "vmms 服务已运行，但 Hyper-V WMI 提供程序暂未就绪。");
    }

    private static VmmsServiceState QueryVmmsStateNative()
    {
        IntPtr scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr svc = OpenServiceW(scm, "vmms", ServiceQueryStatus);
            if (svc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!QueryServiceStatusEx(svc, ScStatusProcessInfo, out ServiceStatusProcess status,
                        Marshal.SizeOf<ServiceStatusProcess>(), out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return (VmmsServiceState)status.dwCurrentState;
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private static int StartVmmsNative()
    {
        IntPtr scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return Marshal.GetLastWin32Error();
        try
        {
            IntPtr svc = OpenServiceW(scm, "vmms", ServiceQueryStatus | ServiceStart);
            if (svc == IntPtr.Zero) return Marshal.GetLastWin32Error();
            try
            {
                return StartServiceW(svc, 0, IntPtr.Zero) ? 0 : Marshal.GetLastWin32Error();
            }
            finally { CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }

    private static bool ProbeHyperVWmiNative()
    {
        try
        {
            var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
            using var searcher = new ManagementObjectSearcher(ms,
                new ObjectQuery("SELECT Name FROM Msvm_VirtualSystemManagementService"));
            using var col = searcher.Get();
            foreach (ManagementBaseObject obj in col)
            {
                using (obj) return true;
            }
            return false;
        }
        catch
        {
            return WmiConnectionCacheClearOnFailure();
        }
    }

    private static bool WmiConnectionCacheClearOnFailure()
    {
        WmiConnectionCache.Clear();
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
        public uint dwProcessId;
        public uint dwServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr hService, uint dwNumServiceArgs, IntPtr lpServiceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(
        IntPtr hService, int infoLevel, out ServiceStatusProcess lpBuffer, int cbBufSize, out int pcbBytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);
}
