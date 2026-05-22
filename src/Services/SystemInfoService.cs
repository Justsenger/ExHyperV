using System.Management;

namespace ExHyperV.Services
{
    public class SystemInfoService
    {
        public record SystemInfo(
            string Caption,
            string OSArchitecture,
            string CpuModel,
            string MemCap);

        public Task<SystemInfo> GetSystemInfoAsync() => Task.Run(GetSystemInfo);

        private SystemInfo GetSystemInfo()
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
                    if (version.Length >= 5)
                        osCaption = $"{osCaption} Build.{version.Substring(version.Length - 5)}";
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