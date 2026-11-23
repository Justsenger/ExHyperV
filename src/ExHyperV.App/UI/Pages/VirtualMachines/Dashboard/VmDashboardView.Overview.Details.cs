
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Feedback;
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
        Brush primaryBrush = ThemeHelper.TryBrush("TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        Brush secondaryBrush = ThemeHelper.TryBrush("TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        Brush cautionBrush = ThemeHelper.TryBrush("SystemFillColorCautionBrush",
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
        if (monospace) tb.FontFamily = AppFonts.Monospace;
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

    private async void OnOverviewGearClick(object sender, RoutedEventArgs e) => await UiEvent.RunAsync(async () =>
    {
        if (selectedOverviewKey == "cpu") await JumpToSettingsAsync("processor");
        else if (selectedOverviewKey == "mem") await JumpToSettingsAsync("memory");
        else if (selectedOverviewKey.StartsWith("disk:", StringComparison.Ordinal)) await JumpToSettingsAsync("devices", "storage");
        else if (selectedOverviewKey.StartsWith("net:", StringComparison.Ordinal)) await JumpToSettingsAsync("devices", "network");
        else if (selectedOverviewKey.StartsWith("gpu:", StringComparison.Ordinal)) await JumpToSettingsAsync("devices", "gpu");
    }, message => ShowError(message));
}
