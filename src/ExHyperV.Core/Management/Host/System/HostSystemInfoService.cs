using ExHyperV.Tools;
using System.Runtime.InteropServices;
using System.Management;

namespace ExHyperV.Services
{
    /// <summary>读取用于显示的宿主系统概况，并在进程内缓存首次查询任务。</summary>
    public static class HostSystemInfoService
    {
        private static readonly Lazy<Task<SystemInfo>> CachedSystemInfo =
            new(() => Task.Run(GetSystemInfo));

        /// <summary>宿主基本信息汇总，包含系统名称与构建号、系统位数、处理器型号说明和内存容量文本。</summary>
        /// <param name="Caption">操作系统名称及构建号，例如 Windows 11 Pro (Build 26100)。</param>
        /// <param name="OSArchitecture">操作系统的位数文本，例如 64 位。处理器指令集见 HostProcessorPlatformService.GetArchitecture。</param>
        /// <param name="CpuModel">处理器型号的显示文本；可能附带频率，多处理器时还附带数量。</param>
        /// <param name="MemCap">已安装物理内存的容量文本，按 1,073,741,824 字节换算，标签为 GB。</param>
        public record SystemInfo(
            string Caption,
            string OSArchitecture,
            string CpuModel,
            string MemCap);

        private static readonly Lazy<Task<string>> CachedProcessorDisplayName =
            new(() => Task.Run(GetProcessorDisplayName));

        /// <summary>一次读取系统名称与构建号、系统位数、处理器型号说明和已安装物理内存容量。</summary>
        /// <returns>包含四项显示文本的基本信息汇总，对应 Caption、OSArchitecture、CpuModel 和 MemCap。</returns>
        /// <remarks>首次调用执行实际查询，后续调用复用进程内缓存。各项不可用时返回 N/A 等回退文本，首次回退结果也会被缓存。</remarks>
        public static Task<SystemInfo> GetSystemInfoAsync() => CachedSystemInfo.Value;

        /// <summary>一次读取并格式化宿主物理处理器型号，合并同型号数量并在异构多路时分行展示。</summary>
        /// <returns>格式化后的宿主物理处理器型号显示文本；不可用时返回回退文本。</returns>
        public static Task<string> GetProcessorDisplayNameAsync() => CachedProcessorDisplayName.Value;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

        /// <summary>读取宿主可用和总物理内存（MiB），采样失败时返回 null。</summary>
        /// <returns>可用及总物理内存（MiB），读取失败时为 null。</returns>
        public static (long AvailableMiB, long TotalMiB)? TryReadHostMemoryMiB()
        {
            try
            {
                var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
                if (!GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
                    return null;

                const ulong mib = 1024UL * 1024UL;
                return ((long)(status.ullAvailPhys / mib), (long)(status.ullTotalPhys / mib));
            }
            catch
            {
                return null;
            }
        }

        private static SystemInfo GetSystemInfo()
        {
            string osCaption = "N/A";
            string osArch = "N/A";
            string cpuInfo = "N/A";
            string memoryInfo = "N/A GB";

            // OS 信息
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Caption, OSArchitecture, Version FROM Win32_OperatingSystem");
                foreach (ManagementObject obj in searcher.Get())
                {
                    osCaption = obj["Caption"]?.ToString()?.Replace("Microsoft ", "") ?? "N/A";
                    osArch = obj["OSArchitecture"]?.ToString() ?? "N/A";
                    string version = obj["Version"]?.ToString() ?? "";
                    string build = version.Split('.').LastOrDefault() ?? "";
                    if (!string.IsNullOrEmpty(build))
                        osCaption = $"{osCaption} (Build {build})";
                    break;
                }
            }
            catch (Exception diagnosticError) { CoreDiagnostics.Report("OsInfoReadFailed", error: diagnosticError); }

            // CPU 信息
            try
            {
                var cpus = new List<(string Name, double SpeedGHz)>();
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, MaxClockSpeed FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString()?.Trim() ?? "Unknown CPU";
                    double speedGHz = 0;
                    if (obj["MaxClockSpeed"] != null &&
                        double.TryParse(obj["MaxClockSpeed"].ToString(),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double mhz))
                        speedGHz = Math.Round(mhz / 1000, 2);
                    cpus.Add((name, speedGHz));
                }

                if (cpus.Any())
                {
                    var (name, speed) = cpus.First();
                    string speedSuffix = (name.IndexOf("GHz", StringComparison.OrdinalIgnoreCase) == -1 && speed > 0)
                        ? $" @ {speed.ToString(System.Globalization.CultureInfo.InvariantCulture)} GHz"
                        : "";
                    cpuInfo = cpus.Count > 1
                        ? $"{name}{speedSuffix} x{cpus.Count}"
                        : $"{name}{speedSuffix}";
                }
            }
            catch (Exception diagnosticError) { CoreDiagnostics.Report("ProcessorInfoReadFailed", error: diagnosticError); }

            // 内存信息
            try
            {
                double totalGb = 0;
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Capacity FROM Win32_PhysicalMemory");
                foreach (ManagementObject obj in searcher.Get())
                {
                    if (obj["Capacity"] != null &&
                        double.TryParse(obj["Capacity"].ToString(),
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double cap))
                        totalGb += cap / (1024 * 1024 * 1024);
                }
                if (totalGb > 0)
                    memoryInfo = $"{Math.Round(totalGb, 2).ToString(System.Globalization.CultureInfo.InvariantCulture)} GB";
            }
            catch (Exception diagnosticError) { CoreDiagnostics.Report("MemoryInfoReadFailed", error: diagnosticError); }

            return new SystemInfo(osCaption, osArch, cpuInfo, memoryInfo);
        }

        private static string GetProcessorDisplayName()
        {
            try
            {
                var counts = new Dictionary<string, int>(StringComparer.Ordinal);
                var order = new List<string>();
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
                foreach (ManagementObject obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString()?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (counts.TryGetValue(name, out int existing)) counts[name] = existing + 1;
                    else { counts[name] = 1; order.Add(name); }
                }
                if (order.Count == 0) return string.Empty;
                if (order.Count == 1)
                {
                    string single = order[0];
                    int count = counts[single];
                    return count > 1 ? $"{single} × {count}" : single;
                }
                return string.Join(Environment.NewLine, order.Select(name => $"{name} × {counts[name]}"));
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
