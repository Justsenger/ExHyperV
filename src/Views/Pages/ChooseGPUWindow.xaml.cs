using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Management.Automation;
using System.Windows;
using System.Windows.Controls;
using ExHyperV.Views.Pages;
using Wpf.Ui.Controls;
using static ExHyperV.Views.Pages.GPUPage;
using ListView = Wpf.Ui.Controls.ListView;

namespace ExHyperV;

public partial class ChooseGPUWindow : FluentWindow
{
    public string GPUManu = null;

    public string Machinename;

    public ChooseGPUWindow(string vmname, List<GPUInfo> Hostgpulist)
    {
        Machinename = vmname;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        InitializeComponent();
        Items = new ObservableCollection<GPU>();

        foreach (var gpu in Hostgpulist)
        {
            if (gpu.Pname == null) continue;
            Items.Add(new GPU
            {
                GPUname = gpu.Name, Path = gpu.Pname, Id = gpu.InstanceId,
                Iconpath = Utils.GetGpuImagePath(gpu.Manu, gpu.Name), Manu = gpu.Manu
            });
        }

        DataContext = this;
    }

    public ObservableCollection<GPU> Items { get; set; }
    public event EventHandler<(string, string)> GPUSelected;

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        ConfirmButton.IsEnabled = false;
        var selectedGpu = GpuListView.SelectedItem as GPU;
        if (selectedGpu != null)
        {
            progress.IsIndeterminate = true;

            var result = await Task.Run(() => GPUMount(Machinename, selectedGpu.Path, selectedGpu.Manu));

            if (result == "running")
            {
                ContentDialog Dialog = new()
                {
                    Title = LocalizationHelper.GetString("Settings"),
                    Content = Utils.TextBlock3(LocalizationHelper.GetString("Colsefirst")),
                    CloseButtonText = LocalizationHelper.GetString("OK")
                };
                Dialog.DialogHost = ContentPresenterForDialogs;

                await Dialog.ShowAsync(CancellationToken.None);
            }
            else if (result == "error")
            {
                ContentDialog Dialog = new()
                {
                    Title = LocalizationHelper.GetString("Settings"),
                    Content = Utils.TextBlock3(LocalizationHelper.GetString("drivererror")),
                    CloseButtonText = LocalizationHelper.GetString("OK")
                };
                Dialog.DialogHost = ContentPresenterForDialogs;

                await Dialog.ShowAsync(CancellationToken.None);
            }
            else if (result == "OK")
            {
                progress.IsIndeterminate = false;
                GPUSelected?.Invoke(this, (selectedGpu.GPUname, Machinename));
            }
        }

        Close();
    }


    private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var listView = sender as ListView;

        if (listView.SelectedItem != null)
            ConfirmButton.IsEnabled = true;
        else
            ConfirmButton.IsEnabled = false;
    }

    public string GPUMount(string vmname, string gpupath, string manu)
    {
        try
        {
            var ps = PowerShell.Create();

            ps.AddScript($@"(Get-VM -Name '{vmname}').State");
            if (ps.Invoke()[0].ToString() != "Off") return "running";

            ps.AddScript($"Set-VM -GuestControlledCacheTypes $true -VMName '{vmname}'");
            ps.Invoke();
            ps.AddScript(
                $"Set-VM -HighMemoryMappedIoSpace 32GB –VMName '{vmname}'");
            ps.Invoke();
            ps.AddScript($"Set-VM -LowMemoryMappedIoSpace 128MB -VMName '{vmname}'");
            ps.Invoke();
            ps.AddScript($"Get-VM -VMName '{vmname}' | select *");
            ps.Invoke();

            ps.AddScript($"Add-VMGpuPartitionAdapter -VMName '{vmname}' -InstancePath '{gpupath}'");
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript(
                $"Set-VMGpuPartitionAdapter -VMName '{vmname}' -MinPartitionVRAM 80000000 -MaxPartitionVRAM 100000000 -OptimalPartitionVRAM 100000000 -MinPartitionEncode 80000000 -MaxPartitionEncode 100000000 -OptimalPartitionEncode 100000000 -MinPartitionDecode 80000000 -MaxPartitionDecode 100000000 -OptimalPartitionDecode 100000000 -MinPartitionCompute 80000000 -MaxPartitionCompute 100000000 -OptimalPartitionCompute 100000000");
            ps.Invoke();

            //todo分支，如果未找到路径，不报错，而是转为linux注入模式，要求输入用户名和密码等待ssh链接。

            ps.AddScript($"(Get-VMHardDiskDrive -vmname '{vmname}')[0].Path");
            var harddiskpath = ps.Invoke()[0].ToString();

            ps.AddScript(@$"
            $VHD = Mount-VHD -Path '{harddiskpath}' -PassThru
            $VHD | Get-Disk | Get-Partition | Where-Object {{ -not $_.DriveLetter }} | Add-PartitionAccessPath -AssignDriveLetter
            $volumes = $VHD | Get-Disk | Get-Partition | Get-Volume

            foreach ($volume in $volumes) {{
                if ($volume.DriveLetter -and (Test-Path ""$($volume.DriveLetter):\Windows\System32"")) {{
                    Write-Output $volume.DriveLetter
                    break 
                }}
            }}
            ");

            var letter = ps.Invoke()[0].ToString();


            var sourceFolder = @"C:\Windows\System32\DriverStore\FileRepository";
            var destinationFolder = letter + @":\Windows\System32\HostDriverStore\FileRepository";

            if (!Directory.Exists(destinationFolder)) Directory.CreateDirectory(destinationFolder);

            var process = new Process
            {
                StartInfo =
                {
                    FileName = "robocopy",
                    Arguments = $"\"{sourceFolder}\" \"{destinationFolder}\" /MIR /NP /NJH /NFL /NDL",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            process.BeginOutputReadLine();
            process.WaitForExit();

            SetFolderReadOnly(destinationFolder);


            if (manu.Contains("NVIDIA")) NvidiaReg(letter + ":");

            ps.AddScript($"Dismount-VHD -Path '{harddiskpath}'");
            ps.Invoke();

            return "OK";
        }

        catch
        {
            return "error";
        }
    }

    private static void SetFolderReadOnly(string folderPath)
    {
        var directoryInfo = new DirectoryInfo(folderPath);
        directoryInfo.Attributes |= FileAttributes.ReadOnly;

        foreach (var subFolder in Directory.GetDirectories(folderPath)) SetFolderReadOnly(subFolder);

        foreach (var file in Directory.GetFiles(folderPath))
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
    }

    public void NvidiaReg(string letter)
    {
        var localKeyPath = @"HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm";
        var tempRegFile = AppDomain.CurrentDomain.BaseDirectory + @"nvlddmkm.reg";
        RunPsScript($@"reg export ""{localKeyPath}"" ""{tempRegFile}"" /y"); //导出本机注册表信息
        var systemHiveFile = $@"{letter}\Windows\System32\Config\SYSTEM";
        RunPsScript($@"reg load HKLM\OfflineSystem ""{systemHiveFile}"""); //离线注册表挂载到本机注册表
        var originalText = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm";
        var targetText = @"HKEY_LOCAL_MACHINE\OfflineSystem\ControlSet001\Services\nvlddmkm";
        var regContent = File.ReadAllText(tempRegFile);
        regContent = regContent.Replace(originalText, targetText);
        regContent = regContent.Replace("DriverStore", "HostDriverStore");
        File.WriteAllText(tempRegFile, regContent);
        RunPsScript($@"reg import ""{tempRegFile}"""); // 导入
        RunPsScript("reg unload HKLM\\OfflineSystem"); // 卸载
    }

    private void RunPsScript(string script)
    {
        using (var ps = PowerShell.Create())
        {
            ps.AddScript(script);
            var results = ps.Invoke();
        }
    }

    public class GPU
    {
        public required string GPUname { get; set; }
        public required string Path { get; set; }
        public required string Iconpath { get; set; }

        public required string Id { get; set; }

        public required string Manu { get; set; }
    }
}