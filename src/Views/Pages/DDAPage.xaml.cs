using System.Windows.Controls;
namespace ExHyperV.Views.Pages;
using System;
using System.Collections.Generic;
using System.Management.Automation;
using System.Windows;
using Wpf.Ui.Controls;
using System.Windows.Media;
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
        //更新UI
        Dispatcher.Invoke(() =>
        {
            ParentPanel.Children.Clear();
            progressRing.Visibility = Visibility.Collapsed; //隐藏加载条
            foreach (var device in deviceList) //更新UI
            {
                AddCardExpander(device.FriendlyName, device.Status, device.ClassType, device.InstanceId, device.Path, device.VmNames);
            }
            refreshlock = false;
        });
    }
    public void AddCardExpander(string friendlyName, string status, string classType, string instanceId, string path, List<string> vmNames)
    {
        var rowCount = ParentPanel.RowDefinitions.Count; // 获取当前行数
        ParentPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 增加新的一行

        var cardExpander = Utils.CardExpander1();
        cardExpander.Icon = Utils.FontIcon1(classType, friendlyName);

        var headerGrid = new Grid(); // 创建 header 的 Grid 布局，包含两列，第一列占满剩余空间，第二列根据内容自适应宽度
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var drivername = Utils.TextBlock1(friendlyName); //设备名
        Grid.SetColumn(drivername, 0); // 添加到第一列
        headerGrid.Children.Add(drivername);

        var Menu = Utils.DropDownButton1(status); //右侧按钮
        Grid.SetColumn(Menu, 1); // 添加按钮到第二列

        var contextMenu = new ContextMenu();
        contextMenu.Items.Add(CreateMenuItem("主机")); //单独添加一个主机选项，和后面的虚拟机列表融合
        foreach (var vmName in vmNames)
        {
            contextMenu.Items.Add(CreateMenuItem(vmName));
        }
        MenuItem CreateMenuItem(string header)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) =>
            {
                if (Menu.Content != header) // 当用户选项不等于目前的选项时
                {  
                    Switchvm(Menu, (string)Menu.Content, header, instanceId, path);
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
        for (int i = 0; i < 3; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        }
        var textBlocks = new TextBlock[]
        {
        new TextBlock { Text = "类型", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
        new TextBlock { Text = "实例ID", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
        new TextBlock { Text = "路径", FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
        };
        var dataTextBlocks = new TextBlock[]
        {
        new TextBlock { Text = classType, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
        new TextBlock { Text = instanceId, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
        new TextBlock { Text = path, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 10) },
        };
        for (int i = 0; i < 3; i++)
        {
            Grid.SetRow(textBlocks[i], i);
            Grid.SetColumn(textBlocks[i], 0); // 第一列
            grid.Children.Add(textBlocks[i]);

            Grid.SetRow(dataTextBlocks[i], i);
            Grid.SetColumn(dataTextBlocks[i], 1); // 第二列
            grid.Children.Add(dataTextBlocks[i]);
        }
        contentPanel.Children.Add(grid);
        cardExpander.Content = contentPanel;
        Grid.SetRow(cardExpander, rowCount);
        ParentPanel.Children.Add(cardExpander);
    }

    public string GetIconPath(string deviceType, string friendlyName)
    {
        switch (deviceType)
        {
            case "Display":
                return "\xF211";  // 显卡图标 
            case "Net":
                return "\xE839";  // 网络图标
            case "USB":
                return friendlyName.Contains("USB4")
                    ? "\xE945"    // 雷电接口图标
                    : "\xECF0";   // 普通USB图标
            case "HIDClass":
                return "\xE928";  // HID设备图标
            case "SCSIAdapter":
            case "HDC":
                return "\xEDA2";  // 存储控制器图标
            default:
                return friendlyName.Contains("Audio")
                    ? "\xE995"     // 音频设备图标
                    : "\xE950";    // 默认图标
        }
    }

    private async Task Switchvm(DropDownButton menu, string Nowname, string Vmname, string instanceId, string path)
    {
        TextBlock contentTextBlock = new TextBlock
        {
            Text = "修改虚拟机的关机动作为强制断电...",
            HorizontalAlignment = HorizontalAlignment.Center, // 水平居中
            VerticalAlignment = VerticalAlignment.Center,     // 垂直居中
            TextWrapping = TextWrapping.Wrap                  // 允许文本换行
        };

        ContentDialog myDialog = new()
        {
            Title = "设定修改中",
            Content = contentTextBlock,
            CloseButtonText = "操作不可中断，请等待",
        };
        myDialog.Closing += (sender, args) => { args.Cancel = true; };// 禁止用户点击按钮触发关闭事件
        var ms = Application.Current.MainWindow as MainWindow; //设置父节点
        myDialog.DialogHost = ms.ContentPresenterForDialogs;
        var backgroundTask = Task.Run(() => PerformBackgroundOperationAsync(menu, myDialog, contentTextBlock, Vmname, instanceId, path, Nowname));
        await myDialog.ShowAsync(CancellationToken.None);
    }
    private async Task PerformBackgroundOperationAsync(DropDownButton menu,ContentDialog dialog,TextBlock contentTextBlock,string Vmname,string instanceId,string path,string Nowname)
    {
        var (psCommands, messages) = GetCommandsAndMessages(Vmname,instanceId,path,Nowname); //四元组获取对应的命令和消息提示
        for (int i = 0; i < messages.Length; i++)
        {
            Application.Current.Dispatcher.Invoke(() =>{contentTextBlock.Text = messages[i];}); //更新提示
            Thread.Sleep(200); //引入一定的延时，显示步骤
            var logOutput = await ExecutePowerShellCommand(psCommands[i]); //执行命令，并获取日志
            if (logOutput.Any(log => log.Contains("错误")))
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    contentTextBlock.Text += "\n"+ string.Join(Environment.NewLine, logOutput); // 附加一条中断信息
                    dialog.CloseButtonText = "OK"; // 更新按钮文本
                    dialog.Closing += (sender, args) => { args.Cancel = false; }; //允许用户点击关闭
                    ddarefresh(null, null);
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
            ddarefresh(null, null);
        });
    }

    private async Task<List<string>> ExecutePowerShellCommand(string psCommand)
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
                PowerShellInstance.AddScript("Get-Module -ListAvailable -Name Hyper-V");
                var hypervstatus = PowerShellInstance.Invoke();
                if (hypervstatus.Count != 0) //已安装
                {
                    PowerShellInstance.AddScript(@"
                    $vm = Get-VM | Select-Object Name, HighMemoryMappedIoSpace, LowMemoryMappedIoSpace
                    return @($vm)");//获取虚拟机信息
                    var vmdata = PowerShellInstance.Invoke();
                    foreach (var vm in vmdata)
                    {
                        var Name = vm.Members["Name"]?.Value?.ToString();
                        var Highmap = vm.Members["HighMemoryMappedIoSpace"]?.Value?.ToString();
                        var Lowmap = vm.Members["LowMemoryMappedIoSpace"]?.Value?.ToString();
                        if (!string.IsNullOrEmpty(Name)){vmNameList.Add(Name);}//名字不为空则添加该虚拟机
                        var script = $@"Get-VMAssignableDevice -VMName '{Name}' | Select-Object InstanceID";

                        PowerShellInstance.AddScript(script); //获取虚拟机下的设备列表
                        var deviceData = PowerShellInstance.Invoke();//获取设备列表
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
                else {}//如果没有安装HyperV模块，则无法获取VM信息，但不影响正常的硬件读取。

                //获取 PCIP 设备信息
                string scripts1 = @"
                $pciDevices = Get-PnpDevice | Where-Object { $_.InstanceId -like 'PCIP\*' }
                $instanceIds = $pciDevices.InstanceId
                $pciDevices | Select-Object Class, InstanceId, FriendlyName, Status |ForEach-Object {$_}";
                PowerShellInstance.AddScript(scripts1);
                var PCIPData = PowerShellInstance.Invoke();
                if (PCIPData != null && PCIPData.Count > 0)
                {
                    foreach (var PCIP in PCIPData)
                    {
                        var instanceId = PCIP.Members["InstanceId"]?.Value?.ToString().Substring(4); //获取PCIP后面的编号
                        //如果满足条件：该设备未分配给虚拟机，但PCIP状态等于ok，说明也未分配给主机，处于卸除态。
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
    public (string[] commands, string[] messages) GetCommandsAndMessages(string Vmname, string instanceId, string path, string Nowname)
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
    
    //分配设备之前，自动添加注册表信息。


    private void ddarefresh(object sender, RoutedEventArgs e) {

        if (!refreshlock)
        {
            refreshlock = true;
            progressRing.Visibility = Visibility.Visible; //显示加载条
            Task.Run(() => Initialinfo()); //获取设备信息
        }

        
    }



    //根据DDA工具显示，其获取高低MIMO空间是通过“Get-VM | Select-Object Name, HighMemoryMappedIoSpace, LowMemoryMappedIoSpace”
    //直接得到的，并没有进行额外分析。
    //根据互联网信息，对于直通方案并不需要修改，保持128-512即可。
    //但是对于分区（GPU-P/GPU-PV)方案，由于需要直接访问内存区域，需要修改高位内存！


}
