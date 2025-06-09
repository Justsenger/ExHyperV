namespace ExHyperV.Views.Pages;
using System;

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
        Date.Text = Utils.GetLinkerTime().ToString("yyyy/MM/dd HH:mm");
        Utils.Run("Set-ExecutionPolicy RemoteSigned -Scope Process -Force");
        var script = @"
        $os = Get-WmiObject Win32_OperatingSystem | Select-Object Caption, OSArchitecture, version
        $cpu = Get-WmiObject Win32_Processor | Select-Object Name, MaxClockSpeed
        $memory = (Get-WmiObject Win32_PhysicalMemory | Measure-Object -Property Capacity -Sum).Sum / 1GB
        return @($os, $cpu, $memory)";
        var results = Utils.Run(script);
        string osCaption = results[0].Properties["Caption"].Value.ToString().Replace("Microsoft ", "");
        string osVersion = results[0].Properties["version"].Value.ToString();
        osVersion = osVersion.Substring(osVersion.Length - 5);
        string osArch = results[0].Properties["OSArchitecture"].Value.ToString();
        string cpuName = results[1].Properties["Name"].Value.ToString();
        double cpuSpeedGHz = Math.Round(Convert.ToDouble(results[1].Properties["MaxClockSpeed"]?.Value ?? 0) / 1000, 2);
        string cpuInfo = $"{cpuName.Trim()} @ {cpuSpeedGHz} GHz";
        double totalMemory = Math.Round(Convert.ToDouble(results[2].BaseObject ?? 0), 2);
        string memoryInfo = $"{totalMemory} GB";

        Caption.Text = osCaption+" Build."+ osVersion;
        OSArchitecture.Text = osArch;
        CPUmodel.Text = cpuInfo;
        MemCap.Text = memoryInfo;
    }

}
