
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
}
