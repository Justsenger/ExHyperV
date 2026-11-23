using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmNetworkSettingsView : VmSettingsView
{
    private static readonly ConcurrentDictionary<Guid, string> UncertainOperations = new();
    private static readonly VmNetworkCardExpansion CardExpansion = new();
    private readonly StackPanel page = new() { Padding = new(0, 0, 0, 18) };
    private readonly ScrollViewer scroll = new()
    {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch
    };
    private readonly List<Action> updateIpTiles = [];
    private IReadOnlyDictionary<Guid, string> switches = new Dictionary<Guid, string>();
    private IReadOnlyDictionary<Guid, uint> iovCapacities = new Dictionary<Guid, uint>();
    private readonly Dictionary<Guid, string> switchModes = [];
    private Action? switchModesChanged;
    private IReadOnlyList<VmNetworkAdapter> adapters = [];
    private VmNetworkPolicyDraft? policy;
    private bool loadedSnapshot, loading, running;
    private enum IpReadState { Pending, Reading, Finished }
    private IpReadState ipRead;
    private int ipReadVersion;
    private string? warning;
    private InfoBar? ipWarning;
    private int generation;
    private VmState power;
    private VmState? powerDuringLoad;
    private bool CanWrite => loadedSnapshot && !IsStopped && !UncertainOperations.ContainsKey(VmId);
    private string? MacLock => VmNetworkService.GetMacModificationCondition(power).CanModify ? null : "关闭虚拟机后可设置静态 MAC 地址。";

    public VmNetworkSettingsView(Guid vmId) : base(vmId)
    {
        scroll.Content = page;
        AttachScrollbarClearance(scroll, page);
        SetBody(scroll, handlesScrolling: true);
        ActualThemeChanged += (_, _) =>
        {
            if (IsStopped || (!loadedSnapshot && !loading)) return;
            double offset = scroll.VerticalOffset;
            if (policy != null) RenderPolicy();
            else if (adding) RenderAdd();
            else RenderList();
            scroll.ChangeView(null, offset, null, disableAnimation: true);
        };
        IsEnabledChanged += (_, _) =>
        {
            if (IsEnabled && !IsStopped && loading)
            {
                loading = false;
                RenderList();
            }
        };
        RenderList();
    }

    protected override async Task LoadAsync()
    {
        Guid? editing = policy?.Saved.DeviceId;
        loadedSnapshot = false;
        loading = true;
        ipRead = IpReadState.Pending;
        powerDuringLoad = null;
        switchModes.Clear();
        ipWarning = null;
        adapters = [];
        RenderList();
        try
        {
            var catalog = VmNetworkService.GetAvailableSwitchesAsync();
            var devices = VmNetworkService.GetNetworkAdaptersAsync(VmId);
            var state = ReadContextAsync();
            var capacities = VmNetworkService.GetSwitchIovCapacitiesAsync();
            await Task.WhenAll(catalog, devices, state, capacities);
            if (!devices.Result.HasData) throw new InvalidOperationException(CoreMessageResolver.ResolveError(devices.Result));
            if (!catalog.Result.HasData) throw new InvalidOperationException(CoreMessageResolver.ResolveError(catalog.Result));
            if (IsStopped) return;
            switches = catalog.Result.Data;
            adapters = devices.Result.Data;
            iovCapacities = capacities.Result;
            generation = state.Result.Generation;
            power = powerDuringLoad ?? state.Result.State;
            running = power == VmState.Running;
            loadedSnapshot = true;
            var current = adapters.FirstOrDefault(a => a.DeviceId == editing);
            if (current != null && CanWrite) SetPolicySnapshot(current);
            else policy = null;
        }
        finally
        {
            loading = false;
            if (!IsStopped)
            {
                if (policy != null && CanWrite) RenderPolicy();
                else RenderList();
            }
        }
        if (switches.Count > 0 && adapters.Count > 0) _ = CompleteSwitchModesAsync(adapters);
    }

    internal void UpdatePowerState(VmState nextState)
    {
        if (IsStopped || nextState == VmState.Unknown) return;
        if (loading) powerDuringLoad = nextState;
        bool wasRunning = running;
        bool nextRunning = nextState == VmState.Running;
        if (power == nextState && wasRunning == nextRunning) return;
        power = nextState;
        running = nextRunning;
        if (!loadedSnapshot) return;

        if (running != wasRunning)
        {
            ipRead = IpReadState.Pending;
            if (ipWarning?.Parent == page) page.Children.Remove(ipWarning);
            ipWarning = null;
            foreach (var adapter in adapters) adapter.IpAddresses.Clear();
        }
        if (adding)
        {
            if (LegacyLock != null) addLegacy = false;
            if (MacLock != null && addDraft != null) addDraft.IsStaticMac = false;
            RenderAdd();
        }
        else if (policy != null)
        {
            if (MacLock != null && policy.MacChanged) policy.RestoreMac();
            RenderPolicy();
        }
        else if (running != wasRunning) RefreshListIps();
    }

    private async Task CompleteSwitchModesAsync(IReadOnlyList<VmNetworkAdapter> snapshot)
    {
        // The display snapshot can require host topology and NAT queries. Let the list
        // and editors work with the authoritative GUID catalog while that read completes.
        List<SwitchInfo> details;
        try { (details, _) = await HyperVSwitchService.GetNetworkInfoAsync(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); return; }
        if (IsStopped || !ReferenceEquals(snapshot, adapters)) return;
        foreach (var item in details)
        {
            if (!string.IsNullOrEmpty(item.StateError) || !Guid.TryParse(item.Id, out var id)) continue;
            string? mode = (item.SwitchType, item.SwitchKind) switch
            {
                (SwitchMode.NAT, _) => "NAT",
                (SwitchMode.Bridge, _) => "外部桥接",
                (SwitchMode.Isolated, HyperVSwitchKind.Internal) => "内部隔离",
                (SwitchMode.Isolated, HyperVSwitchKind.Private) => "专用",
                _ => null
            };
            if (mode != null) switchModes[id] = mode;
        }
        switchModesChanged?.Invoke();
    }

    private void RefreshListIps()
    {
        if (running && ipRead == IpReadState.Pending && adapters.Any(a => a.IsConnected))
        {
            ipRead = IpReadState.Reading;
            _ = CompleteIpsAsync(adapters);
        }
        foreach (var update in updateIpTiles) update();
    }

    private async Task CompleteIpsAsync(IReadOnlyList<VmNetworkAdapter> snapshot)
    {
        // FillDynamicIpsAsync mutates its input. Publish results only for the current boot and snapshot.
        int version = ++ipReadVersion;
        var connected = snapshot.Where(a => a.IsConnected).Select(a => (Adapter: a, Read: a.Clone())).ToArray();
        string? error = null;
        try
        {
            var result = await VmNetworkService.FillDynamicIpsAsync(VmId, connected.Select(pair => pair.Read));
            if (!result.Success) error = CoreMessageResolver.ResolveError(result);
        }
        catch (Exception ex) { error = CoreMessageResolver.ResolveException(ex); }
        if (IsStopped || !ReferenceEquals(snapshot, adapters) || ipRead != IpReadState.Reading || version != ipReadVersion) return;
        foreach (var (adapter, read) in connected) adapter.IpAddresses = read.IpAddresses;
        ipRead = IpReadState.Finished;
        if (error != null) ipWarning = Notice("IP 地址查询未完成", error, InfoBarSeverity.Informational);
        if (policy == null && !adding)
        {
            foreach (var update in updateIpTiles) update();
            if (ipWarning != null) page.Children.Add(ipWarning);
        }
    }

    private void BeginPage(double spacing = 10)
    {
        if (!adding) SetHeaderTrailingAction(null);
        switchModesChanged = null;
        page.Spacing = spacing;
        page.Children.Clear();
        updateIpTiles.Clear();
        if (UncertainOperations.TryGetValue(VmId, out var blocked))
            page.Children.Add(Notice("网卡操作结果待确认", blocked));
        else if (warning != null)
            page.Children.Add(Notice("请核对网卡配置", warning));
    }

    private void BackToList()
    {
        adding = false;
        addDraft = null;
        policy = null;
        RenderList();
        ScrollToTop();
    }

    private void ScrollToTop() => scroll.ChangeView(null, 0, null, disableAnimation: true);

    private void RenderList()
    {
        BeginPage();
        if (loading)
        {
            page.Children.Add(Actions(new ProgressRing { IsActive = true, Width = 20, Height = 20 }, new TextBlock { Text = "正在加载网卡配置…" }));
            return;
        }
        var expandedIds = CardExpansion.GetExpanded(VmId, adapters);
        foreach (var adapter in adapters) page.Children.Add(AdapterCard(adapter, expandedIds.Contains(adapter.DeviceId)));
        if (loadedSnapshot || adapters.Count > 0)
            page.Children.Add(BuildAddRowCard(adapters.Count == 0));
        else if (!loading)
        {
            var retry = Notice("未能加载网卡列表", "读取当前虚拟机网卡配置未完成，可重新加载。", InfoBarSeverity.Informational);
            retry.ActionButton = Command("重新加载", async () => await RunAsync(LoadAsync));
            page.Children.Add(retry);
        }
        if (ipWarning != null) page.Children.Add(ipWarning);
        RefreshListIps();
    }

    private Button BuildAddRowCard(bool isEmpty)
    {
        var rowGrid = new Grid { ColumnSpacing = 12 };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new(7),
            Background = ThemeBrush(this, "SubtleFillColorSecondaryBrush"),
            BorderBrush = ThemeBrush(this, "DividerStrokeColorDefaultBrush"),
            BorderThickness = new(1),
            Child = new FontIcon
            {
                Glyph = "\uE710",
                FontFamily = new FontFamily(SegoeFont),
                FontSize = 16,
                Foreground = ThemeBrush(this, "TextFillColorPrimaryBrush")
            }
        };
        Grid.SetColumn(badge, 0);
        rowGrid.Children.Add(badge);

        var title = new TextBlock
        {
            Text = "添加网卡",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeBrush(this, "TextFillColorPrimaryBrush")
        };
        Grid.SetColumn(title, 1);
        rowGrid.Children.Add(title);

        var button = new Button
        {
            Content = rowGrid,
            Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ThemeBrush(this, isEmpty ? "CardBackgroundFillColorDefaultBrush" : "SubtleFillColorTertiaryBrush"),
            BorderBrush = ThemeBrush(this, "CardStrokeColorDefaultBrush"),
            BorderThickness = new(1),
            CornerRadius = new(8),
            Padding = new(16, isEmpty ? 14 : 12, 16, isEmpty ? 14 : 12),
            MinHeight = isEmpty ? 68 : 60,
            IsEnabled = CanWrite
        };
        button.Click += (_, _) => OpenAdd();
        return button;
    }

    private Border AdapterCard(VmNetworkAdapter adapter, bool expanded)
    {
        var heading = new VmWrapPanel { Spacing = 8 };
        heading.Children.Add(new TextBlock
        {
            Text = adapter.Name, FontSize = 15, FontWeight = FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1
        });
        foreach (string tag in PolicyLabels(adapter, header: true))
        {
            var badge = Badge(this, tag);
            badge.Height = 20;
            badge.Padding = new Thickness(7, 0, 7, 0);
            ((TextBlock)badge.Child).VerticalAlignment = VerticalAlignment.Center;
            heading.Children.Add(badge);
        }
        var subtitle = new TextBlock
        {
            FontSize = 12, Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1
        };
        void UpdateSubtitle()
        {
            subtitle.Text = !adapter.IsConnected ? "未连接"
                : adapter.SwitchId is Guid id && switchModes.TryGetValue(id, out var mode)
                    ? $"{adapter.SwitchName} ({mode})" : adapter.SwitchName;
            ToolTipService.SetToolTip(subtitle, subtitle.Text);
        }
        UpdateSubtitle();
        switchModesChanged += UpdateSubtitle;
        var label = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { heading, subtitle } };
        var configure = IconButton(this, "\uF6AA", "配置端口策略", () => OpenPolicy(adapter), useFluentFont: true);
        var remove = IconButton(this, "\uF60A", "移除网卡", async () => await RunAsync(() => RemoveAsync(adapter)), useFluentFont: true);
        configure.IsEnabled = remove.IsEnabled = CanWrite;
        ((FontIcon)configure.Content).FontSize = ((FontIcon)remove.Content).FontSize = 16;
        configure.Tapped += (_, e) => e.Handled = true;
        remove.Tapped += (_, e) => e.Handled = true;

        var chevronIcon = new FontIcon
        {
            Glyph = expanded ? "\uE70E" : "\uE70D",
            FontFamily = new FontFamily(SegoeFont),
            FontSize = 12,
            Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush")
        };
        var chevron = new Button
        {
            Width = 26, Height = 26, MinWidth = 0, MinHeight = 0, Padding = new(0),
            Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"], Content = chevronIcon
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chevron, expanded ? "折叠网卡详情" : "展开网卡详情");
        var actions = Actions(6, configure, remove, chevron);

        var headerGrid = new Grid { ColumnSpacing = 12 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconBadge = new Border
        {
            Width = 38, Height = 38, CornerRadius = new(7),
            Opacity = adapter.IsConnected ? 1 : 0.62,
            Background = ThemeBrush(this, "SubtleFillColorSecondaryBrush"),
            BorderBrush = ThemeBrush(this, "DividerStrokeColorDefaultBrush"),
            BorderThickness = new(1),
            Child = new FontIcon
            {
                Glyph = "\uE774",
                FontFamily = new FontFamily(SegoeFont),
                FontSize = 17
            }
        };
        Grid.SetColumn(iconBadge, 0);
        Grid.SetColumn(label, 1);
        Grid.SetColumn(actions, 2);
        headerGrid.Children.Add(iconBadge);
        headerGrid.Children.Add(label);
        headerGrid.Children.Add(actions);

        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var hoverBrush = ThemeBrush(this, "SubtleFillColorTertiaryBrush");
        var headerHost = new Border
        {
            Padding = new(16, 12, 16, 12),
            Background = transparent,
            CornerRadius = expanded ? new CornerRadius(7, 7, 0, 0) : new CornerRadius(7),
            Child = headerGrid
        };

        ContentControl IpTile(string ipLabel, AddressFamily family)
        {
            var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            void Update()
            {
                bool readingIps = ipRead == IpReadState.Reading;
                var ips = running && adapter.IsConnected && !readingIps
                    ? adapter.IpAddresses.Where(s => IPAddress.TryParse(s, out var ip) && ip.AddressFamily == family).ToArray()
                    : [];
                string value = ips.Length == 0 ? (readingIps && adapter.IsConnected ? "正在查询…" : "未获取")
                    : family == AddressFamily.InterNetwork ? string.Join("，", ips) : ips[0];
                host.Content = KvTile(this, ipLabel, value, ips.FirstOrDefault(),
                    family == AddressFamily.InterNetwork && ips.Length > 1 ? "复制首个 IPv4 地址" : $"复制 {ipLabel}", ShowError);
            }
            updateIpTiles.Add(Update);
            return host;
        }
        string? mac = adapter.HasReportedMac ? adapter.MacAddress : null;
        List<(FrameworkElement Cell, bool Wide)> tiles =
        [
            (KvTile(this, "网卡类型", adapter.IsLegacy ? "旧版网卡" : "标准网卡"), false),
            (KvTile(this, "MAC 地址", mac ?? "尚未分配", mac, "复制 MAC 地址", ShowError), false),
            (IpTile("IPv4 地址", AddressFamily.InterNetwork), false),
            (IpTile("IPv6 地址", AddressFamily.InterNetworkV6), false)
        ];
        string summary = string.Join("，", PolicyLabels(adapter, header: false));
        if (summary.Length > 0) tiles.Add((KvTile(this, "端口策略", summary), true));
        var details = KvGrid([.. tiles]);

        var divider = new Border
        {
            Height = 1,
            Background = ThemeBrush(this, "DividerStrokeColorDefaultBrush"),
            Visibility = expanded ? Visibility.Visible : Visibility.Collapsed
        };
        var bodyHost = new Border
        {
            Padding = new(16, 12, 16, 14),
            Visibility = expanded ? Visibility.Visible : Visibility.Collapsed,
            Child = details
        };

        void ApplyExpandedVisual(bool next)
        {
            expanded = next;
            CardExpansion.SetExpanded(VmId, adapter.DeviceId, next);
            chevronIcon.Glyph = next ? "\uE70E" : "\uE70D";
            headerHost.CornerRadius = next ? new CornerRadius(7, 7, 0, 0) : new CornerRadius(7);
            divider.Visibility = next ? Visibility.Visible : Visibility.Collapsed;
            bodyHost.Visibility = next ? Visibility.Visible : Visibility.Collapsed;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chevron, next ? "折叠网卡详情" : "展开网卡详情");
            if (next && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            {
                var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(120), FillBehavior = FillBehavior.Stop };
                Storyboard.SetTarget(fade, bodyHost);
                Storyboard.SetTargetProperty(fade, "Opacity");
                new Storyboard { Children = { fade } }.Begin();
            }
        }

        chevron.Click += (_, _) => ApplyExpandedVisual(!expanded);
        headerHost.PointerEntered += (_, _) => headerHost.Background = hoverBrush;
        headerHost.PointerExited += (_, _) => headerHost.Background = transparent;
        headerHost.PointerCanceled += (_, _) => headerHost.Background = transparent;
        headerHost.Tapped += (_, e) =>
        {
            if (e.Handled) return;
            for (var node = e.OriginalSource as DependencyObject; node != null && node != headerHost; node = VisualTreeHelper.GetParent(node))
                if (node is Button) return;
            ApplyExpandedVisual(!expanded);
        };

        return new Border
        {
            Background = ThemeBrush(this, "CardBackgroundFillColorDefaultBrush"),
            BorderBrush = ThemeBrush(this, "CardStrokeColorDefaultBrush"),
            BorderThickness = new(1),
            CornerRadius = new(8),
            Child = new StackPanel { Children = { headerHost, divider, bodyHost } }
        };
    }

    private static IEnumerable<string> PolicyLabels(VmNetworkAdapter a, bool header)
    {
        if (header && a.IsLegacy) yield return "旧版";
        if (a.VlanMode == VlanOperationMode.Access && a.AccessVlanId > 0) yield return $"VLAN {a.AccessVlanId}";
        if (a.VlanMode == VlanOperationMode.Trunk) yield return $"Trunk ({a.NativeVlanId})";
        if (a.VlanMode == VlanOperationMode.Private) yield return $"PVLAN {a.PvlanPrimaryId}";
        if (a.BandwidthLimitBitsPerSecond > 0)
        {
            bool giga = a.BandwidthLimitBitsPerSecond % 1_000_000_000 == 0;
            string number = giga ? (a.BandwidthLimitBitsPerSecond / 1_000_000_000).ToString()
                : VmNetworkPolicyDraft.FormatBandwidthMbps(a.BandwidthLimitBitsPerSecond);
            yield return $"限速 {number}" + (giga ? (header ? " Gbps" : "G") : (header ? " Mbps" : "M"));
        }
        if (a.SriovEnabled) yield return "SR-IOV";
        if (a.MacSpoofingAllowed) yield return "允许 MAC 欺骗";
        if (!header)
        {
            if (a.DhcpGuardEnabled) yield return "DHCP 防护";
            if (a.RouterGuardEnabled) yield return "路由器防护";
            if (a.TeamingAllowed) yield return "网卡组队";
        }
        if (a.MonitorMode == PortMonitorMode.Source) yield return header ? "镜像: 源" : "镜像源";
        if (a.MonitorMode == PortMonitorMode.Destination) yield return header ? "镜像: 目标" : "镜像目标";
        if (!header && a.StormLimit > 0) yield return $"风暴限制 {a.StormLimit} 包/秒";
    }

    private static Button Command(string label, Action click, bool accent = false)
    {
        var button = new Button { Content = label };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => click();
        return button;
    }

    private async Task RemoveAsync(VmNetworkAdapter adapter)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = "移除网卡", Content = $"移除“{adapter.Name}”后，将断开这张网卡的连接。",
            PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || !CanWrite) return;
        var result = await VmNetworkService.RemoveNetworkAdapterAsync(VmId, adapter);
        if (result.Success) CardExpansion.SetExpanded(VmId, adapter.DeviceId, false);
        await FinishWriteAsync($"已移除“{adapter.Name}”", result.Success ? null : CoreMessageResolver.ResolveError(result), result.ErrorKind);
    }

    private async Task FinishWriteAsync(string success, string? error, ApiErrorKind kind)
    {
        adding = false;
        warning = error;
        if (kind == ApiErrorKind.OutcomeUnknown)
            UncertainOperations[VmId] = $"{error}\n底层任务的最终结果尚未确认，已暂停本次程序内对此虚拟机的网卡修改。";
        NotifyChanged();
        try { await LoadAsync(); }
        catch (Exception ex)
        {
            string detail = $"{error ?? success}\n刷新失败：{CoreMessageResolver.ResolveException(ex)}";
            warning = detail;
            RenderList();
            ShowError(detail);
            return;
        }
        if (error != null) ShowError(UncertainOperations.GetValueOrDefault(VmId) ?? error);
        else if (!IsStopped) ShowAppliedToast(success);
    }
}
