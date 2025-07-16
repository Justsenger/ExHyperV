using System.Windows.Controls;
namespace ExHyperV.Views.Pages;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection.Metadata;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using ExHyperV;
using Wpf.Ui.Controls;



public partial class VMNetPage
{
    public bool refreshlock = false;
    List<SwitchInfo> _switchList = []; //存储交换机数据
    public VMNetPage()
    {
        InitializeComponent();
        Task.Run(() => Initialinfo()); //获取宿主虚拟交换机信息
    }
    //虚拟交换机的数据结构
    public class SwitchInfo(string switchName, string switchType, string host, string id, string phydesc)
    {
        public string SwitchName { get; set; } = switchName;
        public string SwitchType { get; set; } = switchType;
        public string AllowManagementOS { get; set; } = host;
        public string Id { get; set; } = id;
        public string NetAdapterInterfaceDescription { get; set; } = phydesc;
    }

    //网络适配器的数据结构
    public class AdapterInfo
    {
        public string VMName { get; set; }
        public string MacAddress { get; set; }
        public string Status { get; set; }
        public string IPAddresses { get; set; }

        // 构造函数
        public AdapterInfo(string vMName, string macAddress, string status, string ipAddresses)
        {
            VMName = vMName;
            MacAddress = macAddress;
            Status = status;
            IPAddresses = ipAddresses;
        }
    }

    // 用于存储物理网卡信息的类
    public class PhysicalAdapterInfo
    {
        public string InterfaceDescription { get; private set; }

        public PhysicalAdapterInfo(string desc)
        {
            this.InterfaceDescription = desc;
        }
    }

    private async void refresh(object sender, RoutedEventArgs e)
    {
        await Initialinfo();
    }

    private async Task Initialinfo(){
        _switchList.Clear();
        List<PhysicalAdapterInfo> physicalAdapterList = []; //存储物理网卡数据
        await GetInfo(_switchList, physicalAdapterList); 
        Dispatcher.Invoke(() => 
        {
            ParentPanel.Children.Clear(); 
        });

        foreach (var Switch1 in _switchList)
        {
            Dispatcher.Invoke(() => //更新UI
            {
                bool _isInitializing = true;
                bool _isUpdatingUiFromCode = false;

                // =========================================================================
                // Header 部分
                // =========================================================================
                var cardExpander = Utils.CardExpander1();
                cardExpander.Icon = Utils.FontIcon1("Switch", Switch1.SwitchName);
                Grid.SetRow(cardExpander, ParentPanel.RowDefinitions.Count);
                ParentPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var headerGrid = new Grid();
                headerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                headerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var drivername = Utils.TextBlock1(Switch1.SwitchName);
                var statusPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var statusCircle = new Ellipse { Width = 8, Height = 8, Margin = new Thickness(0, 0, 6, 0) };

                // Header文本和状态的更新将在UpdateUIState中进行，这里只做基础初始化
                var statusTextBlock = Utils.TextBlock12("正在确定状态...");
                statusTextBlock.Margin = new Thickness(0);
                statusPanel.Children.Add(statusCircle);
                statusPanel.Children.Add(statusTextBlock);
                Grid.SetRow(drivername, 0);
                Grid.SetRow(statusPanel, 1);
                headerGrid.Children.Add(drivername);
                headerGrid.Children.Add(statusPanel);
                cardExpander.Header = headerGrid;

                // =========================================================================
                // 阶段1 - 控件声明
                // =========================================================================
                var buttonPadding = new Thickness(8, 6, 8, 4);
                var rbBridge = new RadioButton { Content = "桥接模式", GroupName = Switch1.Id, Padding = buttonPadding };
                var rbNat = new RadioButton { Content = "NAT模式", GroupName = Switch1.Id, Padding = buttonPadding };
                var rbNone = new RadioButton { Content = "无上游", GroupName = Switch1.Id, Padding = buttonPadding };
                var upstreamDropDown = Utils.DropDownButton1("请选择网卡...");
                var hostConnectionSwitch = new ToggleSwitch { IsChecked = (Switch1.AllowManagementOS?.ToLower() == "true"), HorizontalAlignment = HorizontalAlignment.Left };
                var dhcpSwitch = new ToggleSwitch { IsChecked = false, HorizontalAlignment = HorizontalAlignment.Left };
                var topologyCanvas = new Canvas();

                // =========================================================================
                // 阶段2 - 局部函数定义
                // =========================================================================
                async Task UpdateUIState()//更新界面UI
                {
                    if (_isUpdatingUiFromCode) return;
                    _isUpdatingUiFromCode = true;

                    var isBridgeMode = rbBridge.IsChecked == true;
                    var isNatMode = rbNat.IsChecked == true;

                    bool hostConnectionEnabled;
                    bool dhcpEnabled;
                    string currentMode;
                    string headerStatusText;
                    bool headerIsConnected;

                    if (isBridgeMode)
                    {
                        currentMode = "Bridge";
                        hostConnectionEnabled = false;
                        hostConnectionSwitch.IsChecked = true;
                        dhcpEnabled = false;

                        headerIsConnected = !string.IsNullOrEmpty(Switch1.NetAdapterInterfaceDescription);
                        headerStatusText = headerIsConnected ? "已连接到：" + Switch1.NetAdapterInterfaceDescription : "未连接上游网络";
                    }
                    else if (isNatMode)
                    {
                        currentMode = "NAT";
                        hostConnectionEnabled = false;
                        hostConnectionSwitch.IsChecked = true;
                        dhcpEnabled = false;
                        headerIsConnected = true;
                        headerStatusText = headerIsConnected ? "已连接到：" + Switch1.NetAdapterInterfaceDescription : "未连接上游网络"; ;
                    }
                    else // 无上游模式
                    {
                        currentMode = "None";
                        hostConnectionEnabled = true;
                        dhcpEnabled = false;

                        headerIsConnected = false;
                        headerStatusText = "未连接上游网络";
                    }

                    hostConnectionSwitch.IsEnabled = hostConnectionEnabled;
                    dhcpSwitch.IsEnabled = dhcpEnabled;
                    if (!dhcpSwitch.IsEnabled) { dhcpSwitch.IsChecked = false; }

                    switch (currentMode)
                    {
                        case "Bridge" or "NAT":
                            upstreamDropDown.IsEnabled = true;
                            upstreamDropDown.Content = !string.IsNullOrEmpty(Switch1.NetAdapterInterfaceDescription)
                                ? Switch1.NetAdapterInterfaceDescription
                                : "请选择网卡...";
                            break;
                        case "None":
                        default:
                            upstreamDropDown.IsEnabled = false;
                            upstreamDropDown.Content = "不可用";
                            break;
                    }

                    statusTextBlock.Text = headerStatusText;
                    statusCircle.Fill = new SolidColorBrush(headerIsConnected ? Colors.Green : Colors.Red);

                    var content = upstreamDropDown.Content as string;
                    if (!string.IsNullOrEmpty(content) && !content.Contains("请选择")) { //不为空，且不为待选择状态
                        await BuildVerticalTopology(); //绘制拓扑图
                    }

                    _isUpdatingUiFromCode = false;

                }

                async Task BuildVerticalTopology()
                {

                    try
                    {
                        List<AdapterInfo> adapters = await GetFullSwitchNetworkStateAsync(Switch1.SwitchName);
                        topologyCanvas.Children.Clear();
                        double iconSize = 28;
                        double radius = iconSize / 2;
                        double verticalSpacing = 70;
                        double horizontalVmSpacing = 140;
                        double lineThickness = 1.5;
                        double upstreamY = 20;
                        double switchY = upstreamY + verticalSpacing;
                        double vmBusY = switchY + verticalSpacing;
                        double vmY = vmBusY + 35;

                        void CreateNode(string deviceType, string name, string ipAddress, string macAddress, double x, double y, bool allowWrapping = false)
                        {
                            var nodeIcon = Utils.FontIcon1(deviceType, "");
                            nodeIcon.FontSize = iconSize;
                            Canvas.SetLeft(nodeIcon, x - iconSize / 2);
                            Canvas.SetTop(nodeIcon, y - iconSize / 2);
                            topologyCanvas.Children.Add(nodeIcon);
                            var textPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Orientation = Orientation.Vertical };
                            var nodeText = new TextBlock { Text = name, FontSize = 12, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 0, 0, 2) };
                            if (allowWrapping) { nodeText.MaxWidth = horizontalVmSpacing - 10; nodeText.TextWrapping = TextWrapping.Wrap; }
                            nodeText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                            textPanel.Children.Add(nodeText);
                            if (!string.IsNullOrEmpty(macAddress))
                            {
                                var macText = new TextBlock { Text = macAddress, FontSize = 10, TextAlignment = TextAlignment.Center };
                                macText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                                macText.Opacity = 0.8;
                                textPanel.Children.Add(macText);
                            }
                            if (!string.IsNullOrEmpty(ipAddress))
                            {
                                var ipText = new TextBlock { Text = ipAddress, FontSize = 11, TextAlignment = TextAlignment.Center };
                                ipText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                                textPanel.Children.Add(ipText);
                            }
                            textPanel.Loaded += (s, e) =>
                            {
                                var panel = s as StackPanel;
                                Canvas.SetLeft(panel, x - panel.ActualWidth / 2);
                                Canvas.SetTop(panel, y + iconSize / 2 + 5);
                            };
                            topologyCanvas.Children.Add(textPanel);
                        }

                        void DrawLine(double x1, double y1, double x2, double y2)
                        {
                            var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = lineThickness };
                            line.SetResourceReference(Shape.StrokeProperty, "TextFillColorSecondaryBrush");
                            topologyCanvas.Children.Add(line);
                        }

                        string ParseIPv4(string ipAddressesString)
                        {
                            if (string.IsNullOrEmpty(ipAddressesString)) return "";
                            ipAddressesString = ipAddressesString.Trim('{', '}');
                            var ips = ipAddressesString.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var ip in ips)
                            {
                                if (System.Net.IPAddress.TryParse(ip, out var parsedIp) && parsedIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                {
                                    return parsedIp.ToString();
                                }
                            }
                            return "";
                        }

                        var clients = new List<(string Name, string IpAddress, string MacAddress)>();
                        foreach (var adapter in adapters)
                        {
                            string vmIp = ParseIPv4(adapter.IPAddresses);
                            clients.Add((adapter.VMName, vmIp, adapter.MacAddress));
                        }

                        bool hasUpstream = rbBridge.IsChecked == true || rbNat.IsChecked == true;
                        double totalWidth = Math.Max(200, (clients.Count > 0 ? clients.Count : 1) * horizontalVmSpacing);
                        double centerX = totalWidth / 2;

                        CreateNode("Switch", Switch1.SwitchName, "", "", centerX, switchY);

                        if (hasUpstream)
                        {
                            string upstreamName = upstreamDropDown.Content as string ?? "上游网络";
                            CreateNode("Upstream", upstreamName, "", "", centerX, upstreamY);
                            DrawLine(centerX, upstreamY + radius, centerX, switchY - radius);
                        }

                        if (clients.Any())
                        {
                            double startX = centerX - ((clients.Count - 1) * horizontalVmSpacing) / 2;
                            DrawLine(centerX, switchY + radius, centerX, vmBusY);
                            if (clients.Count > 1)
                            {
                                DrawLine(startX, vmBusY, startX + (clients.Count - 1) * horizontalVmSpacing, vmBusY);
                            }
                            for (int i = 0; i < clients.Count; i++)
                            {
                                var client = clients[i];
                                double currentClientX = startX + i * horizontalVmSpacing;
                                CreateNode("Net", client.Name, client.IpAddress, client.MacAddress, currentClientX, vmY, allowWrapping: true);
                                DrawLine(currentClientX, vmBusY, currentClientX, vmY - radius);
                            }
                        }

                        topologyCanvas.Width = totalWidth + 40;
                        topologyCanvas.Height = vmY + 40 + 20 + 20;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error building topology: {ex.Message}");
                    }
                }
                async void OnSettingsChanged(object sender, RoutedEventArgs e)
                {
                    if (_isInitializing) return;
                    if (sender is RadioButton rb && rb.IsChecked != true) return;

                    var contentBeforeUpdate = upstreamDropDown.Content as string;
                    bool isSwitchingToUpstreamMode = rbBridge.IsChecked == true || rbNat.IsChecked == true;

                    if (isSwitchingToUpstreamMode && contentBeforeUpdate == "不可用")
                    {
                        Switch1.NetAdapterInterfaceDescription = null;
                    }

                    // 1. 确定用户意图的模式
                    string selectedMode = "Isolated";
                    if (rbBridge.IsChecked == true) selectedMode = "Bridge";
                    else if (rbNat.IsChecked == true) selectedMode = "NAT";

                    var selectedAdapterContent = upstreamDropDown.Content as string;

                    // 2. 执行配置更改前的验证
                    // 验证NAT模式唯一性
                    if (selectedMode == "NAT")
                    {
                        var existingNatSwitch = _switchList.FirstOrDefault(s => s.SwitchType == "NAT" && s.Id != Switch1.Id);
                        if (existingNatSwitch != null)
                        {
                            Utils.Show(
                                $"操作失败：系统只允许存在一个NAT网络。\n\n使用了NAT模式交换机是: '{existingNatSwitch.SwitchName}'");
                            await UpdateUIState(); // 恢复UI到操作前状态
                            return;
                        }
                    }

                    // 验证桥接网卡唯一性
                    if (selectedMode == "Bridge")
                    {
                        if (!string.IsNullOrEmpty(selectedAdapterContent) && selectedAdapterContent != "请选择网卡..." && selectedAdapterContent != "不可用")
                        {
                            var conflictingSwitch = _switchList.FirstOrDefault(s =>
                               s.SwitchType == "External" &&
                               s.NetAdapterInterfaceDescription == selectedAdapterContent &&
                               s.Id != Switch1.Id);

                            if (conflictingSwitch != null)
                            {
                                Utils.Show(
                                    $"操作失败：物理网卡 '{selectedAdapterContent}' 已经分配给交换机 '{conflictingSwitch.SwitchName}' 。");
                                await Initialinfo(); // 恢复UI到操作前状态
                                return;
                            }
                        }
                    }

                    // 如果需要上游网络但未选择，则仅更新UI并返回
                    if ((selectedMode == "Bridge" || selectedMode == "NAT") && (selectedAdapterContent == "不可用" || selectedAdapterContent == "请选择网卡..."))
                    {
                        await UpdateUIState();
                        return;
                    }

                    // 3. 执行配置更改
                    var waitPage = new WaitPage();
                    var mainWindow = Application.Current.MainWindow;

                    try
                    {
                        bool allowManagementOS = hostConnectionSwitch.IsChecked ?? false;
                        bool enableDhcp = dhcpSwitch.IsChecked ?? false; // 虽然当前未启用，但保留逻辑

                        // 显示等待页面，执行耗时操作
                        waitPage.Show();
                        mainWindow.IsEnabled = false;
                        waitPage.Owner = mainWindow;

                        await Utils.UpdateSwitchConfigurationAsync(Switch1.SwitchName, selectedMode, selectedAdapterContent, allowManagementOS, enableDhcp);

                        // 操作成功，关闭等待页面
                        waitPage.Close();
                        mainWindow.IsEnabled = true;

                        // 4. 使用最新数据完全刷新整个页面
                        await Initialinfo();
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show($"更新交换机配置失败: {ex.Message}");
                        // 确保即使失败也关闭等待页面并恢复主窗口
                        if (waitPage.IsVisible) waitPage.Close();
                        if (!mainWindow.IsEnabled) mainWindow.IsEnabled = true;
                        // 失败后也刷新一下，以恢复到正确的原始状态
                        await Initialinfo();
                    }
                }
                MenuItem CreateNetworkCardMenuItem(string cardName)
                {
                    var item = new MenuItem { Header = cardName };
                    item.Click += (s, e) =>
                    {
                        Switch1.NetAdapterInterfaceDescription = cardName;
                        upstreamDropDown.Content = cardName;
                        OnSettingsChanged(upstreamDropDown, new RoutedEventArgs());
                    };
                    return item;
                }

                // =========================================================================
                // 阶段3 - 组装UI树并绑定事件
                // =========================================================================
                var contentPanel = new StackPanel { Margin = new Thickness(50, 10, 0, 10) };
                var settingsGrid = new Grid();
                settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(125) });
                settingsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                settingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                contentPanel.Children.Add(settingsGrid);
                var rowMargin = new Thickness(0, 0, 0, 4);
                var rowMargin2 = new Thickness(0, 0, 0, 12);

                var modeLabel = Utils.TextBlock2("网络模式", 0, 0);
                modeLabel.VerticalAlignment = VerticalAlignment.Center; modeLabel.Margin = rowMargin;
                settingsGrid.Children.Add(modeLabel);
                var radioPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                radioPanel.Children.Add(rbBridge); radioPanel.Children.Add(rbNat); radioPanel.Children.Add(rbNone);
                radioPanel.Margin = rowMargin;
                Grid.SetRow(radioPanel, 0); Grid.SetColumn(radioPanel, 1);
                settingsGrid.Children.Add(radioPanel);

                var upstreamLabel = Utils.TextBlock2("上游网络", 1, 0);
                upstreamLabel.VerticalAlignment = VerticalAlignment.Center; upstreamLabel.Margin = rowMargin2;
                settingsGrid.Children.Add(upstreamLabel);
                upstreamDropDown.Margin = rowMargin2;
                var upstreamContextMenu = new ContextMenu();
                var physicalAdapterDescriptions = physicalAdapterList.Select(p => p.InterfaceDescription).ToList();
                string currentConnectedAdapter = Switch1.NetAdapterInterfaceDescription;
                if (!string.IsNullOrEmpty(currentConnectedAdapter) && !physicalAdapterDescriptions.Contains(currentConnectedAdapter))
                {
                    physicalAdapterDescriptions.Add(currentConnectedAdapter);
                }
                foreach (var adapterName in physicalAdapterDescriptions)
                {
                    upstreamContextMenu.Items.Add(CreateNetworkCardMenuItem(adapterName));
                }
                upstreamDropDown.Flyout = upstreamContextMenu;
                Grid.SetRow(upstreamDropDown, 1); Grid.SetColumn(upstreamDropDown, 1);
                settingsGrid.Children.Add(upstreamDropDown);

                var hostConnectionLabel = Utils.TextBlock2("宿主连接", 2, 0);
                hostConnectionLabel.VerticalAlignment = VerticalAlignment.Center; hostConnectionLabel.Margin = rowMargin2;
                settingsGrid.Children.Add(hostConnectionLabel);
                hostConnectionSwitch.Margin = rowMargin2;
                Grid.SetRow(hostConnectionSwitch, 2); Grid.SetColumn(hostConnectionSwitch, 1);
                settingsGrid.Children.Add(hostConnectionSwitch);

                rbBridge.Checked += OnSettingsChanged;
                rbNat.Checked += OnSettingsChanged;
                rbNone.Checked += OnSettingsChanged;
                hostConnectionSwitch.Click += OnSettingsChanged;
                dhcpSwitch.Click += OnSettingsChanged;

                var topologySectionPanel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
                var topologyTitle = new TextBlock { Text = "网络拓扑", FontSize = 16, Margin = new Thickness(0, 0, 0, 10) };
                topologySectionPanel.Children.Add(topologyTitle);
                topologySectionPanel.Children.Add(topologyCanvas);
                contentPanel.Children.Add(topologySectionPanel);

                // =========================================================================
                // 阶段4 - 设置初始状态
                // =========================================================================
                switch (Switch1.SwitchType)
                {
                    case "External": rbBridge.IsChecked = true; break;
                    case "NAT": rbNat.IsChecked = true; break;
                    case "Internal": case "Private": default: rbNone.IsChecked = true; break;
                }

                var _ = UpdateUIState();

                cardExpander.Content = contentPanel;
                ParentPanel.Children.Add(cardExpander);
                _isInitializing = false;
            });

        }

        Dispatcher.Invoke(() => //隐藏加载条
        {
            progressRing.Visibility = Visibility.Collapsed;

        });
    }

    private static async Task GetInfo(List<SwitchInfo> SwitchList, List<PhysicalAdapterInfo> physicalAdapterList)
        {
            try
            { 
                    List<AdapterInfo> VMAdapterList = new List<AdapterInfo>() ;// 存储虚拟适配器
                    Utils.Run("Set-ExecutionPolicy RemoteSigned -Scope Process -Force"); //设置策略

                    //检查是否安装hyperv，没安装就不获取信息。

                    if (Utils.Run("Get-Module -ListAvailable -Name Hyper-V").Count == 0) { return; }


            //获取物理网卡信息

            var phydata = Utils.Run(@"Get-NetAdapter -Physical | select Name, InterfaceDescription");
            if (phydata != null)
            {
                foreach (var result in phydata)
                {
                    var phyDesc = result.Properties["InterfaceDescription"]?.Value?.ToString();

                    if (!string.IsNullOrEmpty(phyDesc))
                    {
                        physicalAdapterList.Add(new PhysicalAdapterInfo(phyDesc));
                    }
                }
            }


            //获取虚拟交换机信息
            var switchdata = Utils.Run(@"Get-VMSwitch | Select-Object Name, Id, SwitchType, AllowManagementOS, NetAdapterInterfaceDescription");
                    if (switchdata == null) { return; } 

                    foreach (var result in switchdata)
                    {
                        var SwitchName = result.Properties["Name"]?.Value?.ToString();
                        var SwitchType = result.Properties["SwitchType"]?.Value?.ToString();
                        var Host = result.Properties["AllowManagementOS"]?.Value?.ToString();
                        var Id = result.Properties["Id"]?.Value?.ToString();
                        var Phydesc = result.Properties["NetAdapterInterfaceDescription"]?.Value?.ToString();

                        string? ICSAdapter = GetIcsSourceAdapterName(SwitchName);

                        SwitchType = (ICSAdapter != null) ? "NAT" : SwitchType;
                        Phydesc = (ICSAdapter != null) ? ICSAdapter : Phydesc;

                        SwitchList.Add(new SwitchInfo(SwitchName, SwitchType, Host, Id, Phydesc));
                    }
                    
        }
        catch(Exception ex){
            System.Windows.MessageBox.Show(ex.ToString());
        }
    }


    private static string? GetIcsSourceAdapterName(string switchName)
    {
        // 最终版脚本:
        // 1. 使用 HNetCfg.HNetShare COM 对象找到 ICS 源连接的“名称” (例如 "WLAN")。
        // 2. 使用 Get-NetAdapter cmdlet，根据这个名称查找对应的网络适配器。
        // 3. 从找到的适配器对象中，返回其 .InterfaceDescription 属性，这就是物理描述符。
        string script = @"
param([string]$switchName)

try {
    $PublicAdapterNameToFind = ""vEthernet ({0})"" -f $switchName

    $netShareManager = New-Object -ComObject HNetCfg.HNetShare

    $icsSourceAdapterName = $null
    $icsGatewayIsCorrect = $false

    # 步骤1: 遍历连接，找到 ICS 源的连接名称
    foreach ($connection in $netShareManager.EnumEveryConnection) {
        $config = $netShareManager.INetSharingConfigurationForINetConnection.Invoke($connection)
        
        if ($config.SharingEnabled) {
            $props = $netShareManager.NetConnectionProps.Invoke($connection)
            
            if (($config.SharingConnectionType -eq 1) -and ($props.Name -eq $PublicAdapterNameToFind)) {
                $icsGatewayIsCorrect = $true
            }
            elseif ($config.SharingConnectionType -eq 0) {
                $icsSourceAdapterName = $props.Name
            }
        }
    }

    # 步骤2: 如果找到了源连接名称，就用它来获取物理描述符
    if (($icsGatewayIsCorrect) -and ($null -ne $icsSourceAdapterName)) {
        try {
            # 使用 Get-NetAdapter 获取适配器的详细信息
            $adapterDetails = Get-NetAdapter -Name $icsSourceAdapterName -ErrorAction Stop
            
            # 返回 InterfaceDescription 属性，这是我们真正想要的物理描述符
            return $adapterDetails.InterfaceDescription
        }
        catch {
            # 如果 Get-NetAdapter 失败，作为一个安全的备用方案，返回连接名称
            return $icsSourceAdapterName
        }
    }

    return $null
}
catch {
    return $null
}
";

        // C# 调用部分保持不变，它已经是正确的了
        try
        {
            using (var ps = System.Management.Automation.PowerShell.Create())
            {
                ps.AddScript(script);
                ps.AddParameter("switchName", switchName);

                var results = ps.Invoke();

                if (ps.Streams.Error.Count > 0)
                {
                    return null;
                }

                if (results.Count > 0 && results[0] != null)
                {
                    return results[0].BaseObject?.ToString();
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }
    private List<AdapterInfo> GetFullSwitchNetworkState(string switchName)
    {
        string vmAdaptersScript =
            $"Get-VMNetworkAdapter -VMName * | Where-Object {{ $_.SwitchName -eq '{switchName}' }} | " +
            "Select-Object VMName, " +
            "@{Name='MacAddress'; Expression={($_.MacAddress).Insert(2, ':').Insert(5, ':').Insert(8, ':').Insert(11, ':').Insert(14, ':')}}, " +
            "Status, @{Name='IPAddresses'; Expression={($_.IPAddresses -join ',')}}";
        string hostAdapterScript =
            $"$vmAdapter = Get-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}';" +
            "if ($vmAdapter) {" +
            "    $netAdapter = Get-NetAdapter | Where-Object { ($_.MacAddress -replace '-') -eq ($vmAdapter.MacAddress -replace '-') -and ($_.InterfaceDescription -like '*Hyper-V*') };" +
            "    if ($netAdapter) {" +
            "        $ipAddressObjects = Get-NetIPAddress -InterfaceIndex $netAdapter.InterfaceIndex -ErrorAction SilentlyContinue;" +
            "        if ($ipAddressObjects) {" +
            "            $ipAddresses = ($ipAddressObjects.IPAddress) -join ',';" +
            "        } else {" +
            "            $ipAddresses = '';" +
            "        }" +
            "        [PSCustomObject]@{" +
            "            VMName      = '主机';" +
            "            MacAddress  = ($netAdapter.MacAddress -replace '-', ':');" +
            "            Status      = $netAdapter.Status.ToString();" +
            "            IPAddresses = $ipAddresses;" +
            "        };" +
            "    }" +
            "}";

        try
        {
            var vmResults = Utils.Run(vmAdaptersScript);
            var hostResults = Utils.Run(hostAdapterScript);
            var allAdapters = new List<AdapterInfo>();

            if (vmResults != null)
            {
                foreach (var pso in vmResults)
                {
                    allAdapters.Add(new AdapterInfo(
                        pso.Properties["VMName"]?.Value?.ToString() ?? "",
                        pso.Properties["MacAddress"]?.Value?.ToString() ?? "",
                        pso.Properties["Status"]?.Value?.ToString() ?? "",
                        pso.Properties["IPAddresses"]?.Value?.ToString() ?? ""
                    ));
                }
            }
            if (hostResults != null)
            {
                foreach (var pso in hostResults)
                {
                    allAdapters.Add(new AdapterInfo(
                        pso.Properties["VMName"]?.Value?.ToString() ?? "",
                        pso.Properties["MacAddress"]?.Value?.ToString() ?? "",
                        pso.Properties["Status"]?.Value?.ToString() ?? "",
                        pso.Properties["IPAddresses"]?.Value?.ToString() ?? ""
                    ));
                }
            }

            return allAdapters;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error getting full network state for switch '{switchName}': {ex.Message}");
            return new List<AdapterInfo>();
        }
    }
    private async Task<List<AdapterInfo>> GetFullSwitchNetworkStateAsync(string switchName)
    {
        return await Task.Run(() => GetFullSwitchNetworkState(switchName));
    }

}
