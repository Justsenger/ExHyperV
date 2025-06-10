using System.Collections;
using System.Globalization;
using System.Management.Automation;

namespace ExHyperV.Views.Pages;

public partial class MainPage
{
    public MainPage()
    {
        InitializeComponent();
        SystemInfo();
    }

    private void SystemInfo()
    {
        Version.Text = Utils.Version;
        Author.Text = Utils.Author;
        Date.Text = Utils.LinkerTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
        Utils.Run("Set-ExecutionPolicy RemoteSigned -Scope Process -Force");
        const string script = """

                                          $os = Get-WmiObject Win32_OperatingSystem | Select-Object Caption, OSArchitecture, Version
                                          $cpu = Get-WmiObject Win32_Processor | Select-Object Name, MaxClockSpeed
                                          $memory = (Get-WmiObject Win32_PhysicalMemory | Measure-Object -Property Capacity -Sum).Sum / 1GB
                                          return @($os, $cpu, [double]$memory)
                              """;
        var results = Utils.Run(script);

        var osCaption = "N/A";
        var osVersion = "";
        var osArch = "N/A";
        var cpuInfo = "N/A";
        var memoryInfo = "N/A GB";

        if (results is { Count: > 0 })
        {
            var osObject = results[0];
            var captionProperty = osObject.Properties["Caption"];
            if (captionProperty?.Value is not null)
                osCaption = captionProperty.Value.ToString()!.Replace("Microsoft ", "");

            var versionProperty = osObject.Properties["Version"];
            if (versionProperty?.Value is not null)
            {
                osVersion = versionProperty.Value.ToString()!;
                if (osVersion.Length >= 5) osVersion = osVersion[^5..];
            }

            osArch = osObject.Properties["OSArchitecture"]?.Value?.ToString() ?? "N/A";
        }

        if (results is { Count: > 1 })
        {
            var cpuData = results[1].BaseObject;
            var cpus = new List<PSObject>();

            if (cpuData is IEnumerable enumerableData and not string)
            {
                foreach (var item in enumerableData)
                    if (item is PSObject pso)
                        cpus.Add(pso);
            }
            else if (cpuData is PSObject singleCpuPso)
            {
                cpus.Add(singleCpuPso);
            }
            else if (results[1].Properties["Name"]?.Value is not null)
            {
                cpus.Add(results[1]);
            }


            if (cpus.Count != 0)
            {
                var firstCpu = cpus[0];
                var cpuName = firstCpu.Properties["Name"]?.Value?.ToString()?.Trim() ?? "Unknown CPU";
                double cpuSpeedGHz = 0;
                if (firstCpu.Properties["MaxClockSpeed"]?.Value is not null && double.TryParse(
                        firstCpu.Properties["MaxClockSpeed"].Value!.ToString(), NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var mcsRaw))
                    cpuSpeedGHz = Math.Round(mcsRaw / 1000, 2);

                var speedSuffix = "";
                if (!cpuName.Contains("GHz", StringComparison.OrdinalIgnoreCase) && cpuSpeedGHz > 0)
                    speedSuffix = $" @ {cpuSpeedGHz.ToString(CultureInfo.InvariantCulture)} GHz";

                cpuInfo = cpus.Count > 1 ? $"{cpuName}{speedSuffix} x{cpus.Count}" : $"{cpuName}{speedSuffix}";
            }
        }

        if (results is { Count: > 2 } && results[2].BaseObject is not null && double.TryParse(
                results[2].BaseObject.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture,
                out var totalMemoryRaw))
        {
            var totalMemory = Math.Round(totalMemoryRaw, 2);
            memoryInfo = $"{totalMemory.ToString(CultureInfo.InvariantCulture)} GB";
        }

        Caption.Text = string.IsNullOrEmpty(osVersion) ? osCaption : $"{osCaption} Build.{osVersion}";
        OsArchitecture.Text = osArch;
        CpuModel.Text = cpuInfo;
        MemCap.Text = memoryInfo;
    }
}