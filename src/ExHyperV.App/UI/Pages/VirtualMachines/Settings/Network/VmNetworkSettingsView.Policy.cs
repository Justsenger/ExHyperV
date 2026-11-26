using System.Globalization;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using static ExHyperV.App.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Network;

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
        apply = Command(L10n.Get("Common_Apply"), async () => await RunAsync(() => ApplyPolicyAsync(draft)), accent: true);
        var header = PivotHeader(this, ((string, string, bool, Action)[])[
            ("\uE71B", L10n.Get("Dash_Connect"), tab == PolicyTab.Connection, () => SelectTab(PolicyTab.Connection)),
            ("\uE8EC", "VLAN", tab == PolicyTab.Vlan, () => SelectTab(PolicyTab.Vlan)),
            ("\uE71C", L10n.Get("Vtpm_Policy"), tab == PolicyTab.Security, () => SelectTab(PolicyTab.Security))
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
                bypassNotice = InlineBanner(L10n.Get("Net_SriovBandwidthCaution"), InfoBarSeverity.Warning);
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
        box.Resources["TextControlBackgroundFocused"] = ThemeHelper.ThemeBrush(this, "ControlFillColorTertiaryBrush");
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
        string unit = bandwidth ? "Mbps" : L10n.Get("Net_PacketsPerSecond");
        string zero = bandwidth ? L10n.Get("Net_UnlimitedRate") : L10n.Get("Net_NoLimit");
        string title = bandwidth ? L10n.Get("Net_OutboundLimit") : L10n.Get("Net_StormLimit");
        string help = bandwidth ? L10n.Get("Net_OutboundLimitHelp") : L10n.Get("Net_StormLimitHelp");
        decimal Amount() => bandwidth ? draft.Values.BandwidthLimitBitsPerSecond / 1_000_000m : draft.Values.StormLimit;
        var box = InlineNumberBox(bandwidth ? bandwidthText : stormText, L10n.Format("Net_FieldValue", title));
        var slider = new SettingsEnergySlider(EnergySliderKind.Custom, compact: false);
        slider.SetRange(0, (stops.Length - 1) * PositionsPerStop);
        slider.SnapValue = pos => (pos + PositionsPerStop / 2) / PositionsPerStop * PositionsPerStop;
        slider.CustomHintFormatter = _ => Amount() == 0 ? zero : $"{Amount():0.######} {unit}";
        AutomationProperties.SetName(slider, title);
        var units = new TextBlock
        {
            Text = unit,
            FontSize = 14,
            Foreground = ThemeHelper.ThemeBrush(this, "TextFillColorSecondaryBrush"),
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
        cards.Children.Add(PillCard(L10n.Get("Net_VlanMode"), L10n.Get("Net_VlanModeHelp"), a.VlanMode,
            mode => { a.VlanMode = mode; RenderPolicy(); },
            (VlanOperationMode.Off, L10n.Get("Common_Off")), (VlanOperationMode.Access, "Access"), (VlanOperationMode.Trunk, "Trunk"), (VlanOperationMode.Private, "PVLAN")));
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
                message = L10n.Get("Net_VlanListHelp");
                severity = InfoBarSeverity.Error;
            }
            else if (draft.SwitchId is not Guid id) message = L10n.Get("Net_VlanDisconnected");
            else if (switchModes.GetValueOrDefault(id).Mode == SwitchMode.NAT)
            {
                message = L10n.Get("Net_NatVlanWarning");
                severity = InfoBarSeverity.Warning;
            }
            else if (switchModes.GetValueOrDefault(id).Mode == SwitchMode.Bridge) message = L10n.Get("Net_BridgeVlanWarning");
            else if (a.VlanMode == VlanOperationMode.Trunk) message = L10n.Get("Net_TrunkVlanWarning");
            else if (gatewayHint) message = L10n.Get("Net_PrivateVlanWarning");

            notice.Content = message == null ? null : InlineBanner(message, severity);
            notice.Visibility = message == null ? Visibility.Collapsed : Visibility.Visible;
        }

        switch (a.VlanMode)
        {
            case VlanOperationMode.Access:
                cards.Children.Add(VlanIdCard(L10n.Get("Net_AccessVlanId"), L10n.Get("Net_AccessVlanHelp"), a.AccessVlanId, id => a.AccessVlanId = id));
                break;
            case VlanOperationMode.Trunk:
                cards.Children.Add(VlanIdCard(L10n.Get("Net_NativeVlan"), L10n.Get("Net_NativeVlanHelp"), a.NativeVlanId, id => a.NativeVlanId = id));
                cards.Children.Add(VlanListCard(L10n.Get("Net_AllowedVlans"), draft.AllowedVlans, value => { draft.AllowedVlans = value; UpdateNotice(); }));
                break;
            case VlanOperationMode.Private:
                cards.Children.Add(PillCard(L10n.Get("Net_PrivateVlanRole"), L10n.Get("Net_PrivateVlanRoleHelp"), a.PvlanMode,
                    role => { a.PvlanMode = role; RenderPolicy(); },
                    (PvlanMode.Isolated, L10n.Get("Net_IsolatedPort")), (PvlanMode.Community, L10n.Get("Net_CommunityPort")), (PvlanMode.Promiscuous, L10n.Get("Net_PromiscuousPort"))));
                var primary = VlanIdCard(L10n.Get("Net_PrimaryVlanId"), L10n.Get("Net_PrimaryVlanHelp"), a.PvlanPrimaryId, id => a.PvlanPrimaryId = id);
                if (a.PvlanMode == PvlanMode.Promiscuous)
                {
                    cards.Children.Add(primary);
                }
                else
                {
                    var secondary = VlanIdCard(L10n.Get("Net_SecondaryVlanId"), L10n.Get("Net_SecondaryVlanHelp"), a.PvlanSecondaryId, id => a.PvlanSecondaryId = id);
                    cards.Children.Add(new SettingsFieldPanel { Spacing = 12, Children = { primary, secondary } });
                }
                break;
            default:
                group.Children.Add(InlineBanner(L10n.Get("Net_UnknownVlanMode"), InfoBarSeverity.Warning));
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
        var box = InlineNumberBox(current.ToString(CultureInfo.InvariantCulture), L10n.Format("Net_FieldValue", title));
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
        return EnergyCard(title, L10n.Format("Net_VlanRangeHelp", help), box, slider);
    }

    private FrameworkElement VlanListCard(string title, string text, Action<string> set)
    {
        var input = CompactTextBox(text, "10, 20-30", 240, height: 32);
        input.TextChanged += (_, _) => { set(input.Text); UpdatePolicy(); };
        return PortSetting(title, L10n.Get("Net_VlanRangeExamples"), input);
    }

    private string? IovLock(VmNetworkPolicyDraft draft)
    {
        if (draft.Values.IsLegacy) return L10n.Get("Net_LegacyNoSriov");
        if (draft.SwitchId is not Guid id) return L10n.Get("Net_SriovSwitchRequired");
        return iovCapacities.TryGetValue(id, out var capacity) && capacity == 0
            ? L10n.Get("Net_NoVirtualFunctions") : null;
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
                ToggleCard(L10n.Get("Net_AllowMacSpoofing"), L10n.Get("Net_MacSpoofingHelp"), a.MacSpoofingAllowed, value => a.MacSpoofingAllowed = value),
                ToggleCard(L10n.Get("Net_AllowTeaming"), L10n.Get("Net_TeamingHelp"), a.TeamingAllowed, value => a.TeamingAllowed = value),
                ToggleCard(L10n.Get("Net_DhcpGuard"), L10n.Get("Net_DhcpGuardHelp"), a.DhcpGuardEnabled, value => a.DhcpGuardEnabled = value),
                ToggleCard(L10n.Get("Net_RouterGuard"), L10n.Get("Net_RouterGuardHelp"), a.RouterGuardEnabled, value => a.RouterGuardEnabled = value)
            }
        });
        cards.Children.Add(PillCard(L10n.Get("Net_PortMirroring"), L10n.Get("Net_PortMirroringHelp"),
            a.MonitorMode, mode => { a.MonitorMode = mode; UpdatePolicy(); },
            (PortMonitorMode.None, L10n.Get("Common_Disable")), (PortMonitorMode.Source, L10n.Get("Net_SourcePort")), (PortMonitorMode.Destination, L10n.Get("Net_DestinationPort"))));
        cards.Children.Add(RateCard(draft, bandwidth: false));

        string? reason = IovLock(draft);
        string help = L10n.Get("Net_SriovHelp");
        if (draft.SwitchId is Guid id && !iovCapacities.ContainsKey(id))
            help += L10n.Get("Net_UnknownHardwareHelp");
        // A configured synthetic adapter can still turn SR-IOV off after losing a capable switch.
        bool canClear = !a.IsLegacy && a.SriovEnabled && reason != null;
        if (canClear) help += " " + reason;
        cards.Children.Add(ToggleCard(L10n.Get("Net_Sriov"), help, a.SriovEnabled,
            value => { a.SriovEnabled = value; if (canClear) RenderPolicy(); }, canClear ? null : reason));
        bypassNotice = InlineBanner(L10n.Get("Net_SriovWarning"), InfoBarSeverity.Warning);
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
            Background = ThemeHelper.ThemeBrush(this, accent),
            Child = new FontIcon { Glyph = glyph, FontFamily = AppFonts.SegoeFluent, FontSize = 10,
                Foreground = ThemeHelper.ThemeBrush(this, "TextOnAccentFillColorPrimaryBrush") }
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
            BorderThickness = new(1), BorderBrush = ThemeHelper.ThemeBrush(this, "CardStrokeColorDefaultBrush"),
            Background = ThemeHelper.ThemeBrush(this, background), Child = row
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
            Invalid(PolicyTab.Connection, MacLock ?? L10n.Format("Net_EnterStaticMacExample", MacExample));
            return;
        }
        if (!TryBandwidth(out ulong bits) || draft.BandwidthChanged && bits > 0 && bits < a.BandwidthReservationBitsPerSecond)
        {
            Invalid(PolicyTab.Connection, L10n.Get("Net_InvalidBandwidth"));
            return;
        }
        var vlan = draft.VlanChanges();
        if (vlan.Count > 0 && draft.PrepareVlan() is string vlanError) { Invalid(PolicyTab.Vlan, L10n.Get(vlanError)); return; }
        if (!TryStorm(out _)) { Invalid(PolicyTab.Security, L10n.Get("Net_InvalidStormLimit")); return; }
        if (a.SriovEnabled && (!draft.Saved.SriovEnabled || draft.ConnectionChanged) && IovLock(draft) is string iovError)
        {
            Invalid(PolicyTab.Security, iovError);
            return;
        }

        var steps = new List<(string Label, Func<Task<ApiResponse>> Write)>();
        if (draft.ConnectionChanged)
        {
            a.IsConnected = draft.SwitchId.HasValue;
            steps.Add((L10n.Get("Net_SwitchConnection"), () => VmNetworkService.UpdateConnectionAsync(VmId, a, draft.SwitchId)));
        }
        if (draft.MacChanged) steps.Add((L10n.Get("Dev_Mac"), () => VmNetworkService.SetMacAddressAsync(VmId, a, mac!)));
        if (vlan.Count > 0) steps.Add(("VLAN", () => VmNetworkService.ApplyVlanSettingsAsync(VmId, a, vlan)));
        if (draft.BandwidthChanged) steps.Add((L10n.Get("Net_OutboundLimit"), () => VmNetworkService.ApplyBandwidthSettingsAsync(VmId, a, (string[])[nameof(a.BandwidthLimitBitsPerSecond)])));

        var completed = new List<string>();
        var security = draft.SecurityChanges();
        if (security.Count > 0)
        {
            steps.Add((L10n.Get("Net_SecurityAndPassthrough"), async () =>
            {
                var result = await VmNetworkService.ApplySecurityAndOffloadSettingsWithResultAsync(VmId, a, security);
                if (result.Success) return ApiResponse.Ok();
                if (result.SecurityApplied) completed.Add(L10n.Get("Net_PortSecurity"));
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
                string applied = completed.Count == 0 ? "" : L10n.Format("Net_AppliedStep", string.Join("、", completed));
                await FinishWriteAsync("", L10n.Format("Net_StepIncomplete", applied, label, CoreMessageResolver.ResolveError(result)), result.ErrorKind);
                return;
            }
            completed.Add(label);
        }
        await FinishWriteAsync(L10n.Format("Net_PortPolicyApplied", a.Name), null, ApiErrorKind.None);
    }
}
