using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExHyperV.Tools;

// SystemHypervisorInformation 的调度器字节从 Windows 10 1903 起使用；此前该位置为保留字段。
// 这是未文档化布局：只接受已知长度及布尔字段，未知调度器编号交给调用方回退。
internal static class HypervisorSchedulerInfo
{
    internal static (bool? Present, byte Scheduler) Read()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362)) return (null, 0);
        try
        {
            int status = NtQuerySystemInformation(91, out HypervisorInformation info, 16, out uint returned);
            if (status < 0 || returned != 16 || info.Connected > 1 || info.DebuggingEnabled > 1 || info.Present > 1)
                return (null, 0);
            return (info.Present == 1, info.Scheduler);
        }
        catch (Exception error)
        {
            Debug.WriteLine($"[Hyper-V scheduler] Kernel query failed: {error.Message}");
            return (null, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HypervisorInformation
    {
        public byte Connected;
        public byte DebuggingEnabled;
        public byte Present;
        public byte Scheduler;
        public uint Padding;
        public ulong EnabledEnlightenments;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQuerySystemInformation(int informationClass,
        out HypervisorInformation information, uint length, out uint returnedLength);
}
