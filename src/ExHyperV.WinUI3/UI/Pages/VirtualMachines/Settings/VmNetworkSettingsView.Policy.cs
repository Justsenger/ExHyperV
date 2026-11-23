using System.Globalization;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmNetworkSettingsView
{
    private enum PolicyTab { Connection, Vlan, Security }
    private static readonly int[] BandwidthStops = [0, 1, 5, 10, 20, 50, 100, 200, 300, 500, 1000, 2500, 5000, 10000];
    private static readonly int[] StormStops = [0, 50, 100, 200, 500, 1000, 2000, 5000, 10000];
    private PolicyTab tab;
    private Button? apply;
    private Border? bypassNotice;
    private string bandwidthText = "", stormText = "";

    private void OpenPolicy(VmNetworkAdapter adapter)
    {
        if (!CanWrite) return;
        tab = PolicyTab.Connection;
        SetPolicySnapshot(adapter);
        RenderPolicy();
        ScrollToTop();
    }

    private void SetPolicySnapshot(VmNetworkAdapter adapter)
    {
        policy = new(adapter);
        bandwidthText = VmNetworkPolicyDraft.FormatBandwidthMbps(adapter.BandwidthLimitBitsPerSecond);
        stormText = adapter.StormLimit.ToString(CultureInfo.InvariantCulture);
    }

    private void SelectTab(PolicyTab next)
    {
        tab = next;
        RenderPolicy();
        ScrollToTop();
    }

    private void RenderPolicy()
    {
        if (policy is not { } draft) return;
        BeginPage(16);
        bypassNotice = null;
        apply = Command("应用", async () => await RunAsync(() => ApplyPolicyAsync(draft)), accent: true);
        var header = PivotHeader(this, ((string, string, bool, Action)[])[
            ("\uE71B", "连接", tab == PolicyTab.Connection, () => SelectTab(PolicyTab.Connection)),
            ("\uE8EC", "VLAN", tab == PolicyTab.Vlan, () => SelectTab(PolicyTab.Vlan)),
            ("\uE71C", "策略", tab == PolicyTab.Security, () => SelectTab(PolicyTab.Security))
        ], BackToList, apply, actionBottomMargin: 2);
        header.Padding = new Thickness(0, 0, 0, 1);
        page.Children.Add(header);
        var cards = new StackPanel { Spacing = 12 };
        var group = new StackPanel { Spacing = 8, Children = { cards } };
        page.Children.Add(group);
        switch (tab)
        {
            case PolicyTab.Connection:
                cards.Children.Add(SwitchRow(draft.SwitchId, draft.Saved.SwitchName, id => { draft.SwitchId = id; UpdatePolicy(); }));
                cards.Children.Add(MacRow(draft.Values, UpdatePolicy));
                cards.Children.Add(RateCard(draft, bandwidth: true));
                bypassNotice = InlineBanner("已启用 SR-IOV 硬件直通，出站带宽上限限速可能被硬件通道绕过", InfoBarSeverity.Warning);
                group.Children.Add(bypassNotice);
                break;
            case PolicyTab.Vlan:
                BuildVlan(draft, cards, group);
                break;
            case PolicyTab.Security:
                BuildSecurity(draft, cards, group);
                break;
        }
        UpdatePolicy();
    }

    private void UpdatePolicy()
    {
        if (apply == null || policy is not { } draft) return;
        apply.IsEnabled = CanWrite && (draft.IsDirty || !TryBandwidth(out _) || !TryStorm(out _));
        var a = draft.Values;
        if (bypassNotice != null)
        {
            bool showBypass = tab == PolicyTab.Connection
                ? a.SriovEnabled && a.BandwidthLimitBitsPerSecond > 0
                : a.SriovEnabled && (a.MacSpoofingAllowed || a.TeamingAllowed || a.DhcpGuardEnabled ||
                    a.RouterGuardEnabled || a.MonitorMode != PortMonitorMode.None || a.StormLimit > 0 || a.BandwidthLimitBitsPerSecond > 0);
            bypassNotice.Visibility = showBypass ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private bool TryBandwidth(out ulong bits) => VmNetworkPolicyDraft.TryParseBandwidthMbps(bandwidthText, out bits);

    private bool TryStorm(out uint packets) => uint.TryParse(stormText, NumberStyles.None, CultureInfo.InvariantCulture, out packets);

    private TextBox InlineNumberBox(string initialText, string automationName)
    {
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var box = new TextBox
        {
            Text = initialText,
            FontSize = 22,
            Padding = new Thickness(2, 0, 2, 0),
            MinWidth = 24, MaxWidth = 84, MinHeight = 30, Height = 30,
            TextAlignment = TextAlignment.Right,
            BorderThickness = new Thickness(0),
            BorderBrush = transparent,
            Background = transparent,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        box.Resources["TextControlBackground"] = transparent;
        box.Resources["TextControlBackgroundDisabled"] = transparent;
        box.Resources["TextControlBackgroundPointerOver"] = transparent;
        box.Resources["TextControlBackgroundFocused"] = ThemeBrush(this, "ControlFillColorTertiaryBrush");
        foreach (string state in new[] { "", "PointerOver", "Focused", "Disabled" })
            box.Resources["TextControlBorderBrush" + state] = transparent;
        AttachCompactTextBehavior(box);
        AutomationProperties.SetName(box, automationName);
        return box;
    }

    private Border EnergyCard(string title, string help, FrameworkElement value, SettingsEnergySlider slider)
    {
        var head = Split(SettingLabel(title, help), value);
        head.ColumnSpacing = 10;
        var content = new Grid { RowSpacing = 16 };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(head);
        Grid.SetRow(slider, 1);
        slider.VerticalAlignment = VerticalAlignment.Bottom;
        slider.HorizontalAlignment = HorizontalAlignment.Stretch;
        content.Children.Add(slider);
        return PortCard(content, energy: true);
    }

    // The shared slider exposes integer positions. Subdividing each interval lets
    // a typed value sit between stops without rounding the saved bit rate or PPS.
    private const int PositionsPerStop = 1000;

    private static int RatePosition(int[] stops, decimal amount)
    {
        for (int i = 1; i < stops.Length; i++)
        {
            if (amount > stops[i]) continue;
            decimal fraction = (amount - stops[i - 1]) / (stops[i] - stops[i - 1]);
            return (i - 1) * PositionsPerStop + (int)Math.Round(fraction * PositionsPerStop);
        }
        return (stops.Length - 1) * PositionsPerStop;
    }

    private static void BindSliderSteps(SettingsEnergySlider slider, int min, int max, int stride, Action<int> edit)
    {
        slider.ValueChanged += edit;
        slider.PreviewKeyDown += (_, e) =>
        {
            int current = (int)Math.Round((double)slider.Value / stride, MidpointRounding.AwayFromZero);
            int? next = e.Key switch
            {
                VirtualKey.Left or VirtualKey.Down => current - 1,
                VirtualKey.Right or VirtualKey.Up => current + 1,
                VirtualKey.PageDown => current - 10,
                VirtualKey.PageUp => current + 10,
                VirtualKey.Home => min,
                VirtualKey.End => max,
                _ => null
            };
            if (next is not int step) return;
            e.Handled = true;
            slider.Value = Math.Clamp(step, min, max) * stride;
            edit(slider.Value);
        };
    }

    private Border RateCard(VmNetworkPolicyDraft draft, bool bandwidth)
    {
        var stops = bandwidth ? BandwidthStops : StormStops;
        string unit = bandwidth ? "Mbps" : "包/秒";
        string zero = bandwidth ? "不限速" : "不限制";
        string title = bandwidth ? "出站带宽上限" : "风暴限制";
        string help = bandwidth ? "限制此网卡向外部发送数据的最大速率，0 表示不限速" : "限制每秒广播、组播与未知单播风暴流量上限，0 表示不限制";
        decimal Amount() => bandwidth ? draft.Values.BandwidthLimitBitsPerSecond / 1_000_000m : draft.Values.StormLimit;
        var box = InlineNumberBox(bandwidth ? bandwidthText : stormText, title + "数值");
        var slider = new SettingsEnergySlider(EnergySliderKind.Custom, compact: false);
        slider.SetRange(0, (stops.Length - 1) * PositionsPerStop);
        slider.SnapValue = pos => (pos + PositionsPerStop / 2) / PositionsPerStop * PositionsPerStop;
        slider.CustomHintFormatter = _ => Amount() == 0 ? zero : $"{Amount():0.######} {unit}";
        AutomationProperties.SetName(slider, title);
        var units = new TextBlock
        {
            Text = unit,
            FontSize = 14,
            Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        };
        bool syncing = false;
        slider.Value = RatePosition(stops, Amount());
        box.TextChanged += (_, _) =>
        {
            if (syncing) return;
            syncing = true;
            try
            {
                if (bandwidth)
                {
                    bandwidthText = box.Text.Trim();
                    if (TryBandwidth(out ulong bits)) draft.Values.BandwidthLimitBitsPerSecond = bits;
                }
                else
                {
                    stormText = box.Text.Trim();
                    if (TryStorm(out uint packets)) draft.Values.StormLimit = packets;
                }
                slider.Value = RatePosition(stops, Amount());
            }
            finally { syncing = false; }
            UpdatePolicy();
        };
        BindSliderSteps(slider, 0, stops.Length - 1, PositionsPerStop, position =>
        {
            if (syncing) return;
            syncing = true;
            try
            {
                int stop = stops[position / PositionsPerStop];
                string nextText = stop.ToString(CultureInfo.InvariantCulture);
                box.Text = nextText;
                if (bandwidth)
                {
                    bandwidthText = nextText;
                    draft.Values.BandwidthLimitBitsPerSecond = (ulong)stop * 1_000_000UL;
                }
                else
                {
                    stormText = nextText;
                    draft.Values.StormLimit = (uint)stop;
                }
            }
            finally { syncing = false; }
            UpdatePolicy();
        });
        var valueStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { box, units }
        };
        return EnergyCard(title, help, valueStack, slider);
    }

    private FrameworkElement PillCard<T>(string title, string help, T selected, Action<T> change, params (T Value, string Label)[] choices)
        where T : struct, Enum
    {
        var pill = new SettingsSegmentedPill { Width = choices.Length * 80 };
        if (pill.Content is Border track) track.Height = 38;
        pill.SetOptions(choices.Select(item => new SettingOption(item.Value, item.Label)));
        pill.SelectedValue = selected;
        pill.SelectionChanged += value => { if (value is T next) change(next); };
        return PortSetting(title, help, pill);
    }

    private void BuildVlan(VmNetworkPolicyDraft draft, StackPanel cards, StackPanel group)
    {
        var a = draft.Values;
        cards.Children.Add(PillCard("VLAN 模式", "划分虚拟局域网隔离广播域，支持 Access、Trunk 与 PVLAN 模式", a.VlanMode,
            mode => { a.VlanMode = mode; RenderPolicy(); },
            (VlanOperationMode.Off, "关闭"), (VlanOperationMode.Access, "Access"), (VlanOperationMode.Trunk, "Trunk"), (VlanOperationMode.Private, "PVLAN")));
        if (a.VlanMode == VlanOperationMode.Off) return;

        var notice = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void UpdateNotice()
        {
            string? message = null;
            var severity = InfoBarSeverity.Informational;
            bool promiscuous = a.VlanMode == VlanOperationMode.Private && a.PvlanMode == PvlanMode.Promiscuous;
            bool gatewayHint = a.VlanMode == VlanOperationMode.Private && !promiscuous;
            if (a.VlanMode == VlanOperationMode.Trunk && !VmNetworkPolicyDraft.TryParseVlanList(draft.AllowedVlans, out _))
            {
                message = "VLAN 列表须使用 1 至 4094 的编号或升序区间，例如 10, 20, 30-40";
                severity = InfoBarSeverity.Error;
            }
            else if (draft.SwitchId is not Guid id) message = "未连接虚拟交换机，策略将在接入后生效";
            else if (switchModes.GetValueOrDefault(id) == "NAT")
            {
                message = "NAT 交换机开启 VLAN 后无法访问外网";
                severity = InfoBarSeverity.Warning;
            }
            else if (switchModes.GetValueOrDefault(id) == "外部桥接") message = "需物理网卡与上游交换机放行对应 VLAN";
            else if (a.VlanMode == VlanOperationMode.Trunk) message = "须在虚拟机系统内配置对应 VLAN 子接口";
            else if (gatewayHint) message = "隔离与团体端口需同主 VLAN 混杂端口才可通网关";

            notice.Content = message == null ? null : InlineBanner(message, severity);
            notice.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        }

        switch (a.VlanMode)
        {
            case VlanOperationMode.Access:
                cards.Children.Add(VlanIdCard("接入 VLAN ID", "该端口仅收发此单一 VLAN 标签流量", a.AccessVlanId, id => a.AccessVlanId = id));
                break;
            case VlanOperationMode.Trunk:
                cards.Children.Add(VlanIdCard("本征 VLAN", "未标记帧所属的默认 VLAN", a.NativeVlanId, id => a.NativeVlanId = id));
                cards.Children.Add(VlanListCard("允许的 VLAN 列表", draft.AllowedVlans, value => { draft.AllowedVlans = value; UpdateNotice(); }));
                break;
            case VlanOperationMode.Private:
                cards.Children.Add(PillCard("专用 VLAN 端口角色", "隔离端口仅可与混杂端口通信，团体端口可在同团体内互访", a.PvlanMode,
                    role => { a.PvlanMode = role; RenderPolicy(); },
                    (PvlanMode.Isolated, "隔离"), (PvlanMode.Community, "团体"), (PvlanMode.Promiscuous, "混杂")));
                var primary = VlanIdCard("主 VLAN ID", "指定主 VLAN 编号", a.PvlanPrimaryId, id => a.PvlanPrimaryId = id);
                if (a.PvlanMode == PvlanMode.Promiscuous)
                {
                    cards.Children.Add(primary);
                }
                else
                {
                    var secondary = VlanIdCard("辅助 VLAN ID", "指定次级隔离或团体 VLAN 编号", a.PvlanSecondaryId, id => a.PvlanSecondaryId = id);
                    cards.Children.Add(new SettingsFieldPanel { Spacing = 12, Children = { primary, secondary } });
                }
                break;
            default:
                group.Children.Add(InlineBanner("无法识别当前 VLAN 模式，选择模式后可以重新配置此端口", InfoBarSeverity.Warning));
                return;
        }
        UpdateNotice();
        switchModesChanged += UpdateNotice;
        group.Children.Add(notice);
    }

    private Border VlanIdCard(string title, string help, int current, Action<int> set)
    {
        var slider = new SettingsEnergySlider(EnergySliderKind.Custom, compact: false);
        slider.SetRange(1, 4094);
        slider.CustomHintFormatter = value => $"VLAN {value}";
        slider.Value = current > 0 ? current : 1;
        AutomationProperties.SetName(slider, title);
        var box = InlineNumberBox(current.ToString(CultureInfo.InvariantCulture), title + "数值");
        int ReadId() => int.TryParse(box.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, 1, 4094) : 1;
        void SetId(int id)
        {
            current = id;
            set(id);
            slider.Value = id;
            UpdatePolicy();
        }
        box.TextChanged += (_, _) =>
        {
            // Hyper-V can report an untagged native VLAN (0). Preserve that snapshot until edited.
            if (box.Text != current.ToString(CultureInfo.InvariantCulture)) SetId(ReadId());
        };
        box.LostFocus += (_, _) => box.Text = current.ToString(CultureInfo.InvariantCulture);
        BindSliderSteps(slider, 1, 4094, 1, id =>
        {
            SetId(id);
            box.Text = id.ToString(CultureInfo.InvariantCulture);
        });
        return EnergyCard(title, help + "，有效范围 1 至 4094", box, slider);
    }

    private FrameworkElement VlanListCard(string title, string text, Action<string> set)
    {
        var input = CompactTextBox(text, "10, 20-30", 240, height: 32);
        input.TextChanged += (_, _) => { set(input.Text); UpdatePolicy(); };
        return PortSetting(title, "支持逗号与区间，例如 10, 20, 30-40", input);
    }

    private string? IovLock(VmNetworkPolicyDraft draft)
    {
        if (draft.Values.IsLegacy) return "旧版网卡不支持 SR-IOV。";
        if (draft.SwitchId is not Guid id) return "请先连接支持 SR-IOV 的虚拟交换机。";
        return iovCapacities.TryGetValue(id, out var capacity) && capacity == 0
            ? "所选交换机报告的虚拟功能容量为 0，无法启用 SR-IOV。" : null;
    }

    private void BuildSecurity(VmNetworkPolicyDraft draft, StackPanel cards, StackPanel group)
    {
        var a = draft.Values;
        FrameworkElement ToggleCard(string title, string help, bool value, Action<bool> set, string? lockReason = null)
        {
            var toggle = PortSwitch();
            toggle.IsOn = value;
            toggle.IsEnabled = lockReason == null;
            toggle.Toggled += (_, _) => { set(toggle.IsOn); UpdatePolicy(); };
            return PortSetting(title, help, toggle, lockReason);
        }
        cards.Children.Add(new SettingsFieldPanel
        {
            Spacing = 12,
            Children =
            {
                ToggleCard("允许 MAC 欺骗", "允许虚拟机修改发出帧的源 MAC 地址，软路由、嵌套虚拟化或负载均衡场景需开启", a.MacSpoofingAllowed, value => a.MacSpoofingAllowed = value),
                ToggleCard("允许网卡组队", "仅开启组队权限，需进入虚拟机系统内将此网卡与同一台虚拟机的其他网卡绑定聚合，不可在此选择成员", a.TeamingAllowed, value => a.TeamingAllowed = value),
                ToggleCard("DHCP 防护", "拦截来自该虚拟机的非法 DHCP 响应报文，防止局域网地址欺骗", a.DhcpGuardEnabled, value => a.DhcpGuardEnabled = value),
                ToggleCard("路由器防护", "拦截来自该虚拟机的非法 IPv6 路由器通告与重定向报文", a.RouterGuardEnabled, value => a.RouterGuardEnabled = value)
            }
        });
        cards.Children.Add(PillCard("端口镜像", "将此网卡设为端口流量抓包监控的源端口或接收镜像的目标端口",
            a.MonitorMode, mode => { a.MonitorMode = mode; UpdatePolicy(); },
            (PortMonitorMode.None, "禁用"), (PortMonitorMode.Source, "源端口"), (PortMonitorMode.Destination, "目标端口")));
        cards.Children.Add(RateCard(draft, bandwidth: false));

        string? reason = IovLock(draft);
        string help = "为虚拟机直通物理网卡硬件通道，需物理网卡与虚拟交换机同时支持，启用后将绕过软交换机策略、镜像与限速";
        if (draft.SwitchId is Guid id && !iovCapacities.ContainsKey(id))
            help += "。无法读取所选交换机的硬件能力，实际支持情况由 Hyper-V 确认。";
        // A configured synthetic adapter can still turn SR-IOV off after losing a capable switch.
        bool canClear = !a.IsLegacy && a.SriovEnabled && reason != null;
        if (canClear) help += " " + reason;
        cards.Children.Add(ToggleCard("SR-IOV 硬件直通", help, a.SriovEnabled,
            value => { a.SriovEnabled = value; if (canClear) RenderPolicy(); }, canClear ? null : reason));
        bypassNotice = InlineBanner("SR-IOV 硬件直通可能绕过虚拟交换机的软件策略、镜像与流量限制", InfoBarSeverity.Warning);
        group.Children.Add(bypassNotice);
    }

    private Border InlineBanner(string message, InfoBarSeverity severity)
    {
        var (background, accent, glyph) = severity switch
        {
            InfoBarSeverity.Warning => ("InfoBarWarningSeverityBackgroundBrush", "SystemFillColorCautionBrush", "\uE7BA"),
            InfoBarSeverity.Error => ("InfoBarErrorSeverityBackgroundBrush", "SystemFillColorCriticalBrush", "\uE711"),
            _ => ("InfoBarInformationalSeverityBackgroundBrush", "SystemFillColorAttentionBrush", "\uE946")
        };
        var badge = new Border
        {
            Width = 18, Height = 18, CornerRadius = new(9), VerticalAlignment = VerticalAlignment.Center,
            Background = ThemeBrush(this, accent),
            Child = new FontIcon { Glyph = glyph, FontFamily = new FontFamily(SegoeFont), FontSize = 10,
                Foreground = ThemeBrush(this, "TextOnAccentFillColorPrimaryBrush") }
        };
        var text = new TextBlock
        {
            Text = message, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        };
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        row.Children.Add(badge);
        row.Children.Add(text);
        var banner = new Border
        {
            MinHeight = 36, Margin = new(0, 2, 0, 0), Padding = new(12, 7, 12, 7), CornerRadius = new(6),
            BorderThickness = new(1), BorderBrush = ThemeBrush(this, "CardStrokeColorDefaultBrush"),
            Background = ThemeBrush(this, background), Child = row
        };
        AutomationProperties.SetName(banner, message);
        AutomationProperties.SetLiveSetting(banner, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        return banner;
    }

    private async Task ApplyPolicyAsync(VmNetworkPolicyDraft draft)
    {
        if (!CanWrite || !ReferenceEquals(policy, draft)) return;
        var a = draft.Values;
        void Invalid(PolicyTab section, string message)
        {
            SelectTab(section);
            ShowError(message);
        }

        string? mac = a.IsStaticMac ? VmNetworkService.NormalizeStaticMac(a.MacAddress) : "";
        if (draft.MacChanged && (MacLock != null || mac == null))
        {
            Invalid(PolicyTab.Connection, MacLock ?? "请输入有效的静态 MAC 地址，例如 " + MacExample + "。");
            return;
        }
        if (!TryBandwidth(out ulong bits) || draft.BandwidthChanged && bits > 0 && bits < a.BandwidthReservationBitsPerSecond)
        {
            Invalid(PolicyTab.Connection, "请输入有效的 Mbps 数值，出站带宽上限不能低于已配置的带宽保留值。");
            return;
        }
        var vlan = draft.VlanChanges();
        if (vlan.Count > 0 && draft.PrepareVlan() is string vlanError) { Invalid(PolicyTab.Vlan, vlanError); return; }
        if (!TryStorm(out _)) { Invalid(PolicyTab.Security, "风暴限制须为 0 至 4294967295 之间的整数。"); return; }
        if (a.SriovEnabled && (!draft.Saved.SriovEnabled || draft.ConnectionChanged) && IovLock(draft) is string iovError)
        {
            Invalid(PolicyTab.Security, iovError);
            return;
        }

        var steps = new List<(string Label, Func<Task<ApiResponse>> Write)>();
        if (draft.ConnectionChanged)
        {
            a.IsConnected = draft.SwitchId.HasValue;
            steps.Add(("交换机连接", () => VmNetworkService.UpdateConnectionAsync(VmId, a, draft.SwitchId)));
        }
        if (draft.MacChanged) steps.Add(("MAC 地址", () => VmNetworkService.SetMacAddressAsync(VmId, a, mac!)));
        if (vlan.Count > 0) steps.Add(("VLAN", () => VmNetworkService.ApplyVlanSettingsAsync(VmId, a, vlan)));
        if (draft.BandwidthChanged) steps.Add(("出站带宽上限", () => VmNetworkService.ApplyBandwidthSettingsAsync(VmId, a, (string[])[nameof(a.BandwidthLimitBitsPerSecond)])));

        var completed = new List<string>();
        var security = draft.SecurityChanges();
        if (security.Count > 0)
        {
            steps.Add(("安全与硬件直通设置", async () =>
            {
                var result = await VmNetworkService.ApplySecurityAndOffloadSettingsWithResultAsync(VmId, a, security);
                if (result.Success) return ApiResponse.Ok();
                if (result.SecurityApplied) completed.Add("端口安全策略");
                return ApiResponse.Fail(result.Message, result.Failure?.Code ?? -1, result.Failure?.Source ?? ApiErrorSource.None,
                    kind: result.Failure?.Kind ?? ApiErrorKind.OperationFailed);
            }));
        }
        foreach (var (label, write) in steps)
        {
            ApiResponse result;
            try { result = await write(); }
            catch (Exception ex) { result = ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OutcomeUnknown); }
            if (!result.Success)
            {
                string applied = completed.Count == 0 ? "" : $"已应用{string.Join("、", completed)}。\n";
                await FinishWriteAsync("", $"{applied}{label}未完成。{CoreMessageResolver.ResolveError(result)}", result.ErrorKind);
                return;
            }
            completed.Add(label);
        }
        await FinishWriteAsync($"已应用“{a.Name}”的端口策略", null, ApiErrorKind.None);
    }
}
