
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Dashboard;

public sealed partial class VmDashboardView
{
    private void RebuildOverviewRail()
    {
        OverviewRail.Children.Clear();
        overviewRailItems.Clear();
        overviewRailByKey.Clear();

        bool inactive = state is VmState.PoweredOff or VmState.Saved or VmState.Suspended or VmState.FastSaved or VmState.Hibernated;
        if (inactive)
        {
            cpuReadingText = "0%";
            memUsedPercent ??= 0;
            memReadingText = FormatMemoryHeaderReading();
        }

        AddRailItem("cpu", OverviewCategory.Cpu, L10n.Get("Overview_Cpu"), null, FormatCpuRailStat(), transferSparkline: false);
        AddRailItem("mem", OverviewCategory.Memory, L10n.Get("Overview_Memory"), null, FormatMemoryRailStat(), transferSparkline: false);

        for (int i = 0; i < diskEntries.Count; i++)
        {
            var d = diskEntries[i];
            string title = diskEntries.Count > 1 ? L10n.Format("Overview_DiskIndexed", i) : L10n.Get("Overview_Disk");
            AddRailItem("disk:" + d.Key, OverviewCategory.Disk, title, d.RawSubtitle, d.RailStatText, transferSparkline: true, tooltip: d.Tooltip);
        }

        for (int i = 0; i < networkEntries.Count; i++)
        {
            var n = networkEntries[i];
            string title = networkEntries.Count > 1 ? L10n.Format("Overview_NetworkIndexed", i) : L10n.Get("Overview_Network");
            AddRailItem("net:" + n.Key, OverviewCategory.Network, title, n.SwitchDisplay, n.RailStatText, transferSparkline: true, tooltip: n.SwitchDisplay);
        }

        for (int i = 0; i < gpuEntries.Count; i++)
        {
            var g = gpuEntries[i];
            string title = gpuEntries.Count > 1 ? $"GPU {i}" : "GPU";
            AddRailItem("gpu:" + g.Key, OverviewCategory.Gpu, title, g.Title, g.ReadingText, transferSparkline: false, tooltip: g.Title);
        }

        if (!overviewRailByKey.ContainsKey(selectedOverviewKey)) selectedOverviewKey = "cpu";
        SelectOverviewResource(selectedOverviewKey);
        UpdateOverviewTheme();
    }

    private void AddRailItem(string key, OverviewCategory category, string title, string? rawSubtitle, string initialStat, bool transferSparkline, string? tooltip = null)
    {
        var sparkline = new VmMiniSparkline(transferSparkline);

        var titleText = new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        TextBlock? subtitleText = null;
        if (!string.IsNullOrWhiteSpace(rawSubtitle))
        {
            subtitleText = new TextBlock
            {
                Text = MiddleTrim(rawSubtitle, 22),
                FontSize = 11.5,
                TextTrimming = TextTrimming.Clip
            };
        }

        var statText = new TextBlock
        {
            Text = initialStat,
            FontSize = 11.5,
            MinHeight = 15,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(statText, FontNumeralAlignment.Tabular);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        info.Children.Add(titleText);
        if (subtitleText != null) info.Children.Add(subtitleText);
        info.Children.Add(statText);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(sparkline, 0);
        sparkline.Margin = new Thickness(0, 0, 9, 0);
        Grid.SetColumn(info, 1);
        row.Children.Add(sparkline);
        row.Children.Add(info);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"],
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1)
        };
        if (!string.IsNullOrWhiteSpace(tooltip ?? rawSubtitle))
            ToolTipService.SetToolTip(button, tooltip ?? rawSubtitle);

        button.Click += (_, _) => SelectOverviewResource(key);

        var transparent = new SolidColorBrush(Colors.Transparent);
        void ApplyVisuals(bool selected, bool accentMode, Color categoryColor)
        {
            bool dark = ActualTheme != ElementTheme.Light;
            Color neutralBg = dark ? Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x06, 0x00, 0x00, 0x00);
            Color neutralBorder = Color.FromArgb(0x26, 0x88, 0x88, 0x88);
            Color primary = ThemeColor("TextFillColorPrimaryBrush");
            Color secondary = ThemeColor("TextFillColorSecondaryBrush");

            if (accentMode)
            {
                if (selected)
                {
                    Color headerBg = ThemeColor("VmHeaderCheckedBrush");
                    Color headerFg = ThemeColor("VmHeaderForegroundBrush");
                    button.Background = new SolidColorBrush(headerBg);
                    button.BorderBrush = transparent;
                    titleText.FontWeight = FontWeights.SemiBold;
                    titleText.Foreground = new SolidColorBrush(headerFg);
                    statText.Foreground = new SolidColorBrush(headerFg);
                    statText.Opacity = 0.9;
                    if (subtitleText != null)
                    {
                        subtitleText.Foreground = new SolidColorBrush(headerFg);
                        subtitleText.Opacity = 0.9;
                    }

                    Color frameBg = Color.FromArgb(26, headerFg.R, headerFg.G, headerFg.B);
                    Color frameBorder = Color.FromArgb(77, headerFg.R, headerFg.G, headerFg.B);
                    Color fill = Color.FromArgb(51, headerFg.R, headerFg.G, headerFg.B);
                    Color guide = Color.FromArgb(80, headerFg.R, headerFg.G, headerFg.B);
                    sparkline.SetPalette(frameBg, frameBorder, headerFg, fill, guide);
                }
                else
                {
                    Color accent = ThemeColor("VmAccentBrush");
                    button.Background = transparent;
                    button.BorderBrush = transparent;
                    titleText.FontWeight = FontWeights.Medium;
                    titleText.Foreground = new SolidColorBrush(primary);
                    statText.Foreground = new SolidColorBrush(secondary);
                    statText.Opacity = 1;
                    if (subtitleText != null)
                    {
                        subtitleText.Foreground = new SolidColorBrush(secondary);
                        subtitleText.Opacity = 1;
                    }
                    sparkline.SetPalette(
                        neutralBg,
                        neutralBorder,
                        accent,
                        Color.FromArgb(0x40, accent.R, accent.G, accent.B),
                        Color.FromArgb(0x50, secondary.R, secondary.G, secondary.B));
                }
            }
            else
            {
                if (selected)
                {
                    button.Background = ThemeHelper.TryBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(0x10, 0x80, 0x80, 0x80));
                    button.BorderBrush = ThemeHelper.TryBrush("ControlStrokeColorDefaultBrush", neutralBorder);
                }
                else
                {
                    button.Background = transparent;
                    button.BorderBrush = transparent;
                }
                titleText.FontWeight = FontWeights.Medium;
                titleText.Foreground = new SolidColorBrush(primary);
                statText.Foreground = new SolidColorBrush(secondary);
                statText.Opacity = 1;
                if (subtitleText != null)
                {
                    subtitleText.Foreground = new SolidColorBrush(secondary);
                    subtitleText.Opacity = 1;
                }
                sparkline.SetPalette(
                    neutralBg,
                    neutralBorder,
                    categoryColor,
                    Color.FromArgb(0x45, categoryColor.R, categoryColor.G, categoryColor.B),
                    Color.FromArgb(0x50, secondary.R, secondary.G, secondary.B));
            }
        }

        var item = new OverviewRailItem
        {
            Key = key,
            Category = category,
            Button = button,
            Sparkline = sparkline,
            TitleText = titleText,
            SubtitleText = subtitleText,
            StatText = statText,
            ApplyVisuals = ApplyVisuals
        };
        overviewRailItems.Add(item);
        overviewRailByKey[key] = item;
        OverviewRail.Children.Add(button);
    }

    private void SelectOverviewResource(string key)
    {
        if (!overviewRailByKey.ContainsKey(key)) key = "cpu";
        selectedOverviewKey = key;
        if (key == "cpu" && current != null && IsLoaded) cpuNameTask ??= LoadHostCpuDisplayNameAsync();

        CpuDetailPanel.Visibility = key == "cpu" ? Visibility.Visible : Visibility.Collapsed;
        MemoryDetailPanel.Visibility = key == "mem" ? Visibility.Visible : Visibility.Collapsed;
        DiskDetailPanel.Visibility = key.StartsWith("disk:", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        NetworkDetailPanel.Visibility = key.StartsWith("net:", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        GpuDetailPanel.Visibility = key.StartsWith("gpu:", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

        if (key.StartsWith("disk:", StringComparison.Ordinal))
        {
            string diskKey = key["disk:".Length..];
            var entry = diskEntries.FirstOrDefault(d => string.Equals(d.Key, diskKey, StringComparison.OrdinalIgnoreCase));
            DiskDetailContent.Children.Clear();
            if (entry != null) DiskDetailContent.Children.Add(entry.Details);
        }
        else if (key.StartsWith("net:", StringComparison.Ordinal))
        {
            string netKey = key["net:".Length..];
            var entry = networkEntries.FirstOrDefault(n => string.Equals(n.Key, netKey, StringComparison.OrdinalIgnoreCase));
            NetworkDetailContent.Children.Clear();
            if (entry != null) NetworkDetailContent.Children.Add(entry.Details);
        }
        else if (key.StartsWith("gpu:", StringComparison.Ordinal))
        {
            string gpuKey = key["gpu:".Length..];
            var entry = gpuEntries.FirstOrDefault(g => string.Equals(g.Key, gpuKey, StringComparison.OrdinalIgnoreCase));
            RenderGpuKvCard(entry);
            UpdateGpuVisibility();
        }

        UpdateOverviewTheme();
        UpdateOverviewDetailHeader();
        if (key is "cpu" or "mem") LayoutCoreCharts();
    }

    private void UpdateOverviewTheme()
    {
        bool accentMode = IsAccentEnabled;
        Color accent = ThemeColor("VmAccentBrush");

        foreach (var item in overviewRailItems)
            item.ApplyVisuals(item.Key == selectedOverviewKey, accentMode, CategoryColor(item.Category));

        bool light = ActualTheme == ElementTheme.Light;
        OverviewDetailIcon.Foreground = ThemeHelper.TryBrush("TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

        // 同步右侧所有详细图表的曲线主题色
        Color? cpuColor = accentMode ? null : CategoryColor(OverviewCategory.Cpu);
        foreach (var chart in charts.Values) chart.SetAccentColor(cpuColor);
        memoryChart.SetAccentColor(accentMode ? null : CategoryColor(OverviewCategory.Memory));

        Color? diskColor = accentMode ? null : CategoryColor(OverviewCategory.Disk);
        foreach (var rate in diskRates.Values) rate.SetAccentColor(diskColor);

        Color? netColor = accentMode ? null : CategoryColor(OverviewCategory.Network);
        foreach (var rate in networkRates.Values) rate.SetAccentColor(netColor);

        Color? gpuColor = accentMode ? null : CategoryColor(OverviewCategory.Gpu);
        foreach (var chart in gpuCharts) chart.SetAccentColor(gpuColor);

        RenderCpuKvCard();
        RenderMemoryKvCard();
    }
}
