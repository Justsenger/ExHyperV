using System.Windows.Controls;
namespace ExHyperV.Views.Pages;
using System;
using System.Collections.Generic;
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
            public SwitchInfo(string switchName, string switchType, string host, string id, string phydesc,List<AdapterInfo> adapters)
            {
                SwitchName = switchName;
                SwitchType = switchType;
                AllowManagementOS = host;
                Id = id;
                NetAdapterInterfaceDescription = phydesc;
                Adapters = adapters;
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
                        topologyCanvas.Children.Clear();
                        double iconSize = 28; double radius = iconSize / 2; double verticalSpacing = 70;
                        double horizontalVmSpacing = 130; double lineThickness = 1.5; double generalLineCorrection = lineThickness / 2;
                        double switchIconVerticalCorrection = 4.0; double upstreamY = 20; double switchY = upstreamY + verticalSpacing;
                        double vmBusY = switchY + verticalSpacing; double vmY = vmBusY + 35;
                        (FontIcon icon, TextBlock text) CreateNode(string deviceType, string name, double x, double y, bool allowWrapping = false)
                        {
                            var nodeIcon = Utils.FontIcon1(deviceType, ""); nodeIcon.FontSize = iconSize; Canvas.SetLeft(nodeIcon, x - iconSize / 2); Canvas.SetTop(nodeIcon, y - iconSize / 2);
                            topologyCanvas.Children.Add(nodeIcon); var nodeText = new TextBlock { Text = name, FontSize = 12, TextAlignment = TextAlignment.Center };
                            if (allowWrapping) { nodeText.MaxWidth = horizontalVmSpacing - 10; nodeText.TextWrapping = TextWrapping.Wrap; }
                            nodeText.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                            nodeText.Loaded += (s, e) => { Canvas.SetLeft(nodeText, x - nodeText.ActualWidth / 2); Canvas.SetTop(nodeText, y + iconSize / 2 + 5); };
                            topologyCanvas.Children.Add(nodeText); return (nodeIcon, nodeText);
                        }
                        void DrawLine(double x1, double y1, double x2, double y2)
                        {
                            var line = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeThickness = lineThickness };
                            line.SetResourceReference(Shape.StrokeProperty, "TextFillColorSecondaryBrush"); topologyCanvas.Children.Add(line);
                        }
                        var clientNames = new List<string>(); if (hostConnectionSwitch.IsChecked == true) { clientNames.Add("主机"); }
                        foreach (var vmAdapter in Switch1.Adapters) { clientNames.Add(vmAdapter.VMName); }
                        bool hasUpstream = rbBridge.IsChecked == true || rbNat.IsChecked == true;
                        double totalWidth = Math.Max(200, (clientNames.Count > 0 ? clientNames.Count : 1) * horizontalVmSpacing);
                        double centerX = totalWidth / 2; CreateNode("Switch", Switch1.SwitchName, centerX, switchY);
                        if (hasUpstream)
                        {
                            string upstreamName = upstreamDropDown.Content as string ?? "上游网络"; CreateNode("Upstream", upstreamName, centerX, upstreamY);
                            DrawLine(centerX, upstreamY + radius - generalLineCorrection, centerX, switchY - radius + switchIconVerticalCorrection);
                        }
                        if (clientNames.Any())
                        {
                            double startX = centerX - ((clientNames.Count - 1) * horizontalVmSpacing) / 2;
                            DrawLine(centerX, switchY + radius - switchIconVerticalCorrection, centerX, vmBusY);
                            if (clientNames.Count > 1) { DrawLine(startX, vmBusY, startX + (clientNames.Count - 1) * horizontalVmSpacing, vmBusY); }
                            for (int i = 0; i < clientNames.Count; i++)
                            {
                                var clientName = clientNames[i]; double currentClientX = startX + i * horizontalVmSpacing;
                                CreateNode("Net", clientName, currentClientX, vmY, allowWrapping: true);
                                DrawLine(currentClientX, vmBusY, currentClientX, vmY - radius + generalLineCorrection);
                            }
                        }
                        topologyCanvas.Width = totalWidth + 40; topologyCanvas.Height = vmY + 40;
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Error building topology: {ex.Message}"); }
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

                    if (rbBridge.IsChecked == true)
                    {
                        hostConnectionSwitch.IsEnabled = true;
                        dhcpSwitch.IsEnabled = false;
                        UpdateUpstreamState(true);
                    }
                    else if (rbNat.IsChecked == true)
                    {
                        hostConnectionSwitch.IsChecked = true;
                        hostConnectionSwitch.IsEnabled = false;
                        dhcpSwitch.IsEnabled = true;
                        UpdateUpstreamState(true);
                    }
                    else // 无上游模式
                    {
                        hostConnectionSwitch.IsEnabled = true;
                        dhcpSwitch.IsEnabled = true;
                        UpdateUpstreamState(false);
                    }

                    if (hostConnectionSwitch.IsEnabled)
                    {
                        if (hostConnectionSwitch.IsChecked == false)
                        {
                            dhcpSwitch.IsChecked = false;
                            dhcpSwitch.IsEnabled = false;
                        }
                        else
                        {
                            if (rbNat.IsChecked != true && rbBridge.IsChecked != true)
                            {
                                dhcpSwitch.IsEnabled = true;
                            }
                        }
                    }

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
            }
        });
    }

    private async Task GetInfo(List<SwitchInfo> SwitchList, List<PhysicalAdapterInfo> physicalAdapterList)
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
                        
                        List<AdapterInfo> adapters = new List<AdapterInfo>(); 

                        var AdapterData = Utils.Run($@"Get-VMNetworkAdapter -VMName * | Where-Object {{$_.SwitchName -eq '{SwitchName}'}} | select VMName,MacAddress,Status,IPAddresses");//获取连接此交换机的适配器

                        if (AdapterData != null && AdapterData.Count > 0)
                        {
                            foreach (var Adapter in AdapterData)
                            {
                                var VMName = Adapter.Members["VMName"]?.Value?.ToString();
                                var MacAddress = Adapter.Members["MacAddress"]?.Value?.ToString();
                                var Status = Adapter.Members["Status"]?.Value?.ToString();
                                var IPAddresses = Adapter.Members["IPAddresses"]?.Value?.ToString();
                                adapters.Add(new AdapterInfo(VMName, MacAddress, Status, IPAddresses)); //每个适配器都添加
                            }
                        }
                        SwitchList.Add(new SwitchInfo(SwitchName, SwitchType, Host, Id, Phydesc, adapters));
                    }
                    
        }
        catch(Exception ex){
            System.Windows.MessageBox.Show(ex.ToString());
        }
    }



}
