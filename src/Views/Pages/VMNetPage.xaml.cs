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

                // *** 标志位: 用于控制初始化流程和防止UI更新事件重入 ***
                bool _isInitializing = true;
                bool _isUpdatingUiFromCode = false;

                // =========================================================================
                // Header 部分 (无变化)
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
                bool isConnected = !string.IsNullOrEmpty(Switch1.NetAdapterInterfaceDescription);
                string statusText = isConnected ? "已连接到：" + Switch1.NetAdapterInterfaceDescription : "未连接上游网络";
                statusCircle.Fill = new SolidColorBrush(isConnected ? Colors.Green : Colors.Red);
                var statusTextBlock = Utils.TextBlock12(statusText);
                statusTextBlock.Margin = new Thickness(0);
                statusPanel.Children.Add(statusCircle);
                statusPanel.Children.Add(statusTextBlock);
                Grid.SetRow(drivername, 0);
                Grid.SetRow(statusPanel, 1);
                headerGrid.Children.Add(drivername);
                headerGrid.Children.Add(statusPanel);
                cardExpander.Header = headerGrid;

                // =========================================================================
                // 阶段1 - 控件声明 (无变化)
                // =========================================================================
                var buttonPadding = new Thickness(8, 6, 8, 4);
                var rbBridge = new RadioButton { Content = "桥接模式", GroupName = Switch1.Id, Padding = buttonPadding };
                var rbNat = new RadioButton { Content = "NAT模式", GroupName = Switch1.Id, Padding = buttonPadding };
                var rbNone = new RadioButton { Content = "无上游", GroupName = Switch1.Id, Padding = buttonPadding };
                var upstreamDropDown = Utils.DropDownButton1("请选择网卡...");
                var hostConnectionSwitch = new ToggleSwitch { IsChecked = (Switch1.AllowManagementOS?.ToLower() == "true"), HorizontalAlignment = HorizontalAlignment.Left };
                // DHCP的初始状态应从您的数据源获取，这里先默认为false，后续由UpdateUIState修正
                var dhcpSwitch = new ToggleSwitch { IsChecked = false, HorizontalAlignment = HorizontalAlignment.Left };
                var topologyCanvas = new Canvas();

                // =========================================================================
                // 阶段2 - 局部函数定义
                // =========================================================================

                // BuildVerticalTopology 函数 (无变化)
                void BuildVerticalTopology()
                {
                    try
                    {
                        // ------------------- 阶段1: 初始化画布和布局参数 (无变化) -------------------
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

                        // ------------------- 阶段2: 定义核心辅助函数 -------------------

                        // 辅助函数1: [全新重构] 使用 StackPanel 来自动布局文本，确保无重叠
                        void CreateNode(string deviceType, string name, string ipAddress, string macAddress, double x, double y, bool allowWrapping = false)
                        {
                            // 绘制图标 (与之前相同)
                            var nodeIcon = Utils.FontIcon1(deviceType, "");
                            nodeIcon.FontSize = iconSize;
                            Canvas.SetLeft(nodeIcon, x - iconSize / 2);
                            Canvas.SetTop(nodeIcon, y - iconSize / 2);
                            topologyCanvas.Children.Add(nodeIcon);

                            // 创建一个垂直堆叠面板来容纳所有文本
                            var textPanel = new StackPanel
                            {
                                HorizontalAlignment = HorizontalAlignment.Center,
                                Orientation = Orientation.Vertical,
                            };

                            // 1. 添加设备名称
                            var nodeText = new TextBlock
                            {
                                Text = name,
                                FontSize = 12,
                                TextAlignment = TextAlignment.Center,
                                Margin = new Thickness(0, 0, 0, 2) // 在名称和MAC之间添加一点小间距
                            };
                            if (allowWrapping) { nodeText.MaxWidth = horizontalVmSpacing - 10; nodeText.TextWrapping = TextWrapping.Wrap; }
                            nodeText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                            textPanel.Children.Add(nodeText);

                            // 2. 添加MAC地址 (如果存在)
                            if (!string.IsNullOrEmpty(macAddress))
                            {
                                var macText = new TextBlock
                                {
                                    Text = macAddress,
                                    FontSize = 10,
                                    TextAlignment = TextAlignment.Center
                                };
                                macText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                                macText.Opacity = 0.8;
                                textPanel.Children.Add(macText);
                            }

                            // 3. 添加IP地址 (如果存在)
                            if (!string.IsNullOrEmpty(ipAddress))
                            {
                                var ipText = new TextBlock
                                {
                                    Text = ipAddress,
                                    FontSize = 11,
                                    TextAlignment = TextAlignment.Center
                                };
                                ipText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
                                textPanel.Children.Add(ipText);
                            }

                            // 将整个文本面板添加到画布上
                            // 使用 Loaded 事件来确保面板尺寸计算完毕后，再进行居中定位
                            textPanel.Loaded += (s, e) => {
                                var panel = s as StackPanel;
                                Canvas.SetLeft(panel, x - panel.ActualWidth / 2);
                                Canvas.SetTop(panel, y + iconSize / 2 + 5); // 放置在图标下方
                            };
                            topologyCanvas.Children.Add(textPanel);
                        }

                        // 辅助函数2: 绘制连接线 (无变化)
                        void DrawLine(double x1, double y1, double x2, double y2)
                        {
                            var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = lineThickness };
                            line.SetResourceReference(Shape.StrokeProperty, "TextFillColorSecondaryBrush");
                            topologyCanvas.Children.Add(line);
                        }

                        // 辅助函数3: 从原始字符串中解析出第一个有效的IPv4地址 (无变化)
                        string ParseIPv4(string ipAddressesString)
                        {
                            if (string.IsNullOrEmpty(ipAddressesString)) return "";
                            ipAddressesString = ipAddressesString.Trim('{', '}');
                            var ips = ipAddressesString.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (var ip in ips)
                            {
                                if (System.Net.IPAddress.TryParse(ip, out var parsedIp) &&
                                    parsedIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                {
                                    return parsedIp.ToString();
                                }
                            }
                            return "";
                        }

                        // ------------------- 阶段3: 准备数据 (无变化) -------------------
                        var clients = new List<(string Name, string IpAddress, string MacAddress)>();
                        

                        foreach (var adapter in adapters)
                        {
                            string vmIp = ParseIPv4(adapter.IPAddresses);
                            clients.Add((adapter.VMName, vmIp, adapter.MacAddress));
                        }

                        // ------------------- 阶段4: 绘制拓扑图 (调用方式无变化) -------------------
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
                                // 调用 CreateNode，顺序是 (Name, IP, MAC)
                                CreateNode("Net", client.Name, client.IpAddress, client.MacAddress, currentClientX, vmY, allowWrapping: true);
                                DrawLine(currentClientX, vmBusY, currentClientX, vmY - radius);
                            }
                        }

                        // ------------------- 阶段5: 设置画布最终尺寸 -------------------
                        // Canvas会自动扩展，但为了ScrollViewer能正确工作，最好还是设置一个足够大的高度
                        topologyCanvas.Width = totalWidth + 40;
                        topologyCanvas.Height = vmY + 40 + 20 + 20; // 保持足够的高度
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error building topology: {ex.Message}");
                    }
                }
                // UpdateUpstreamState 函数 (无变化)
                void UpdateUpstreamState(bool isEnabled)
                {
                    upstreamDropDown.IsEnabled = isEnabled;
                    if (!isEnabled) { upstreamDropDown.Content = "不可用"; }
                    else { upstreamDropDown.Content = !string.IsNullOrEmpty(Switch1.NetAdapterInterfaceDescription) ? Switch1.NetAdapterInterfaceDescription : "请选择网卡..."; }
                }

                // *** 核心: 全新的、更精确的UI状态更新函数 *** (无变化)
                void UpdateUIState()
                {
                    if (_isUpdatingUiFromCode) return;
                    _isUpdatingUiFromCode = true;

                    // 1. 首先确定当前选择的模式
                    var isBridgeMode = rbBridge.IsChecked == true;
                    var isNatMode = rbNat.IsChecked == true;
                    // 如果都不是，则为“无上游模式”

                    // 2. 根据模式计算出各个UI控件的目标状态
                    bool hostConnectionEnabled;
                    bool dhcpEnabled; // 暂时保留这个变量，但它的值会被覆盖

                    if (isBridgeMode)
                    {
                        // 桥接模式：用户可以自己决定主机是否连接
                        hostConnectionEnabled = true;
                        dhcpEnabled = false;
                        UpdateUpstreamState(true);
                    }
                    else if (isNatMode)
                    {
                        // NAT模式：主机必须连接作为网关
                        hostConnectionEnabled = false; // 不允许用户修改
                        hostConnectionSwitch.IsChecked = true; // 强制开启
                        dhcpEnabled = true; // 这里的计算结果会被忽略
                        UpdateUpstreamState(true);
                    }
                    else // 无上游模式
                    {
                        // 无上游模式：用户可决定主机连接
                        hostConnectionEnabled = true;
                        dhcpEnabled = false;
                        UpdateUpstreamState(false);
                    }

                    // <<< 关键修改：在这里强制禁用所有模式的DHCP功能
                    // 以后要恢复时，只需注释或删除下面这行即可
                    dhcpEnabled = false;

                    // 3. 将计算好的状态一次性应用到UI控件
                    hostConnectionSwitch.IsEnabled = hostConnectionEnabled;
                    dhcpSwitch.IsEnabled = dhcpEnabled;

                    // 最佳实践：当一个开关被禁用时，总是将其状态重置为“关闭”
                    if (!dhcpSwitch.IsEnabled)
                    {
                        dhcpSwitch.IsChecked = false;
                    }

                    // 4. 更新其他依赖项
                    BuildVerticalTopology();

                    _isUpdatingUiFromCode = false;
                }

                // *** 核心: 重构后的统一事件处理器 *** (无变化)
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

                        string? selectedAdapter = (upstreamDropDown.IsEnabled) ? upstreamDropDown.Content as string : null;
                        bool allowManagementOS = hostConnectionSwitch.IsChecked ?? false;
                        bool enableDhcp = dhcpSwitch.IsChecked ?? false;

                        if ((selectedMode == "Bridge" || selectedMode == "NAT") &&
                            (string.IsNullOrEmpty(selectedAdapter) || selectedAdapter.Contains("请选择") || selectedAdapter.Contains("不可用")))
                        {
                            return;
                        }

                         await Utils.UpdateSwitchConfigurationAsync(Switch1.SwitchName, selectedMode, selectedAdapter, allowManagementOS, enableDhcp);
                         BuildVerticalTopology();
                    }
                    catch (Exception ex)
                    {
                        System.Windows.MessageBox.Show($"更新交换机配置失败: {ex.Message}");
                    }
                }

                // =========================================================================
                //         <<<<<<<<<<<<<<<<<<   此处为核心修正区域   >>>>>>>>>>>>>>>>>>
                // =========================================================================
                // CreateNetworkCardMenuItem 函数 (已修正，解决网卡选择后UI不更新的问题)
                MenuItem CreateNetworkCardMenuItem(string cardName)
                {
                    var item = new MenuItem { Header = cardName };
                    item.Click += (s, e) =>
                    {
                        // 1. 核心修正: 首先更新数据模型(Switch1对象)
                        //    这确保了后续的UI刷新函数使用的是最新的数据，而不是旧的。
                        Switch1.NetAdapterInterfaceDescription = cardName;

                        // 2. 立即更新UI显示，提供快速反馈
                        upstreamDropDown.Content = cardName;

                        // 3. 触发统一的事件处理器来刷新所有相关UI状态并执行后台更新。
                        //    (不再需要单独调用 UpdateUIState()，因为 OnSettingsChanged 会做)
                        OnSettingsChanged(upstreamDropDown, new RoutedEventArgs());
                    };
                    return item;
                }

                // =========================================================================
                // 阶段3 - 组装UI树并绑定事件 (无变化)
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

                // 统一绑定所有会影响状态的控件的事件
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
                // 阶段4 - 设置初始状态 (无变化)
                // =========================================================================
                switch (Switch1.SwitchType)
                {
                    case "External": rbBridge.IsChecked = true; break;
                    case "NAT": rbNat.IsChecked = true; break;
                    case "Internal": case "Private": default: rbNone.IsChecked = true; break;
                }

                // 调用一次以应用所有联动规则，确保UI在加载时就处于正确状态
                UpdateUIState();

                cardExpander.Content = contentPanel;
                ParentPanel.Children.Add(cardExpander);

                // 所有初始化完成，允许事件处理器执行后台任务
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
