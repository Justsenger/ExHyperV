
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed partial class HostCpuThreadMatrixView : UserControl
{
    private const string HostKey = "__host__";
    private const string GuestPoolKey = "__guest__";
    private const string OtherKey = "__other__";

    private static readonly Color GuestPoolColor = Color.FromArgb(255, 0x60, 0xCD, 0xFF);
    private static readonly Color OtherColor = Color.FromArgb(255, 0x4B, 0x55, 0x63);
    private static readonly Color[] VmPalette =
    [
        Color.FromArgb(255, 0x6C, 0xCB, 0x5F),
        Color.FromArgb(255, 0x4A, 0xA8, 0x6C),
        Color.FromArgb(255, 0x34, 0x8E, 0x7B),
        Color.FromArgb(255, 0x3D, 0x7A, 0x8A),
        Color.FromArgb(255, 0x4B, 0x6B, 0x7F),
        Color.FromArgb(255, 0x56, 0x64, 0x75),
        Color.FromArgb(255, 0x5C, 0x61, 0x6B)
    ];

    private sealed class LegendChip
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public required Color Color { get; init; }
        public required Border ButtonBorder { get; init; }
        public required Border SwatchBar { get; init; }
        public required TextBlock NameText { get; init; }
        public bool IsHovered { get; set; }
    }

    private sealed record ConsumerDescriptor(string Key, string Label, Color Color, bool IsPinned);

    private readonly Grid root = new() { ColumnSpacing = 14, RowSpacing = 12 };
    private readonly ScrollViewer tileScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly Grid tileGrid = new() { VerticalAlignment = VerticalAlignment.Top };
    private readonly Border legendBox = new()
    {
        Width = 186,
        BorderThickness = new Thickness(1, 0, 0, 0),
        Padding = new Thickness(12, 0, 0, 0)
    };
    private readonly Grid legendContainer = new() { RowSpacing = 8 };
    private readonly Border hostPinBox = new()
    {
        BorderThickness = new Thickness(0, 0, 0, 1),
        Padding = new Thickness(0, 0, 0, 8)
    };
    private readonly StackPanel hostPinStack = new() { Spacing = 6 };
    private readonly ScrollViewer legendScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly Grid legendGrid = new() { ColumnSpacing = 6, RowSpacing = 6, VerticalAlignment = VerticalAlignment.Top };

    private readonly List<HostCpuRingTile> tiles = [];
    private readonly Dictionary<Guid, int> vmColorSlots = new();
    private readonly List<LegendChip> legendChips = [];
    private IReadOnlyList<ConsumerDescriptor> activeConsumers = [];
    private HostCpuThreadReport? lastReport;
    private readonly Dictionary<int, HostCpuThreadSnapshot> lastValidThreads = new();
    private int lastLayoutColumns = -1;
    private int lastLegendColumns = -1;
    private bool isNarrowStacked;
    private double lastAvailableWidth = -1;
    private double lastAvailableHeight = -1;
    private int nextColorSlot;

    private string? hoveredKey;
    private string? selectedKey;

    public HostCpuThreadMatrixView()
    {
        tileScroll.Content = tileGrid;
        tileScroll.AttachScrollbarClearance(tileGrid, bottomSpacing: 0);

        hostPinBox.Child = hostPinStack;

        legendContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        legendContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        Grid.SetRow(hostPinBox, 0);
        legendContainer.Children.Add(hostPinBox);

        legendScroll.Content = legendGrid;
        legendScroll.AttachScrollbarClearance(legendGrid, bottomSpacing: 0);
        Grid.SetRow(legendScroll, 1);
        legendContainer.Children.Add(legendScroll);

        legendBox.Child = legendContainer;

        root.Children.Add(tileScroll);
        root.Children.Add(legendBox);
        ApplyOuterPanePlacement(force: true);

        Content = root;

        root.SizeChanged += (_, _) => ApplyOuterPanePlacement(force: false);
        tileScroll.SizeChanged += (_, _) => ArrangeTiles();
        legendScroll.SizeChanged += (_, _) => ArrangeLegend();
        ActualThemeChanged += (_, _) => RefreshVisuals();
        ApplyThemeBrushes();
    }

    public void InitializeTopology(int logicalProcessorCount)
    {
        int count = Math.Max(1, logicalProcessorCount);
        if (tiles.Count == count) return;
        RebuildTiles(count, lp => new HostCpuRingTile(lp, HyperVCpuMonitorService.GetCoreType(lp)));
        RenderThreadReadings();
    }

    public void UpdateReport(HostCpuThreadReport? report, int fallbackLogicalCount)
    {
        if (report == null)
        {
            InitializeTopology(fallbackLogicalCount);
            return;
        }

        lastReport = report;
        if (report.Sample.IsValid)
        {
            var active = report.Threads.Select(t => t.LogicalProcessorIndex).ToHashSet();
            foreach (int lp in lastValidThreads.Keys.Where(lp => !active.Contains(lp)).ToArray()) lastValidThreads.Remove(lp);
            foreach (var thread in report.Threads)
                if (thread.Sample.IsValid) lastValidThreads[thread.LogicalProcessorIndex] = thread;
        }
        int targetCount = report.Threads.Count > 0 ? report.Threads.Count : Math.Max(1, fallbackLogicalCount);
        if (tiles.Count != targetCount || (report.Threads.Count != 0 && !tiles.Select(t => t.LogicalProcessorIndex).SequenceEqual(report.Threads.Select(t => t.LogicalProcessorIndex))))
        {
            var typeLookup = report.Threads.ToDictionary(t => t.LogicalProcessorIndex, t => t.CoreType);
            RebuildTiles(targetCount, lp => new HostCpuRingTile(lp, typeLookup.TryGetValue(lp, out var t) ? t : HyperVCpuMonitorService.GetCoreType(lp)));
        }

        RenderThreadReadings();
    }

    public void RefreshTheme()
    {
        ApplyThemeBrushes();
        RenderThreadReadings();
    }

    private void RebuildTiles(int count, Func<int, HostCpuRingTile> factory)
    {
        tiles.Clear();
        tileGrid.Children.Clear();
        for (int i = 0; i < count; i++)
        {
            int index = lastReport != null && i < lastReport.Threads.Count
                ? lastReport.Threads[i].LogicalProcessorIndex
                : i;
            var tile = factory(index);
            tiles.Add(tile);
            tileGrid.Children.Add(tile);
        }
        lastLayoutColumns = -1;
        ArrangeTiles(force: true);
    }

    private void ApplyOuterPanePlacement(bool force)
    {
        bool narrow = root.ActualWidth > 0 && root.ActualWidth < 540;
        if (!force && narrow == isNarrowStacked) return;
        isNarrowStacked = narrow;

        root.ColumnDefinitions.Clear();
        root.RowDefinitions.Clear();

        if (narrow)
        {
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(tileScroll, 0);
            Grid.SetColumn(tileScroll, 0);

            legendBox.Width = double.NaN;
            legendBox.MaxHeight = 160;
            legendBox.BorderThickness = new Thickness(0, 1, 0, 0);
            legendBox.Padding = new Thickness(0, 10, 0, 0);
            legendScroll.MaxHeight = 112;
            Grid.SetRow(legendBox, 1);
            Grid.SetColumn(legendBox, 0);
        }
        else
        {
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            Grid.SetRow(tileScroll, 0);
            Grid.SetColumn(tileScroll, 0);

            legendBox.Width = 186;
            legendBox.MaxHeight = double.PositiveInfinity;
            legendBox.BorderThickness = new Thickness(1, 0, 0, 0);
            legendBox.Padding = new Thickness(12, 0, 0, 0);
            legendScroll.MaxHeight = double.PositiveInfinity;
            Grid.SetRow(legendBox, 0);
            Grid.SetColumn(legendBox, 1);
        }

        lastLegendColumns = -1;
        ArrangeLegend();
    }

    private (int Columns, CpuRingDensity Density, double Gap, double? AspectRatio) ResolveGridLayout(int count, double width)
    {
        if (count <= 16)
        {
            int cols = width > 0 && width < 440 ? 3 : 4;
            return (cols, CpuRingDensity.Cols4, 8, 1.34);
        }
        if (count <= 32)
        {
            int cols = width > 0 && width < 520 ? 4 : 6;
            return (cols, CpuRingDensity.Cols6, 7, 1.08);
        }
        int cols8 = width > 0 && width < 580 ? 4 : 8;
        return (cols8, CpuRingDensity.Cols8, 6, null);
    }

    private void ArrangeTiles(bool force = false)
    {
        if (tiles.Count == 0) return;
        double w = tileScroll.ViewportWidth > 0 ? tileScroll.ViewportWidth : tileScroll.ActualWidth;
        double h = tileScroll.ViewportHeight > 0 ? tileScroll.ViewportHeight : tileScroll.ActualHeight;
        if (!force && Math.Abs(w - lastAvailableWidth) < 1 && Math.Abs(h - lastAvailableHeight) < 1)
            return;
        lastAvailableWidth = w;
        lastAvailableHeight = h;

        var (cols, density, gap, aspectRatio) = ResolveGridLayout(tiles.Count, w);
        int rows = (int)Math.Ceiling(tiles.Count / (double)cols);

        tileGrid.ColumnSpacing = gap;
        tileGrid.RowSpacing = gap;

        if (cols != lastLayoutColumns || tileGrid.ColumnDefinitions.Count != cols || tileGrid.RowDefinitions.Count != rows)
        {
            tileGrid.ColumnDefinitions.Clear();
            for (int c = 0; c < cols; c++)
                tileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            tileGrid.RowDefinitions.Clear();
            for (int r = 0; r < rows; r++)
                tileGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            lastLayoutColumns = cols;
        }

        double effW = w > 0 ? Math.Max(0, w - tileGrid.Padding.Left - tileGrid.Padding.Right) : 0;
        double tileW = effW > 0 ? Math.Max(56, (effW - gap * Math.Max(0, cols - 1)) / cols) : 134;
        double targetHeight = aspectRatio is { } ratio
            ? (density == CpuRingDensity.Cols4
                ? Math.Clamp(tileW / ratio, 88, 126)
                : Math.Clamp(tileW / ratio, 76, 108))
            : double.NaN;

        for (int i = 0; i < tiles.Count; i++)
        {
            tiles[i].ApplyDensity(density);
            tiles[i].Height = targetHeight;
            Grid.SetRow(tiles[i], i / cols);
            Grid.SetColumn(tiles[i], i % cols);
        }
    }

    private Color GetVmColor(Guid vmId)
    {
        if (!vmColorSlots.TryGetValue(vmId, out int slot))
        {
            slot = nextColorSlot++;
            vmColorSlots[vmId] = slot;
        }
        return slot < VmPalette.Length ? VmPalette[slot] : OtherColor;
    }

    private Color HostAccentColor =>
        Application.Current.Resources.TryGetValue("VmAccentBrush", out var topVal) && topVal is SolidColorBrush topBrush
            ? topBrush.Color
            : ThemeHelper.ThemeColor(ActualTheme, "VmAccentBrush", Color.FromArgb(255, 0x4C, 0xC2, 0xFF));

    private void RenderThreadReadings()
    {
        Color hostColor = HostAccentColor;

        // Gather stable active consumers across all threads so the legend never flickers.
        var vmConsumers = new Dictionary<Guid, (string Name, int Slot, Color Color)>();
        bool anyUnattributedGuest = false;

        if (lastReport != null)
        {
            foreach (var thread in lastValidThreads.Values)
            {
                if (thread.UnattributedGuestUsagePercent > 0.25)
                    anyUnattributedGuest = true;
                foreach (var c in thread.Consumers)
                {
                    if (!c.IsHost && c.VmId is { } id && id != Guid.Empty)
                    {
                        Color col = GetVmColor(id);
                        int slot = vmColorSlots[id];
                        vmConsumers[id] = (string.IsNullOrWhiteSpace(c.Name) ? id.ToString("D") : c.Name, slot, col);
                    }
                }
            }
        }

        var consumers = new List<ConsumerDescriptor>
        {
            new(HostKey, L10n.Get("Host_Cpu_HostOwner"), hostColor, IsPinned: true)
        };
        if (anyUnattributedGuest)
        {
            consumers.Add(new(GuestPoolKey, L10n.Get("Host_Cpu_UnattributedGuest"), GuestPoolColor, IsPinned: true));
        }
        foreach (var kv in vmConsumers.OrderBy(p => p.Value.Slot))
        {
            consumers.Add(new("vm:" + kv.Key.ToString("N"), kv.Value.Name, kv.Value.Color, IsPinned: false));
        }

        // Reset hover/selected if that VM exited.
        if (selectedKey != null && !consumers.Any(c => string.Equals(c.Key, selectedKey, StringComparison.Ordinal)))
            selectedKey = null;
        if (hoveredKey != null && !consumers.Any(c => string.Equals(c.Key, hoveredKey, StringComparison.Ordinal)))
            hoveredKey = null;

        RebuildLegendIfNeeded(consumers);
        UpdateLegendVisuals();

        string? focused = hoveredKey ?? selectedKey;
        Color? focusedColor = focused == null
            ? null
            : consumers.FirstOrDefault(c => string.Equals(c.Key, focused, StringComparison.Ordinal))?.Color;

        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (!lastValidThreads.TryGetValue(tile.LogicalProcessorIndex, out var snap))
            {
                tile.ShowUnavailable();
                continue;
            }

            var slices = new List<CpuRingSlice>();
            // Host anchors the ring clockwise from 12 o'clock, followed by VMs in stable slot order.
            if (snap.HostUsagePercent is > 0 and float hostUsage)
            {
                slices.Add(new CpuRingSlice(HostKey, L10n.Get("Host_Cpu_HostOwner"), hostUsage, hostColor));
            }

            var orderedVms = snap.Consumers
                .Where(c => !c.IsHost && c.VmId is { } id && id != Guid.Empty && c.UsagePercent > 0)
                .OrderBy(c => vmColorSlots.TryGetValue(c.VmId!.Value, out int s) ? s : int.MaxValue);

            foreach (var c in orderedVms)
            {
                Guid id = c.VmId!.Value;
                slices.Add(new CpuRingSlice("vm:" + id.ToString("N"), c.Name, c.UsagePercent, GetVmColor(id)));
            }

            if (snap.UnattributedGuestUsagePercent > 0)
            {
                slices.Add(new CpuRingSlice(GuestPoolKey, L10n.Get("Host_Cpu_UnattributedGuest"), snap.UnattributedGuestUsagePercent, GuestPoolColor));
            }

            if (snap.UnattributedUsagePercent > 0)
                slices.Add(new CpuRingSlice("unattributed", L10n.Get("Host_Cpu_Unattributed"), snap.UnattributedUsagePercent, OtherColor));

            // Aggregate tiny tail slices (< 2% or beyond top 5) into one clean "Other" arc unless host or focused.
            var sortedForArc = slices.Where(s => s.Percent > 0).ToList();
            var keptArcs = new List<CpuRingSlice>();
            double otherPct = 0;
            foreach (var s in sortedForArc)
            {
                bool isHost = string.Equals(s.ConsumerKey, HostKey, StringComparison.Ordinal);
                bool isFocused = focused != null && string.Equals(s.ConsumerKey, focused, StringComparison.Ordinal);
                if (isHost || isFocused || (keptArcs.Count < 5 && s.Percent >= 2.0))
                    keptArcs.Add(s);
                else
                    otherPct += s.Percent;
            }
            if (otherPct > 0)
                keptArcs.Add(new CpuRingSlice(OtherKey, L10n.Get("Host_Cpu_Other"), otherPct, OtherColor));

            tile.Update(slices, keptArcs, snap.TotalUsagePercent, focused, focusedColor);
        }
    }

    private readonly List<Border> vmLegendButtons = [];

    private void RebuildLegendIfNeeded(IReadOnlyList<ConsumerDescriptor> next)
    {
        bool same = next.Count == activeConsumers.Count;
        if (same)
        {
            for (int i = 0; i < next.Count; i++)
            {
                if (next[i].Key != activeConsumers[i].Key
                    || next[i].Label != activeConsumers[i].Label
                    || next[i].Color != activeConsumers[i].Color
                    || next[i].IsPinned != activeConsumers[i].IsPinned)
                {
                    same = false;
                    break;
                }
            }
        }
        if (same) return;

        double savedOffset = legendScroll.VerticalOffset;
        activeConsumers = next;
        legendChips.Clear();
        vmLegendButtons.Clear();
        hostPinStack.Children.Clear();
        legendGrid.Children.Clear();

        foreach (var c in next)
        {
            var swatch = new Border
            {
                Width = 3,
                Height = 14,
                CornerRadius = new CornerRadius(1.5),
                VerticalAlignment = VerticalAlignment.Center
            };
            var name = new TextBlock
            {
                Text = c.Label,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(swatch, 0);
            Grid.SetColumn(name, 1);
            row.Children.Add(swatch);
            row.Children.Add(name);

            var border = new Border
            {
                Height = 32,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Child = row
            };
            ToolTipService.SetToolTip(border, c.Label);

            var chip = new LegendChip
            {
                Key = c.Key,
                Label = c.Label,
                Color = c.Color,
                ButtonBorder = border,
                SwatchBar = swatch,
                NameText = name
            };

            border.PointerEntered += (_, _) =>
            {
                chip.IsHovered = true;
                if (hoveredKey != chip.Key)
                {
                    hoveredKey = chip.Key;
                    RenderThreadReadings();
                }
            };
            border.PointerExited += (_, _) =>
            {
                chip.IsHovered = false;
                if (hoveredKey == chip.Key)
                {
                    hoveredKey = null;
                    RenderThreadReadings();
                }
            };
            border.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
                selectedKey = selectedKey == chip.Key ? null : chip.Key;
                RenderThreadReadings();
            };

            legendChips.Add(chip);
            if (c.IsPinned)
            {
                hostPinStack.Children.Add(border);
            }
            else
            {
                vmLegendButtons.Add(border);
                legendGrid.Children.Add(border);
            }
        }

        legendScroll.Visibility = vmLegendButtons.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        lastLegendColumns = -1;
        ArrangeLegend();
        legendScroll.ChangeView(null, savedOffset, null, disableAnimation: true);
    }

    private void ArrangeLegend()
    {
        int cols = isNarrowStacked ? 2 : 1;
        int rows = Math.Max(1, (int)Math.Ceiling(vmLegendButtons.Count / (double)cols));

        if (lastLegendColumns != cols || legendGrid.ColumnDefinitions.Count != cols || legendGrid.RowDefinitions.Count != rows)
        {
            legendGrid.ColumnDefinitions.Clear();
            for (int c = 0; c < cols; c++)
                legendGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            legendGrid.RowDefinitions.Clear();
            for (int r = 0; r < rows; r++)
                legendGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            lastLegendColumns = cols;
        }

        for (int i = 0; i < vmLegendButtons.Count; i++)
        {
            Grid.SetRow(vmLegendButtons[i], i / cols);
            Grid.SetColumn(vmLegendButtons[i], i % cols);
        }
    }

    private void UpdateLegendVisuals()
    {
        bool light = ActualTheme == ElementTheme.Light;
        Color defaultBg = ThemeHelper.ThemeColor(ActualTheme, "ControlFillColorDefaultBrush",
            light ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
        Color hoverBg = ThemeHelper.ThemeColor(ActualTheme, "ControlFillColorSecondaryBrush",
            light ? Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        Color defaultBorder = ThemeHelper.ThemeColor(ActualTheme, "ControlStrokeColorDefaultBrush",
            light ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        Color primaryFg = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White);

        string? focused = hoveredKey ?? selectedKey;
        foreach (var chip in legendChips)
        {
            bool active = focused == chip.Key || selectedKey == chip.Key;
            if (active)
            {
                chip.ButtonBorder.Background = new SolidColorBrush(chip.Color);
                chip.ButtonBorder.BorderBrush = new SolidColorBrush(chip.Color);
                chip.NameText.Foreground = new SolidColorBrush(ThemeHelper.ReadableForeground(chip.Color));
                chip.NameText.FontWeight = FontWeights.SemiBold;
                chip.SwatchBar.Background = new SolidColorBrush(Color.FromArgb(115, 0, 0, 0));
            }
            else
            {
                chip.ButtonBorder.Background = new SolidColorBrush(chip.IsHovered ? hoverBg : defaultBg);
                chip.ButtonBorder.BorderBrush = new SolidColorBrush(defaultBorder);
                chip.NameText.Foreground = new SolidColorBrush(primaryFg);
                chip.NameText.FontWeight = FontWeights.Normal;
                chip.SwatchBar.Background = new SolidColorBrush(chip.Color);
            }
        }
    }

    private void ApplyThemeBrushes()
    {
        Brush divider = ThemeHelper.TryResourceBrush(ActualTheme, "DividerStrokeColorDefaultBrush",
            Color.FromArgb(0x22, 0x80, 0x80, 0x80));
        legendBox.BorderBrush = divider;
        hostPinBox.BorderBrush = divider;
    }

    private void RefreshVisuals()
    {
        ApplyThemeBrushes();
        RenderThreadReadings();
    }
}
