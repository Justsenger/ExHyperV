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
using ExHyperV.Views.Pages;

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
                Items.Add(new GPU { GPUname = gpu.Name, Path = gpu.Pname ,Id = gpu.InstanceId,Iconpath = Utils.GetGpuImagePath(gpu.Manu),Manu = gpu.Manu});
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

        public string GPUMount(string vmname,string gpupath,string manu) {


            PowerShell ps = PowerShell.Create();

            //1.先检测VM是否关闭，没有关机则停止操作。
            ps.AddScript($@"(Get-VM -Name '{vmname}').State");
            if (ps.Invoke()[0].ToString() != "Off") {
                return "running"; //没关机
            }
            //2.设定缓存接管和低位内存=128和高位内存=32G。（关机已通过）
            ps.AddScript($"Set-VM -GuestControlledCacheTypes $true -VMName '{vmname}'");
            ps.Invoke();
            ps.AddScript($"Set-VM -HighMemoryMappedIoSpace 32GB –VMName '{vmname}'"); //设定32G已经很大了，据说准确的设定是基于显存向上取整的最大二进制数，比如33G就要设定为64G了
            ps.Invoke();
            ps.AddScript($"Set-VM -LowMemoryMappedIoSpace 128MB -VMName '{vmname}'"); //设定为128MB，祖传别动，否则USB直通受影响
            ps.Invoke();
            ps.AddScript($"Get-VM -VMName '{vmname}' | select *"); //还得全部查找刷新一下
            ps.Invoke();

            //3.添加GPU分区，使用默认参数。这是因为消费级硬件，或者非Grid驱动的显卡，无法限制虚拟机资源使用，只能竞争。
            ps.AddScript($"Add-VMGpuPartitionAdapter -VMName '{vmname}' -InstancePath '{gpupath}'");
            ps.Invoke();
            ps.Commands.Clear();
            ps.AddScript($"Set-VMGpuPartitionAdapter -VMName '{vmname}' -MinPartitionVRAM 80000000 -MaxPartitionVRAM 100000000 -OptimalPartitionVRAM 100000000 -MinPartitionEncode 80000000 -MaxPartitionEncode 100000000 -OptimalPartitionEncode 100000000 -MinPartitionDecode 80000000 -MaxPartitionDecode 100000000 -OptimalPartitionDecode 100000000 -MinPartitionCompute 80000000 -MaxPartitionCompute 100000000 -OptimalPartitionCompute 100000000");
            ps.Invoke();


            //4.驱动复制，直接把整个"C:\Windows\System32\DriverStore\FileRepository"拿进虚拟机。

            //获取虚拟机系统盘路径，一般默认为第一块。
            ps.AddScript($"(Get-VMHardDiskDrive -vmname '{vmname}')[0].Path");
            var harddiskpath = ps.Invoke()[0].ToString();

            //挂载硬盘并寻找第一个系统分区的盘符
            ps.AddScript(@$"
            $volumes =  Mount-VHD -Path '{harddiskpath}' -PassThru | Get-Disk | Get-Partition | Get-Volume
            foreach ($volume in $volumes) {{
                if ($volume.DriveLetter -and (Test-Path ""$($volume.DriveLetter):\Windows\System32"")) {{
                    Write-Output $volume.DriveLetter
                    break 
                }}
            }}
            ");

            var letter = ps.Invoke()[0].ToString(); //仅仅是一个字母

            string sourceFolder = @"C:\Windows\System32\DriverStore\FileRepository";
            string destinationFolder = letter + @":\Windows\System32\HostDriverStore\FileRepository";

            // 创建目标文件夹（如果不存在）
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
            process.BeginOutputReadLine();
            process.WaitForExit();
             
            SetFolderReadOnly(destinationFolder); // 设置目标文件夹及其所有文件为只读属性，防止nvlddmkm文件丢失

            //对于N卡，需要修补注册表信息：nvlddmkm
            if (manu.Contains("NVIDIA")) { 
                NvidiaReg(letter + ":"); 
            }

            ps.AddScript($"Dismount-VHD -Path '{harddiskpath}'");//卸载磁盘
            ps.Invoke();

            return "OK";


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
            RunPsScript($@"reg export ""{localKeyPath}"" ""{tempRegFile}"" /y"); //导出本机注册表信息
            string systemHiveFile = $@"{letter}\Windows\System32\Config\SYSTEM";
            RunPsScript($@"reg load HKLM\OfflineSystem ""{systemHiveFile}"""); //离线注册表挂载到本机注册表
            string originalText = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\nvlddmkm";
            string targetText = @"HKEY_LOCAL_MACHINE\OfflineSystem\ControlSet001\Services\nvlddmkm";
            string regContent = File.ReadAllText(tempRegFile);
            regContent = regContent.Replace(originalText, targetText);
            regContent = regContent.Replace("DriverStore", "HostDriverStore");
            File.WriteAllText(tempRegFile, regContent); 
            RunPsScript($@"reg import ""{tempRegFile}"""); // 导入
            RunPsScript("reg unload HKLM\\OfflineSystem"); // 卸载

            //注册表应该是要修复，否则nvlddmkm.sys会莫名丢失。

        }

        private void RunPsScript(string script)
        {
            using (PowerShell ps = PowerShell.Create())
            {
                ps.AddScript(script);
                var results = ps.Invoke();

            }
        }
    }
}
