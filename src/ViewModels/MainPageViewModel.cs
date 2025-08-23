using System.Globalization;
using System.Management.Automation;
using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Tools;

namespace ExHyperV.ViewModels
{
    public partial class MainPageViewModel : ObservableObject
    {
        [ObservableProperty]
        private string? _caption;

        [ObservableProperty]
        private string? _OSArchitecture;

        [ObservableProperty]
        private string? _cpuModel;

        [ObservableProperty]
        private string? _memCap;

        [ObservableProperty]
        private string? _appVersion;

        [ObservableProperty]
        private string? _author;

        [ObservableProperty]
        private string? _buildDate;

        public MainPageViewModel()
        {
            AppVersion = Utils.Version;
            Author = Utils.Author;
            BuildDate = Utils.GetLinkerTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);

            static (string Caption, string OSArchitecture, string CpuModel, string MemCap) LoadSystemData()
            {
                Utils.Run("Set-ExecutionPolicy RemoteSigned -Scope Process -Force");
                var script = @"
                    $os = Get-CimInstance Win32_OperatingSystem | Select-Object Caption, OSArchitecture, Version
                    $cpu = Get-CimInstance Win32_Processor | Select-Object Name, MaxClockSpeed
                    $memory = (Get-CimInstance Win32_PhysicalMemory | Measure-Object -Property Capacity -Sum).Sum / 1GB
                    return @($os, $cpu, [double]$memory)";
                var results = Utils.Run(script);

                string osCaption = "N/A";
                string osVersion = "";
                string osArch = "N/A";
                string cpuInfo = "N/A";
                string memoryInfo = "N/A GB";

                if (results != null && results.Count > 0 && results[0]?.Properties["Caption"]?.Value != null)
                {
                    osCaption = results[0].Properties["Caption"].Value.ToString().Replace("Microsoft ", "");
                    if (results[0].Properties["Version"]?.Value != null)
                    {
                        osVersion = results[0].Properties["Version"].Value.ToString();
                        if (osVersion.Length >= 5)
                        {
                            osVersion = osVersion.Substring(osVersion.Length - 5);
                        }
                    }
                    osArch = results[0].Properties["OSArchitecture"]?.Value?.ToString() ?? "N/A";
                }

                if (results != null && results.Count > 1 && results[1] != null)
                {
                    object cpuData = results[1].BaseObject;
                    var cpus = new List<PSObject>();

                    if (cpuData is System.Collections.IEnumerable enumerableData && !(cpuData is string)) { foreach (var item in enumerableData) { if (item is PSObject pso) cpus.Add(pso); } }
                    else if (cpuData is PSObject singleCpuPso) { cpus.Add(singleCpuPso); }
                    else if (results[1].Properties["Name"]?.Value != null) { cpus.Add(results[1]); }

                    if (cpus.Any())
                    {
                        PSObject firstCpu = cpus.First();
                        string cpuName = firstCpu.Properties["Name"]?.Value?.ToString()?.Trim() ?? "Unknown CPU";
                        double cpuSpeedGHz = 0;
                        if (firstCpu.Properties["MaxClockSpeed"]?.Value != null && double.TryParse(firstCpu.Properties["MaxClockSpeed"].Value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double mcsRaw))
                        {
                            cpuSpeedGHz = Math.Round(mcsRaw / 1000, 2);
                        }
                        string speedSuffix = (cpuName.IndexOf("GHz", StringComparison.OrdinalIgnoreCase) == -1 && cpuSpeedGHz > 0) ? $" @ {cpuSpeedGHz.ToString(CultureInfo.InvariantCulture)} GHz" : "";
                        cpuInfo = cpus.Count > 1 ? $"{cpuName}{speedSuffix} x{cpus.Count}" : $"{cpuName}{speedSuffix}";
                    }
                }

                if (results != null && results.Count > 2 && results[2]?.BaseObject != null && double.TryParse(results[2].BaseObject.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double totalMemoryRaw))
                {
                    double totalMemory = Math.Round(totalMemoryRaw, 2);
                    memoryInfo = $"{totalMemory.ToString(CultureInfo.InvariantCulture)} GB";
                }

                return (
                    Caption: string.IsNullOrEmpty(osVersion) ? osCaption : $"{osCaption} Build.{osVersion}",
                    OSArchitecture: osArch,
                    CpuModel: cpuInfo,
                    MemCap: memoryInfo
                );
            }

            var systemData = Task.Run(LoadSystemData).Result;

            Caption = systemData.Caption;
            OSArchitecture = systemData.OSArchitecture;
            CpuModel = systemData.CpuModel;
            MemCap = systemData.MemCap;
        }
    }
}