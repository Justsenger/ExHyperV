using System.Windows.Controls;
namespace ExHyperV.Views.Pages;
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using Wpf.Ui.Controls;



public partial class VMNetPage
{
    public bool refreshlock = false;
    private bool _isUpdatingUiFromCode = false;
    public VMNetPage()
    {
        InitializeComponent();
        Task.Run(() => Initialinfo()); //获取宿主虚拟交换机信息
    }
    private void VMNetPage_Loaded(object sender, RoutedEventArgs e)
    {
        WaitPage waitwindows = new();
        waitwindows.ShowDialog();
    }


    //虚拟交换机的数据结构
    public class SwitchInfo
        {
            public string SwitchName { get; set; }
            public string SwitchType { get; set; }
            public string AllowManagementOS { get; set; }
            public string Id { get; set; }
            public string NetAdapterInterfaceDescription { get; set; }
            public List<AdapterInfo> Adapters { get; set; }  // 网络适配器列表

            // 构造函数
            public SwitchInfo(string switchName, string switchType, string host, string id, string phydesc)
            {
                SwitchName = switchName;
                SwitchType = switchType;
                AllowManagementOS = host;
                Id = id;
                NetAdapterInterfaceDescription = phydesc;
        }
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


    private async void Initialinfo(){
        List<SwitchInfo> SwitchList = new List<SwitchInfo>(); //存储交换机数据
        List<PhysicalAdapterInfo> physicalAdapterList = new List<PhysicalAdapterInfo>(); //存储物理网卡数据
        await GetInfo(SwitchList, physicalAdapterList); //获取数据

        Dispatcher.Invoke(() => //清空交换机列表
        {
            ParentPanel.Children.Clear();
            
        });

        foreach (var Switch1 in SwitchList)
        {
            List<AdapterInfo> adapters = GetFullSwitchNetworkState(Switch1.SwitchName);

            Dispatcher.Invoke(() => //更新UI
            {
                ParentPanel.Children.Clear();
                progressRing.Visibility = Visibility.Collapsed; //隐藏加载条

                foreach (var Switch1 in SwitchList)
                {
                    // *** 标志位: 用于控制初始化流程和防止UI更新事件重入 ***
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

                    // Header文本和状态的最终更新将在UpdateUIState中进行，这里只做基础初始化
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

                    void BuildVerticalTopology()
                    {
                        try
                        {
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
                                textPanel.Loaded += (s, e) => {
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
                            List<AdapterInfo> adapters = GetFullSwitchNetworkState(Switch1.SwitchName);
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

                    void UpdateUpstreamState(string mode)
                    {
                        switch (mode)
                        {
                            case "Bridge":
                                upstreamDropDown.IsEnabled = true;
                                upstreamDropDown.Content = !string.IsNullOrEmpty(Switch1.NetAdapterInterfaceDescription)
                                    ? Switch1.NetAdapterInterfaceDescription
                                    : "请选择网卡...";
                                break;
                            case "NAT":
                                upstreamDropDown.IsEnabled = false;
                                upstreamDropDown.Content = "自动适应";
                                break;
                            case "None":
                            default:
                                upstreamDropDown.IsEnabled = false;
                                upstreamDropDown.Content = "不可用";
                                break;
                        }
                    }

                    void UpdateUIState()
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
                            hostConnectionEnabled = true;
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
                            // 在此硬编码NAT模式的描述文本
                            headerStatusText = "已连接到：通过主机共享网络";
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

                        UpdateUpstreamState(currentMode);

                        statusTextBlock.Text = headerStatusText;
                        statusCircle.Fill = new SolidColorBrush(headerIsConnected ? Colors.Green : Colors.Red);

                        BuildVerticalTopology();

                        _isUpdatingUiFromCode = false;
                    }

                    async void OnSettingsChanged(object sender, RoutedEventArgs e)
                    {
                        UpdateUIState();

                        if (_isInitializing) return;
                        if (sender is RadioButton rb && rb.IsChecked != true) return;

                        try
                        {
                            string selectedMode = "Isolated";
                            if (rbBridge.IsChecked == true) selectedMode = "Bridge";
                            else if (rbNat.IsChecked == true) selectedMode = "NAT";

                            string? selectedAdapter = null;
                            if (selectedMode == "Bridge")
                            {
                                var content = upstreamDropDown.Content as string;
                                if (string.IsNullOrEmpty(content) || content.Contains("请选择") || content.Contains("不可用"))
                                {
                                    return;
                                }
                                selectedAdapter = content;
                            }

                            bool allowManagementOS = hostConnectionSwitch.IsChecked ?? false;
                            bool enableDhcp = dhcpSwitch.IsChecked ?? false;

                            await Utils.UpdateSwitchConfigurationAsync(Switch1.SwitchName, selectedMode, selectedAdapter, allowManagementOS, enableDhcp);
                            BuildVerticalTopology();
                        }
                        catch (Exception ex)
                        {
                            System.Windows.MessageBox.Show($"更新交换机配置失败: {ex.Message}");
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

                    var dhcpLabel = Utils.TextBlock2("简易DHCP", 3, 0);
                    dhcpLabel.VerticalAlignment = VerticalAlignment.Center; dhcpLabel.Margin = new Thickness(0, 0, 0, 10);
                    settingsGrid.Children.Add(dhcpLabel);
                    dhcpSwitch.Margin = new Thickness(0, 0, 0, 10);
                    Grid.SetRow(dhcpSwitch, 3); Grid.SetColumn(dhcpSwitch, 1);
                    settingsGrid.Children.Add(dhcpSwitch);

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

                    UpdateUIState();

                    cardExpander.Content = contentPanel;
                    ParentPanel.Children.Add(cardExpander);

                    _isInitializing = false;
                }
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

                        bool isConfiguredAsNat = IsSwitchNatConfigured(SwitchName);

                        SwitchType = isConfiguredAsNat ? "NAT" : SwitchType;

                        SwitchList.Add(new SwitchInfo(SwitchName, SwitchType, Host, Id, Phydesc));
                    }
                    
        }
        catch(Exception ex){
            System.Windows.MessageBox.Show(ex.ToString());
        }
    }


    private static bool IsSwitchNatConfigured(string switchName)
    {
        // 使用 C# 的 verbatim string (@"...") 和 string interpolation ($"...") 来构建脚本
        // 这使得我们可以轻松地将 switchName 变量插入到脚本中。
        string script = $@"
        try {{
            $ipString = (Get-NetIPAddress -InterfaceAlias (Get-NetAdapter | Where-Object {{ ($_.MacAddress -replace '[-:]') -eq ((Get-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}' -ErrorAction Stop).MacAddress -replace '[-:]') }}).Name -AddressFamily IPv4 -ErrorAction Stop)[0].IPAddress
            $adapterIP = [System.Net.IPAddress]::Parse($ipString)

            $matchingRule = Get-NetNat -ErrorAction SilentlyContinue | Where-Object {{
                $natSubnetParts = $_.InternalIPInterfaceAddressPrefix.Split('/')
                $natNetworkAddress = [System.Net.IPAddress]::Parse($natSubnetParts[0])
                $prefixLength = [int]$natSubnetParts[1]

                $subnetMaskInt = [uint32]::MaxValue -shl (32 - $prefixLength)
                $subnetMaskBytes = [System.BitConverter]::GetBytes($subnetMaskInt)
                [array]::Reverse($subnetMaskBytes)

                $adapterIPBytes = $adapterIP.GetAddressBytes()

                $resultBytes = for ($i = 0; $i -lt 4; $i++) {{ $adapterIPBytes[$i] -band $subnetMaskBytes[$i] }}
                $resultIP = [System.Net.IPAddress]::new($resultBytes)

                $resultIP.Equals($natNetworkAddress)
            }} | Select-Object -First 1

            [bool]$matchingRule
        }}
        catch {{
            $false
        }}
    ";

        try
        {
            // 运行脚本并解析返回的结果
            var results = Utils.Run(script);

            // PowerShell 的 $true/$false 会被作为布尔类型的对象返回
            if (results != null && results.Count > 0 && results[0]?.BaseObject is bool boolResult)
            {
                return boolResult;
            }
        }
        catch
        {
            // 如果执行脚本时发生任何异常（例如权限问题），都安全地返回 false
            return false;
        }

        // 如果没有返回任何结果，也视为 false
        return false;
    }

    // 保持 C# 方法结构不变
    private List<AdapterInfo> GetFullSwitchNetworkState(string switchName)
    {
        // *** MODIFIED: 虚拟机脚本 ***
        // 使用 calculated property (@{...}) 来格式化无分隔符的MAC地址
        string vmAdaptersScript =
            $"Get-VMNetworkAdapter -VMName * | Where-Object {{ $_.SwitchName -eq '{switchName}' }} | " +
            "Select-Object VMName, " +
            "@{Name='MacAddress'; Expression={($_.MacAddress).Insert(2, ':').Insert(5, ':').Insert(8, ':').Insert(11, ':').Insert(14, ':')}}, " +
            "Status, @{Name='IPAddresses'; Expression={($_.IPAddresses -join ',')}}";

        // *** MODIFIED: 宿主机脚本 ***
        // 使用 -replace 操作符将 '-' 替换为 ':'
        string hostAdapterScript =
            $"$vmAdapter = Get-VMNetworkAdapter -ManagementOS -SwitchName '{switchName}';" +
            "if ($vmAdapter) {" +
            "    $netAdapter = Get-NetAdapter | Where-Object { (($_.MacAddress -replace '-') -eq ($vmAdapter.MacAddress -replace '-')) -and ($_.Virtual -eq $true) };" +
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
            // 1. 分别执行 PowerShell 命令 (逻辑不变)
            var vmResults = Utils.Run(vmAdaptersScript);
            var hostResults = Utils.Run(hostAdapterScript);

            // 2. 解析和合并结果 (逻辑不变)
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

}
