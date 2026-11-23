using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

public sealed partial class VmDashboardView
{
    private enum OverviewCategory { Cpu, Memory, Disk, Network, Gpu }

    private sealed class OverviewRailItem
    {
        public required string Key { get; init; }
        public required OverviewCategory Category { get; init; }
        public required Button Button { get; init; }
        public required VmMiniSparkline Sparkline { get; init; }
        public required TextBlock TitleText { get; init; }
        public TextBlock? SubtitleText { get; init; }
        public required TextBlock StatText { get; init; }
        public required Action<bool, bool, Color> ApplyVisuals { get; init; }
    }

    private readonly List<OverviewRailItem> overviewRailItems = [];
    private readonly Dictionary<string, OverviewRailItem> overviewRailByKey = new(StringComparer.Ordinal);
    private string selectedOverviewKey = "cpu";
    private string cachedHostCpuTitle = L10n.Get("Overview_VirtualProcessor");
    private bool appearanceHooked;

    // CPU & Memory cached state for right-hand KV & headers
    private int? cpuCoreCount;
    private bool? cpuNestedVirt;
    private SmtMode? cpuSmtMode;
    private double? cpuUsagePercent;

    private long? memStartupMiB;
    private bool? memDynamicEnabled;
    private long? memMinMiB;
    private long? memMaxMiB;
    private long? memAssignedMiB;
    private double? memUsedPercent;
    private int? memPressurePercent;
    private long? memDemandMiB;
    private long? memSmartPagingMiB;

    // Primary readings formatted for header & rail
    private string cpuReadingText = "---";
    private string memReadingText = "---";
    private string gpuReadingText = "---";

    private static Color CategoryColor(OverviewCategory category) => category switch
    {
        OverviewCategory.Cpu => Color.FromArgb(255, 0x38, 0x98, 0xEC),
        OverviewCategory.Memory => Color.FromArgb(255, 0x9D, 0x5C, 0xE6),
        OverviewCategory.Disk => Color.FromArgb(255, 0x4B, 0xA6, 0x3A),
        OverviewCategory.Network => Color.FromArgb(255, 0xD9, 0x46, 0xEF),
        OverviewCategory.Gpu => Color.FromArgb(255, 0x8B, 0x5C, 0xF6),
        _ => Color.FromArgb(255, 0x38, 0x98, 0xEC)
    };

    private static OverviewCategory CategoryFromKey(string key) => key switch
    {
        "cpu" => OverviewCategory.Cpu,
        "mem" => OverviewCategory.Memory,
        _ when key.StartsWith("disk:", StringComparison.Ordinal) => OverviewCategory.Disk,
        _ when key.StartsWith("net:", StringComparison.Ordinal) => OverviewCategory.Network,
        _ when key.StartsWith("gpu:", StringComparison.Ordinal) => OverviewCategory.Gpu,
        _ => OverviewCategory.Cpu
    };

    private static bool IsAccentEnabled =>
        ((App)Application.Current).Appearance?.Settings.AccentEnabled ?? true;

    private void InitializeOverview()
    {
        MemoryChartHost.Children.Clear();
        MemoryChartHost.Children.Add(memoryChart);
        OverviewPane.SizeChanged += (_, _) =>
        {
            UpdateOverviewResponsiveLayout();
            LayoutCoreCharts();
        };
        CpuDetailPanel.SizeChanged += (_, _) => LayoutCoreCharts();
    }

    private void AttachAppearanceEvents()
    {
        if (appearanceHooked) return;
        if (Application.Current is App { Appearance: { } appearance })
        {
            appearance.Changed += OnAppAppearanceChanged;
            appearanceHooked = true;
        }
    }

    private void DetachAppearanceEvents()
    {
        if (!appearanceHooked) return;
        if (Application.Current is App { Appearance: { } appearance })
            appearance.Changed -= OnAppAppearanceChanged;
        appearanceHooked = false;
    }

    private void OnAppAppearanceChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(UpdateOverviewTheme);

    private Task? cpuNameTask;

    private async Task LoadHostCpuDisplayNameAsync()
    {
        string cpu = await HostSystemInfoService.GetProcessorDisplayNameAsync();
        if (string.IsNullOrWhiteSpace(cpu))
        {
            var fallback = await HostSystemInfoService.GetSystemInfoAsync();
            cpu = fallback.CpuModel;
        }
        if (string.IsNullOrWhiteSpace(cpu) || cpu == "N/A") return;
        cachedHostCpuTitle = cpu;
        if (IsLoaded && selectedOverviewKey == "cpu") UpdateOverviewDetailHeader();
    }

    private void ResetOverviewTelemetry()
    {
        cpuCoreCount = null;
        cpuNestedVirt = null;
        cpuSmtMode = null;
        cpuUsagePercent = null;
        memStartupMiB = memMinMiB = memMaxMiB = memAssignedMiB = memDemandMiB = memSmartPagingMiB = null;
        memDynamicEnabled = null;
        memUsedPercent = null;
        memPressurePercent = null;
        cpuReadingText = memReadingText = gpuReadingText = "---";
        foreach (var item in overviewRailItems)
        {
            item.Sparkline.Clear();
            item.StatText.Text = "---";
        }
        RenderCpuKvCard();
        RenderMemoryKvCard();
        UpdateOverviewDetailHeader();
    }

    private void ClearOverviewMetricsOnStart()
    {
        cpuUsagePercent = null;
        memAssignedMiB = memDemandMiB = memSmartPagingMiB = null;
        memUsedPercent = null;
        memPressurePercent = null;
        cpuReadingText = "---";
        memReadingText = FormatMemoryHeaderReading();
        gpuReadingText = "---";
        foreach (var chart in charts.Values) chart.Clear();
        memoryChart.Clear();
        foreach (var chart in gpuCharts) chart.Clear();
        Array.Clear(gpuValues);
        foreach (var item in overviewRailItems)
        {
            item.Sparkline.Clear();
            if (item.Key == "cpu") item.StatText.Text = FormatCpuRailStat();
            else if (item.Key == "mem") item.StatText.Text = FormatMemoryRailStat();
        }
        RenderMemoryKvCard();
        UpdateOverviewDetailHeader();
    }

    private void SetOverviewInactive()
    {
        cpuUsagePercent = 0;
        cpuReadingText = "0%";
        memAssignedMiB = memDemandMiB = memSmartPagingMiB = null;
        memPressurePercent = null;
        memUsedPercent = 0;
        memReadingText = FormatMemoryHeaderReading();
        gpuReadingText = "0%";

        foreach (var chart in charts.Values) chart.SetInactive();
        memoryChart.SetInactive();
        foreach (var chart in gpuCharts) chart.SetInactive();
        Array.Clear(gpuValues);
        foreach (var rate in diskRates.Values.Concat(networkRates.Values)) rate.SetInactive();
        ClearNetworkAddresses();

        if (overviewRailByKey.TryGetValue("cpu", out var cpuItem))
        {
            cpuItem.Sparkline.SetInactive();
            cpuItem.StatText.Text = FormatCpuRailStat();
        }
        if (overviewRailByKey.TryGetValue("mem", out var memItem))
        {
            memItem.Sparkline.SetInactive();
            memItem.StatText.Text = FormatMemoryRailStat();
        }
        foreach (var disk in diskEntries)
            if (overviewRailByKey.TryGetValue("disk:" + disk.Key, out var rail))
            {
                rail.Sparkline.SetInactive();
                rail.StatText.Text = "";
            }
        foreach (var net in networkEntries)
            if (overviewRailByKey.TryGetValue("net:" + net.Key, out var rail))
            {
                rail.Sparkline.SetInactive();
                rail.StatText.Text = "";
            }
        foreach (var gpu in gpuEntries)
        {
            gpu.ReadingText = "0%";
            if (overviewRailByKey.TryGetValue("gpu:" + gpu.Key, out var rail))
            {
                rail.Sparkline.SetInactive();
                rail.StatText.Text = "0%";
            }
        }

        UpdateGpuVisibility();
        RenderMemoryKvCard();
        UpdateOverviewDetailHeader();
    }

    private void UpdateOverviewConfiguration(VmQueryRow row)
    {
        cpuCoreCount = row.Get(VmProcessorFields.Count) is { HasValue: true } count ? count.Value : null;
        cpuNestedVirt = row.Get(VmProcessorFields.ExposeVirtualizationExtensions) is { HasValue: true } nested ? nested.Value : null;
        cpuSmtMode = row.Get(VmProcessorFields.SmtMode) is { HasValue: true } smt ? smt.Value : null;

        memStartupMiB = row.Get(VmMemoryFields.Startup) is { HasValue: true } startup ? startup.Value : null;
        memDynamicEnabled = row.Get(VmMemoryFields.DynamicMemoryEnabled) is { HasValue: true } dyn ? dyn.Value : null;
        memMinMiB = row.Get(VmMemoryFields.Minimum) is { HasValue: true } min ? min.Value : null;
        memMaxMiB = row.Get(VmMemoryFields.Maximum) is { HasValue: true } max ? max.Value : null;

        bool inactive = state is VmState.PoweredOff or VmState.Saved or VmState.Suspended or VmState.FastSaved or VmState.Hibernated;
        if (inactive || cpuReadingText == "---")
        {
            cpuReadingText = inactive ? "0%" : cpuReadingText;
            if (overviewRailByKey.TryGetValue("cpu", out var cpuRail))
                cpuRail.StatText.Text = FormatCpuRailStat();
        }
        if (inactive || memReadingText == "---")
        {
            memReadingText = FormatMemoryHeaderReading();
            if (overviewRailByKey.TryGetValue("mem", out var memRail))
                memRail.StatText.Text = FormatMemoryRailStat();
        }

        RenderCpuKvCard();
        RenderMemoryKvCard();
        if (selectedOverviewKey is "cpu" or "mem") UpdateOverviewDetailHeader();
    }

    private void UpdateOverviewLiveMetrics(VmQueryRow sample)
    {
        if (sample.Get(VmProcessorFields.UsagePercent) is { HasValue: true } cpu)
        {
            cpuUsagePercent = cpu.Value;
            cpuReadingText = $"{cpu.Value:N1}%";
            if (overviewRailByKey.TryGetValue("cpu", out var cpuRail))
            {
                cpuRail.Sparkline.Add(cpu.Value);
                cpuRail.StatText.Text = FormatCpuRailStat();
            }
        }
        foreach (var core in sample.GetCollection(VmQueryCollection.Processors).Rows)
        {
            var index = core.Get(VmProcessorMetricFields.Index);
            var usage = core.Get(VmProcessorMetricFields.UsagePercent);
            if (index.HasValue && usage.HasValue && charts.TryGetValue(index.Value, out var chart))
                chart.Add(usage.Value, $"{usage.Value:N1}%");
        }

        if (sample.Get(VmMemoryFields.AssignedMiB) is { HasValue: true } assigned) memAssignedMiB = assigned.Value;
        if (sample.Get(VmMemoryFields.PressurePercent) is { HasValue: true } pressure) memPressurePercent = pressure.Value;
        if (sample.Get(VmMemoryFields.DemandMiB) is { HasValue: true } demand) memDemandMiB = demand.Value;
        if (sample.Get(VmMemoryFields.SmartPagingMiB) is { HasValue: true } smartPaging) memSmartPagingMiB = smartPaging.Value;

        if (sample.Get(VmMemoryFields.AvailablePercent) is { HasValue: true } available)
        {
            double usedPercent = Math.Clamp(100 - available.Value, 0, 100);
            memUsedPercent = usedPercent;
            memoryChart.Add(usedPercent, $"{usedPercent:N0}%");
            if (overviewRailByKey.TryGetValue("mem", out var memRail))
                memRail.Sparkline.Add(usedPercent);
        }

        memReadingText = FormatMemoryHeaderReading();
        if (overviewRailByKey.TryGetValue("mem", out var memItem))
            memItem.StatText.Text = FormatMemoryRailStat();

        UpdateGpuCharts(sample);
        UpdateDeviceRates(sample);
        UpdateNetworkAddresses(sample);

        RenderMemoryKvCard();
        UpdateOverviewDetailHeader();
    }

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
                    button.Background = TryResourceBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(0x10, 0x80, 0x80, 0x80));
                    button.BorderBrush = TryResourceBrush("ControlStrokeColorDefaultBrush", neutralBorder);
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
        OverviewDetailIcon.Foreground = TryResourceBrush("TextFillColorPrimaryBrush",
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

    private void UpdateOverviewDetailHeader()
    {
        OverviewDetailPill.Visibility = Visibility.Collapsed;
        OverviewDetailIconRotation.Angle = 0;

        if (selectedOverviewKey == "cpu")
        {
            OverviewDetailIcon.Glyph = "\uF58E";
            OverviewDetailIconRotation.Angle = 90;
            OverviewDetailTitle.FontSize = 16;
            OverviewDetailTitle.Text = cachedHostCpuTitle;
            OverviewDetailSubtitle.Text = "";
            OverviewDetailSubtitle.Visibility = Visibility.Collapsed;
            OverviewDetailIconBox.VerticalAlignment = VerticalAlignment.Center;
            OverviewDetailIconBox.Margin = new Thickness(0);
            OverviewDetailReading.Text = cpuReadingText;
        }
        else if (selectedOverviewKey == "mem")
        {
            OverviewDetailIcon.Glyph = "\uEEA0";
            OverviewDetailTitle.FontSize = 18;
            OverviewDetailTitle.Text = L10n.Get("Overview_Memory");
            OverviewDetailSubtitle.Text = "";
            OverviewDetailSubtitle.Visibility = Visibility.Collapsed;
            OverviewDetailIconBox.VerticalAlignment = VerticalAlignment.Center;
            OverviewDetailIconBox.Margin = new Thickness(0);
            OverviewDetailReading.Text = memReadingText;
        }
        else if (selectedOverviewKey.StartsWith("disk:", StringComparison.Ordinal))
        {
            string diskKey = selectedOverviewKey["disk:".Length..];
            var entry = diskEntries.FirstOrDefault(d => string.Equals(d.Key, diskKey, StringComparison.OrdinalIgnoreCase));
            OverviewDetailIcon.Glyph = entry?.IsOptical == true ? "\uE958" : "\uEDA2";
            OverviewDetailTitle.FontSize = 18;
            OverviewDetailTitle.Text = entry?.Title ?? L10n.Get("Overview_Disk");
            OverviewDetailSubtitle.Text = "";
            OverviewDetailSubtitle.Visibility = Visibility.Collapsed;
            OverviewDetailIconBox.VerticalAlignment = VerticalAlignment.Center;
            OverviewDetailIconBox.Margin = new Thickness(0);
            OverviewDetailReading.Text = entry?.HeaderReadingText ?? "";
        }
        else if (selectedOverviewKey.StartsWith("net:", StringComparison.Ordinal))
        {
            string netKey = selectedOverviewKey["net:".Length..];
            var entry = networkEntries.FirstOrDefault(n => string.Equals(n.Key, netKey, StringComparison.OrdinalIgnoreCase));
            OverviewDetailIcon.Glyph = "\uE774";
            OverviewDetailTitle.FontSize = 18;
            OverviewDetailTitle.Text = entry?.SwitchDisplay ?? L10n.Get("Overview_Disconnected");
            OverviewDetailSubtitle.Text = "";
            OverviewDetailSubtitle.Visibility = Visibility.Collapsed;
            OverviewDetailIconBox.VerticalAlignment = VerticalAlignment.Center;
            OverviewDetailIconBox.Margin = new Thickness(0);
            OverviewDetailReading.Text = "";
        }
        else if (selectedOverviewKey.StartsWith("gpu:", StringComparison.Ordinal))
        {
            string gpuKey = selectedOverviewKey["gpu:".Length..];
            var entry = gpuEntries.FirstOrDefault(g => string.Equals(g.Key, gpuKey, StringComparison.OrdinalIgnoreCase));
            OverviewDetailIcon.Glyph = "\uF211";
            OverviewDetailTitle.FontSize = 18;
            OverviewDetailTitle.Text = entry?.Title ?? L10n.Get("Overview_NoGpu");
            OverviewDetailSubtitle.Text = "";
            OverviewDetailSubtitle.Visibility = Visibility.Collapsed;
            OverviewDetailIconBox.VerticalAlignment = VerticalAlignment.Center;
            OverviewDetailIconBox.Margin = new Thickness(0);
            OverviewDetailReading.Text = entry != null ? gpuReadingText : "";
        }
    }

    private void UpdateOverviewResponsiveLayout()
    {
        double width = OverviewPane.ActualWidth;
        if (width <= 0) return;

        if (width < 560)
        {
            OverviewRailColumn.Width = new GridLength(1, GridUnitType.Star);
            OverviewDetailColumn.Width = new GridLength(0);
            OverviewTopRow.Height = GridLength.Auto;
            OverviewBottomRow.Height = new GridLength(1, GridUnitType.Star);
            OverviewRailScroll.MaxHeight = 176;
            Grid.SetRow(OverviewRailScroll, 0);
            Grid.SetColumn(OverviewRailScroll, 0);
            Grid.SetRow(OverviewDetailCard, 1);
            Grid.SetColumn(OverviewDetailCard, 0);
        }
        else
        {
            OverviewRailColumn.Width = new GridLength(width < 680 ? 196 : 224);
            OverviewDetailColumn.Width = new GridLength(1, GridUnitType.Star);
            OverviewTopRow.Height = new GridLength(1, GridUnitType.Star);
            OverviewBottomRow.Height = new GridLength(0);
            OverviewRailScroll.MaxHeight = double.PositiveInfinity;
            Grid.SetRow(OverviewRailScroll, 0);
            Grid.SetColumn(OverviewRailScroll, 0);
            Grid.SetRow(OverviewDetailCard, 0);
            Grid.SetColumn(OverviewDetailCard, 1);
        }
    }

    private void RenderCpuKvCard()
    {
        string nestedText = cpuNestedVirt switch { true => L10n.Get("Common_On"), false => L10n.Get("Common_Off"), _ => "---" };
        string htText = cpuSmtMode switch
        {
            SmtMode.MultiThread => L10n.Get("Common_On"),
            SmtMode.SingleThread => L10n.Get("Common_Off"),
            SmtMode.Inherit => L10n.Get("Common_Auto"),
            _ => "---"
        };
        string? htTip = cpuSmtMode switch
        {
            SmtMode.MultiThread => L10n.Get("Overview_SmtMulti"),
            SmtMode.SingleThread => L10n.Get("Overview_SmtSingle"),
            SmtMode.Inherit => L10n.Get("Overview_SmtInherit"),
            _ => null
        };

        CpuKvCard.Child = BuildTitleFirstKvGrid(
        ((string, string, string?, bool)[])[
            (L10n.Get("Overview_NestedVirt"), nestedText, null, false),
            (L10n.Get("Overview_Hyperthreading"), htText, htTip, false)
        ]);
    }

    private void RenderMemoryKvCard()
    {
        bool running = stateVerified && state == VmState.Running;
        bool dynamicOn = memDynamicEnabled == true;
        string dynText = memDynamicEnabled switch { true => L10n.Get("Common_On"), false => L10n.Get("Common_Off"), _ => "---" };

        var items = new List<(string Title, string StateText, string? Tooltip, bool Caution)>
        {
            (L10n.Get("Overview_DynamicMemory"), dynText, L10n.Get("Overview_DynamicMemoryTip"), false)
        };

        if (dynamicOn)
        {
            string range = memMinMiB.HasValue && memMaxMiB.HasValue
                ? $"{FormatMemoryCapacity(memMinMiB.Value)} - {FormatMemoryCapacity(memMaxMiB.Value)}"
                : "---";
            items.Add((L10n.Get("Overview_MemoryRange"), range, L10n.Get("Overview_MemoryRangeTip"), false));
            items.Add((L10n.Get("Overview_MemoryPressure"), running && memPressurePercent.HasValue ? $"{memPressurePercent.Value}%" : "---", L10n.Get("Overview_MemoryPressureTip"), false));
            items.Add((L10n.Get("Overview_MemoryDemand"), running && memDemandMiB.HasValue ? FormatMemoryCapacity(memDemandMiB.Value) : "---", L10n.Get("Overview_MemoryDemandTip"), false));

            if (running && memSmartPagingMiB is > 0)
            {
                items.Add((L10n.Get("Overview_SmartPaging"), L10n.Format("Overview_SmartPagingActive", FormatMemoryCapacity(memSmartPagingMiB.Value)), L10n.Get("Overview_SmartPagingTip"), true));
            }
        }

        MemoryKvCard.Child = BuildTitleFirstKvGrid(items);
    }

    private Brush TryResourceBrush(string key, Color fallback)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : new SolidColorBrush(fallback);

    private sealed record KvTileSpec(
        string Title,
        string StateText = "",
        string? Tooltip = null,
        bool Caution = false,
        bool Wide = false,
        string? CopyValue = null,
        IReadOnlyList<string>? MultiValues = null);

    private Grid BuildTitleFirstKvGrid(IReadOnlyList<(string Title, string StateText, string? Tooltip, bool Caution)> items)
        => BuildTitleFirstKvGrid(items.Select(it => new KvTileSpec(it.Title, it.StateText, it.Tooltip, it.Caution)).ToArray());

    private Grid BuildTitleFirstKvGrid(IReadOnlyList<KvTileSpec> items)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        bool light = ActualTheme == ElementTheme.Light;
        Brush primaryBrush = TryResourceBrush("TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        Brush secondaryBrush = TryResourceBrush("TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        Brush cautionBrush = TryResourceBrush("SystemFillColorCautionBrush",
            light ? Color.FromArgb(255, 0x9D, 0x5D, 0x00) : Color.FromArgb(255, 0xFC, 0xE1, 0x00));
        var wellStyle = (Style)Resources["OverviewKvWellStyle"];

        int row = 0, col = 0;
        foreach (var item in items)
        {
            if (item.Wide && col != 0)
            {
                row++;
                col = 0;
            }
            while (grid.RowDefinitions.Count <= row)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cell = new StackPanel { Spacing = 2 };

            var headRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            headRow.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 13.5,
                FontWeight = FontWeights.Medium,
                Foreground = primaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (!string.IsNullOrWhiteSpace(item.CopyValue))
                headRow.Children.Add(CreateInlineCopyButton(item.CopyValue, item.Title, secondaryBrush));

            cell.Children.Add(headRow);

            if (item.MultiValues is { Count: > 1 } multi)
            {
                var list = new StackPanel { Spacing = 3, Margin = new Thickness(0, 2, 0, 0) };
                foreach (string val in multi)
                {
                    var valRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    var valBlock = new TextBlock
                    {
                        Text = val,
                        FontSize = 12,
                        Foreground = secondaryBrush,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(valBlock, FontNumeralAlignment.Tabular);
                    ToolTipService.SetToolTip(valBlock, val);
                    valRow.Children.Add(valBlock);
                    valRow.Children.Add(CreateInlineCopyButton(val, item.Title, secondaryBrush));
                    list.Children.Add(valRow);
                }
                cell.Children.Add(list);
            }
            else
            {
                string displayVal = string.IsNullOrWhiteSpace(item.StateText) ? "---" : item.StateText;
                var stateBlock = new TextBlock
                {
                    Text = item.Wide && displayVal.Length > 52 ? MiddleTrim(displayVal, 52) : displayVal,
                    FontSize = 12,
                    FontWeight = item.Caution ? FontWeights.Medium : FontWeights.Normal,
                    Foreground = item.Caution ? cautionBrush : secondaryBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                if (item.Wide || displayVal.Length > 32)
                    ToolTipService.SetToolTip(stateBlock, displayVal);
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(stateBlock, FontNumeralAlignment.Tabular);
                cell.Children.Add(stateBlock);
            }

            var card = new Border
            {
                Style = wellStyle,
                Child = cell
            };
            if (!string.IsNullOrWhiteSpace(item.Tooltip))
                ToolTipService.SetToolTip(card, item.Tooltip);

            Grid.SetRow(card, row);
            Grid.SetColumn(card, item.Wide ? 0 : col);
            if (item.Wide) Grid.SetColumnSpan(card, 2);
            grid.Children.Add(card);

            if (item.Wide)
            {
                row++;
                col = 0;
            }
            else if (col == 1)
            {
                row++;
                col = 0;
            }
            else
            {
                col = 1;
            }
        }
        return grid;
    }

    private Button CreateInlineCopyButton(string copyValue, string label, Brush iconBrush)
    {
        var icon = new FontIcon
        {
            Glyph = "\uE8C8",
            FontSize = 11.5,
            Foreground = iconBrush
        };
        var button = new Button
        {
            Content = icon,
            Width = 22,
            Height = 20,
            MinWidth = 22,
            MinHeight = 20,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(button, L10n.Format("Dev_CopyValueTooltip", copyValue));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L10n.Format("Dev_CopyValueAria", label, copyValue));
        button.Click += async (_, _) =>
        {
            try
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(copyValue);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
                icon.Glyph = "\uE73E";
                ToolTipService.SetToolTip(button, L10n.Get("Dev_Copied"));
                await Task.Delay(1600);
                icon.Glyph = "\uE8C8";
                ToolTipService.SetToolTip(button, L10n.Format("Dev_CopyValueTooltip", copyValue));
            }
            catch (Exception error)
            {
                ShowError(L10n.Format("Dev_CopyFailed", error.Message));
            }
        };
        return button;
    }

    private string FormatCpuRailStat()
    {
        string usage = state == VmState.Running && cpuUsagePercent.HasValue ? $"{cpuUsagePercent.Value:N1}%" : "0%";
        return cpuCoreCount.HasValue ? L10n.Format("Overview_CpuUsageWithCores", usage, cpuCoreCount.Value) : usage;
    }

    private string FormatMemoryHeaderReading()
    {
        long? capacityMiB = memAssignedMiB ?? memStartupMiB;
        if (!capacityMiB.HasValue || capacityMiB.Value <= 0) return "---";
        bool running = state == VmState.Running;
        double usedMiB = running && memUsedPercent.HasValue
            ? capacityMiB.Value * memUsedPercent.Value / 100d
            : 0;
        return FormatMemoryPairText(usedMiB, capacityMiB.Value, slashSpaced: true);
    }

    private string FormatMemoryRailStat()
    {
        long? capacityMiB = memAssignedMiB ?? memStartupMiB;
        if (!capacityMiB.HasValue || capacityMiB.Value <= 0) return "---";
        bool running = state == VmState.Running;
        double usedPct = running && memUsedPercent.HasValue ? memUsedPercent.Value : 0;
        double usedMiB = running && memUsedPercent.HasValue
            ? capacityMiB.Value * usedPct / 100d
            : 0;
        return $"{Math.Round(usedPct):0}% {FormatMemoryPairText(usedMiB, capacityMiB.Value, slashSpaced: false)}";
    }

    private static string FormatMemoryPairText(double usedMiB, long capacityMiB, bool slashSpaced)
    {
        string sep = slashSpaced ? " / " : "/";
        if (capacityMiB >= 1024)
            return $"{usedMiB / 1024d:0.0}{sep}{capacityMiB / 1024d:0.0} GB";
        return $"{Math.Round(usedMiB):0}{sep}{capacityMiB} MB";
    }

    private static string FormatMemoryCapacity(long mib)
    {
        if (mib >= 1024)
            return mib % 1024 == 0 ? $"{mib / 1024} GB" : $"{mib / 1024d:0.0} GB";
        return $"{mib} MB";
    }

    private void RenderGpuKvCard(GpuEntry? gpu)
    {
        if (gpu == null)
        {
            GpuKvCard.Child = BuildTitleFirstKvGrid((KvTileSpec[])[new KvTileSpec(L10n.Get("Overview_GpuCardTitle"), L10n.Get("Overview_NoGpu"), Wide: true)]);
            return;
        }

        string manu = string.IsNullOrWhiteSpace(gpu.Manu) ? "---" : gpu.Manu;
        string vendor = string.IsNullOrWhiteSpace(gpu.Vendor) ? "---" : gpu.Vendor;
        string vram = IsMissingText(gpu.MemoryText) ? "---" : gpu.MemoryText;
        string drv = IsMissingText(gpu.DriverVersion) ? "---" : gpu.DriverVersion;
        string inst = IsMissingText(gpu.InstanceId) ? "---" : gpu.InstanceId;

        GpuKvCard.Child = BuildTitleFirstKvGrid((KvTileSpec[])[
            new KvTileSpec(L10n.Get("Overview_GpuChipVendor"), manu, CopyValue: manu != "---" ? manu : null),
            new KvTileSpec(L10n.Get("Overview_GpuBoardVendor"), vendor, CopyValue: vendor != "---" ? vendor : null),
            new KvTileSpec(L10n.Get("Overview_GpuDedicatedVram"), vram, CopyValue: vram != "---" ? vram : null),
            new KvTileSpec(L10n.Get("Overview_GpuDriverVersion"), drv, CopyValue: drv != "---" ? drv : null),
            new KvTileSpec(L10n.Get("Overview_GpuDevicePath"), inst, Wide: true, CopyValue: inst != "---" ? inst : null)
        ]);
    }

    private static Grid BuildKvGrid(IReadOnlyList<(string Label, UIElement Value, bool SpanFull)> items)
    {
        var grid = new Grid { ColumnSpacing = 24, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0, col = 0;
        foreach (var (label, value, spanFull) in items)
        {
            if (spanFull && col != 0)
            {
                row++;
                col = 0;
            }
            while (grid.RowDefinitions.Count <= row)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cell = new StackPanel { Spacing = 3 };
            cell.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            });
            cell.Children.Add(value);

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, spanFull ? 0 : col);
            if (spanFull) Grid.SetColumnSpan(cell, 2);
            grid.Children.Add(cell);

            if (spanFull)
            {
                row++;
                col = 0;
            }
            else if (col == 1)
            {
                row++;
                col = 0;
            }
            else
            {
                col = 1;
            }
        }
        return grid;
    }

    private static TextBlock KvValue(string text, bool monospace = false)
    {
        var tb = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(text) ? "---" : text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        if (monospace) tb.FontFamily = new FontFamily("Consolas");
        Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        return tb;
    }

    private static string FormatMiB(double mib)
    {
        if (mib <= 0) return "0.0 GB";
        return mib >= 1024 ? $"{mib / 1024d:0.##} GB" : $"{mib:0} MB";
    }

    private static string MiddleTrim(string text, int maxLength = 22)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        int tail = Math.Max(4, maxLength / 2 - 1);
        int head = Math.Max(4, maxLength - tail - 1);
        return $"{text[..head]}…{text[^tail..]}";
    }

    private async void OnOverviewGearClick(object sender, RoutedEventArgs e)
    {
        if (selectedOverviewKey == "cpu") await JumpToSettingsAsync("processor");
        else if (selectedOverviewKey == "mem") await JumpToSettingsAsync("memory");
        else if (selectedOverviewKey.StartsWith("disk:", StringComparison.Ordinal)) await JumpToSettingsAsync("devices", "storage");
        else if (selectedOverviewKey.StartsWith("net:", StringComparison.Ordinal)) await JumpToSettingsAsync("devices", "network");
        else if (selectedOverviewKey.StartsWith("gpu:", StringComparison.Ordinal)) await JumpToSettingsAsync("devices", "gpu");
    }
}
