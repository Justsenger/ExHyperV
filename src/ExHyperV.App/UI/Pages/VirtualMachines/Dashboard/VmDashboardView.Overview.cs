
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Dashboard;

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
}
