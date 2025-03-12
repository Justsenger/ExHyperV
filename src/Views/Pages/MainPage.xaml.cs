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
        $os = Get-WmiObject Win32_OperatingSystem | Select-Object Caption, OSArchitecture
        $cpu = Get-WmiObject Win32_Processor | Select-Object Name, MaxClockSpeed
        $memory = (Get-WmiObject Win32_PhysicalMemory | Measure-Object -Property Capacity -Sum).Sum / 1GB
        $gpu = Get-WmiObject Win32_VideoController | Where-Object { $_.AdapterRAM -gt 0 } | Select-Object Name
        return @($os, $cpu, $memory, $gpu)";
        var results = Utils.Run(script);
        string osCaption = results[0].Properties["Caption"].Value.ToString().Replace("Microsoft ", "");
        string osArch = results[0].Properties["OSArchitecture"].Value.ToString();
        string cpuName = results[1].Properties["Name"].Value.ToString();
        double cpuSpeedGHz = Math.Round(Convert.ToDouble(results[1].Properties["MaxClockSpeed"]?.Value ?? 0) / 1000, 2);
        string cpuInfo = $"{cpuName.Trim()} @ {cpuSpeedGHz} GHz";
        double totalMemory = Math.Round(Convert.ToDouble(results[2].BaseObject ?? 0), 2);
        string memoryInfo = $"{totalMemory} GB";
        string gpuName = "未知显卡";
        if (results != null && results.Count > 3 && results[3].Properties["Name"]?.Value != null)
        {gpuName = results[3].Properties["Name"].Value.ToString();}
        Caption.Text = osCaption;
        OSArchitecture.Text = osArch;
        CPUmodel.Text = cpuInfo;
        MemCap.Text = memoryInfo;
        GPUmodel.Text = gpuName;
    }

}
