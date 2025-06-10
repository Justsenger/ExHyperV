using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ExHyperV.Models;
using Wpf.Ui.Controls;
using ListView = Wpf.Ui.Controls.ListView;

namespace ExHyperV.Views.Pages;

public partial class ChooseGpuWindow
{
    // Fields
    private readonly string _machineName;

    // Constructors
    public ChooseGpuWindow(string vmname, List<GpuInfo> hostGpuList)
    {
        _machineName = vmname;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        InitializeComponent();

        var gpuItems = hostGpuList
            .Where(x => !string.IsNullOrEmpty(x.Pname))
            .Select(x => new Gpu(
                x.Name,
                x.Pname,
                Utils.GpuImagePath(x.Manu, x.Name),
                x.InstanceId,
                x.Manu))
            .ToList();

        GpuListView.ItemsSource = gpuItems;
    }

    // Events
    public event EventHandler<GpuSelectedEventArgs>? GpuSelected;

    private static string GpuMount(string vmname, string gpupath, string manu)
    {
        try
        {
            //1.先检测VM是否关闭，没有关机则停止操作。
            var vmStateResult = Utils.Run($"(Get-VM -Name '{vmname}').State");
            if (vmStateResult.Count == 0 || vmStateResult[0].ToString() != "Off") return "running"; //没关机

            // 2-3. Set VM configuration and add GPU partition - Use single PowerShell session to improve performance
            var vmConfigCommands = new[]
            {
                $"Set-VM -GuestControlledCacheTypes $true -VMName '{vmname}'",
                $"Set-VM -HighMemoryMappedIoSpace 32GB –VMName '{vmname}'", // Setting 32GB is already quite large, it's said that the accurate setting is based on the maximum binary number rounded up from VRAM, for example 33GB should be set to 64GB
                $"Set-VM -LowMemoryMappedIoSpace 128MB -VMName '{vmname}'", // Set to 128MB, traditional setting don't change, otherwise USB passthrough will be affected
                $"Get-VM -VMName '{vmname}' | select *", // Need to search and refresh everything
                $"Add-VMGpuPartitionAdapter -VMName '{vmname}' -InstancePath '{gpupath}'",
                $"Set-VMGpuPartitionAdapter -VMName '{vmname}' -MinPartitionVRAM 80000000 -MaxPartitionVRAM 100000000 -OptimalPartitionVRAM 100000000 -MinPartitionEncode 80000000 -MaxPartitionEncode 100000000 -OptimalPartitionEncode 100000000 -MinPartitionDecode 80000000 -MaxPartitionDecode 100000000 -OptimalPartitionDecode 100000000 -MinPartitionCompute 80000000 -MaxPartitionCompute 100000000 -OptimalPartitionCompute 100000000"
            };

            // 执行所有VM配置命令在一个PowerShell会话中
            Utils.RunMultipleVoid(vmConfigCommands);

            //4.驱动复制，直接把整个"C:\Windows\System32\DriverStore\FileRepository"拿进虚拟机。

            //todo分支，如果未找到路径，不报错，而是转为linux注入模式，要求输入用户名和密码等待ssh链接。

            //获取虚拟机系统盘路径，一般默认为第一块。
            var harddiskpathResult = Utils.Run($"(Get-VMHardDiskDrive -vmname '{vmname}')[0].Path");
            if (harddiskpathResult.Count == 0) return "error";
            var harddiskpath = harddiskpathResult[0].ToString();

            //挂载硬盘并寻找第一个系统分区的盘符
            var letterResult = Utils.Run($$"""
                                           $VHD = Mount-VHD -Path '{{harddiskpath}}' -PassThru
                                           $VHD | Get-Disk | Get-Partition | Where-Object { -not $_.DriveLetter } | Add-PartitionAccessPath -AssignDriveLetter
                                           $volumes = $VHD | Get-Disk | Get-Partition | Get-Volume

                                           foreach ($volume in $volumes) {
                                               if ($volume.DriveLetter -and (Test-Path "$($volume.DriveLetter):\Windows\System32")) {
                                                   Write-Output $volume.DriveLetter
                                                   break
                                               }
                                           }
                                           """);

            if (letterResult.Count == 0) return "error";
            var letter = letterResult[0].ToString(); //仅仅是一个字母

            const string sourceFolder = @"C:\Windows\System32\DriverStore\FileRepository";
            var destinationFolder = letter + @":\Windows\System32\HostDriverStore\FileRepository";

            // 创建目标文件夹（如果不存在）
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

            //对于N卡，需要修补注册表信息：nvlddmkm
            if (manu.Contains("NVIDIA")) NvidiaReg(letter + ":");

            Utils.Run($"Dismount-VHD -Path '{harddiskpath}'"); //卸载磁盘

            return "OK";
        }
        catch
        {
            return "error";
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ConfirmButton.IsEnabled = false;
            if (GpuListView.SelectedItem is Gpu selectedGpu)
            {
                Progress.IsIndeterminate = true; //等待条

                var result = await Task.Run(() => GpuMount(_machineName, selectedGpu.Path, selectedGpu.Manu));
                // Next, the actual GPU allocation will be executed.
                switch (result)
                {
                    case "running":
                    {
                        var dialog = new ContentDialog
                        {
                            Title = Properties.Resources.Settings,
                            Content = Utils.CreateCenteredTextBlock(Properties.Resources.Colsefirst),
                            CloseButtonText = Properties.Resources.OK,
                            DialogHost = ContentPresenterForDialogs
                        };

                        await dialog.ShowAsync(CancellationToken.None); // Show dialog box
                        break;
                    }
                    case "error":
                    {
                        var dialog = new ContentDialog
                        {
                            Title = Properties.Resources.Settings,
                            Content = Utils.CreateCenteredTextBlock(Properties.Resources.drivererror),
                            CloseButtonText = Properties.Resources.OK,
                            DialogHost = ContentPresenterForDialogs
                        };

                        await dialog.ShowAsync(CancellationToken.None); // Show dialog box
                        break;
                    }
                    case "OK":
                        Progress.IsIndeterminate = false; //等待条结束
                        GpuSelected?.Invoke(this, new GpuSelectedEventArgs(selectedGpu.Name, _machineName)); // 触发事件
                        break;
                }
            }

            Close();
        }
        catch (Exception ex)
        {
            // Safe handling of all exceptions in async void method
            try
            {
                Progress.IsIndeterminate = false; // Stop progress indicator
                ConfirmButton.IsEnabled = true; // Restore button

                var errorDialog = new ContentDialog
                {
                    Title = Properties.Resources.Settings,
                    Content = Utils.CreateCenteredTextBlock($"Unexpected error: {ex.Message}"),
                    CloseButtonText = Properties.Resources.OK,
                    DialogHost = ContentPresenterForDialogs
                };

                await errorDialog.ShowAsync(CancellationToken.None);
            }
            catch
            {
                // If even showing error dialog failed, just close window
                // to avoid application crash
                Close();
            }
        }
    }

    private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView listView)
            // If there are selected items, enable the "Confirm" button
            ConfirmButton.IsEnabled = listView.SelectedItem is not null;
    }

    private static void SetFolderReadOnly(string folderPath)
    {
        // Set folder attributes to read-only
        var directoryInfo = new DirectoryInfo(folderPath);
        directoryInfo.Attributes |= FileAttributes.ReadOnly;

        // Set all subfolders' attributes to read-only
        foreach (var subFolder in Directory.GetDirectories(folderPath)) SetFolderReadOnly(subFolder);

        // Set all files' attributes to read-only
        foreach (var file in Directory.GetFiles(folderPath))
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
    }

    private static void NvidiaReg(string letter)
    {
        const string localKeyPath = @"HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm";
        var tempRegFile = AppDomain.CurrentDomain.BaseDirectory + "nvlddmkm.reg";
        var systemHiveFile = $@"{letter}\Windows\System32\Config\SYSTEM";

        // 优化：使用单个PowerShell会话执行注册表操作
        var registryCommands = new[]
        {
            $"reg export \"{localKeyPath}\" \"{tempRegFile}\" /y", //导出本机注册表信息
            $"reg load HKLM\\OfflineSystem \"{systemHiveFile}\"" //离线注册表挂载到本机注册表
        };

        Utils.RunMultipleVoid(registryCommands);

        // 修改注册表文件内容
        const string originalText = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm";
        const string targetText = @"HKEY_LOCAL_MACHINE\OfflineSystem\ControlSet001\Services\nvlddmkm";
        var regContent = File.ReadAllText(tempRegFile);
        regContent = regContent.Replace(originalText, targetText);
        regContent = regContent.Replace("DriverStore", "HostDriverStore");
        File.WriteAllText(tempRegFile, regContent);

        // 导入和卸载操作
        var finalCommands = new[]
        {
            $"reg import \"{tempRegFile}\"", // 导入
            "reg unload HKLM\\OfflineSystem" // 卸载
        };

        Utils.RunMultipleVoid(finalCommands);

        //注册表应该是要修复，否则nvlddmkm.sys会莫名丢失。
    }
}