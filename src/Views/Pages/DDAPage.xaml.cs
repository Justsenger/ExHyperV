using System.Windows.Controls;
namespace ExHyperV.Views.Pages;
using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Windows;
using Wpf.Ui.Controls;
using System.Windows.Media;
using System.Security.AccessControl;
using System.Xml.Linq;

public partial class DDAPage
{
    public bool refreshlock = false;

    public DDAPage()
    {
        InitializeComponent();
        Task.Run(() => Initialinfo()); //获取设备信息
    }
    public class DeviceInfo
    {
        public string FriendlyName { get; set; }
        public string Status { get; set; }
        public string ClassType { get; set; }
        public string InstanceId { get; set; }
        public string Path { get; set; }
        public List<string> VmNames { get; set; }  // 存储虚拟机名称列表

        // 构造函数
        public DeviceInfo(string friendlyName, string status, string classType, string instanceId,List<string> vmNames,string path)
        {
            FriendlyName = friendlyName;
            Status = status;
            ClassType = classType;
            InstanceId = instanceId;
            VmNames = vmNames; 
            Path = path;
        }
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
                contextMenu.Items.Add(CreateMenuItem("主机")); //单独添加一个主机选项，和后面的虚拟机列表融合
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
                                Text = "修改虚拟机的关机动作为强制断电...",
                                HorizontalAlignment = HorizontalAlignment.Center, // 水平居中
                                VerticalAlignment = VerticalAlignment.Center,     // 垂直居中
                                TextWrapping = TextWrapping.Wrap                  // 允许文本换行
                            };

                            ContentDialog Dialog = new()
                            {
                                Title = "设定修改中",
                                Content = contentTextBlock,
                                CloseButtonText = "操作不可中断，请等待",
                            };

                            Dialog.Closing += (sender, args) => { args.Cancel = true; };// 禁止用户点击按钮触发关闭事件
                            Dialog.DialogHost = ((MainWindow)Application.Current.MainWindow).ContentPresenterForDialogs;

                            await Dialog.ShowAsync(CancellationToken.None); //显示提示框

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

                var textData = new (string text, int row, int column)[]
                {
                    ("类型", 0, 0),
                    ("实例ID", 1, 0),
                    ("路径", 2, 0),
                    (device.ClassType, 0, 1),
                    (device.InstanceId, 1, 1),
                    (device.Path, 2, 1),
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
            var logOutput = await DDAps(psCommands[i]); //执行命令，并获取日志
            if (logOutput.Any(log => log.Contains("错误")))
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
            contentTextBlock.Text = "操作完成！";
            dialog.CloseButtonText = "OK"; // 更新按钮文本
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
            {foreach (var error in errorStream){logOutput.Add($"错误: {error.ToString()}");}}// 将错误信息添加到日志
        }
        catch (Exception ex){logOutput.Add($"错误: {ex.Message}");}//意料之外的错误
        return logOutput; // 返回日志输出
    }

    private async Task GetInfo(List<DeviceInfo> deviceList)
    {
        try
        {
            using (PowerShell PowerShellInstance = PowerShell.Create())
            {
                Dictionary<string, string> vmdevice = new Dictionary<string, string>(); //存储虚拟机挂载的PCIP设备
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
                        {vmdevice[instanceId] = "#已卸除"; }
                    }
                }

                // 获取 PCI 设备信息。连续查询三遍才能收到所有设备的完整信息。特性！（发现问题，有时三遍都查询不完）
                string scripts = @"
                $pciDevices = Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCI\*' }
                $instanceIds = $pciDevices.InstanceId
                $pathMap = @{}
                Get-PnpDeviceProperty -InstanceId $instanceIds -KeyName DEVPKEY_Device_LocationPaths |
                    ForEach-Object { 
                        $pathMap[$_.InstanceId] = $_.Data[0] 
                    }
                Get-PnpDeviceProperty -InstanceId $instanceIds -KeyName DEVPKEY_Device_LocationPaths |
                    ForEach-Object { 
                        $pathMap[$_.InstanceId] = $_.Data[0] 
                    }
                Get-PnpDeviceProperty -InstanceId $instanceIds -KeyName DEVPKEY_Device_LocationPaths |
                    ForEach-Object { 
                        $pathMap[$_.InstanceId] = $_.Data[0] 
                    }
                $pciDevices | Select-Object Class, InstanceId, FriendlyName, Status ,Service |
                    ForEach-Object {
                        $_ | Add-Member -NotePropertyName 'Path' -NotePropertyValue $pathMap[$_.InstanceId]
                        $_
                    }";
                PowerShellInstance.AddScript(scripts);

                var Pcidata = PowerShellInstance.Invoke();
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
                    

                    // 针对unknown设备进行检查，有三种情况：1.已物理移除。2.分配给了虚拟机。3.卸除态。

                    // 看是否分配给虚拟机还是已移除
                    if (status == "Unknown" && !string.IsNullOrEmpty(instanceId) && instanceId.Length > 3)
                    {
                        if (vmdevice.ContainsKey(instanceId.Substring(3))) // 检查去掉前三位后的PCI的InstanceId是否在存储的vm已分配设备的列表里
                        {
                            status = vmdevice[instanceId.Substring(3)];
                        } 
                        else { continue; } //不在虚拟机列表内，也不属于卸除态，排除。
                    }
                    else {
                        status = "主机"; //正常设备的情况。
                    }
                    deviceList.Add(new DeviceInfo(friendlyName, status, classType, instanceId,vmNameList,path)); //添加到设备列表
                }
            }
        }
        catch(Exception ex){
            System.Windows.MessageBox.Show(ex.ToString());
        }
    }

    // 根据场景返回命令和消息的数组
    private static (string[] commands, string[] messages) DDACommands(string Vmname, string instanceId, string path, string Nowname)
    {
        // 定义命令和消息的默认数组
        string[] commands;
        string[] messages;

        if (Nowname == "#已卸除" && Vmname == "主机")
        {  //将设备折返主机
            commands = new string[]
            {
                $"Mount-VMHostAssignableDevice -LocationPath '{path}'"
            };
            messages = new string[]
            {
            "挂载设备中...",
            };
        }
        else if(Nowname == "#已卸除" && Vmname != "主机")
        { //已卸除设备继续分配给虚拟机
            commands = new string[]
            {   
                $"Add-VMAssignableDevice -LocationPath '{path}' -VMName '{Vmname}'",
            };
            messages = new string[]
            {
            "挂载设备中...",
            };
        }
        // 根据条件判断返回不同的命令和消息
        else if (Nowname == "主机")  // 主机切换到虚拟机
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
            "修改虚拟机的关机动作为强制断电...",
            "让虚拟机可管理CPU缓存...",
            "获取设备的路径...",
            "禁用设备中...",
            "卸载设备中...",
            "挂载设备中...",
            };
        }
        else if (Vmname != "主机"&& Nowname != "主机")  // 虚拟机切换到虚拟机
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
            "修改虚拟机的关机动作为强制断电...",
            "让虚拟机可管理CPU缓存...",
            "获取设备的路径...",
            "卸载设备中...",
            "挂载设备中...",
            };
        }
        else if (Vmname == "主机" && Nowname != "主机")  // 虚拟机切换回主机
        {
            commands = new string[]
            {
                $"(Get-PnpDeviceProperty -InstanceId '{instanceId}' DEVPKEY_Device_LocationPaths).Data[0]",
                $"Remove-VMAssignableDevice -LocationPath '{path}' -VMName '{Nowname}'",
                $"Mount-VMHostAssignableDevice -LocationPath '{path}'",
            };

            messages = new string[]
            {
                "获取设备的路径...",
                "卸载设备中...",
                "挂载设备中...",
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
