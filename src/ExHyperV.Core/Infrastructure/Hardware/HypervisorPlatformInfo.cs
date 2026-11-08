using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExHyperV.Tools;

// 查询本机 Windows Hypervisor Platform 的运行可用性，不读取下次启动的 BCD 配置。
// ARM64 公开支持起点为 Windows 11 24H2 26100.3915；旧系统/缺少组件时保留未知。
internal static class HypervisorPlatformInfo
{
    internal static bool? Read()
    {
        if (RuntimeInformation.OSArchitecture == Architecture.Arm64 &&
            !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100, 3915))
            return null;
        return Read(Query);
    }

    internal static bool? Read(Func<(int Status, uint Written, ulong Value)> query)
    {
        try
        {
            var result = query();
            // HypervisorPresent 输出为 Windows BOOL（4 字节）；其他长度/值不作推断。
            if (result.Status != 0 || result.Written != sizeof(int) || result.Value > 1)
                return null;
            return result.Value == 1;
        }
        catch (Exception error)
        {
            Debug.WriteLine($"[Hyper-V platform] Runtime query unavailable: {error.Message}");
            return null;
        }
    }

    private static (int, uint, ulong) Query()
    {
        ulong value = 0;
        int status = WHvGetCapability(0, ref value, sizeof(ulong), out uint written);
        return (status, written, value);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("WinHvPlatform.dll", ExactSpelling = true)]
    private static extern int WHvGetCapability(int capabilityCode, ref ulong capabilityBuffer,
        uint capabilityBufferSizeInBytes, out uint writtenSizeInBytes);
}
