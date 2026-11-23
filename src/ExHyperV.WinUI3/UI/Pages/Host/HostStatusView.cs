using System.Diagnostics;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.WinUI3.Localization;
using ExHyperV.WinUI3.UI.Pages.VirtualMachines;
using ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;
using ExHyperV.WinUI3.UI.Shared;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExHyperV.WinUI3.UI.Pages.Host;

internal sealed partial class HostStatusView : UserControl
{
    private enum RailCategory { Cpu, Memory, Gpu, Disk, Nic, Switch }

    private sealed class RailEntry
    {
        public required string Key { get; init; }
        public required RailCategory Category { get; init; }
        public required string DetailTitle { get; init; }
        public required string DetailSubtitle { get; init; }
        public required string DetailGlyph { get; init; }
        public double DetailGlyphRotation { get; init; }
        public required Color CategoryColor { get; init; }
        public required Button Button { get; init; }
        public required VmMiniSparkline Sparkline { get; init; }
        public required TextBlock TitleText { get; init; }
        public TextBlock? SubtitleText { get; init; }
        public required TextBlock StatText { get; init; }
        public Action<HostLiveSample>? UpdateSample { get; set; }
    }

    private sealed class DetailPanelEntry
    {
        public required string Key { get; init; }
        public required RailCategory Category { get; init; }
        public required UIElement Root { get; init; }
        public required Action<HostLiveSample> ApplySample { get; init; }
        public required Func<HostLiveSample, string> FormatReading { get; init; }
        public Action? RefreshTheme { get; init; }
    }

    private readonly Grid shellGrid = new() { Margin = new Thickness(16, 0, 16, 16), ColumnSpacing = 12, RowSpacing = 10 };
    private readonly ColumnDefinition railColumn = new() { Width = new GridLength(244) };
    private readonly ColumnDefinition detailColumn = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly RowDefinition topRow = new() { Height = new GridLength(1, GridUnitType.Star) };
    private readonly RowDefinition bottomRow = new() { Height = new GridLength(0) };

    private readonly ScrollViewer railScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly StackPanel railPanel = new() { Spacing = 4 };

    private readonly Border detailCard = new()
    {
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16, 13, 16, 13)
    };
    private readonly Grid detailRoot = new() { RowSpacing = 10 };
    private readonly Border detailHeader = new() { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 10) };
    private readonly Border detailIconBox = new()
    {
        Width = 36,
        Height = 36,
        CornerRadius = new CornerRadius(7),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly FontIcon detailIcon = new() { FontSize = 16, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5) };
    private readonly RotateTransform detailIconRotation = new();
    private readonly TextBlock detailTitle = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock detailSubtitle = new() { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock detailReading = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    private readonly Grid cpuHost = new();
    private readonly HostCpuThreadMatrixView cpuMatrix = new();
    private readonly Grid memHost = new();
    private readonly HostMemoryRingView memRing = new();
    private readonly ScrollViewer detailBodyScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly Grid detailBodyHost = new();

    private readonly List<RailEntry> railEntries = [];
    private readonly Dictionary<string, DetailPanelEntry> detailPanels = new(StringComparer.Ordinal);
    private string selectedKey = "cpu";
    private HostInventorySnapshot? currentInventory;
    private HostLiveSample? lastSample;
    private IDashboardFeedbackHost? feedbackHost;

    private static Color ColorForCategory(RailCategory category) => category switch
    {
        RailCategory.Cpu => Color.FromArgb(255, 0x38, 0x98, 0xEC),
        RailCategory.Memory => Color.FromArgb(255, 0x9D, 0x5C, 0xE6),
        RailCategory.Gpu => Color.FromArgb(255, 0x8B, 0x5C, 0xF6),
        RailCategory.Disk => Color.FromArgb(255, 0x6C, 0xCB, 0x5F),
        RailCategory.Nic => Color.FromArgb(255, 0x46, 0xB3, 0xE6),
        RailCategory.Switch => Color.FromArgb(255, 0x00, 0xB7, 0xC3),
        _ => Color.FromArgb(255, 0x38, 0x98, 0xEC)
    };

    public HostStatusView()
    {
        shellGrid.ColumnDefinitions.Add(railColumn);
        shellGrid.ColumnDefinitions.Add(detailColumn);
        shellGrid.RowDefinitions.Add(topRow);
        shellGrid.RowDefinitions.Add(bottomRow);

        VmSettingsView.AttachScrollbarClearance(railScroll, bottomSpacing: 0);
        VmSettingsView.AttachScrollbarClearance(detailBodyScroll, bottomSpacing: 0);

        railScroll.Content = railPanel;
        Grid.SetRow(railScroll, 0);
        Grid.SetColumn(railScroll, 0);
        shellGrid.Children.Add(railScroll);

        BuildDetailCardShell();
        Grid.SetRow(detailCard, 0);
        Grid.SetColumn(detailCard, 1);
        shellGrid.Children.Add(detailCard);

        Content = shellGrid;

        shellGrid.SizeChanged += (_, _) => UpdateResponsiveLayout();
        ActualThemeChanged += (_, _) => UpdateTheme();
        UpdateTheme();
    }

    internal void BindFeedbackHost(IDashboardFeedbackHost host) => feedbackHost = host;

    private void BuildDetailCardShell()
    {
        detailRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        detailIcon.RenderTransform = detailIconRotation;
        detailIconBox.Child = detailIcon;

        var titleStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(detailTitle);
        titleStack.Children.Add(detailSubtitle);

        Typography.SetNumeralAlignment(detailReading, FontNumeralAlignment.Tabular);

        var headGrid = new Grid { ColumnSpacing = 12 };
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(detailIconBox, 0);
        Grid.SetColumn(titleStack, 1);
        Grid.SetColumn(detailReading, 2);
        headGrid.Children.Add(detailIconBox);
        headGrid.Children.Add(titleStack);
        headGrid.Children.Add(detailReading);
        detailHeader.Child = headGrid;

        Grid.SetRow(detailHeader, 0);
        detailRoot.Children.Add(detailHeader);

        cpuHost.Children.Add(cpuMatrix);
        Grid.SetRow(cpuHost, 1);
        detailRoot.Children.Add(cpuHost);

        memHost.Children.Add(memRing);
        Grid.SetRow(memHost, 1);
        detailRoot.Children.Add(memHost);

        detailBodyScroll.Content = detailBodyHost;
        Grid.SetRow(detailBodyScroll, 1);
        detailRoot.Children.Add(detailBodyScroll);

        detailCard.Child = detailRoot;
    }

    public void ApplyInventory(HostInventorySnapshot inventory)
    {
        currentInventory = inventory;
        double savedRailScroll = railScroll.VerticalOffset;

        cpuMatrix.InitializeTopology(inventory.LogicalProcessorCount);
        memRing.UpdateTelemetry(inventory, null);

        railPanel.Children.Clear();
        railEntries.Clear();
        detailBodyHost.Children.Clear();
        detailPanels.Clear();

        // 1. CPU
        AddCpuRailItem(inventory);

        // 2. Memory
        AddMemoryItem(inventory);

        // 3. GPUs
        for (int i = 0; i < inventory.Gpus.Count; i++)
            AddGpuItem(inventory, inventory.Gpus[i], i);

        // 4. Physical Disks
        foreach (var disk in inventory.Disks)
            AddDiskItem(inventory, disk);

        // 5. Connected Upstream NICs
        foreach (var nic in inventory.PhysicalAdapters)
        {
            if (!nic.LinkUp) continue;
            AddNicItem(inventory, nic);
        }

        // 6. Virtual Switches
        foreach (var sw in inventory.Switches)
            AddSwitchItem(inventory, sw);

        if (!detailPanels.ContainsKey(selectedKey) && selectedKey != "cpu" && selectedKey != "mem")
            selectedKey = "cpu";

        SelectRailKey(selectedKey);

        UpdateTheme();
        railScroll.ChangeView(null, savedRailScroll, null, disableAnimation: true);
    }

    public void ApplySample(HostLiveSample sample)
    {
        lastSample = sample;
        cpuMatrix.UpdateReport(sample.CpuReport, currentInventory?.LogicalProcessorCount ?? Environment.ProcessorCount);
        memRing.UpdateTelemetry(currentInventory, sample);

        foreach (var entry in railEntries)
            entry.UpdateSample?.Invoke(sample);

        if (detailPanels.TryGetValue(selectedKey, out var activeDetail))
        {
            activeDetail.ApplySample(sample);
            detailReading.Text = activeDetail.FormatReading(sample);
        }
        else if (selectedKey == "cpu")
        {
            detailReading.Text = sample.CpuPercent.HasValue ? $"{Math.Round(sample.CpuPercent.Value):0}%" : "---";
        }
        else if (selectedKey == "mem" && sample.Memory is { } mem)
        {
            detailReading.Text = $"{mem.UsedGiB:0.0} / {mem.TotalGiB:0.0} GB";
        }
    }

    public void RefreshTheme()
    {
        UpdateTheme();
        cpuMatrix.RefreshTheme();
        memRing.RefreshTheme();
    }

    private void AddCpuRailItem(HostInventorySnapshot inventory)
    {
        var spark = new VmMiniSparkline(transfer: false);
        var title = new TextBlock { Text = L10n.Get("Overview_Cpu"), FontSize = 13, FontWeight = FontWeights.Medium };
        var stat = new TextBlock { Text = "---%", FontSize = 11.5 };
        Typography.SetNumeralAlignment(stat, FontNumeralAlignment.Tabular);

        var item = CreateRailEntry(
            key: "cpu",
            category: RailCategory.Cpu,
            detailTitle: L10n.Get("Overview_Cpu"),
            detailSubtitle: inventory.CpuModel,
            detailGlyph: "\uF58E",
            detailGlyphRotation: 90,
            categoryColor: ColorForCategory(RailCategory.Cpu),
            sparkline: spark,
            titleText: title,
            subtitleText: null,
            statText: stat,
            tooltip: inventory.CpuModel);

        item.UpdateSample = sample =>
        {
            if (sample.CpuPercent is { } pct)
            {
                spark.Add(pct);
                string freq = sample.CpuFreqGHz is { } gz and > 0 ? $" {gz:0.00} GHz" : string.Empty;
                stat.Text = $"{Math.Round(pct):0}%{freq}";
            }

        };
    }

    private void AddMemoryItem(HostInventorySnapshot inventory)
    {
        const string key = "mem";
        var spark = new VmMiniSparkline(transfer: false);
        var title = new TextBlock { Text = L10n.Get("Overview_Memory"), FontSize = 13, FontWeight = FontWeights.Medium };
        string initialStat = inventory.TotalMemoryGiB > 0 ? $"{inventory.TotalMemoryGiB:0.0} GB" : "---";
        var stat = new TextBlock { Text = initialStat, FontSize = 11.5 };
        Typography.SetNumeralAlignment(stat, FontNumeralAlignment.Tabular);

        var item = CreateRailEntry(
            key: key,
            category: RailCategory.Memory,
            detailTitle: L10n.Get("Overview_Memory"),
            detailSubtitle: L10n.Format("Host_Mem_PhysicalSub", initialStat),
            detailGlyph: "\uEEA0",
            detailGlyphRotation: 0,
            categoryColor: ColorForCategory(RailCategory.Memory),
            sparkline: spark,
            titleText: title,
            subtitleText: null,
            statText: stat,
            tooltip: initialStat);

        item.UpdateSample = sample =>
        {
            if (sample.Memory is not { } mem) return;
            spark.Add(mem.Percent);
            stat.Text = $"{mem.UsedGiB:0.0}/{mem.TotalGiB:0.0} GB ({mem.Percent}%)";
        };
    }

    private void AddGpuItem(HostInventorySnapshot inventory, HostGpuInfo gpu, int index)
    {
        string key = $"gpu:{gpu.InstanceId}";
        string gpuTitle = inventory.Gpus.Count > 1 ? $"GPU {index}" : "GPU";
        var spark = new VmMiniSparkline(transfer: false);
        var title = new TextBlock { Text = gpuTitle, FontSize = 13, FontWeight = FontWeights.Medium };
        var sub = new TextBlock { Text = HostUiHelpers.MiddleTrim(gpu.Name, 24), FontSize = 11.5, TextTrimming = TextTrimming.Clip };
        var stat = new TextBlock { Text = "—", FontSize = 11.5 };
        Typography.SetNumeralAlignment(stat, FontNumeralAlignment.Tabular);

        var item = CreateRailEntry(
            key: key,
            category: RailCategory.Gpu,
            detailTitle: gpuTitle,
            detailSubtitle: gpu.Name,
            detailGlyph: "\uF211",
            detailGlyphRotation: 0,
            categoryColor: ColorForCategory(RailCategory.Gpu),
            sparkline: spark,
            titleText: title,
            subtitleText: sub,
            statText: stat,
            tooltip: gpu.Name);

        var chart3d = new VmUsageChart(L10n.Get("Overview_Gpu3d"), VmUsageChartKind.Gpu, chartHeight: 82);
        var chartCopy = new VmUsageChart(L10n.Get("Overview_GpuCopy"), VmUsageChartKind.Gpu, chartHeight: 82);
        var chartEnc = new VmUsageChart(L10n.Get("Overview_GpuEncode"), VmUsageChartKind.Gpu, chartHeight: 82);
        var chartDec = new VmUsageChart(L10n.Get("Overview_GpuDecode"), VmUsageChartKind.Gpu, chartHeight: 82);

        var chartsGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        chartsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        chartsGrid.ColumnDefinitions.Add(new ColumnDefinition());
        chartsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chartsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(chart3d, 0); Grid.SetColumn(chart3d, 0);
        Grid.SetRow(chartCopy, 0); Grid.SetColumn(chartCopy, 1);
        Grid.SetRow(chartEnc, 1); Grid.SetColumn(chartEnc, 0);
        Grid.SetRow(chartDec, 1); Grid.SetColumn(chartDec, 1);
        chartsGrid.Children.Add(chart3d);
        chartsGrid.Children.Add(chartCopy);
        chartsGrid.Children.Add(chartEnc);
        chartsGrid.Children.Add(chartDec);

        var noCounterNote = new TextBlock
        {
            Text = L10n.Get("Host_Gpu_UnavailableNote"),
            FontSize = 12,
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        string vramText = gpu.MemoryBytes is { } bytes
            ? (bytes >= 1024UL * 1024 * 1024 ? $"{bytes / 1073741824.0:0.#} GB" : $"{bytes / 1048576.0:0} MB")
            : "---";
        string partText = L10n.Get(gpu.IsPartitionable ? "Host_Gpu_Supported" : "Host_Gpu_Unsupported");
        string policyText = L10n.Get(inventory.GpuRestrictionsRelaxed switch { true => "Host_Gpu_PolicyUnlocked", false => "Host_Gpu_PolicyDefault", null => "Common_Unavailable" });

        var kvBorder = new Border();
        var panel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        var scaleHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(noCounterNote);
        panel.Children.Add(scaleHost);
        panel.Children.Add(chartsGrid);
        panel.Children.Add(kvBorder);
        detailBodyHost.Children.Add(panel);

        void RenderGpuKv()
        {
            scaleHost.Content = HostUiHelpers.BuildScaleBar(ActualTheme, L10n.Get("Host_Gpu_Scale"), "100%");
            kvBorder.Child = HostUiHelpers.BuildTitleFirstKvGrid(ActualTheme,
            (HostUiHelpers.KvTileSpec[])[
                new(L10n.Get("Overview_GpuChipVendor"), gpu.Manu, CopyValue: string.IsNullOrWhiteSpace(gpu.Manu) ? null : gpu.Manu),
                new(L10n.Get("Overview_GpuBoardVendor"), gpu.Vendor, CopyValue: string.IsNullOrWhiteSpace(gpu.Vendor) ? null : gpu.Vendor),
                new(L10n.Get("Overview_GpuDedicatedVram"), vramText),
                new(L10n.Get("Overview_GpuDriverVersion"), gpu.DriverVersion, CopyValue: string.IsNullOrWhiteSpace(gpu.DriverVersion) ? null : gpu.DriverVersion),
                new(L10n.Get("Host_Gpu_Partition"), partText),
                new(L10n.Get("Host_Gpu_Policy"), policyText),
                new(L10n.Get("Overview_GpuDevicePath"), gpu.InstanceId, Wide: true, CopyValue: string.IsNullOrWhiteSpace(gpu.InstanceId) ? null : gpu.InstanceId)
            ], CopySuccessToast, CopyErrorBanner);
        }

        void AddReading(VmUsageChart chart, double? value)
        {
            if (value is double pct) chart.Add(pct, $"{pct:0}%");
        }
        chartsGrid.Visibility = scaleHost.Visibility = Visibility.Collapsed;
        noCounterNote.Visibility = Visibility.Visible;
        RenderGpuKv();
        detailPanels[key] = new DetailPanelEntry
        {
            Key = key,
            Category = RailCategory.Gpu,
            Root = panel,
            ApplySample = _ => { },
            FormatReading = _ => stat.Text,
            RefreshTheme = () =>
            {
                Color? custom = HostUiHelpers.IsAccentEnabled ? null : ColorForCategory(RailCategory.Gpu);
                chart3d.SetAccentColor(custom);
                chartCopy.SetAccentColor(custom);
                chartEnc.SetAccentColor(custom);
                chartDec.SetAccentColor(custom);
                RenderGpuKv();
            }
        };

        item.UpdateSample = sample =>
        {
            if (!sample.Gpus.TryGetValue(gpu.InstanceId, out var g))
            {
                noCounterNote.Visibility = Visibility.Visible;
                return;
            }
            chartsGrid.Visibility = scaleHost.Visibility = Visibility.Visible;
            noCounterNote.Visibility = Visibility.Collapsed;
            AddReading(chart3d, g.Pct3D);
            AddReading(chartCopy, g.PctCopy);
            AddReading(chartEnc, g.PctEnc);
            AddReading(chartDec, g.PctDec);
            if (new[] { g.Pct3D, g.PctCopy, g.PctEnc, g.PctDec }.Max() is double peak)
            {
                spark.Add(peak);
                stat.Text = $"{peak:0}%";
            }
        };
    }

    private void AddDiskItem(HostInventorySnapshot inventory, HostDiskInfo disk)
    {
        string key = $"disk:{disk.Number}";
        string diskTitle = L10n.Format("Host_Disk_Title", disk.Number);
        bool isOffline = disk.IsOffline == true;

        var spark = new VmMiniSparkline(transfer: true);
        var title = new TextBlock { Text = diskTitle, FontSize = 13, FontWeight = FontWeights.Medium };
        var sub = new TextBlock { Text = HostUiHelpers.MiddleTrim(disk.FriendlyName, 24), FontSize = 11.5, TextTrimming = TextTrimming.Clip };
        var stat = new TextBlock { Text = isOffline ? L10n.Get("Host_Disk_Offline") : "—", FontSize = 11.5 };
        Typography.SetNumeralAlignment(stat, FontNumeralAlignment.Tabular);

        var item = CreateRailEntry(
            key: key,
            category: RailCategory.Disk,
            detailTitle: diskTitle,
            detailSubtitle: disk.FriendlyName,
            detailGlyph: "\uEDA2",
            detailGlyphRotation: 0,
            categoryColor: ColorForCategory(RailCategory.Disk),
            sparkline: spark,
            titleText: title,
            subtitleText: sub,
            statText: stat,
            tooltip: $"{disk.FriendlyName}\n\\\\.\\PhysicalDrive{disk.Number}");

        var readChart = new VmUsageChart(L10n.Get("Host_Disk_Read"), VmUsageChartKind.Transfer, chartHeight: 136);
        var writeChart = new VmUsageChart(L10n.Get("Host_Disk_Write"), VmUsageChartKind.Transfer, chartHeight: 136);

        var transferGrid = new Grid { ColumnSpacing = 10 };
        transferGrid.ColumnDefinitions.Add(new ColumnDefinition());
        transferGrid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(readChart, 0);
        Grid.SetColumn(writeChart, 1);
        transferGrid.Children.Add(readChart);
        transferGrid.Children.Add(writeChart);

        string sizeText = $"{disk.SizeGiB:0.##} GB";
        string autoMountText = inventory.VolumeAutoMountEnabled switch
        {
            true => L10n.Get("Common_On"),
            false => L10n.Get("Common_Off"),
            _ => "---"
        };
        string nvmeText = inventory.NativeNvmeEnabled switch
        {
            true => L10n.Get("Common_On"),
            false => L10n.Get("Common_Off"),
            _ => "---"
        };

        var kvBorder = new Border();
        var panel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
        var scaleHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(scaleHost);
        panel.Children.Add(transferGrid);
        panel.Children.Add(kvBorder);
        detailBodyHost.Children.Add(panel);

        void RenderDiskKv()
        {
            scaleHost.Content = HostUiHelpers.BuildScaleBar(ActualTheme, L10n.Get("Host_Disk_Scale"), sizeText);
            kvBorder.Child = HostUiHelpers.BuildTitleFirstKvGrid(ActualTheme,
            (HostUiHelpers.KvTileSpec[])[
                new(L10n.Get("Host_Disk_Status"), disk.Status, Caution: isOffline),
                new(L10n.Get("Host_Disk_Capacity"), sizeText),
                new(L10n.Get("Host_Disk_Passthrough"), L10n.Get(disk.CanPassthrough ? "Host_Disk_PassthroughOk" : "Host_Disk_PassthroughNo")),
                new(L10n.Get("Host_Disk_AutoMount"), autoMountText),
                new(L10n.Get("Host_Disk_NativeNvme"), nvmeText),
                new(L10n.Get("Host_Disk_Serial"), disk.SerialNumber, CopyValue: string.IsNullOrWhiteSpace(disk.SerialNumber) ? null : disk.SerialNumber),
                new(L10n.Get("Host_Disk_Path"), string.IsNullOrWhiteSpace(disk.UniqueId) ? $"\\\\.\\PhysicalDrive{disk.Number}" : disk.UniqueId, Wide: true,
                    CopyValue: string.IsNullOrWhiteSpace(disk.UniqueId) ? $"\\\\.\\PhysicalDrive{disk.Number}" : disk.UniqueId)
            ], CopySuccessToast, CopyErrorBanner);
        }

        RenderDiskKv();
        detailPanels[key] = new DetailPanelEntry
        {
            Key = key,
            Category = RailCategory.Disk,
            Root = panel,
            ApplySample = sample =>
            {
                if (!isOffline && sample.Disks.TryGetValue(disk.Number, out var m))
                {
                    readChart.Add(m.ReadBytesPerSec, HostUiHelpers.FormatBytesPerSec(m.ReadBytesPerSec));
                    writeChart.Add(m.WriteBytesPerSec, HostUiHelpers.FormatBytesPerSec(m.WriteBytesPerSec));
                }
                else if (isOffline)
                {
                    readChart.SetInactive();
                    writeChart.SetInactive();
                }
            },
            FormatReading = sample =>
            {
                if (isOffline) return L10n.Get("Host_Disk_Offline");
                return sample.Disks.TryGetValue(disk.Number, out var m)
                    ? $"{Math.Round(m.ActivePercent):0}% / {sizeText}"
                    : sizeText;
            },
            RefreshTheme = () =>
            {
                Color? custom = HostUiHelpers.IsAccentEnabled ? null : ColorForCategory(RailCategory.Disk);
                readChart.SetAccentColor(custom);
                writeChart.SetAccentColor(custom);
                RenderDiskKv();
            }
        };

        item.UpdateSample = sample =>
        {
            if (isOffline)
            {
                spark.SetInactive();
                stat.Text = L10n.Get("Host_Disk_Offline");
                return;
            }
            if (sample.Disks.TryGetValue(disk.Number, out var m))
            {
                spark.Add(m.ReadBytesPerSec + m.WriteBytesPerSec);
                stat.Text = $"{Math.Round(m.ActivePercent):0}%";
                if (selectedKey != key)
                {
                    readChart.Add(m.ReadBytesPerSec, HostUiHelpers.FormatBytesPerSec(m.ReadBytesPerSec));
                    writeChart.Add(m.WriteBytesPerSec, HostUiHelpers.FormatBytesPerSec(m.WriteBytesPerSec));
                }
            }
        };
    }

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

        RenderNicKv();
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

        RenderSwitchKv();
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

    private RailEntry CreateRailEntry(
        string key,
        RailCategory category,
        string detailTitle,
        string detailSubtitle,
        string detailGlyph,
        double detailGlyphRotation,
        Color categoryColor,
        VmMiniSparkline sparkline,
        TextBlock titleText,
        TextBlock? subtitleText,
        TextBlock statText,
        string? tooltip)
    {
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        info.Children.Add(titleText);
        if (subtitleText != null) info.Children.Add(subtitleText);
        info.Children.Add(statText);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sparkline.Margin = new Thickness(0, 0, 9, 0);
        Grid.SetColumn(sparkline, 0);
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
        if (!string.IsNullOrWhiteSpace(tooltip))
            ToolTipService.SetToolTip(button, tooltip);

        button.Click += (_, _) => SelectRailKey(key);
        railPanel.Children.Add(button);

        var entry = new RailEntry
        {
            Key = key,
            Category = category,
            DetailTitle = detailTitle,
            DetailSubtitle = detailSubtitle,
            DetailGlyph = detailGlyph,
            DetailGlyphRotation = detailGlyphRotation,
            CategoryColor = categoryColor,
            Button = button,
            Sparkline = sparkline,
            TitleText = titleText,
            SubtitleText = subtitleText,
            StatText = statText
        };
        railEntries.Add(entry);
        return entry;
    }

    private void SelectRailKey(string key)
    {
        selectedKey = key;
        bool isCpu = key == "cpu";
        bool isMem = key == "mem";
        bool isFullCanvas = isCpu || isMem;

        // CPU and Memory visualizations fill the detail card without consuming top header height.
        detailHeader.Visibility = isFullCanvas ? Visibility.Collapsed : Visibility.Visible;
        cpuHost.Visibility = isCpu ? Visibility.Visible : Visibility.Collapsed;
        memHost.Visibility = isMem ? Visibility.Visible : Visibility.Collapsed;
        detailBodyScroll.Visibility = isFullCanvas ? Visibility.Collapsed : Visibility.Visible;
        detailRoot.RowSpacing = isFullCanvas ? 0 : 10;

        foreach (var pair in detailPanels)
            pair.Value.Root.Visibility = pair.Key == key ? Visibility.Visible : Visibility.Collapsed;

        var selectedEntry = railEntries.FirstOrDefault(e => e.Key == key);
        if (selectedEntry != null && !isFullCanvas)
        {
            detailIcon.Glyph = selectedEntry.DetailGlyph;
            detailIconRotation.Angle = selectedEntry.DetailGlyphRotation;
            detailTitle.Text = selectedEntry.DetailTitle;
            detailSubtitle.Text = selectedEntry.DetailSubtitle;
            detailSubtitle.Visibility = string.IsNullOrWhiteSpace(selectedEntry.DetailSubtitle) ? Visibility.Collapsed : Visibility.Visible;
            if (lastSample != null && detailPanels.TryGetValue(key, out var panel))
            {
                detailReading.Text = panel.FormatReading(lastSample);
            }
        }

        UpdateTheme();
    }

    private void UpdateTheme()
    {
        bool light = ActualTheme == ElementTheme.Light;
        bool accentMode = HostUiHelpers.IsAccentEnabled;

        detailCard.Background = HostUiHelpers.TryResourceBrush(ActualTheme, "VmCardBrush",
            light ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
        detailCard.BorderBrush = HostUiHelpers.TryResourceBrush(ActualTheme, "VmStrokeBrush",
            light ? Color.FromArgb(0x0F, 0, 0, 0) : Color.FromArgb(0x19, 0, 0, 0));
        detailHeader.BorderBrush = HostUiHelpers.TryResourceBrush(ActualTheme, "DividerStrokeColorDefaultBrush",
            Color.FromArgb(0x22, 0x80, 0x80, 0x80));
        detailIconBox.Background = HostUiHelpers.TryResourceBrush(ActualTheme, "SubtleFillColorSecondaryBrush",
            Color.FromArgb(0x12, 0x88, 0x88, 0x88));
        detailIconBox.BorderBrush = HostUiHelpers.TryResourceBrush(ActualTheme, "DividerStrokeColorDefaultBrush",
            Color.FromArgb(0x22, 0x80, 0x80, 0x80));

        Color primary = HostUiHelpers.ThemeColor(ActualTheme, "TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White);
        Color secondary = HostUiHelpers.ThemeColor(ActualTheme, "TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        Color accent = HostUiHelpers.ThemeColor(ActualTheme, "VmAccentBrush", Color.FromArgb(255, 0x4C, 0xC2, 0xFF));
        Color headerBg = HostUiHelpers.ThemeColor(ActualTheme, "VmHeaderCheckedBrush", accent);
        Color headerFg = HostUiHelpers.ThemeColor(ActualTheme, "VmHeaderForegroundBrush", Colors.White);

        detailIcon.Foreground = new SolidColorBrush(primary);
        detailTitle.Foreground = new SolidColorBrush(primary);
        detailSubtitle.Foreground = new SolidColorBrush(secondary);
        detailReading.Foreground = new SolidColorBrush(primary);

        Color neutralBg = light ? Color.FromArgb(0x06, 0, 0, 0) : Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF);
        Color neutralBorder = Color.FromArgb(0x26, 0x88, 0x88, 0x88);
        var transparent = new SolidColorBrush(Colors.Transparent);

        foreach (var entry in railEntries)
        {
            bool sel = entry.Key == selectedKey;
            if (accentMode)
            {
                if (sel)
                {
                    entry.Button.Background = new SolidColorBrush(headerBg);
                    entry.Button.BorderBrush = transparent;
                    entry.TitleText.FontWeight = FontWeights.SemiBold;
                    entry.TitleText.Foreground = new SolidColorBrush(headerFg);
                    entry.StatText.Foreground = new SolidColorBrush(headerFg);
                    entry.StatText.Opacity = 0.9;
                    if (entry.SubtitleText != null)
                    {
                        entry.SubtitleText.Foreground = new SolidColorBrush(headerFg);
                        entry.SubtitleText.Opacity = 0.9;
                    }
                    entry.Sparkline.SetPalette(
                        Color.FromArgb(26, headerFg.R, headerFg.G, headerFg.B),
                        Color.FromArgb(77, headerFg.R, headerFg.G, headerFg.B),
                        headerFg,
                        Color.FromArgb(51, headerFg.R, headerFg.G, headerFg.B),
                        Color.FromArgb(80, headerFg.R, headerFg.G, headerFg.B));
                }
                else
                {
                    entry.Button.Background = transparent;
                    entry.Button.BorderBrush = transparent;
                    entry.TitleText.FontWeight = FontWeights.Medium;
                    entry.TitleText.Foreground = new SolidColorBrush(primary);
                    entry.StatText.Foreground = new SolidColorBrush(secondary);
                    entry.StatText.Opacity = 1;
                    if (entry.SubtitleText != null)
                    {
                        entry.SubtitleText.Foreground = new SolidColorBrush(secondary);
                        entry.SubtitleText.Opacity = 1;
                    }
                    entry.Sparkline.SetPalette(
                        neutralBg,
                        neutralBorder,
                        accent,
                        Color.FromArgb(0x40, accent.R, accent.G, accent.B),
                        Color.FromArgb(0x50, secondary.R, secondary.G, secondary.B));
                }
            }
            else
            {
                entry.Button.Background = sel
                    ? HostUiHelpers.TryResourceBrush(ActualTheme, "SubtleFillColorSecondaryBrush", Color.FromArgb(0x10, 0x80, 0x80, 0x80))
                    : transparent;
                entry.Button.BorderBrush = sel
                    ? HostUiHelpers.TryResourceBrush(ActualTheme, "ControlStrokeColorDefaultBrush", neutralBorder)
                    : transparent;
                entry.TitleText.FontWeight = FontWeights.Medium;
                entry.TitleText.Foreground = new SolidColorBrush(primary);
                entry.StatText.Foreground = new SolidColorBrush(secondary);
                entry.StatText.Opacity = 1;
                if (entry.SubtitleText != null)
                {
                    entry.SubtitleText.Foreground = new SolidColorBrush(secondary);
                    entry.SubtitleText.Opacity = 1;
                }
                entry.Sparkline.SetPalette(
                    neutralBg,
                    neutralBorder,
                    entry.CategoryColor,
                    Color.FromArgb(0x45, entry.CategoryColor.R, entry.CategoryColor.G, entry.CategoryColor.B),
                    Color.FromArgb(0x50, secondary.R, secondary.G, secondary.B));
            }
        }

        foreach (var pair in detailPanels.Values)
            pair.RefreshTheme?.Invoke();
    }

    private void UpdateResponsiveLayout()
    {
        double width = shellGrid.ActualWidth;
        if (width <= 0) return;

        if (width < 560)
        {
            railColumn.Width = new GridLength(1, GridUnitType.Star);
            detailColumn.Width = new GridLength(0);
            topRow.Height = GridLength.Auto;
            bottomRow.Height = new GridLength(1, GridUnitType.Star);
            railScroll.MaxHeight = 176;
            Grid.SetRow(railScroll, 0);
            Grid.SetColumn(railScroll, 0);
            Grid.SetRow(detailCard, 1);
            Grid.SetColumn(detailCard, 0);
        }
        else
        {
            railColumn.Width = new GridLength(width < 680 ? 200 : 244);
            detailColumn.Width = new GridLength(1, GridUnitType.Star);
            topRow.Height = new GridLength(1, GridUnitType.Star);
            bottomRow.Height = new GridLength(0);
            railScroll.MaxHeight = double.PositiveInfinity;
            Grid.SetRow(railScroll, 0);
            Grid.SetColumn(railScroll, 0);
            Grid.SetRow(detailCard, 0);
            Grid.SetColumn(detailCard, 1);
        }
    }

    private void CopySuccessToast(string val) => feedbackHost?.ShowSuccessToast(L10n.Format("Host_CopiedNotice", val));
    private void CopyErrorBanner(string msg) => feedbackHost?.ShowSettingsNotice(msg, InfoBarSeverity.Error);
}
