
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed partial class HostStatusView
{
    private void AddNicItem(HostInventorySnapshot inventory, SwitchUpstreamAdapter nic)
    {
        string key = $"nic:{nic.ConnectionId:N}";
        Color nicColor = HostUiHelpers.MediaColor(nic.MediaKind);
        string mediaTitle = HostUiHelpers.FormatMediaTitle(nic.MediaKind);

        var spark = new VmMiniSparkline(transfer: true);
        var title = new TextBlock { Text = mediaTitle, FontSize = 13, FontWeight = FontWeights.Medium };
        var sub = new TextBlock { Text = HostUiHelpers.MiddleTrim(nic.Name, 22), FontSize = 11.5, TextTrimming = TextTrimming.Clip };
        var stat = new TextBlock { Text = "—", FontSize = 11.5 };
        Typography.SetNumeralAlignment(stat, FontNumeralAlignment.Tabular);

        var item = CreateRailEntry(
            key: key,
            category: RailCategory.Nic,
            detailTitle: nic.Name,
            detailSubtitle: nic.Description,
            detailGlyph: HostUiHelpers.MediaGlyph(nic.MediaKind),
            detailGlyphRotation: 0,
            categoryColor: nicColor,
            sparkline: spark,
            titleText: title,
            subtitleText: sub,
            statText: stat,
            tooltip: $"{nic.Name} ({mediaTitle})\n{nic.Description}");

        var sendChart = new VmUsageChart(L10n.Get("Host_Net_Send"), VmUsageChartKind.Transfer, chartHeight: 136);
        var recvChart = new VmUsageChart(L10n.Get("Host_Net_Recv"), VmUsageChartKind.Transfer, chartHeight: 136);
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(sendChart, 0);
        Grid.SetColumn(recvChart, 1);
        grid.Children.Add(sendChart);
        grid.Children.Add(recvChart);

        var boundSwitch = inventory.Switches.FirstOrDefault(s => s.Upstream?.ConnectionId == nic.ConnectionId);
        string boundText = boundSwitch != null
            ? $"{boundSwitch.SwitchName} ({HostUiHelpers.FormatSwitchMode(boundSwitch.SwitchType)})"
            : L10n.Get("Host_Nic_Idle");

        inventory.AdapterAddresses.TryGetValue(nic.ConnectionId, out var addr);
        string ipv4 = string.IsNullOrWhiteSpace(addr?.IPv4) ? "---" : addr.IPv4;
        string mac = string.IsNullOrWhiteSpace(addr?.Mac) ? "---" : HostUiHelpers.FormatMacWithDashes(addr.Mac);
        string speedText = HostUiHelpers.FormatLinkSpeed(nic.LinkSpeed);
        string mediaBridge = $"{mediaTitle}，{L10n.Get(nic.CanBridge ? "Host_Nic_CanBridge" : "Host_Nic_NatOnly")}";

        var kvBorder = new Border();
        var panel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        var scaleHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(scaleHost);
        panel.Children.Add(grid);
        panel.Children.Add(kvBorder);
        detailBodyHost.Children.Add(panel);

        void RenderNicKv()
        {
            scaleHost.Content = HostUiHelpers.BuildScaleBar(ActualTheme, L10n.Get("Host_Nic_Scale"), speedText);
            kvBorder.Child = HostUiHelpers.BuildTitleFirstKvGrid(ActualTheme,
            (HostUiHelpers.KvTileSpec[])[
                new(L10n.Get("Host_Nic_LinkState"), L10n.Format("Host_Nic_Connected", speedText)),
                new(L10n.Get("Host_Nic_MediaBridge"), mediaBridge),
                new(L10n.Get("Host_Nic_Binding"), boundText),
                new(L10n.Get("Host_Nic_Ipv4"), ipv4, CopyValue: ipv4 != "---" ? ipv4 : null),
                new(L10n.Get("Host_Nic_Mac"), mac, CopyValue: mac != "---" ? mac : null),
                new(L10n.Get("Host_Nic_AdapterName"), nic.Description, Wide: true, CopyValue: nic.Description)
            ], CopySuccessToast, CopyErrorBanner);
        }

        detailPanels[key] = new DetailPanelEntry
        {
            Key = key,
            Category = RailCategory.Nic,
            Root = panel,
            ApplySample = sample =>
            {
                if (sample.Adapters.TryGetValue(nic.ConnectionId, out var m))
                {
                    sendChart.Add(m.BytesSentPerSec, HostUiHelpers.FormatBitsPerSecond(m.BytesSentPerSec));
                    recvChart.Add(m.BytesReceivedPerSec, HostUiHelpers.FormatBitsPerSecond(m.BytesReceivedPerSec));
                }
            },
            FormatReading = _ => speedText,
            RefreshTheme = () =>
            {
                Color? custom = HostUiHelpers.IsAccentEnabled ? null : nicColor;
                sendChart.SetAccentColor(custom);
                recvChart.SetAccentColor(custom);
                RenderNicKv();
            }
        };

        item.UpdateSample = sample =>
        {
            if (sample.Adapters.TryGetValue(nic.ConnectionId, out var m))
            {
                spark.Add(m.TotalBytesPerSec);
                stat.Text = HostUiHelpers.FormatCompactNetRate(m.BytesSentPerSec, m.BytesReceivedPerSec);
                if (selectedKey != key)
                {
                    sendChart.Add(m.BytesSentPerSec, HostUiHelpers.FormatBitsPerSecond(m.BytesSentPerSec));
                    recvChart.Add(m.BytesReceivedPerSec, HostUiHelpers.FormatBitsPerSecond(m.BytesReceivedPerSec));
                }
            }
        };
    }

    private void AddSwitchItem(HostInventorySnapshot inventory, SwitchInfo sw)
    {
        string key = $"sw:{sw.Id}";
        string modeLabel = string.IsNullOrWhiteSpace(sw.StateError) ? HostUiHelpers.FormatSwitchMode(sw.SwitchType) : L10n.Get("Common_Unavailable");

        var spark = new VmMiniSparkline(transfer: true);
        var title = new TextBlock { Text = sw.SwitchName, FontSize = 13, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        var sub = new TextBlock { Text = modeLabel, FontSize = 11.5, TextTrimming = TextTrimming.Clip };
        var stat = new TextBlock { Text = "—", FontSize = 11.5 };
        Typography.SetNumeralAlignment(stat, FontNumeralAlignment.Tabular);

        string upstreamText = sw.Upstream?.DisplayName ?? L10n.Get("Host_Switches_UpstreamNone");
        var item = CreateRailEntry(
            key: key,
            category: RailCategory.Switch,
            detailTitle: sw.SwitchName,
            detailSubtitle: upstreamText,
            detailGlyph: "\uE774",
            detailGlyphRotation: 0,
            categoryColor: ColorForCategory(RailCategory.Switch),
            sparkline: spark,
            titleText: title,
            subtitleText: sub,
            statText: stat,
            tooltip: $"{sw.SwitchName} ({modeLabel}，{upstreamText})");

        var sendChart = new VmUsageChart(L10n.Get("Host_Net_Send"), VmUsageChartKind.Transfer, chartHeight: 136);
        var recvChart = new VmUsageChart(L10n.Get("Host_Net_Recv"), VmUsageChartKind.Transfer, chartHeight: 136);
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(sendChart, 0);
        Grid.SetColumn(recvChart, 1);
        grid.Children.Add(sendChart);
        grid.Children.Add(recvChart);

        string? hostIpv4 = inventory.SwitchEndpoints.GetValueOrDefault(sw.SwitchName)?.FirstOrDefault(e => e.IsManagementOs)?.IpAddress;
        if (string.IsNullOrWhiteSpace(hostIpv4)) hostIpv4 = "---";

        var endpoints = inventory.SwitchEndpoints.TryGetValue(sw.SwitchName, out var eps) ? eps : [];
        string endpointNames = endpoints.Count > 0
            ? string.Join(", ", endpoints.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            : L10n.Get("Host_Sw_None");

        string macPoolText = inventory.HostSettings != null && !string.IsNullOrWhiteSpace(inventory.HostSettings.MinimumMacAddress)
            ? $"{HostUiHelpers.FormatMacWithDashes(inventory.HostSettings.MinimumMacAddress)} → {HostUiHelpers.FormatMacWithDashes(inventory.HostSettings.MaximumMacAddress)}"
            : "---";

        var kvBorder = new Border();
        var panel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        var scaleHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(scaleHost);
        panel.Children.Add(grid);
        panel.Children.Add(kvBorder);
        detailBodyHost.Children.Add(panel);

        void RenderSwitchKv()
        {
            scaleHost.Content = HostUiHelpers.BuildScaleBar(ActualTheme, L10n.Get("Host_Sw_Scale"), modeLabel);
            kvBorder.Child = HostUiHelpers.BuildTitleFirstKvGrid(ActualTheme,
            (HostUiHelpers.KvTileSpec[])[
                new(L10n.Get("Host_Sw_Mode"), modeLabel),
                new(L10n.Get("Host_Sw_HostAttach"), L10n.Get(sw.AllowManagementOS ? "Host_Sw_HostAttached" : "Host_Sw_HostDetached")),
                new(L10n.Get("Host_Sw_HostIpv4"), hostIpv4, CopyValue: hostIpv4 != "---" ? hostIpv4 : null),
                new(L10n.Get("Host_Sw_Endpoints"), endpointNames),
                new(L10n.Get("Host_Sw_MacPool"), macPoolText, Wide: true)
            ], CopySuccessToast, CopyErrorBanner);
        }

        detailPanels[key] = new DetailPanelEntry
        {
            Key = key,
            Category = RailCategory.Switch,
            Root = panel,
            ApplySample = sample =>
            {
                if (sample.Switches.TryGetValue(sw.SwitchName, out var m))
                {
                    sendChart.Add(m.BytesSentPerSec, HostUiHelpers.FormatBitsPerSecond(m.BytesSentPerSec));
                    recvChart.Add(m.BytesReceivedPerSec, HostUiHelpers.FormatBitsPerSecond(m.BytesReceivedPerSec));
                }
            },
            FormatReading = _ => modeLabel,
            RefreshTheme = () =>
            {
                Color? custom = HostUiHelpers.IsAccentEnabled ? null : ColorForCategory(RailCategory.Switch);
                sendChart.SetAccentColor(custom);
                recvChart.SetAccentColor(custom);
                RenderSwitchKv();
            }
        };

        item.UpdateSample = sample =>
        {
            if (sample.Switches.TryGetValue(sw.SwitchName, out var m))
            {
                spark.Add(m.TotalBytesPerSec);
                stat.Text = HostUiHelpers.FormatCompactNetRate(m.BytesSentPerSec, m.BytesReceivedPerSec);
                if (selectedKey != key)
                {
                    sendChart.Add(m.BytesSentPerSec, HostUiHelpers.FormatBitsPerSecond(m.BytesSentPerSec));
                    recvChart.Add(m.BytesReceivedPerSec, HostUiHelpers.FormatBitsPerSecond(m.BytesReceivedPerSec));
                }
            }
        };
    }
}
