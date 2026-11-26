
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Windows.UI;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed partial class HostMemoryRingView : UserControl
{
    private const string HostKey = "__host__";
    private const string FreeKey = "__free__";

    private const double ViewSize = 360;
    private const double Cx = 180;
    private const double Cy = 180;
    private const double RIn = 96;
    private const double ROutBase = 152;
    private const double ROutFocusOffset = 5;

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
        public required string Label { get; set; }
        public required Color Color { get; set; }
        public required Border ButtonBorder { get; init; }
        public required Border SwatchBar { get; init; }
        public required TextBlock NameText { get; init; }
        public required TextBlock SizeText { get; init; }
        public bool IsHovered { get; set; }
        public double GiB { get; set; }
    }

    private sealed record MemoryConsumer(
        string Key,
        string Label,
        double GiB,
        int SharePct,
        Color Color,
        string Tag,
        bool IsPinned,
        bool IsFree);

    private readonly Grid root = new() { ColumnSpacing = 14, RowSpacing = 12 };
    private readonly Border pieCard = new()
    {
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(16, 14, 16, 14)
    };
    private readonly Grid stageHost = new();
    private readonly Viewbox pieViewbox = new()
    {
        Stretch = Stretch.Uniform,
        MaxWidth = 356,
        MaxHeight = 356,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Canvas pieCanvas = new() { Width = ViewSize, Height = ViewSize };
    private readonly Ellipse trackRing = new()
    {
        Width = ROutBase * 2,
        Height = ROutBase * 2,
        StrokeThickness = ROutBase - RIn
    };
    private readonly Ellipse fullSliceRing = new()
    {
        Width = ROutBase * 2,
        Height = ROutBase * 2,
        StrokeThickness = ROutBase - RIn,
        Visibility = Visibility.Collapsed
    };
    private readonly List<ShapePath> arcPool = [];

    private readonly StackPanel centerPanel = new()
    {
        Width = 172,
        Spacing = 2,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };
    private readonly TextBlock centerTitle = new()
    {
        FontSize = 12.5,
        FontWeight = FontWeights.SemiBold,
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly TextBlock centerPct = new()
    {
        FontSize = 40,
        FontWeight = FontWeights.SemiBold,
        LineHeight = 42,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextAlignment = TextAlignment.Center
    };
    private readonly TextBlock centerGb = new()
    {
        FontSize = 12.5,
        FontWeight = FontWeights.Medium,
        Margin = new Thickness(0, 3, 0, 0),
        TextAlignment = TextAlignment.Center
    };
    private readonly TextBlock centerSub = new()
    {
        FontSize = 11,
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

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

    private readonly Dictionary<Guid, int> vmColorSlots = new();
    private readonly Dictionary<string, int> fallbackNameSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LegendChip> legendChips = [];
    private readonly List<Border> vmLegendButtons = [];
    private IReadOnlyList<MemoryConsumer> activeLegendConsumers = [];

    private HostMemoryUsage? memory;
    private IReadOnlyList<VmMemoryAllocationEntry>? allocations;
    private string? hoveredKey;
    private string? selectedKey;
    private bool isNarrowStacked;
    private int lastLegendColumns = -1;
    private int nextColorSlot;

    public HostMemoryRingView()
    {
        Typography.SetNumeralAlignment(centerPct, FontNumeralAlignment.Tabular);
        Typography.SetNumeralAlignment(centerGb, FontNumeralAlignment.Tabular);

        Canvas.SetLeft(trackRing, Cx - ROutBase);
        Canvas.SetTop(trackRing, Cy - ROutBase);
        Canvas.SetLeft(fullSliceRing, Cx - ROutBase);
        Canvas.SetTop(fullSliceRing, Cy - ROutBase);
        pieCanvas.Children.Add(trackRing);
        pieCanvas.Children.Add(fullSliceRing);
        pieViewbox.Child = pieCanvas;

        centerPanel.Children.Add(centerTitle);
        centerPanel.Children.Add(centerPct);
        centerPanel.Children.Add(centerGb);
        centerPanel.Children.Add(centerSub);

        stageHost.Children.Add(pieViewbox);
        stageHost.Children.Add(centerPanel);
        pieCard.Child = stageHost;

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

        root.Children.Add(pieCard);
        root.Children.Add(legendBox);
        ApplyOuterPanePlacement(force: true);

        Content = root;

        root.SizeChanged += (_, _) => ApplyOuterPanePlacement(force: false);
        legendScroll.SizeChanged += (_, _) => ArrangeLegend();
        ActualThemeChanged += (_, _) => RefreshTheme();
        ApplyThemeBrushes();
    }

    public void UpdateTelemetry(HostInventorySnapshot? inventory, HostLiveSample? sample)
    {
        memory = sample?.Memory ?? memory;
        allocations = sample?.VmSummary?.Allocations ?? (sample == null ? inventory?.VmSummary?.Allocations : null) ?? allocations;
        Render();
    }

    public void RefreshTheme()
    {
        ApplyThemeBrushes();
        Render();
    }

    private Color HostAccentColor =>
        Application.Current.Resources.TryGetValue("VmAccentBrush", out var topVal) && topVal is SolidColorBrush topBrush
            ? topBrush.Color
            : ThemeHelper.ThemeColor(ActualTheme, "VmAccentBrush", Color.FromArgb(255, 0x4C, 0xC2, 0xFF));

    private Color GetVmColor(Guid vmId, string name)
    {
        int slot;
        if (vmId != Guid.Empty)
        {
            if (!vmColorSlots.TryGetValue(vmId, out slot))
            {
                slot = nextColorSlot++;
                vmColorSlots[vmId] = slot;
            }
        }
        else if (!fallbackNameSlots.TryGetValue(name, out slot))
        {
            slot = nextColorSlot++;
            fallbackNameSlots[name] = slot;
        }
        return slot < VmPalette.Length ? VmPalette[slot] : OtherColor;
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

            Grid.SetRow(pieCard, 0);
            Grid.SetColumn(pieCard, 0);

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

            Grid.SetRow(pieCard, 0);
            Grid.SetColumn(pieCard, 0);

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

    private void Render()
    {
        if (memory == null || allocations == null)
        {
            centerTitle.Text = L10n.Get("Host_Mem_RingInUseTitle");
            centerPct.Text = memory is { } known ? $"{known.Percent}%" : "—";
            centerGb.Text = memory is { } measured ? $"{measured.UsedGiB:0.0} / {measured.TotalGiB:0.0} GB" : "—";
            centerSub.Text = L10n.Get("Common_Unavailable");
            RebuildLegendIfNeeded([]);
            RenderSlices([], null);
            return;
        }
        var vmAllocs = allocations;
        double totalGiB = memory.TotalGiB, usedGiB = memory.UsedGiB, freeGiB = memory.AvailableGiB;
        double hostGiB = Math.Max(0, usedGiB - vmAllocs.Sum(v => v.AllocatedGiB));
        int inUsePct = memory.Percent;

        int SharePct(double gb) => totalGiB > 0 && gb > 0
            ? Math.Max(1, (int)Math.Round((gb / totalGiB) * 100.0))
            : 0;

        Color hostColor = HostAccentColor;
        var hostConsumer = new MemoryConsumer(
            HostKey,
            L10n.Get("Host_Cpu_HostOwner"),
            hostGiB,
            SharePct(hostGiB),
            hostColor,
            L10n.Get("Host_Mem_HostSub"),
            IsPinned: true,
            IsFree: false);

        var vmConsumers = new List<MemoryConsumer>(vmAllocs.Count);
        foreach (var vm in vmAllocs)
        {
            if (vm.AllocatedGiB <= 0) continue;
            string key = vm.VmId != Guid.Empty ? "vm:" + vm.VmId.ToString("N") : "vm:" + vm.Name;
            Color vmColor = GetVmColor(vm.VmId, vm.Name);
            vmConsumers.Add(new MemoryConsumer(
                key,
                vm.Name,
                vm.AllocatedGiB,
                SharePct(vm.AllocatedGiB),
                vmColor,
                L10n.Get(vm.IsDynamic switch { true => "Host_Mem_ModeDynamic", false => "Host_Mem_ModeFixed", null => "Common_Unavailable" }),
                IsPinned: false,
                IsFree: false));
        }

        var freeConsumer = new MemoryConsumer(
            FreeKey,
            L10n.Get("Host_Mem_FreeTitle"),
            freeGiB,
            SharePct(freeGiB),
            Colors.Transparent,
            string.Empty,
            IsPinned: false,
            IsFree: true);

        // Legend shows only Host (pinned) + Running VMs (no Free chip in sidebar).
        var legendConsumers = new List<MemoryConsumer>(1 + vmConsumers.Count) { hostConsumer };
        legendConsumers.AddRange(vmConsumers);

        var allSlices = new List<MemoryConsumer>(2 + vmConsumers.Count);
        if (hostGiB > 0) allSlices.Add(hostConsumer);
        allSlices.AddRange(vmConsumers);
        if (freeGiB > 0) allSlices.Add(freeConsumer);

        if (selectedKey != null && !allSlices.Any(c => string.Equals(c.Key, selectedKey, StringComparison.Ordinal)))
            selectedKey = null;
        if (hoveredKey != null && !allSlices.Any(c => string.Equals(c.Key, hoveredKey, StringComparison.Ordinal)))
            hoveredKey = null;

        string? focused = hoveredKey ?? selectedKey;
        RebuildLegendIfNeeded(legendConsumers);
        UpdateLegendVisuals(focused);
        RenderSlices(allSlices, focused);
        UpdateCenterReadout(allSlices, focused, totalGiB, usedGiB, freeGiB, inUsePct);
    }

    private void RenderSlices(IReadOnlyList<MemoryConsumer> slices, string? focused)
    {
        bool light = ActualTheme == ElementTheme.Light;
        Color primary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White);
        trackRing.Stroke = new SolidColorBrush(Color.FromArgb(16, primary.R, primary.G, primary.B));

        double sumGiB = 0;
        int nonFreeCount = 0;
        foreach (var s in slices)
        {
            if (s.GiB <= 0) continue;
            sumGiB += s.GiB;
            if (!s.IsFree) nonFreeCount++;
        }

        if (sumGiB <= 0)
        {
            fullSliceRing.Visibility = Visibility.Collapsed;
            for (int i = 0; i < arcPool.Count; i++)
                arcPool[i].Visibility = Visibility.Collapsed;
            return;
        }

        if (slices.Count == 1 && !slices[0].IsFree)
        {
            var single = slices[0];
            bool dimmed = focused != null && !string.Equals(single.Key, focused, StringComparison.Ordinal);
            fullSliceRing.Stroke = new SolidColorBrush(single.Color);
            fullSliceRing.Opacity = dimmed ? 0.16 : 1.0;
            fullSliceRing.Visibility = Visibility.Visible;
            for (int i = 0; i < arcPool.Count; i++)
                arcPool[i].Visibility = Visibility.Collapsed;
            return;
        }

        fullSliceRing.Visibility = Visibility.Collapsed;

        double gapDeg = nonFreeCount > 14 ? 0.85 : nonFreeCount > 6 ? 1.4 : 2.1;
        Color freeFocusFill = Color.FromArgb(31, primary.R, primary.G, primary.B);
        Color freeFocusStroke = Color.FromArgb(72, primary.R, primary.G, primary.B);
        var transparentBrush = new SolidColorBrush(Colors.Transparent);

        double curDeg = -90;
        int used = 0;
        for (int i = 0; i < slices.Count; i++)
        {
            var s = slices[i];
            if (s.GiB <= 0) continue;

            double spanDeg = (s.GiB / sumGiB) * 360.0;
            double effGap = spanDeg > gapDeg * 2.2 ? gapDeg : spanDeg > 1.2 ? 0.35 : 0;
            double aStart = curDeg + effGap / 2.0;
            double aEnd = curDeg + spanDeg - effGap / 2.0;
            curDeg += spanDeg;

            bool isFocused = focused != null && string.Equals(s.Key, focused, StringComparison.Ordinal);
            bool isDimmed = focused != null && !isFocused;
            double rOut = isFocused ? ROutBase + ROutFocusOffset : ROutBase;
            double cornerR = spanDeg >= 6.0 ? 5.0 : 2.5;

            var geom = AnnularRingCanvas.BuildAnnularSector(Cx, Cy, RIn, rOut, aStart, aEnd, cornerR);
            if (geom == null) continue;

            ShapePath path = GetOrCreateArcPath(used++);
            path.Tag = s.Key;
            path.Data = geom;

            if (s.IsFree)
            {
                path.Fill = isFocused ? new SolidColorBrush(freeFocusFill) : transparentBrush;
                path.Stroke = isFocused ? new SolidColorBrush(freeFocusStroke) : transparentBrush;
                path.StrokeThickness = isFocused ? 1.2 : 0;
                path.Opacity = 1.0;
            }
            else
            {
                path.Fill = new SolidColorBrush(s.Color);
                path.Stroke = transparentBrush;
                path.StrokeThickness = 0;
                path.Opacity = isDimmed ? 0.16 : 1.0;
            }

            path.Visibility = Visibility.Visible;
        }

        for (int i = used; i < arcPool.Count; i++)
            arcPool[i].Visibility = Visibility.Collapsed;
    }

    private ShapePath GetOrCreateArcPath(int index)
    {
        if (index < arcPool.Count)
            return arcPool[index];

        var path = new ShapePath();
        path.PointerEntered += (sender, _) =>
        {
            if (sender is ShapePath p && p.Tag is string key && hoveredKey != key)
            {
                hoveredKey = key;
                Render();
            }
        };
        path.PointerExited += (sender, _) =>
        {
            if (sender is ShapePath p && p.Tag is string key && hoveredKey == key)
            {
                hoveredKey = null;
                Render();
            }
        };
        path.PointerPressed += (sender, e) =>
        {
            if (sender is ShapePath p && p.Tag is string key && e.GetCurrentPoint(p).Properties.IsLeftButtonPressed)
            {
                selectedKey = selectedKey == key ? null : key;
                Render();
            }
        };
        arcPool.Add(path);
        pieCanvas.Children.Add(path);
        return path;
    }

    private void UpdateCenterReadout(
        IReadOnlyList<MemoryConsumer> slices,
        string? focused,
        double totalGiB,
        double usedGiB,
        double freeGiB,
        int inUsePct)
    {
        bool light = ActualTheme == ElementTheme.Light;
        Color primary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White);
        Color secondary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        Color tertiary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorTertiaryBrush",
            light ? Color.FromArgb(0x70, 0, 0, 0) : Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));

        MemoryConsumer? focusItem = focused != null
            ? slices.FirstOrDefault(s => string.Equals(s.Key, focused, StringComparison.Ordinal))
            : null;

        centerTitle.Foreground = new SolidColorBrush(secondary);
        centerGb.Foreground = new SolidColorBrush(primary);
        centerSub.Foreground = new SolidColorBrush(tertiary);

        if (focusItem == null)
        {
            centerTitle.Text = L10n.Get("Host_Mem_RingInUseTitle");
            centerPct.Text = $"{inUsePct}%";
            centerPct.Foreground = new SolidColorBrush(primary);
            centerGb.Text = $"{usedGiB:0.0} / {totalGiB:0.0} GB";
            centerSub.Text = L10n.Format("Host_Mem_IdleSubFmt", $"{freeGiB:0.0}");
            centerSub.Visibility = Visibility.Visible;
            return;
        }

        centerTitle.Text = focusItem.Label;
        centerPct.Text = $"{focusItem.SharePct}%";
        centerPct.Foreground = new SolidColorBrush(focusItem.IsFree ? primary : focusItem.Color);
        centerGb.Text = $"{focusItem.GiB:0.0} / {totalGiB:0.0} GB";

        if (string.IsNullOrWhiteSpace(focusItem.Tag))
        {
            centerSub.Text = string.Empty;
            centerSub.Visibility = Visibility.Collapsed;
        }
        else
        {
            centerSub.Text = focusItem.Tag;
            centerSub.Visibility = Visibility.Visible;
        }
    }

    private void RebuildLegendIfNeeded(IReadOnlyList<MemoryConsumer> next)
    {
        bool sameStructure = next.Count == activeLegendConsumers.Count;
        if (sameStructure)
        {
            for (int i = 0; i < next.Count; i++)
            {
                if (next[i].Key != activeLegendConsumers[i].Key
                    || next[i].Label != activeLegendConsumers[i].Label
                    || next[i].Color != activeLegendConsumers[i].Color
                    || next[i].IsPinned != activeLegendConsumers[i].IsPinned)
                {
                    sameStructure = false;
                    break;
                }
            }
        }

        if (sameStructure)
        {
            activeLegendConsumers = next;
            for (int i = 0; i < legendChips.Count && i < next.Count; i++)
            {
                legendChips[i].GiB = next[i].GiB;
                legendChips[i].SizeText.Text = $"{next[i].GiB:0.0}G";
                ToolTipService.SetToolTip(legendChips[i].ButtonBorder, $"{next[i].Label} ({next[i].GiB:0.0} GB)");
            }
            return;
        }

        double savedOffset = legendScroll.VerticalOffset;
        activeLegendConsumers = next;
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
            var size = new TextBlock
            {
                Text = $"{c.GiB:0.0}G",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            };
            Typography.SetNumeralAlignment(size, FontNumeralAlignment.Tabular);

            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(swatch, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(size, 2);
            row.Children.Add(swatch);
            row.Children.Add(name);
            row.Children.Add(size);

            var border = new Border
            {
                Height = 32,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Child = row
            };
            ToolTipService.SetToolTip(border, $"{c.Label} ({c.GiB:0.0} GB)");

            var chip = new LegendChip
            {
                Key = c.Key,
                Label = c.Label,
                Color = c.Color,
                ButtonBorder = border,
                SwatchBar = swatch,
                NameText = name,
                SizeText = size,
                GiB = c.GiB
            };

            border.PointerEntered += (_, _) =>
            {
                chip.IsHovered = true;
                if (hoveredKey != chip.Key)
                {
                    hoveredKey = chip.Key;
                    Render();
                }
            };
            border.PointerExited += (_, _) =>
            {
                chip.IsHovered = false;
                if (hoveredKey == chip.Key)
                {
                    hoveredKey = null;
                    Render();
                }
            };
            border.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed) return;
                selectedKey = selectedKey == chip.Key ? null : chip.Key;
                Render();
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

    private void UpdateLegendVisuals(string? focused)
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
        Color secondaryFg = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));

        foreach (var chip in legendChips)
        {
            bool active = focused == chip.Key || selectedKey == chip.Key;
            if (active)
            {
                Color fg = ThemeHelper.ReadableForeground(chip.Color);
                chip.ButtonBorder.Background = new SolidColorBrush(chip.Color);
                chip.ButtonBorder.BorderBrush = new SolidColorBrush(chip.Color);
                chip.NameText.Foreground = new SolidColorBrush(fg);
                chip.NameText.FontWeight = FontWeights.SemiBold;
                chip.SizeText.Foreground = new SolidColorBrush(fg);
                chip.SwatchBar.Background = new SolidColorBrush(Color.FromArgb(115, 0, 0, 0));
            }
            else
            {
                chip.ButtonBorder.Background = new SolidColorBrush(chip.IsHovered ? hoverBg : defaultBg);
                chip.ButtonBorder.BorderBrush = new SolidColorBrush(defaultBorder);
                chip.NameText.Foreground = new SolidColorBrush(primaryFg);
                chip.NameText.FontWeight = FontWeights.Normal;
                chip.SizeText.Foreground = new SolidColorBrush(secondaryFg);
                chip.SwatchBar.Background = new SolidColorBrush(chip.Color);
            }
        }
    }

    private void ApplyThemeBrushes()
    {
        bool light = ActualTheme == ElementTheme.Light;
        Brush divider = ThemeHelper.TryResourceBrush(ActualTheme, "DividerStrokeColorDefaultBrush",
            Color.FromArgb(0x22, 0x80, 0x80, 0x80));
        pieCard.Background = ThemeHelper.TryResourceBrush(ActualTheme, "ControlFillColorDefaultBrush",
            light ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
        pieCard.BorderBrush = ThemeHelper.TryResourceBrush(ActualTheme, "ControlStrokeColorDefaultBrush",
            light ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        legendBox.BorderBrush = divider;
        hostPinBox.BorderBrush = divider;
    }
}
