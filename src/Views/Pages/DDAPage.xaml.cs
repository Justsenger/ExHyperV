using System.Windows.Controls;
namespace ExHyperV.Views.Pages;
using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Windows;
using Wpf.Ui.Controls;

public partial class DDAPage
{
    public bool refreshlock = false;

    public DDAPage()
    {
        InitializeComponent();
        Task.Run(() => IsServer());
        Task.Run(() => Initialinfo()); //获取设备信息
    }
                public class DeviceInfo
                {
                    public string FriendlyName { get; set; }
                    public string Status { get; set; }
                    public string ClassType { get; set; }
                    public string InstanceId { get; set; }
                    public string Path { get; set; }
                    public string Vendor { get; set; }
                    public List<string> VmNames { get; set; }  // 存储虚拟机名称列表

                    // 构造函数
        public DeviceInfo(string friendlyName, string status, string classType, string instanceId, List<string> vmNames, string path, string vendor)
        {
            FriendlyName = friendlyName;
            Status = status;
            ClassType = classType;
            InstanceId = instanceId;
            VmNames = vmNames;
            Path = path;
            Vendor = vendor;
        }
    }
                

                private async void IsServer()
                {

                    var result = Utils.Run("(Get-WmiObject -Class Win32_OperatingSystem).ProductType");
                    Dispatcher.Invoke(() =>
                    {
                        if (result[0].ToString()=="3") { Isserver.IsOpen = false; } //服务器版本，关闭提示
                    });
                }

                private async void Initialinfo(){
                    List<DeviceInfo> deviceList = new List<DeviceInfo>();
                    await GetInfo(deviceList); //获取数据

                    Dispatcher.Invoke(() => //更新UI
                    {
                        ParentPanel.Children.Clear();
                        progressRing.Visibility = Visibility.Collapsed; //隐藏加载条
                        foreach (var device in deviceList) //更新UI
                        {
                            var cardExpander = Utils.CardExpander1();
                            cardExpander.Icon = Utils.FontIcon1(device.ClassType, device.FriendlyName);
                            Grid.SetRow(cardExpander, ParentPanel.RowDefinitions.Count);
                            ParentPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 增加新的一行

                            var headerGrid = new Grid(); // 创建 header 的 Grid 布局，包含两列，第一列占满剩余空间，第二列根据内容自适应宽度
                            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                            var drivername = Utils.TextBlock1(device.FriendlyName); //设备名
                            Grid.SetColumn(drivername, 0); // 添加到第一列
                            headerGrid.Children.Add(drivername);

                            var Menu = Utils.DropDownButton1(device.Status); //右侧按钮
                            Grid.SetColumn(Menu, 1); // 添加按钮到第二列

                            var contextMenu = new ContextMenu();
                            contextMenu.Items.Add(CreateMenuItem(ExHyperV.Properties.Resources.Host)); //单独添加一个主机选项，和后面的虚拟机列表融合
                            foreach (var vmName in device.VmNames)
                            {
                                contextMenu.Items.Add(CreateMenuItem(vmName));
                            }
                            MenuItem CreateMenuItem(string header)
                            {
                                var item = new MenuItem { Header = header };
                                item.Click += async (s, e) =>
                                {
                                    if ((String)Menu.Content != header) // 当用户选项不等于目前的选项时
                                    {
                                        TextBlock contentTextBlock = new TextBlock
                                        {
                                            Text = ExHyperV.Properties.Resources.string5,
                                            HorizontalAlignment = HorizontalAlignment.Center, // 水平居中
                                            VerticalAlignment = VerticalAlignment.Center,     // 垂直居中
                                            TextWrapping = TextWrapping.Wrap                  // 允许文本换行
                                        };

                                        ContentDialog Dialog = new()
                                        {
                                            Title = ExHyperV.Properties.Resources.setting,
                                            Content = contentTextBlock,
                                            CloseButtonText = ExHyperV.Properties.Resources.wait,
                                        };

                                        Dialog.Closing += (sender, args) => { args.Cancel = true; };// 禁止用户点击按钮触发关闭事件
                                        Dialog.DialogHost = ((MainWindow)Application.Current.MainWindow).ContentPresenterForDialogs;

                                        Dialog.ShowAsync(CancellationToken.None); //显示提示框 不能写为await

                                        await DDAps(Menu, Dialog, contentTextBlock, header, device.InstanceId, device.Path, (String)Menu.Content); //执行命令行
                                    }
                                };
                                return item;
                            }
                            Menu.Flyout = contextMenu;
                            headerGrid.Children.Add(Menu);
                            cardExpander.Header = headerGrid;

                            // 详细数据
                            var contentPanel = new StackPanel { Margin = new Thickness(50, 10, 0, 0) };

                            var grid = new Grid();
                            // 定义 Grid 的列和行
                            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
                            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });

                            var textData = new (string text, int row, int column)[]
                            {
                                (ExHyperV.Properties.Resources.kind, 0, 0),
                                (ExHyperV.Properties.Resources.Instanceid, 1, 0),
                                (ExHyperV.Properties.Resources.path, 2, 0),
                                ("制造商", 3, 0),
                                (device.ClassType, 0, 1),
                                (device.InstanceId, 1, 1),
                                (device.Path, 2, 1),
                                (device.Vendor, 3, 1),
                            };

                            foreach (var (text, row, column) in textData)
                            {
                                var textBlock = Utils.TextBlock2(text, row, column);
                                grid.Children.Add(textBlock);
                            }

                            contentPanel.Children.Add(grid);
                            cardExpander.Content = contentPanel;
                            ParentPanel.Children.Add(cardExpander);
                        }
                        refreshlock = false;
                    });
                
    }
                private async Task DDAps(DropDownButton menu,ContentDialog dialog,TextBlock contentTextBlock,string Vmname,string instanceId,string path,string Nowname)
                {
                    var (psCommands, messages) = DDACommands(Vmname,instanceId,path,Nowname); //通过四元组获取对应的命令和消息提示
                    for (int i = 0; i < messages.Length; i++)
                    {
                        Application.Current.Dispatcher.Invoke(() =>{contentTextBlock.Text = messages[i];}); //更新提示
                        Thread.Sleep(200); //引入一定的延时，显示步骤
                        //System.Windows.MessageBox.Show("执行的命令："+psCommands[i]);

                        var logOutput = await DDAps(psCommands[i]); //执行命令，并获取日志

                        //System.Windows.MessageBox.Show("已经获取到日志");
                        if (logOutput.Any(log => log.Contains("Error")))
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                contentTextBlock.Text += "\n"+ string.Join(Environment.NewLine, logOutput); // 附加一条中断信息
                                dialog.CloseButtonText = "OK"; // 更新按钮文本
                                dialog.Closing += (sender, args) => { args.Cancel = false; }; //允许用户点击关闭
                                DDArefresh(null, null);
                            });
                            return;// 退出循环

                        }
                    }
                    
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        menu.Content = Vmname;
                        contentTextBlock.Text = ExHyperV.Properties.Resources.operated;
                        dialog.CloseButtonText = ExHyperV.Properties.Resources.OK; // 更新按钮文本
                        dialog.Closing += (sender, args) => { args.Cancel = false; };// 允许用户关闭
                        DDArefresh(null, null);
                    });
                }
                private async Task<List<string>> DDAps(string psCommand)
                {
                    List<string> logOutput = new List<string>();
                    try{
                        var powerShell = PowerShell.Create(); // 创建 PowerShell 会话并执行命令
                        powerShell.AddScript(psCommand); // 添加 PowerShell 脚本
                        var result = await Task.Run(() => powerShell.Invoke());// 异步执行命令
                        foreach (var item in result)// 将输出添加到 logOutput 列表
                        { logOutput.Add(item.ToString());}// 将每个输出项添加到日志列表中
                        var errorStream = powerShell.Streams.Error.ReadAll(); // 检查标准错误输出流，看是否捕捉到错误
                        if (errorStream.Count > 0)
                        {foreach (var error in errorStream){logOutput.Add($"Error: {error.ToString()}");}}// 将错误信息添加到日志
                    }
                    catch (Exception ex){logOutput.Add($"Error: {ex.Message}");}//意料之外的错误
                    return logOutput; // 返回日志输出
                }
                private async Task GetInfo(List<DeviceInfo> deviceList)
                {

                    var pciInfoProvider = new PciInfoProvider();
                    await pciInfoProvider.EnsureInitializedAsync();

                    try
                    {
                        using (PowerShell PowerShellInstance = PowerShell.Create())
                        {
                            Dictionary<string, string> vmdevice = new Dictionary<string, string>(); //存储虚拟机挂载的PCIP设备列表
                            List<string> vmNameList = new List<string>() ;// 存储虚拟机列表
                            PowerShellInstance.AddScript("Set-ExecutionPolicy RemoteSigned -Scope Process -Force"); //设置策略
                            PowerShellInstance.AddScript("Import-Module PnpDevice");

                            //1.获取虚拟机相关信息，将已经分配了的设备存入字典以便后续读取。

                            //检查是否安装hyperv
                            var hypervstatus =Utils.Run("Get-Module -ListAvailable -Name Hyper-V");

                            if (hypervstatus.Count != 0) //已安装
                            {
                                //获取虚拟机信息
                                var vmdata = Utils.Run(@"Get-VM | Select-Object Name");
                                foreach (var vm in vmdata)
                                {
                                    var Name = vm.Members["Name"]?.Value?.ToString();

                                    if (!string.IsNullOrEmpty(Name)){vmNameList.Add(Name);}//名字不为空则添加该虚拟机

                                    var deviceData = Utils.Run($@"Get-VMAssignableDevice -VMName '{Name}' | Select-Object InstanceID");//获取虚拟机的设备列表

                                    if (deviceData != null && deviceData.Count > 0)
                                    {
                                        foreach (var device in deviceData)
                                        {
                                            var instanceId = device.Members["InstanceID"]?.Value?.ToString().Substring(4);
                                            if (!string.IsNullOrEmpty(instanceId) && !string.IsNullOrEmpty(Name))
                                            {
                                                vmdevice[instanceId] = Name; // 将 InstanceID 和 VMName 存入字典
                                            }
                                        }
                                    }
                                }
                            }
                            //如果没有安装HyperV模块，则无法获取VM信息，但不影响正常的硬件读取。

                            //获取 PCIP 设备信息
                            var PCIPData = Utils.Run("Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCIP\\*' } | Select-Object Class, InstanceId, FriendlyName, Status");
                            if (PCIPData != null && PCIPData.Count > 0)
                            {
                                foreach (var PCIP in PCIPData)
                                {
                                    var instanceId = PCIP.Members["InstanceId"]?.Value?.ToString().Substring(4); //获取PCIP后面的编号
                                    //如果满足条件：该设备未分配给虚拟机+PCIP状态等于OK，则说明并未分配给主机，处于卸除态。
                                    if (!vmdevice.ContainsKey(instanceId)&& PCIP.Members["Status"]?.Value?.ToString()=="OK"&&!string.IsNullOrEmpty(instanceId)) //状态为OK，非空
                                    {vmdevice[instanceId] = ExHyperV.Properties.Resources.removed; }
                                }
                            }

                            // 获取 PCI 设备信息。需要多查询几遍才能获取所有设备的完整信息。
                            string scripts = @"
                            function Invoke-GetPathBatch {
                                param($Ids, $Map, $Key)
                                if ($Ids.Count -eq 0) { return }
                                Get-PnpDeviceProperty -InstanceId $Ids -KeyName $Key -ErrorAction SilentlyContinue | ForEach-Object {
                                    if ($_.Data -and $_.Data.Count -gt 0) { $Map[$_.InstanceId] = $_.Data[0] }
                                }
                            }

                            $maxRetries = 3; $retryIntervalSeconds = 1; 
                            $KeyName = 'DEVPKEY_Device_LocationPaths'

                            $pciDevices = Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCI\*' }
                            if (-not $pciDevices) { exit }
                            $pciDeviceCount = $pciDevices.Count
                            if ($pciDeviceCount -gt 200) {
                                $batchSize = 100
                            } else {
                                $batchSize = $pciDeviceCount
                                if ($batchSize -lt 1) { $batchSize = 1 }
                            }
                            $allInstanceIds = $pciDevices.InstanceId; $pathMap = @{}
                            if ($allInstanceIds.Count -gt 0) { 
                                $numBatches = [Math]::Ceiling($allInstanceIds.Count / $batchSize)
                                for ($i = 0; $i -lt $numBatches; $i++) {
                                    $batch = $allInstanceIds[($i * $batchSize) .. ([Math]::Min((($i + 1) * $batchSize - 1), ($allInstanceIds.Count - 1)))]
                                    if ($batch.Count -gt 0) {
                                        Invoke-GetPathBatch -Ids $batch -Map $pathMap -Key $KeyName
                                    }
                                }
                            }
                            $idsNeedingPath = $allInstanceIds | Where-Object { -not $pathMap.ContainsKey($_) }
                            $attemptCount = 0
                            while (($idsNeedingPath.Count -gt 0) -and ($attemptCount -lt $maxRetries)) {
                                $attemptCount++
                                if ($idsNeedingPath.Count -gt 0) { 
                                    $numRetryBatches = [Math]::Ceiling($idsNeedingPath.Count / $batchSize)
                                    for ($j = 0; $j -lt $numRetryBatches; $j++) {
                                        $batch = $idsNeedingPath[($j * $batchSize) .. ([Math]::Min((($j + 1) * $batchSize - 1), ($idsNeedingPath.Count - 1)))]
                                        if ($batch.Count -gt 0) {
                                            Invoke-GetPathBatch -Ids $batch -Map $pathMap -Key $KeyName
                                        }
                                    }
                                }
                                $idsNeedingPath = $allInstanceIds | Where-Object { -not $pathMap.ContainsKey($_) }
                                if (($idsNeedingPath.Count -gt 0) -and ($attemptCount -lt $maxRetries)) { Start-Sleep -Seconds $retryIntervalSeconds }
                            }

                            $pciDevices | Select-Object Class, InstanceId, FriendlyName, Status, Service | ForEach-Object {
                                $val = $null; if ($pathMap.ContainsKey($_.InstanceId)) { $val = $pathMap[$_.InstanceId] }
                                $_ | Add-Member -NotePropertyName 'Path' -NotePropertyValue $val -Force; $_
                            }";

                var Pcidata = Utils.Run(scripts);
                var sortedResults = Pcidata
                .Where(result => result!= null)  // 过滤掉为空的元素
                .OrderBy(result => result.Members["Service"]?.Value?.ToString()[0])  // 按按类型排序，即Class 字段首字母
                .ToList();
                foreach (var result in sortedResults)
                {
                    var friendlyName = result.Members["FriendlyName"]?.Value?.ToString();
                    var status = result.Members["Status"]?.Value?.ToString();
                    var classType = result.Members["Class"]?.Value?.ToString();
                    var instanceId = result.Members["InstanceId"]?.Value?.ToString();
                    var path = result.Members["Path"]?.Value?.ToString();
                    var service = result.Members["Service"]?.Value?.ToString();
                    if (service == "pci" || service == null) {
                        continue; //排除此类设备
                    }
                    
                    // 状态为Unknown的设备，有三种情况：1.物理移除。2.已分配给虚拟机。3.卸除态。
                    // 看是否分配给虚拟机
                    if (status == "Unknown" && !string.IsNullOrEmpty(instanceId) && instanceId.Length > 3)
                    {
                        if (vmdevice.ContainsKey(instanceId.Substring(3))) // 检查去掉前三位后的PCI的InstanceId是否在存储的vm已分配设备的列表里
                        {
                            status = vmdevice[instanceId.Substring(3)];
                        } 
                        else { continue; } //不在虚拟机列表内，也不属于卸除态，说明已经物理移除。
                    }
                    else {
                        status = Properties.Resources.Host; //挂载在主机上的情况。
                    }
                    string vendor = pciInfoProvider.GetVendorFromInstanceId(instanceId);
                    deviceList.Add(new DeviceInfo(friendlyName, status, classType, instanceId,vmNameList,path,vendor)); //添加到设备列表
                }
            }
        }
        catch(Exception ex){
            System.Windows.MessageBox.Show(ex.ToString());
        }
    }
    private static (string[] commands, string[] messages) DDACommands(string Vmname, string instanceId, string path, string Nowname)
    {
        // 定义命令和消息的默认数组
        string[] commands;
        string[] messages;

        if (Nowname == Properties.Resources.removed && Vmname == Properties.Resources.Host)
        {  //将设备折返主机
            commands = new string[]
            {
                $"Mount-VMHostAssignableDevice -LocationPath '{path}'"
            };
            messages = new string[]
            {
                ExHyperV.Properties.Resources.mounting,
            };
        }
        else if(Nowname == Properties.Resources.removed && Vmname != Properties.Resources.Host)
        { //已卸除设备继续分配给虚拟机
            commands = new string[]
            {   
                $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'",
            };
            messages = new string[]
            {
                ExHyperV.Properties.Resources.mounting,
            };
        }
        // 根据条件判断返回不同的命令和消息
        else if (Nowname == Properties.Resources.Host)  // 主机切换到虚拟机
        {
            commands = new string[]
            {
            $"Set-VM -Name '{Vmname}' -AutomaticStopAction TurnOff",  // 改为强制断电
            $"Set-VM -GuestControlledCacheTypes $true -VMName '{Vmname}'",
            $"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]",
            $"Disable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false",
            $"Dismount-VMHostAssignableDevice -Force -LocationPath '{path}'",
            $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'",
            };
            messages = new string[]
            {
            Properties.Resources.string5,
            ExHyperV.Properties.Resources.cpucache,
            ExHyperV.Properties.Resources.getpath,
            ExHyperV.Properties.Resources.Disabledevice,
            ExHyperV.Properties.Resources.Dismountdevice,
            ExHyperV.Properties.Resources.mounting,
            };
        }
        else if (Vmname != Properties.Resources.Host&& Nowname != Properties.Resources.Host)  // 虚拟机切换到虚拟机
        {
            commands = new string[]
            {
            $"Set-VM -Name '{Vmname}' -AutomaticStopAction TurnOff",  // 改为强制断电
            $"Set-VM -GuestControlledCacheTypes $true -VMName '{Vmname}'",
            $"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]",
            $"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{Nowname}'",
            $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'",
            };

            messages = new string[]
            {
            Properties.Resources.string5,
            Properties.Resources.cpucache,
            Properties.Resources.getpath,
            Properties.Resources.Dismountdevice,
            Properties.Resources.mounting,
            };
        }
        else if (Vmname == Properties.Resources.Host && Nowname != Properties.Resources.Host)  // 虚拟机切换回主机
        {
            commands = new string[]
            {
                $"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]",
                $"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{Nowname}'",
                $"Mount-VMHostAssignableDevice -LocationPath '{path}'",
                $"Enable-PnpDevice -InstanceId '{instanceId}' -Confirm:$false",
            };

            messages = new string[]
            {
                Properties.Resources.getpath,
                Properties.Resources.Dismountdevice,
                Properties.Resources.mounting,
                ExHyperV.Properties.Resources.enabling,
            };
        }
        else
        {
            commands = new string[0];
            messages = new string[0];
        }
        return (commands, messages);

    }
    public void DDArefresh(object sender, RoutedEventArgs e) {
        if (!refreshlock)
        {
            refreshlock = true;
            progressRing.Visibility = Visibility.Visible; //显示加载条
            Task.Run(() => Initialinfo()); //获取设备信息
        } 
    }
}
