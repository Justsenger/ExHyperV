using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed partial class HostGpuOverviewView : UserControl
{
    private const string HostKey = "__host__";

    private sealed record GpuEngineValues(double? D3, double? Copy, double? Enc, double? Dec)
    {
        public double? Peak => new[] { D3, Copy, Enc, Dec }.Where(v => v.HasValue).Select(v => v!.Value) is var vals && vals.Any()
            ? vals.Max()
            : null;
    }

    private sealed record GpuConsumerModel(
        string Key,
        string Name,
        string RoleTag,
        Color Color,
        GpuEngineValues Engines,
        bool HasActivity,
        bool IsHost);

    private sealed record GpuCardModel(
        int Index,
        HostGpuInfo Info,
        int SharedVmCount,
        GpuEngineValues CardTotal,
        double? TotalPeakPct,
        IReadOnlyList<GpuConsumerModel> Consumers);

    private sealed class LegendChipRef
    {
        public required string Key { get; init; }
        public required Color Color { get; init; }
        public required Border ButtonBorder { get; init; }
        public required TextBlock NameText { get; init; }
        public required TextBlock ReadoutText { get; init; }
    }

    private sealed class EngineTileRefs
    {
        public required Func<GpuEngineValues, double?> Selector { get; init; }
        public required TextBlock ValText { get; init; }
        public required ContentControl BarHost { get; init; }
        public required Shared.Controls.WrapPanel TagsWrap { get; init; }
    }

    private sealed class ConsumerRowRefs
    {
        public required string Key { get; init; }
        public required TextBlock Pill3D { get; init; }
        public required TextBlock PillCopy { get; init; }
        public required TextBlock PillEnc { get; init; }
        public required TextBlock PillDec { get; init; }
    }

    private sealed class GpuCardRefs
    {
        public required TextBlock SharedBadgeText { get; init; }
        public required TextBlock ReadingVal { get; init; }
        public required TextBlock ReadingSub { get; init; }
        public required TextBlock BarRight { get; init; }
        public required ContentControl ShareBarHost { get; init; }
        public required TextBlock NoCounterNote { get; init; }
        public required List<EngineTileRefs> Engines { get; init; }
        public required List<ConsumerRowRefs> Rows { get; init; }
    }

    private readonly Grid root = new() { ColumnSpacing = 14, RowSpacing = 12 };
    private readonly ScrollViewer cardsScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly StackPanel cardsStack = new() { Spacing = 12 };
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
    private readonly StackPanel legendList = new() { Spacing = 6 };

    private HostInventorySnapshot? inventory;
    private HostLiveSample? lastSample;
    private IReadOnlyList<GpuCardModel> currentCards = [];
    private IReadOnlyList<(string Key, string Name, Color Color, double? Peak)> legendConsumers = [];
    private readonly List<GpuCardRefs> cardRefs = [];
    private readonly List<LegendChipRef> legendChips = [];
    private readonly List<(string Key, FrameworkElement Element, Action<bool, bool>? OnFocus)> staticFocusTargets = [];
    private readonly List<(string Key, FrameworkElement Element)> dynamicBarFocusTargets = [];
    private readonly Dictionary<Guid, int> vmColorSlots = new();
    private int nextColorSlot;

    private string? hoveredKey;
    private string? selectedKey;
    private bool isNarrowStacked;
    private string lastStructureKey = string.Empty;

    public HostGpuOverviewView()
    {
        cardsScroll.Content = cardsStack;
        cardsScroll.AttachScrollbarClearance(cardsStack, bottomSpacing: 0);

        hostPinBox.Child = hostPinStack;
        legendScroll.Content = legendList;
        legendScroll.AttachScrollbarClearance(legendList, bottomSpacing: 0);

        legendContainer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        legendContainer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(hostPinBox, 0);
        Grid.SetRow(legendScroll, 1);
        legendContainer.Children.Add(hostPinBox);
        legendContainer.Children.Add(legendScroll);
        legendBox.Child = legendContainer;

        root.Children.Add(cardsScroll);
        root.Children.Add(legendBox);
        ApplyOuterPlacement(force: true);

        Content = root;
        root.SizeChanged += (_, _) => ApplyOuterPlacement(force: false);
        ActualThemeChanged += (_, _) => Rebuild(forceStructure: true);
    }

    public void UpdateTelemetry(HostInventorySnapshot? snapshot, HostLiveSample? sample)
    {
        bool invChanged = !ReferenceEquals(inventory, snapshot);
        inventory = snapshot ?? inventory;
        if (sample != null) lastSample = sample;
        Rebuild(forceStructure: invChanged);
    }

    public void RefreshTheme() => Rebuild(forceStructure: true);

    private void ApplyOuterPlacement(bool force)
    {
        double w = root.ActualWidth;
        bool narrow = w > 0 && w < 700;
        if (!force && narrow == isNarrowStacked && root.ColumnDefinitions.Count > 0) return;
        isNarrowStacked = narrow;

        root.ColumnDefinitions.Clear();
        root.RowDefinitions.Clear();

        if (narrow)
        {
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(cardsScroll, 0); Grid.SetColumn(cardsScroll, 0);
            Grid.SetRow(legendBox, 1); Grid.SetColumn(legendBox, 0);
            legendBox.Width = double.NaN;
            legendBox.MaxHeight = 200;
            legendBox.BorderThickness = new Thickness(0, 1, 0, 0);
            legendBox.Padding = new Thickness(0, 10, 0, 0);
        }
        else
        {
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(186) });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(cardsScroll, 0); Grid.SetColumn(cardsScroll, 0);
            Grid.SetRow(legendBox, 0); Grid.SetColumn(legendBox, 1);
            legendBox.Width = 186;
            legendBox.MaxHeight = double.PositiveInfinity;
            legendBox.BorderThickness = new Thickness(1, 0, 0, 0);
            legendBox.Padding = new Thickness(12, 0, 0, 0);
        }
    }

    private Color GetVmColor(Guid vmId)
    {
        if (!vmColorSlots.TryGetValue(vmId, out int slot))
            vmColorSlots[vmId] = slot = nextColorSlot++;
        return HostUiHelpers.GetVmPaletteColor(slot);
    }

    private void Rebuild(bool forceStructure)
    {
        Color hostColor = HostUiHelpers.GetHostConsumerColor(ActualTheme);
        var gpus = inventory?.Gpus ?? [];
        var vmGpus = lastSample?.VmGpus;
        bool singleGpu = gpus.Count == 1;

        var builtCards = new List<GpuCardModel>(gpus.Count);
        var vmLegendMap = new Dictionary<string, (string Name, Color Color, double? Peak)>(StringComparer.Ordinal);
        double? hostGlobalPeak = null;

        for (int i = 0; i < gpus.Count; i++)
        {
            var gpu = gpus[i];
            HostGpuActivity? gpuAct = null;
            lastSample?.Gpus.TryGetValue(gpu.InstanceId, out gpuAct);
            var totalEngines = new GpuEngineValues(gpuAct?.Pct3D, gpuAct?.PctCopy, gpuAct?.PctEnc, gpuAct?.PctDec);

            var assignedVms = vmGpus?
                .Where(v => v.AssignedDeviceInstanceIds.Any(id => string.Equals(id, gpu.InstanceId, StringComparison.OrdinalIgnoreCase)))
                .ToList() ?? [];

            var attributableVms = singleGpu && vmGpus != null
                ? vmGpus.Where(v => v.IsRunning && (v.HasEngineInstance || assignedVms.Any(a => a.VmId == v.VmId))).ToList()
                : [];

            var vmConsumers = new List<GpuConsumerModel>();
            if (singleGpu)
            {
                foreach (var vm in attributableVms)
                {
                    Color col = GetVmColor(vm.VmId);
                    string key = $"vm:{vm.VmId:N}";
                    var eng = new GpuEngineValues(vm.Gpu3dPct, vm.GpuCopyPct, vm.GpuEncPct, vm.GpuDecPct);
                    vmConsumers.Add(new GpuConsumerModel(key, vm.Name, L10n.Get("Host_Gpu_RoleVm"), col, eng, eng.Peak.HasValue, false));
                    if (!vmLegendMap.TryGetValue(key, out var existing) || (eng.Peak ?? -1) > (existing.Peak ?? -1))
                        vmLegendMap[key] = (vm.Name, col, eng.Peak);
                }
            }
            else
            {
                foreach (var vm in assignedVms)
                {
                    Color col = GetVmColor(vm.VmId);
                    string key = $"vm:{vm.VmId:N}";
                    vmConsumers.Add(new GpuConsumerModel(key, vm.Name, L10n.Get("Host_Gpu_RoleVm"), col, new(null, null, null, null), false, false));
                    vmLegendMap.TryAdd(key, (vm.Name, col, null));
                }
            }

            static double? SubtractVmLoad(double? total, IEnumerable<double?> vmLoads)
            {
                if (!total.HasValue) return null;
                double vmSum = vmLoads.Where(x => x.HasValue).Sum(x => x!.Value);
                return Math.Max(0, total.Value - vmSum);
            }

            var hostEngines = singleGpu
                ? new GpuEngineValues(
                    SubtractVmLoad(totalEngines.D3, vmConsumers.Select(v => v.Engines.D3)),
                    SubtractVmLoad(totalEngines.Copy, vmConsumers.Select(v => v.Engines.Copy)),
                    SubtractVmLoad(totalEngines.Enc, vmConsumers.Select(v => v.Engines.Enc)),
                    SubtractVmLoad(totalEngines.Dec, vmConsumers.Select(v => v.Engines.Dec)))
                : totalEngines;

            var hostConsumer = new GpuConsumerModel(
                HostKey,
                L10n.Get("Host_Cpu_HostOwner"),
                L10n.Get("Host_Gpu_RoleHost"),
                hostColor,
                hostEngines,
                hostEngines.Peak.HasValue,
                true);
            if (hostEngines.Peak is { } hp && (!hostGlobalPeak.HasValue || hp > hostGlobalPeak.Value))
                hostGlobalPeak = hp;

            var consumers = new List<GpuConsumerModel> { hostConsumer };
            consumers.AddRange(vmConsumers);

            builtCards.Add(new GpuCardModel(
                i,
                gpu,
                assignedVms.Count,
                totalEngines,
                totalEngines.Peak,
                consumers));
        }

        currentCards = builtCards;
        var legendListData = new List<(string Key, string Name, Color Color, double? Peak)>
        {
            (HostKey, L10n.Get("Host_Cpu_HostOwner"), hostColor, hostGlobalPeak)
        };
        legendListData.AddRange(vmLegendMap.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Color, kv.Value.Peak)));
        legendConsumers = legendListData;

        if (selectedKey != null && !legendConsumers.Any(x => x.Key == selectedKey)) selectedKey = null;
        if (hoveredKey != null && !legendConsumers.Any(x => x.Key == hoveredKey)) hoveredKey = null;

        string structKey = string.Join("|", builtCards.Select(c =>
            $"{c.Info.InstanceId}:{string.Join(",", c.Consumers.Select(x => x.Key))}"));

        if (forceStructure || !string.Equals(structKey, lastStructureKey, StringComparison.Ordinal))
        {
            RenderVisualTree();
            lastStructureKey = structKey;
        }
        else
        {
            UpdateLiveValues();
        }
        ApplyFocusVisuals();
    }

    private void RenderVisualTree()
    {
        bool light = ActualTheme == ElementTheme.Light;
        Color primary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White);
        Color secondary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        Brush cardBg = ThemeHelper.TryResourceBrush(ActualTheme, "VmCardBrush",
            light ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
        Brush cardStroke = ThemeHelper.TryResourceBrush(ActualTheme, "VmStrokeBrush",
            light ? Color.FromArgb(0x0F, 0, 0, 0) : Color.FromArgb(0x19, 0, 0, 0));
        Brush divider = ThemeHelper.TryResourceBrush(ActualTheme, "DividerStrokeColorDefaultBrush",
            Color.FromArgb(0x22, 0x80, 0x80, 0x80));
        Brush wellBg = ThemeHelper.TryResourceBrush(ActualTheme, "ControlFillColorDefaultBrush",
            light ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
        Brush wellBorder = ThemeHelper.TryResourceBrush(ActualTheme, "ControlStrokeColorDefaultBrush",
            light ? Color.FromArgb(0x18, 0, 0, 0) : Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));

        legendBox.BorderBrush = divider;
        hostPinBox.BorderBrush = divider;

        cardsStack.Children.Clear();
        cardRefs.Clear();
        staticFocusTargets.Clear();
        dynamicBarFocusTargets.Clear();
        legendChips.Clear();
        hostPinStack.Children.Clear();
        legendList.Children.Clear();

        if (currentCards.Count == 0)
        {
            cardsStack.Children.Add(new Border
            {
                Background = cardBg,
                BorderBrush = cardStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(18, 22, 18, 22),
                Child = new TextBlock
                {
                    Text = L10n.Get("Host_Gpu_NoPhysical"),
                    FontSize = 13,
                    Foreground = new SolidColorBrush(secondary)
                }
            });
        }
        else
        {
            if (currentCards.Count > 1)
            {
                cardsStack.Children.Add(new Border
                {
                    Background = wellBg,
                    BorderBrush = cardStroke,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 8, 12, 8),
                    Child = new TextBlock
                    {
                        Text = L10n.Get("Host_Gpu_MultiGpuNote"),
                        FontSize = 12,
                        Foreground = new SolidColorBrush(secondary),
                        TextWrapping = TextWrapping.Wrap
                    }
                });
            }

            foreach (var card in currentCards)
                cardsStack.Children.Add(BuildGpuCard(card, primary, secondary, cardBg, cardStroke, divider, wellBg, wellBorder));
        }

        if (legendConsumers.Count > 0)
        {
            hostPinStack.Children.Add(BuildLegendChip(legendConsumers[0], primary, secondary));
            if (legendConsumers.Count == 1)
            {
                legendList.Children.Add(new TextBlock
                {
                    Text = L10n.Get("Host_Gpu_NoAssignedVms"),
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(secondary),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 2, 4, 0)
                });
            }
            else
            {
                foreach (var item in legendConsumers.Skip(1))
                    legendList.Children.Add(BuildLegendChip(item, primary, secondary));
            }
        }

        UpdateLiveValues();
    }

    private Border BuildGpuCard(
        GpuCardModel card,
        Color primary,
        Color secondary,
        Brush cardBg,
        Brush cardStroke,
        Brush divider,
        Brush wellBg,
        Brush wellBorder)
    {
        var body = new StackPanel { Spacing = 12 };

        var headGrid = new Grid { ColumnSpacing = 12 };
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Color hostAccent = HostUiHelpers.GetHostConsumerColor(ActualTheme);
        var iconBox = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(7),
            Background = ThemeHelper.TryResourceBrush(ActualTheme, "SubtleFillColorSecondaryBrush", Color.FromArgb(0x12, 0x88, 0x88, 0x88)),
            BorderBrush = divider,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon
            {
                Glyph = "\uF211",
                FontSize = 16,
                Foreground = new SolidColorBrush(hostAccent)
            }
        };
        Grid.SetColumn(iconBox, 0);
        headGrid.Children.Add(iconBox);

        var titleCol = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleLine.Children.Add(new TextBlock
        {
            Text = $"GPU {card.Index}",
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(primary),
            VerticalAlignment = VerticalAlignment.Center
        });
        titleLine.Children.Add(new TextBlock
        {
            Text = card.Info.Name,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(secondary),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleCol.Children.Add(titleLine);

        var sharedBadge = HostUiHelpers.BuildMiniBadge(ActualTheme, L10n.Format("Host_Gpu_SharedVmsBadge", card.SharedVmCount));
        var badgeWrap = new Shared.Controls.WrapPanel { Spacing = 6 };
        badgeWrap.Children.Add(HostUiHelpers.BuildMiniBadge(ActualTheme,
            L10n.Get(card.Info.IsPartitionable ? "Host_Gpu_Partitionable" : "Host_Gpu_NotPartitionable")));
        badgeWrap.Children.Add(HostUiHelpers.BuildMiniBadge(ActualTheme,
            L10n.Format("Host_Gpu_DedicatedVramBadge", HostUiHelpers.FormatVramSize(card.Info.MemoryBytes))));
        if (card.Info.IsPartitionable)
            badgeWrap.Children.Add(sharedBadge);
        titleCol.Children.Add(badgeWrap);
        Grid.SetColumn(titleCol, 1);
        headGrid.Children.Add(titleCol);

        var readingCol = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var readingVal = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(primary),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        Typography.SetNumeralAlignment(readingVal, FontNumeralAlignment.Tabular);
        var readingSub = new TextBlock
        {
            FontSize = 11,
            Foreground = new SolidColorBrush(secondary),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        readingCol.Children.Add(readingVal);
        readingCol.Children.Add(readingSub);
        Grid.SetColumn(readingCol, 2);
        headGrid.Children.Add(readingCol);
        body.Children.Add(headGrid);

        // Hero share bar container
        var barSection = new StackPanel { Spacing = 5 };
        var barLabelRow = new Grid();
        barLabelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        barLabelRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        barLabelRow.Children.Add(new TextBlock { Text = L10n.Get("Host_Gpu_ShareTitle"), FontSize = 11.5, Foreground = new SolidColorBrush(secondary) });
        var barRight = new TextBlock { FontSize = 11.5, Foreground = new SolidColorBrush(secondary) };
        Typography.SetNumeralAlignment(barRight, FontNumeralAlignment.Tabular);
        Grid.SetColumn(barRight, 1);
        barLabelRow.Children.Add(barRight);
        barSection.Children.Add(barLabelRow);
        var shareBarHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        barSection.Children.Add(shareBarHost);
        body.Children.Add(barSection);

        var noCounterNote = new TextBlock
        {
            Text = L10n.Get("Host_Gpu_UnavailableNote"),
            FontSize = 12,
            Foreground = new SolidColorBrush(secondary),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        body.Children.Add(noCounterNote);

        // 4 Engine tiles
        var engGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        engGrid.ColumnDefinitions.Add(new ColumnDefinition());
        engGrid.ColumnDefinitions.Add(new ColumnDefinition());
        engGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        engGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var engineSpecs = new (string Label, Func<GpuEngineValues, double?> Selector, int Row, int Col)[]
        {
            (L10n.Get("Overview_Gpu3d"), e => e.D3, 0, 0),
            (L10n.Get("Overview_GpuCopy"), e => e.Copy, 0, 1),
            (L10n.Get("Overview_GpuEncode"), e => e.Enc, 1, 0),
            (L10n.Get("Overview_GpuDecode"), e => e.Dec, 1, 1)
        };
        var engineRefs = new List<EngineTileRefs>();
        foreach (var spec in engineSpecs)
        {
            var stack = new StackPanel { Spacing = 7 };
            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(new TextBlock { Text = spec.Label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(primary) });
            var valBlock = new TextBlock { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(primary) };
            Typography.SetNumeralAlignment(valBlock, FontNumeralAlignment.Tabular);
            Grid.SetColumn(valBlock, 1);
            head.Children.Add(valBlock);
            stack.Children.Add(head);

            var engBarHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            stack.Children.Add(engBarHost);

            var tagsWrap = new Shared.Controls.WrapPanel { Spacing = 8 };
            stack.Children.Add(tagsWrap);

            var tileBorder = new Border
            {
                Background = wellBg,
                BorderBrush = wellBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(11, 9, 11, 9),
                Child = stack
            };
            Grid.SetRow(tileBorder, spec.Row);
            Grid.SetColumn(tileBorder, spec.Col);
            engGrid.Children.Add(tileBorder);
            engineRefs.Add(new EngineTileRefs { Selector = spec.Selector, ValText = valBlock, BarHost = engBarHost, TagsWrap = tagsWrap });
        }
        body.Children.Add(engGrid);

        // Consumer rows
        var rowsStack = new StackPanel { Spacing = 6 };
        var rowRefs = new List<ConsumerRowRefs>();
        foreach (var c in card.Consumers)
        {
            var (rowBorder, rowRef) = BuildConsumerRow(c, primary, wellBg, wellBorder, hostAccent);
            rowsStack.Children.Add(rowBorder);
            rowRefs.Add(rowRef);
        }
        body.Children.Add(rowsStack);

        cardRefs.Add(new GpuCardRefs
        {
            SharedBadgeText = (TextBlock)sharedBadge.Child,
            ReadingVal = readingVal,
            ReadingSub = readingSub,
            BarRight = barRight,
            ShareBarHost = shareBarHost,
            NoCounterNote = noCounterNote,
            Engines = engineRefs,
            Rows = rowRefs
        });

        return new Border
        {
            Background = cardBg,
            BorderBrush = cardStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 14, 16, 14),
            Child = body
        };
    }

    private (Border Row, ConsumerRowRefs Refs) BuildConsumerRow(GpuConsumerModel c, Color primary, Brush wellBg, Brush wellBorder, Color accent)
    {
        var rowGrid = new Grid { ColumnSpacing = 10 };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new Border
        {
            Width = 3,
            Height = 14,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(c.Color),
            VerticalAlignment = VerticalAlignment.Center
        });
        left.Children.Add(new TextBlock
        {
            Text = c.Name,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(primary),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        left.Children.Add(HostUiHelpers.BuildMiniBadge(ActualTheme, c.RoleTag));
        rowGrid.Children.Add(left);

        var p3d = HostUiHelpers.BuildPillTag(ActualTheme, L10n.Get("Host_Gpu_Engine3d"), "---");
        var pCopy = HostUiHelpers.BuildPillTag(ActualTheme, L10n.Get("Host_Gpu_EngineCopy"), "---");
        var pEnc = HostUiHelpers.BuildPillTag(ActualTheme, L10n.Get("Host_Gpu_EngineEnc"), "---");
        var pDec = HostUiHelpers.BuildPillTag(ActualTheme, L10n.Get("Host_Gpu_EngineDec"), "---");

        var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        pills.Children.Add(p3d);
        pills.Children.Add(pCopy);
        pills.Children.Add(pEnc);
        pills.Children.Add(pDec);
        Grid.SetColumn(pills, 1);
        rowGrid.Children.Add(pills);

        var border = new Border
        {
            Background = wellBg,
            BorderBrush = wellBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 7, 10, 7),
            Child = rowGrid
        };
        Brush focusBg = ThemeHelper.TryResourceBrush(ActualTheme, "ControlFillColorSecondaryBrush",
            Color.FromArgb(0x16, 0x80, 0x80, 0x80));
        Color tintBorder = Color.FromArgb(140, accent.R, accent.G, accent.B);
        AttachStaticInteraction(border, c.Key, (focused, _) =>
        {
            border.Background = focused ? focusBg : wellBg;
            border.BorderBrush = focused ? new SolidColorBrush(tintBorder) : wellBorder;
        });

        static TextBlock ValText(Border b) => (TextBlock)((StackPanel)b.Child).Children[1];
        return (border, new ConsumerRowRefs
        {
            Key = c.Key,
            Pill3D = ValText(p3d),
            PillCopy = ValText(pCopy),
            PillEnc = ValText(pEnc),
            PillDec = ValText(pDec)
        });
    }

    private Border BuildStackedBar(IReadOnlyList<(string Key, Color Color, double Pct)> slices, double height, Brush barBg, Brush wellBorder)
    {
        var trackGrid = new Grid { ColumnSpacing = 2 };
        double used = 0;
        foreach (var slice in slices)
        {
            double pct = Math.Clamp(slice.Pct, 0, Math.Max(0, 100 - used));
            if (pct <= 0.1) continue;
            used += pct;
            int colIdx = trackGrid.ColumnDefinitions.Count;
            trackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1.5, pct), GridUnitType.Star) });
            var seg = new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(slice.Color) };
            Grid.SetColumn(seg, colIdx);
            AttachDynamicInteraction(seg, slice.Key);
            trackGrid.Children.Add(seg);
        }
        double remaining = Math.Max(0, 100 - used);
        if (remaining > 0.1 || trackGrid.ColumnDefinitions.Count == 0)
            trackGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, remaining), GridUnitType.Star) });

        return new Border
        {
            Height = height,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = wellBorder,
            Background = barBg,
            Padding = new Thickness(1),
            Child = trackGrid
        };
    }

    private Border BuildLegendChip((string Key, string Name, Color Color, double? Peak) item, Color primary, Color secondary)
    {
        var row = new Grid { ColumnSpacing = 7 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(item.Color),
            VerticalAlignment = VerticalAlignment.Center
        });

        var nameText = new TextBlock
        {
            Text = item.Name,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(primary),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(nameText, 1);
        row.Children.Add(nameText);

        var readoutText = new TextBlock
        {
            Text = FormatPctOrDash(item.Peak),
            FontSize = 11,
            Foreground = new SolidColorBrush(secondary),
            VerticalAlignment = VerticalAlignment.Center
        };
        Typography.SetNumeralAlignment(readoutText, FontNumeralAlignment.Tabular);
        Grid.SetColumn(readoutText, 2);
        row.Children.Add(readoutText);

        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 5, 7, 5),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            Child = row
        };
        legendChips.Add(new LegendChipRef { Key = item.Key, Color = item.Color, ButtonBorder = border, NameText = nameText, ReadoutText = readoutText });
        AttachStaticInteraction(border, item.Key, null);
        return border;
    }

    private void UpdateLiveValues()
    {
        bool light = ActualTheme == ElementTheme.Light;
        Color primary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White);
        Color secondary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        Brush wellBorder = ThemeHelper.TryResourceBrush(ActualTheme, "ControlStrokeColorDefaultBrush",
            light ? Color.FromArgb(0x18, 0, 0, 0) : Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF));
        Brush barBg = ThemeHelper.TryResourceBrush(ActualTheme, "ControlFillColorTertiaryBrush",
            light ? Color.FromArgb(0x0A, 0, 0, 0) : Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));

        dynamicBarFocusTargets.Clear();
        string? focus = hoveredKey ?? selectedKey;

        for (int i = 0; i < currentCards.Count && i < cardRefs.Count; i++)
        {
            var card = currentCards[i];
            var r = cardRefs[i];

            r.SharedBadgeText.Text = L10n.Format("Host_Gpu_SharedVmsBadge", card.SharedVmCount);

            var focusedConsumer = focus != null ? card.Consumers.FirstOrDefault(c => c.Key == focus) : null;
            if (focusedConsumer != null)
            {
                r.ReadingVal.Text = FormatPctOrDash(focusedConsumer.Engines.Peak);
                r.ReadingVal.Foreground = new SolidColorBrush(focusedConsumer.Color);
                r.ReadingSub.Text = L10n.Format("Host_Gpu_PeakEngine", focusedConsumer.Name);
            }
            else
            {
                r.ReadingVal.Text = FormatPctOrDash(card.TotalPeakPct);
                r.ReadingVal.Foreground = new SolidColorBrush(primary);
                r.ReadingSub.Text = L10n.Get("Host_Gpu_PhysTotalLoad");
            }

            if (focusedConsumer != null)
                r.BarRight.Text = L10n.Format("Host_Gpu_ShareFocus", focusedConsumer.Name, focusedConsumer.Engines.Peak is { } fp ? Math.Round(fp) : 0);
            else
                r.BarRight.Text = card.TotalPeakPct is { } p ? L10n.Format("Host_Gpu_ShareTotal", Math.Round(p)) : "---";

            r.ShareBarHost.Content = BuildStackedBar(
                card.Consumers.Where(c => c.Engines.Peak is > 0).Select(c => (c.Key, c.Color, Math.Min(100, c.Engines.Peak!.Value))).ToArray(),
                14, barBg, wellBorder);

            r.NoCounterNote.Visibility = card.TotalPeakPct.HasValue ? Visibility.Collapsed : Visibility.Visible;

            foreach (var er in r.Engines)
            {
                double? total = er.Selector(card.CardTotal);
                var activeSlices = card.Consumers
                    .Select(c => (c.Key, c.Name, c.Color, Val: er.Selector(c.Engines)))
                    .Where(x => x.Val is > 0)
                    .Select(x => (x.Key, x.Name, x.Color, Val: x.Val!.Value))
                    .ToArray();
                var focusedSlice = focus != null ? activeSlices.FirstOrDefault(s => s.Key == focus) : default;
                bool hasFocusedSlice = !string.IsNullOrEmpty(focusedSlice.Key);

                er.ValText.Text = focus != null
                    ? (hasFocusedSlice ? $"{Math.Round(focusedSlice.Val):0}%" : (total.HasValue ? "0%" : "---"))
                    : FormatPctOrDash(total);
                er.ValText.Foreground = new SolidColorBrush(hasFocusedSlice ? focusedSlice.Color : primary);

                er.BarHost.Content = BuildStackedBar(
                    activeSlices.Select(s => (s.Key, s.Color, Math.Min(100, s.Val))).ToArray(),
                    8, barBg, wellBorder);

                er.TagsWrap.Children.Clear();
                if (activeSlices.Length == 0)
                {
                    er.TagsWrap.Children.Add(new TextBlock
                    {
                        Text = total.HasValue ? L10n.Get("Host_Gpu_EngineIdle") : "---",
                        FontSize = 11,
                        Foreground = new SolidColorBrush(secondary)
                    });
                }
                else
                {
                    foreach (var slice in activeSlices)
                    {
                        var tag = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                        tag.Children.Add(new Border
                        {
                            Width = 7,
                            Height = 7,
                            CornerRadius = new CornerRadius(2),
                            Background = new SolidColorBrush(slice.Color),
                            VerticalAlignment = VerticalAlignment.Center
                        });
                        tag.Children.Add(new TextBlock { Text = slice.Name, FontSize = 11, Foreground = new SolidColorBrush(secondary) });
                        var num = new TextBlock { Text = $"{Math.Round(slice.Val):0}%", FontSize = 11, Foreground = new SolidColorBrush(primary) };
                        Typography.SetNumeralAlignment(num, FontNumeralAlignment.Tabular);
                        tag.Children.Add(num);
                        AttachDynamicInteraction(tag, slice.Key);
                        er.TagsWrap.Children.Add(tag);
                    }
                }
            }

            for (int rowIdx = 0; rowIdx < card.Consumers.Count && rowIdx < r.Rows.Count; rowIdx++)
            {
                var c = card.Consumers[rowIdx];
                var rr = r.Rows[rowIdx];
                rr.Pill3D.Text = FormatPctOrDash(c.Engines.D3);
                rr.PillCopy.Text = FormatPctOrDash(c.Engines.Copy);
                rr.PillEnc.Text = FormatPctOrDash(c.Engines.Enc);
                rr.PillDec.Text = FormatPctOrDash(c.Engines.Dec);
            }
        }

        for (int i = 0; i < legendConsumers.Count && i < legendChips.Count; i++)
            legendChips[i].ReadoutText.Text = FormatPctOrDash(legendConsumers[i].Peak);
    }

    private void AttachStaticInteraction(FrameworkElement el, string key, Action<bool, bool>? onFocus)
    {
        staticFocusTargets.Add((key, el, onFocus));
        BindPointerHandlers(el, key);
    }

    private void AttachDynamicInteraction(FrameworkElement el, string key)
    {
        dynamicBarFocusTargets.Add((key, el));
        BindPointerHandlers(el, key);
    }

    private void BindPointerHandlers(FrameworkElement el, string key)
    {
        el.PointerEntered += (_, _) =>
        {
            if (hoveredKey == key) return;
            hoveredKey = key;
            UpdateLiveValues();
            ApplyFocusVisuals();
        };
        el.PointerExited += (_, _) =>
        {
            if (hoveredKey != key) return;
            hoveredKey = null;
            UpdateLiveValues();
            ApplyFocusVisuals();
        };
        el.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            selectedKey = selectedKey == key ? null : key;
            UpdateLiveValues();
            ApplyFocusVisuals();
        };
    }

    private void ApplyFocusVisuals()
    {
        string? focus = hoveredKey ?? selectedKey;
        bool hasFocus = !string.IsNullOrEmpty(focus);

        foreach (var (key, element, onFocus) in staticFocusTargets)
        {
            bool isFocused = hasFocus && key == focus;
            bool isDimmed = hasFocus && !isFocused;
            element.Opacity = isDimmed ? 0.25 : 1.0;
            onFocus?.Invoke(isFocused, isDimmed);
        }

        foreach (var (key, element) in dynamicBarFocusTargets)
            element.Opacity = hasFocus && key != focus ? 0.25 : 1.0;

        bool light = ActualTheme == ElementTheme.Light;
        Brush selectedBg = ThemeHelper.TryResourceBrush(ActualTheme, "SubtleFillColorSecondaryBrush",
            light ? Color.FromArgb(0x10, 0, 0, 0) : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        Color accent = HostUiHelpers.GetHostConsumerColor(ActualTheme);
        var focusBorder = new SolidColorBrush(Color.FromArgb(170, accent.R, accent.G, accent.B));
        var transparent = new SolidColorBrush(Colors.Transparent);

        foreach (var chip in legendChips)
        {
            bool isSelected = selectedKey == chip.Key;
            bool isHovered = hoveredKey == chip.Key;
            chip.ButtonBorder.Background = isSelected || isHovered ? selectedBg : transparent;
            chip.ButtonBorder.BorderBrush = isSelected || isHovered ? focusBorder : transparent;
            chip.NameText.FontWeight = isSelected || isHovered ? FontWeights.SemiBold : FontWeights.Normal;
            chip.ButtonBorder.Opacity = hasFocus && focus != chip.Key ? 0.35 : 1.0;
        }
    }

    private static string FormatPctOrDash(double? value) =>
        value is double pct && double.IsFinite(pct) ? $"{Math.Clamp(Math.Round(pct), 0, 100):0}%" : "---";
}
