
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

        string cpuHeaderTitle = string.IsNullOrWhiteSpace(inventory.CpuModel) ? L10n.Get("Overview_Cpu") : inventory.CpuModel;

        var item = CreateRailEntry(
            key: "cpu",
            category: RailCategory.Cpu,
            detailTitle: cpuHeaderTitle,
            detailSubtitle: string.Empty,
            detailGlyph: "\uF58E",
            detailGlyphRotation: 90,
            categoryColor: ColorForCategory(RailCategory.Cpu),
            sparkline: spark,
            titleText: title,
            subtitleText: null,
            statText: stat,
            tooltip: cpuHeaderTitle);

        int lpCount = Math.Max(1, inventory.LogicalProcessorCount);
        detailFactories["cpu"] = () =>
        {
            int cols = lpCount <= 4 ? 2 : lpCount > 32 ? 8 : lpCount > 16 ? 6 : 4;
            var coreCharts = new VmUsageChart[lpCount];
            var coreGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
            for (int c = 0; c < cols; c++)
                coreGrid.ColumnDefinitions.Add(new ColumnDefinition());
            int rows = (int)Math.Ceiling(lpCount / (double)cols);
            for (int r = 0; r < rows; r++)
                coreGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int i = 0; i < lpCount; i++)
            {
                var chart = new VmUsageChart(i.ToString(), VmUsageChartKind.Processor, chartHeight: VmUsageChart.ProcessorHeight);
                coreCharts[i] = chart;
                Grid.SetRow(chart, i / cols);
                Grid.SetColumn(chart, i % cols);
                coreGrid.Children.Add(chart);
            }

            string? lastFreqRendered = null;
            string currentFreqText = "---";
            HostCpuThreadReport? lastCpuReportRef = null;
            var kvBorder = new Border();
            var panel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
            panel.Children.Add(coreGrid);
            panel.Children.Add(kvBorder);

            void RenderCpuKv()
            {
                string virtText = L10n.Get(inventory.CpuVirtOk ? "Host_Perf_Enabled" : "Host_Perf_Disabled");
                bool hvReady = inventory.Diagnostics?.IsReady == true;
                string hvText = L10n.Get(hvReady ? "Host_HyperVReady" : "Host_HyperVNotReady");
                string schedText = HostUiHelpers.FormatSchedulerLabel(inventory.RunningScheduler, full: true);
                string numaText = L10n.Get(inventory.NumaSpanningEnabled switch
                {
                    true => "Common_On",
                    false => "Common_Off",
                    null => "Common_Unavailable"
                });
                string archText = inventory.Arch.ToString().ToUpperInvariant();
                string osText = string.IsNullOrWhiteSpace(inventory.OsCaption) ? "---" : inventory.OsCaption;

                kvBorder.Child = HostUiHelpers.BuildTitleFirstKvGrid(ActualTheme,
                (HostUiHelpers.KvTileSpec[])[
                    new(L10n.Get("Host_Perf_LogicalProcessors"), L10n.Format("Host_Perf_ThreadsVal", lpCount)),
                    new(L10n.Get("Host_Perf_CurFreq"), currentFreqText),
                    new(L10n.Get("Host_Env_CpuVirt"), virtText, Caution: !inventory.CpuVirtOk),
                    new(L10n.Get("Host_Perf_Platform"), hvText, Caution: !hvReady),
                    new(L10n.Get("Host_Perf_Scheduler"), schedText),
                    new(L10n.Get("Host_Mem_NumaSpanning"), numaText),
                    new(L10n.Get("Host_Arch"), archText),
                    new(L10n.Get("Host_Os"), osText, CopyValue: string.IsNullOrWhiteSpace(inventory.OsCaption) ? null : inventory.OsCaption)
                ], CopySuccessToast, CopyErrorBanner);
            }

            return new DetailPanelEntry
            {
                Key = "cpu",
                Category = RailCategory.Cpu,
                Root = panel,
                ApplySample = sample =>
                {
                    if (sample.CpuReport is { Sample.IsValid: true } report && !ReferenceEquals(report, lastCpuReportRef))
                    {
                        lastCpuReportRef = report;
                        foreach (var t in report.Threads)
                        {
                            if (t.LogicalProcessorIndex >= 0 && t.LogicalProcessorIndex < coreCharts.Length && t.Sample.IsValid)
                            {
                                double clamped = Math.Clamp(t.TotalUsagePercent, 0, 100);
                                coreCharts[t.LogicalProcessorIndex].Add(clamped, $"{Math.Round(clamped):0}%");
                            }
                        }
                    }
                    string nextFreq = sample.CpuFreqGHz is { } gz and > 0 ? $"{gz:0.00} GHz" : "---";
                    if (!string.Equals(nextFreq, lastFreqRendered, StringComparison.Ordinal))
                    {
                        currentFreqText = nextFreq;
                        lastFreqRendered = nextFreq;
                        RenderCpuKv();
                    }
                },
                FormatReading = sample => sample.CpuPercent is { } pct ? $"{Math.Round(pct):0}%" : "---%",
                RefreshTheme = () =>
                {
                    Color? custom = HostUiHelpers.IsAccentEnabled ? null : ColorForCategory(RailCategory.Cpu);
                    foreach (var ch in coreCharts) ch.SetAccentColor(custom);
                    RenderCpuKv();
                }
            };
        };

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

        detailFactories[key] = () =>
        {
            var trendChart = new VmUsageChart(L10n.Get("Host_Mem_Trend"), VmUsageChartKind.Memory, chartHeight: 136);
            string usedStr = "---";
            string availStr = "---";
            string totalStr = initialStat;
            string? lastKvSig = null;
            HostMemoryUsage? lastMemRef = null;

            var kvBorder = new Border();
            var panel = new StackPanel { Spacing = 10, Visibility = Visibility.Collapsed };
            panel.Children.Add(trendChart);
            panel.Children.Add(kvBorder);

            void RenderMemKv()
            {
                string numaText = L10n.Get(inventory.NumaSpanningEnabled switch
                {
                    true => "Common_On",
                    false => "Common_Off",
                    null => "Common_Unavailable"
                });
                string planText = string.IsNullOrWhiteSpace(inventory.ActivePowerPlanName) ? "---" : inventory.ActivePowerPlanName;

                kvBorder.Child = HostUiHelpers.BuildTitleFirstKvGrid(ActualTheme,
                (HostUiHelpers.KvTileSpec[])[
                    new(L10n.Get("Host_Mem_InUse"), usedStr),
                    new(L10n.Get("Host_Mem_Available"), availStr),
                    new(L10n.Get("Host_Mem_PhysicalCap"), totalStr),
                    new(L10n.Get("Host_Mem_NumaSpanning"), numaText),
                    new(L10n.Get("Host_Mem_PowerPlan"), planText)
                ], CopySuccessToast, CopyErrorBanner);
            }

            return new DetailPanelEntry
            {
                Key = key,
                Category = RailCategory.Memory,
                Root = panel,
                ApplySample = sample =>
                {
                    if (sample.Memory is not { } m || ReferenceEquals(m, lastMemRef)) return;
                    lastMemRef = m;
                    trendChart.Add(m.Percent, $"{m.Percent}%");
                    usedStr = $"{m.UsedGiB:0.0} GB";
                    availStr = $"{m.AvailableGiB:0.0} GB";
                    totalStr = $"{m.TotalGiB:0.0} GB";
                    string sig = $"{usedStr}|{availStr}|{totalStr}";
                    if (!string.Equals(sig, lastKvSig, StringComparison.Ordinal))
                    {
                        lastKvSig = sig;
                        RenderMemKv();
                    }
                },
                FormatReading = sample => sample.Memory is { } m
                    ? $"{m.UsedGiB:0.0} / {m.TotalGiB:0.0} GB"
                    : initialStat,
                RefreshTheme = () =>
                {
                    Color? custom = HostUiHelpers.IsAccentEnabled ? null : ColorForCategory(RailCategory.Memory);
                    trendChart.SetAccentColor(custom);
                    RenderMemKv();
                }
            };
        };

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

        detailFactories[key] = () =>
        {
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
                Visibility = Visibility.Visible
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
            chartsGrid.Visibility = scaleHost.Visibility = Visibility.Collapsed;

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

            static void AddReading(VmUsageChart chart, double? value)
            {
                if (value is double pct && double.IsFinite(pct))
                {
                    double clamped = Math.Clamp(pct, 0, 100);
                    chart.Add(clamped, $"{clamped:0}%");
                }
            }

            return new DetailPanelEntry
            {
                Key = key,
                Category = RailCategory.Gpu,
                Root = panel,
                ApplySample = sample =>
                {
                    if (!sample.Gpus.TryGetValue(gpu.InstanceId, out var g))
                    {
                        chartsGrid.Visibility = scaleHost.Visibility = Visibility.Collapsed;
                        noCounterNote.Visibility = Visibility.Visible;
                        return;
                    }
                    chartsGrid.Visibility = scaleHost.Visibility = Visibility.Visible;
                    noCounterNote.Visibility = Visibility.Collapsed;
                    AddReading(chart3d, g.Pct3D);
                    AddReading(chartCopy, g.PctCopy);
                    AddReading(chartEnc, g.PctEnc);
                    AddReading(chartDec, g.PctDec);
                },
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
        };

        item.UpdateSample = sample =>
        {
            if (!sample.Gpus.TryGetValue(gpu.InstanceId, out var g)) return;
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

        string sizeText = $"{disk.SizeGiB:0.##} GB";
        detailFactories[key] = () =>
        {
            var readChart = new VmUsageChart(L10n.Get("Host_Disk_Read"), VmUsageChartKind.Transfer, chartHeight: 136);
            var writeChart = new VmUsageChart(L10n.Get("Host_Disk_Write"), VmUsageChartKind.Transfer, chartHeight: 136);

            var transferGrid = new Grid { ColumnSpacing = 10 };
            transferGrid.ColumnDefinitions.Add(new ColumnDefinition());
            transferGrid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(readChart, 0);
            Grid.SetColumn(writeChart, 1);
            transferGrid.Children.Add(readChart);
            transferGrid.Children.Add(writeChart);

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

            return new DetailPanelEntry
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
            }
        };
    }
}
