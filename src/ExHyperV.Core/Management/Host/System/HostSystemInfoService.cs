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

        /// <summary>一次读取系统名称与构建号、系统位数、处理器型号说明和已安装物理内存容量。</summary>
        /// <returns>包含四项显示文本的基本信息汇总，对应 Caption、OSArchitecture、CpuModel 和 MemCap。</returns>
        /// <remarks>首次调用执行实际查询，后续调用复用进程内缓存。各项不可用时返回 N/A 等回退文本，首次回退结果也会被缓存。</remarks>
        public static Task<SystemInfo> GetSystemInfoAsync() => CachedSystemInfo.Value;

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
            catch { }

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
            catch { }

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
            catch { }

            return new SystemInfo(osCaption, osArch, cpuInfo, memoryInfo);
        }
    }
}
