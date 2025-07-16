using Wpf.Ui.Controls;
using System.Windows;
using System.Collections.ObjectModel;
using static ExHyperV.Views.Pages.GPUPage;
using System.Windows.Controls;
using System.Xml.Linq;
using System.Management.Automation;
using System.Diagnostics;
using System;
using System.IO;

using Wpf.Ui;

namespace ExHyperV
{
    public partial class ChooseGPUWindow : FluentWindow
    {
        public event EventHandler<(string, string)> GPUSelected;

        public string Machinename = null;
        public string GPUManu = null;
        public ObservableCollection<GPU> Items { get; set; }
        public ChooseGPUWindow(string vmname, List<GPUInfo> Hostgpulist)
        {
            Machinename = vmname;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            InitializeComponent();
            Items = new ObservableCollection<GPU>();

            foreach (var gpu in Hostgpulist)
            {
               
                if (gpu.Pname == null) { continue; }
                Items.Add(new GPU { GPUname = gpu.Name, Path = gpu.Pname ,Id = gpu.InstanceId,Iconpath = Utils.GetGpuImagePath(gpu.Manu,gpu.Name),Manu = gpu.Manu});
            }
            this.DataContext = this;
        }
        public class GPU
        {
            public required string GPUname { get; set; }
            public required string Path { get; set; }
            public required string Iconpath { get; set; }

            public required string Id { get; set; }

            public required string Manu { get; set; }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            ConfirmButton.IsEnabled = false;
            var selectedGpu = GpuListView.SelectedItem as GPU;
            if (selectedGpu != null)
            {
                progress.IsIndeterminate = true; //等待条

                var result = await Task.Run(() => GPUMount(Machinename, selectedGpu.Path,selectedGpu.Manu));
                //接下来，将执行真正的GPU分配。
                if (result == "running")
                {
                    ContentDialog Dialog = new()
                    {
                        Title = ExHyperV.Properties.Resources.Settings,
                        Content = Utils.TextBlock3(ExHyperV.Properties.Resources.Colsefirst),
                        CloseButtonText = ExHyperV.Properties.Resources.OK,
                    };
                    Dialog.DialogHost = ContentPresenterForDialogs;

                    await Dialog.ShowAsync(CancellationToken.None); //显示提示框

                }
                else if (result != "OK")
                {
                    ContentDialog Dialog = new()
                    {
                        Title = ExHyperV.Properties.Resources.Settings,
                        Content = Utils.TextBlock3(result),
                        CloseButtonText = ExHyperV.Properties.Resources.OK,
                    };
                    Dialog.DialogHost = ContentPresenterForDialogs;

                    await Dialog.ShowAsync(CancellationToken.None); //显示提示框
                }
                else if (result == "OK") {
                    progress.IsIndeterminate = false; //等待条结束
                    GPUSelected?.Invoke(this, (selectedGpu.GPUname, Machinename));  // 触发事件
                }

            }
            this.Close();
        }


        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var listView = sender as Wpf.Ui.Controls.ListView;

            // 如果有选中的项，启用 "确定" 按钮
            if (listView.SelectedItem != null)
            {
                ConfirmButton.IsEnabled = true;
            }
            else
            {
                ConfirmButton.IsEnabled = false;
            }
        }

        public string GPUMount(string vmname, string gpupath, string manu)
        {
            string harddiskpath = null;

            try
            {
                var vmStateResult = Utils.Run($"(Get-VM -Name '{vmname}').State");
                if (vmStateResult == null || vmStateResult.Count == 0) return "error";
                if (vmStateResult[0].ToString() != "Off")
                {
                    return $"错误: 虚拟机 '{vmname}' 正在运行中，请先将其关闭。";
                }

                string vmConfigScript = $@"
            Set-VM -GuestControlledCacheTypes $true -VMName '{vmname}'
            Set-VM -HighMemoryMappedIoSpace 64GB –VMName '{vmname}'
            Set-VM -LowMemoryMappedIoSpace 128MB -VMName '{vmname}'
            Add-VMGpuPartitionAdapter -VMName '{vmname}' -InstancePath '{gpupath}'
            Set-VMGpuPartitionAdapter -VMName '{vmname}' -MinPartitionVRAM 80000000 -MaxPartitionVRAM 100000000 -OptimalPartitionVRAM 100000000 -MinPartitionEncode 80000000 -MaxPartitionEncode 100000000 -OptimalPartitionEncode 100000000 -MinPartitionDecode 80000000 -MaxPartitionDecode 100000000 -OptimalPartitionDecode 100000000 -MinPartitionCompute 80000000 -MaxPartitionCompute 100000000 -OptimalPartitionCompute 100000000
        ";
                if (Utils.Run(vmConfigScript) == null)
                {
                    return "错误：GPU分区参数设定失败。";
                }

                var harddiskPathResult = Utils.Run($"(Get-VMHardDiskDrive -vmname '{vmname}')[0].Path");
                if (harddiskPathResult == null || harddiskPathResult.Count == 0)
                {
                    return $"错误: 无法获取虚拟机 '{vmname}' 的硬盘路径。";
                }
                harddiskpath = harddiskPathResult[0].ToString();

                // 挂载VHD并获取盘符
                var mountScript = @$"
            $regPath = ""HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer""; $regKey = ""NoDriveTypeAutoRun"";
            $originalValue = Get-ItemProperty -Path $regPath -Name $regKey -ErrorAction SilentlyContinue;
            try {{
                if (-not (Test-Path $regPath)) {{ New-Item -Path $regPath -Force | Out-Null }};
                Set-ItemProperty -Path $regPath -Name $regKey -Value 255 -Type DWord -Force;
                $VHD = Mount-VHD -Path '{harddiskpath}' -PassThru -ErrorAction Stop;
                Start-Sleep -Seconds 1;
                $VHD | Get-Disk | Get-Partition | Where-Object {{ -not $_.DriveLetter }} | Add-PartitionAccessPath -AssignDriveLetter | Out-Null;
                $volumes = $VHD | Get-Disk | Get-Partition | Get-Volume;
                foreach ($volume in $volumes) {{
                    if ($volume.DriveLetter -and (Test-Path ""$($volume.DriveLetter):\Windows\System32\config\SYSTEM"")) {{
                        Write-Output $volume.DriveLetter;
                        break;
                    }}
                }}
            }} finally {{
                if ($originalValue) {{ Set-ItemProperty -Path $regPath -Name $regKey -Value $originalValue.$regKey -Force; }}
                else {{ Remove-ItemProperty -Path $regPath -Name $regKey -Force -ErrorAction SilentlyContinue; }}
            }}";
                var letterResult = Utils.Run(mountScript);
                if (letterResult == null || letterResult.Count == 0)
                {
                    return $"错误: 挂载硬盘 '{harddiskpath}' 或查找其中的系统分区失败。";
                }
                string letter = letterResult[0].ToString();

                // 设置只读
                string sourceFolder = @"C:\Windows\System32\DriverStore\FileRepository";
                string destinationFolder = letter + @":\Windows\System32\HostDriverStore\FileRepository";

                if (!Directory.Exists(destinationFolder)) { Directory.CreateDirectory(destinationFolder); }

                var process = new Process
                {
                    StartInfo = {
                FileName = "robocopy",
                Arguments = $"\"{sourceFolder}\" \"{destinationFolder}\" /MIR /NP /NJH /NFL /NDL",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
                };
                process.Start();
                process.WaitForExit();

                SetFolderReadOnly(destinationFolder);

                if (manu.Contains("NVIDIA"))
                {
                    NvidiaReg(letter + ":");
                }

                return "OK";
            }
            catch (Exception ex)
            {
                return $"错误: 发生意外的系统异常 - {ex.Message}";
            }
            finally
            {
                if (!string.IsNullOrEmpty(harddiskpath))
                {
                    Utils.Run($"Dismount-VHD -Path '{harddiskpath}' -ErrorAction SilentlyContinue");
                }
            }
        }
        static void SetFolderReadOnly(string folderPath)
        {

            // 设置文件夹属性为只读
            DirectoryInfo directoryInfo = new DirectoryInfo(folderPath);
            directoryInfo.Attributes |= FileAttributes.ReadOnly;

            // 设置所有子文件夹的属性为只读
            foreach (string subFolder in Directory.GetDirectories(folderPath))
            {
                SetFolderReadOnly(subFolder);
            }

            // 设置所有文件的属性为只读
            foreach (string file in Directory.GetFiles(folderPath))
            {
                File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
            }
        }
        public void NvidiaReg(string letter)
        {
            string localKeyPath = @"HKLM\SYSTEM\CurrentControlSet\Services\nvlddmkm";
            string tempRegFile = AppDomain.CurrentDomain.BaseDirectory+@"nvlddmkm.reg";
            Utils.Run($@"reg export ""{localKeyPath}"" ""{tempRegFile}"" /y"); //导出本机注册表信息
            string systemHiveFile = $@"{letter}\Windows\System32\Config\SYSTEM";
            Utils.Run($@"reg load HKLM\OfflineSystem ""{systemHiveFile}"""); //离线注册表挂载到本机注册表
            string originalText = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm";
            string targetText = @"HKEY_LOCAL_MACHINE\OfflineSystem\ControlSet001\Services\nvlddmkm";
            string regContent = File.ReadAllText(tempRegFile);
            regContent = regContent.Replace(originalText, targetText);
            regContent = regContent.Replace("DriverStore", "HostDriverStore");
            File.WriteAllText(tempRegFile, regContent); 
            Utils.Run($@"reg import ""{tempRegFile}"""); // 导入
            Utils.Run("reg unload HKLM\\OfflineSystem"); // 卸载

            //注册表应该是要修复，否则nvlddmkm.sys会莫名丢失。

        }
    }
}
