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

internal sealed partial class HostStorageOverviewView : UserControl
{
    private const string HostKey = "__host__";

    private sealed record StorageConsumerModel(
        string Key,
        string Name,
        string RoleTag,
        Color Color,
        double? ReadBps,
        double? WriteBps,
        long? Iops,
        long? Queue,
        bool IsHost)
    {
        public double? TotalBps => ReadBps.HasValue || WriteBps.HasValue
            ? (ReadBps ?? 0) + (WriteBps ?? 0)
            : null;
    }

    private sealed record DiskCardModel(
        HostDiskInfo Disk,
        bool IsOffline,
        string StatusBadge,
        double? TotalReadBps,
        double? TotalWriteBps,
        double? TotalBps,
        IReadOnlyList<StorageConsumerModel> Consumers);

    private sealed class ConsumerRowRefs
    {
        public required string Key { get; init; }
        public required Color Color { get; init; }
        public required Border Root { get; init; }
        public required ColumnDefinition ReadBarCol { get; init; }
        public required ColumnDefinition WriteBarCol { get; init; }
        public required ColumnDefinition EmptyBarCol { get; init; }
        public required TextBlock ReadPillValue { get; init; }
        public required TextBlock WritePillValue { get; init; }
        public TextBlock? IopsPillValue { get; init; }
        public TextBlock? QueuePillValue { get; init; }
    }

    private sealed class DiskCardRefs
    {
        public required int DiskNumber { get; init; }
        public required TextBlock StatusBadgeText { get; init; }
        public required TextBlock ReadingVal { get; init; }
        public required TextBlock ReadingSub { get; init; }
        public required TextBlock ShareRightText { get; init; }
        public required FrameworkElement ShareSection { get; init; }
        public required Grid ShareBarGrid { get; init; }
        public required List<(string Key, Color Color, ColumnDefinition Col)> ShareCols { get; init; }
        public required List<ConsumerRowRefs> Rows { get; init; }
    }

    private sealed class LegendChipRef
    {
        public required string Key { get; init; }
        public required Border Border { get; init; }
        public required TextBlock NameText { get; init; }
        public required TextBlock RateText { get; init; }
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
    private IReadOnlyList<DiskCardModel> currentCards = [];
    private IReadOnlyList<(string Key, string Name, Color Color, double? TotalBps)> legendConsumers = [];
    private readonly List<DiskCardRefs> cardRefs = [];
    private readonly List<LegendChipRef> legendChips = [];
    private readonly List<(string Key, FrameworkElement Element, Action<bool, bool>? OnFocus)> focusTargets = [];
    private readonly Dictionary<Guid, int> vmColorSlots = new();
    private int nextColorSlot;

    private string? hoveredKey;
    private string? selectedKey;
    private bool isNarrowStacked;
    private string lastStructureKey = string.Empty;

    public HostStorageOverviewView()
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
        var disks = inventory?.Disks ?? [];
        var vmDisks = lastSample?.VmStorage;

        var cards = new List<DiskCardModel>(disks.Count);
        var totalsByKey = new Dictionary<string, (string Name, Color Color, double? TotalBps)>(StringComparer.Ordinal);
        totalsByKey[HostKey] = (L10n.Get("Host_Cpu_HostOwner"), hostColor, null);

        static double? SumOpt(double? a, double? b) =>
            a.HasValue || b.HasValue ? (a ?? 0) + (b ?? 0) : null;

        foreach (var disk in disks)
        {
            bool offline = disk.IsOffline == true;
            HostDiskActivity? diskAct = null;
            if (!offline) lastSample?.Disks.TryGetValue(disk.Number, out diskAct);

            var passthrough = vmDisks?
                .Where(v => v.DiskNumber == disk.Number)
                .ToList() ?? [];

            var consumers = new List<StorageConsumerModel>();
            if (!offline)
            {
                double? hostRead = diskAct?.ReadBytesPerSec;
                double? hostWrite = diskAct?.WriteBytesPerSec;
                consumers.Add(new StorageConsumerModel(
                    HostKey,
                    L10n.Get("Host_Cpu_HostOwner"),
                    L10n.Get("Host_Storage_RoleHost"),
                    hostColor,
                    hostRead,
                    hostWrite,
                    null,
                    null,
                    true));
                double? hostSum = SumOpt(hostRead, hostWrite);
                if (hostSum.HasValue)
                {
                    var cur = totalsByKey[HostKey];
                    totalsByKey[HostKey] = (cur.Name, cur.Color, (cur.TotalBps ?? 0) + hostSum.Value);
                }
            }

            foreach (var vm in passthrough)
            {
                Color col = GetVmColor(vm.VmId);
                string key = $"vm:{vm.VmId:N}";
                long? iops = vm.ReadOpsPerSec.HasValue || vm.WriteOpsPerSec.HasValue
                    ? (vm.ReadOpsPerSec ?? 0) + (vm.WriteOpsPerSec ?? 0)
                    : null;
                consumers.Add(new StorageConsumerModel(
                    key,
                    vm.Name,
                    L10n.Get("Host_Storage_RolePassthrough"),
                    col,
                    vm.ReadBytesPerSec,
                    vm.WriteBytesPerSec,
                    iops,
                    vm.QueueLength,
                    false));

                double? vmSum = SumOpt(vm.ReadBytesPerSec, vm.WriteBytesPerSec);
                if (!totalsByKey.TryGetValue(key, out var prev))
                    totalsByKey[key] = (vm.Name, col, vmSum);
                else if (vmSum.HasValue)
                    totalsByKey[key] = (prev.Name, prev.Color, (prev.TotalBps ?? 0) + vmSum.Value);
            }

            double? totalRead = consumers.Any(c => c.ReadBps.HasValue) ? consumers.Where(c => c.ReadBps.HasValue).Sum(c => c.ReadBps!.Value) : null;
            double? totalWrite = consumers.Any(c => c.WriteBps.HasValue) ? consumers.Where(c => c.WriteBps.HasValue).Sum(c => c.WriteBps!.Value) : null;
            double? totalBps = SumOpt(totalRead, totalWrite);

            string statusBadge = passthrough.Count > 0 && offline
                ? L10n.Get("Host_Storage_StatusPassthrough")
                : offline
                    ? L10n.Get("Host_Disk_Offline")
                    : passthrough.Count > 0
                        ? L10n.Format("Host_Storage_StatusShared", passthrough.Count)
                        : L10n.Get("Host_Storage_StatusHostOnly");

            cards.Add(new DiskCardModel(disk, offline, statusBadge, totalRead, totalWrite, totalBps, consumers));
        }

        currentCards = cards;
        var legendOrder = new List<(string Key, string Name, Color Color, double? TotalBps)>
        {
            (HostKey, totalsByKey[HostKey].Name, totalsByKey[HostKey].Color, totalsByKey[HostKey].TotalBps)
        };
        foreach (var kv in totalsByKey)
        {
            if (kv.Key != HostKey)
                legendOrder.Add((kv.Key, kv.Value.Name, kv.Value.Color, kv.Value.TotalBps));
        }
        legendConsumers = legendOrder;

        if (selectedKey != null && !legendConsumers.Any(x => x.Key == selectedKey)) selectedKey = null;
        if (hoveredKey != null && !legendConsumers.Any(x => x.Key == hoveredKey)) hoveredKey = null;

        string structKey = string.Join("|", cards.Select(c =>
            $"{c.Disk.Number}:{c.IsOffline}:{string.Join(",", c.Consumers.Select(x => x.Key))}"));

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
        Brush barBg = ThemeHelper.TryResourceBrush(ActualTheme, "ControlFillColorTertiaryBrush",
            light ? Color.FromArgb(0x0A, 0, 0, 0) : Color.FromArgb(0x0F, 0xFF, 0xFF, 0xFF));

        legendBox.BorderBrush = divider;
        hostPinBox.BorderBrush = divider;

        cardsStack.Children.Clear();
        cardRefs.Clear();
        legendChips.Clear();
        focusTargets.Clear();
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
                Child = new TextBlock { Text = L10n.Get("Host_Storage_NoPhysical"), FontSize = 13, Foreground = new SolidColorBrush(secondary) }
            });
        }
        else
        {
            foreach (var card in currentCards)
                cardsStack.Children.Add(BuildDiskCard(card, primary, secondary, cardBg, cardStroke, divider, wellBg, wellBorder, barBg));
        }

        if (legendConsumers.Count > 0)
        {
            hostPinStack.Children.Add(BuildLegendChip(legendConsumers[0], primary, secondary));
            if (legendConsumers.Count == 1)
            {
                legendList.Children.Add(new TextBlock
                {
                    Text = L10n.Get("Host_Storage_NoVms"),
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

    private Border BuildDiskCard(
        DiskCardModel card,
        Color primary,
        Color secondary,
        Brush cardBg,
        Brush cardStroke,
        Brush divider,
        Brush wellBg,
        Brush wellBorder,
        Brush barBg)
    {
        var body = new StackPanel { Spacing = 12 };

        var headGrid = new Grid { ColumnSpacing = 12 };
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Color diskAccent = Color.FromArgb(255, 0x6C, 0xCB, 0x5F);
        var iconBox = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(7),
            Background = ThemeHelper.TryResourceBrush(ActualTheme, "SubtleFillColorSecondaryBrush", Color.FromArgb(0x12, 0x88, 0x88, 0x88)),
            BorderBrush = divider,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon { Glyph = "\uEDA2", FontSize = 16, Foreground = new SolidColorBrush(diskAccent) }
        };
        Grid.SetColumn(iconBox, 0);
        headGrid.Children.Add(iconBox);

        var titleCol = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var titleLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleLine.Children.Add(new TextBlock
        {
            Text = L10n.Format("Host_Disk_Title", card.Disk.Number),
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(primary),
            VerticalAlignment = VerticalAlignment.Center
        });
        titleLine.Children.Add(new TextBlock
        {
            Text = card.Disk.FriendlyName,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(secondary),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleCol.Children.Add(titleLine);

        var statusBadge = HostUiHelpers.BuildMiniBadge(ActualTheme, card.StatusBadge);
        var badgeWrap = new Shared.Controls.WrapPanel { Spacing = 6 };
        badgeWrap.Children.Add(HostUiHelpers.BuildMiniBadge(ActualTheme, $"{card.Disk.SizeGiB:0.##} GB"));
        badgeWrap.Children.Add(statusBadge);
        badgeWrap.Children.Add(HostUiHelpers.BuildMiniBadge(ActualTheme,
            L10n.Get(card.Disk.CanPassthrough ? "Host_Storage_PassthroughBadge" : "Host_Storage_SystemOrAssignedBadge")));
        titleCol.Children.Add(badgeWrap);
        Grid.SetColumn(titleCol, 1);
        headGrid.Children.Add(titleCol);

        var readingCol = new StackPanel { Spacing = 1, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var readingVal = new TextBlock
        {
            FontSize = 20,
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

        var shareCols = new List<(string Key, Color Color, ColumnDefinition Col)>();
        var shareBarGrid = new Grid { ColumnSpacing = 2 };
        foreach (var c in card.Consumers)
        {
            var colDef = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            int colIdx = shareBarGrid.ColumnDefinitions.Count;
            shareBarGrid.ColumnDefinitions.Add(colDef);
            var seg = new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(c.Color) };
            Grid.SetColumn(seg, colIdx);
            AttachInteraction(seg, c.Key, null);
            shareBarGrid.Children.Add(seg);
            shareCols.Add((c.Key, c.Color, colDef));
        }

        var shareSection = new StackPanel
        {
            Spacing = 5,
            Visibility = card.Consumers.Count > 0 ? Visibility.Visible : Visibility.Collapsed
        };
        var shareHead = new Grid();
        shareHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shareHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shareHead.Children.Add(new TextBlock { Text = L10n.Get("Host_Storage_ShareTitle"), FontSize = 11.5, Foreground = new SolidColorBrush(secondary) });
        var shareRight = new TextBlock { FontSize = 11.5, Foreground = new SolidColorBrush(secondary) };
        Typography.SetNumeralAlignment(shareRight, FontNumeralAlignment.Tabular);
        Grid.SetColumn(shareRight, 1);
        shareHead.Children.Add(shareRight);
        shareSection.Children.Add(shareHead);
        shareSection.Children.Add(new Border
        {
            Height = 14,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = wellBorder,
            Background = barBg,
            Padding = new Thickness(1),
            Child = shareBarGrid
        });
        body.Children.Add(shareSection);

        var rowRefs = new List<ConsumerRowRefs>();
        if (card.Consumers.Count == 0)
        {
            var idleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            idleRow.Children.Add(new FontIcon { Glyph = "\uEDA2", FontSize = 14, Foreground = new SolidColorBrush(secondary) });
            idleRow.Children.Add(new TextBlock
            {
                Text = L10n.Get(card.Disk.CanPassthrough ? "Host_Storage_IdleOfflinePassthrough" : "Host_Storage_IdleOfflineOther"),
                FontSize = 12,
                Foreground = new SolidColorBrush(secondary),
                TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(new Border
            {
                Background = wellBg,
                BorderBrush = wellBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(12, 10, 12, 10),
                Child = idleRow
            });
        }
        else
        {
            var rowsStack = new StackPanel { Spacing = 6 };
            foreach (var c in card.Consumers)
            {
                var rowRef = BuildConsumerRow(c, primary, wellBg, wellBorder, barBg);
                rowRefs.Add(rowRef);
                rowsStack.Children.Add(rowRef.Root);
            }
            body.Children.Add(rowsStack);
        }

        cardRefs.Add(new DiskCardRefs
        {
            DiskNumber = card.Disk.Number,
            StatusBadgeText = (TextBlock)statusBadge.Child,
            ReadingVal = readingVal,
            ReadingSub = readingSub,
            ShareRightText = shareRight,
            ShareSection = shareSection,
            ShareBarGrid = shareBarGrid,
            ShareCols = shareCols,
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

    private ConsumerRowRefs BuildConsumerRow(StorageConsumerModel c, Color primary, Brush wellBg, Brush wellBorder, Brush barBg)
    {
        var rowGrid = new Grid { ColumnSpacing = 12 };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new Border { Width = 3, Height = 14, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(c.Color), VerticalAlignment = VerticalAlignment.Center });
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

        // Mini stacked read/write bar
        var rwGrid = new Grid { ColumnSpacing = 1 };
        var readCol = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        var writeCol = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        var emptyCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        rwGrid.ColumnDefinitions.Add(readCol);
        rwGrid.ColumnDefinitions.Add(writeCol);
        rwGrid.ColumnDefinitions.Add(emptyCol);
        var readFill = new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(c.Color) };
        var writeFill = new Border { CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(c.Color), Opacity = 0.55 };
        Grid.SetColumn(readFill, 0);
        Grid.SetColumn(writeFill, 1);
        rwGrid.Children.Add(readFill);
        rwGrid.Children.Add(writeFill);

        var barTrack = new Border
        {
            Height = 8,
            CornerRadius = new CornerRadius(3),
            Background = barBg,
            BorderBrush = wellBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = rwGrid
        };
        Grid.SetColumn(barTrack, 1);
        rowGrid.Children.Add(barTrack);

        var pills = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var readPill = HostUiHelpers.BuildPillTag(ActualTheme, L10n.Get("Host_Disk_Read"), "---");
        var writePill = HostUiHelpers.BuildPillTag(ActualTheme, L10n.Get("Host_Disk_Write"), "---");
        pills.Children.Add(readPill);
        pills.Children.Add(writePill);

        TextBlock? iopsVal = null;
        TextBlock? queueVal = null;
        if (!c.IsHost)
        {
            var iopsPill = HostUiHelpers.BuildPillTag(ActualTheme, L10n.Get("Host_Storage_Iops"), "---");
            var queuePill = HostUiHelpers.BuildPillTag(ActualTheme, L10n.Get("Host_Storage_Queue"), "---");
            pills.Children.Add(iopsPill);
            pills.Children.Add(queuePill);
            iopsVal = (TextBlock)((StackPanel)iopsPill.Child).Children[1];
            queueVal = (TextBlock)((StackPanel)queuePill.Child).Children[1];
        }
        Grid.SetColumn(pills, 2);
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
        Color tint = HostUiHelpers.GetHostConsumerColor(ActualTheme);
        var focusBorder = new SolidColorBrush(Color.FromArgb(140, tint.R, tint.G, tint.B));
        AttachInteraction(border, c.Key, (focused, _) =>
        {
            border.Background = focused ? focusBg : wellBg;
            border.BorderBrush = focused ? focusBorder : wellBorder;
        });

        return new ConsumerRowRefs
        {
            Key = c.Key,
            Color = c.Color,
            Root = border,
            ReadBarCol = readCol,
            WriteBarCol = writeCol,
            EmptyBarCol = emptyCol,
            ReadPillValue = (TextBlock)((StackPanel)readPill.Child).Children[1],
            WritePillValue = (TextBlock)((StackPanel)writePill.Child).Children[1],
            IopsPillValue = iopsVal,
            QueuePillValue = queueVal
        };
    }

    private Border BuildLegendChip((string Key, string Name, Color Color, double? TotalBps) item, Color primary, Color secondary)
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

        var rateText = new TextBlock
        {
            Text = item.TotalBps is { } bps ? HostUiHelpers.FormatBytesPerSec(bps) : "---",
            FontSize = 11,
            Foreground = new SolidColorBrush(secondary),
            VerticalAlignment = VerticalAlignment.Center
        };
        Typography.SetNumeralAlignment(rateText, FontNumeralAlignment.Tabular);
        Grid.SetColumn(rateText, 2);
        row.Children.Add(rateText);

        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(7, 5, 7, 5),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            Child = row
        };
        legendChips.Add(new LegendChipRef { Key = item.Key, Border = border, NameText = nameText, RateText = rateText });
        AttachInteraction(border, item.Key, null);
        return border;
    }

    private void UpdateLiveValues()
    {
        bool light = ActualTheme == ElementTheme.Light;
        Color primary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White);
        Color secondary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));

        string? focus = hoveredKey ?? selectedKey;

        for (int i = 0; i < currentCards.Count && i < cardRefs.Count; i++)
        {
            var card = currentCards[i];
            var r = cardRefs[i];

            r.StatusBadgeText.Text = card.StatusBadge;
            var focusedConsumer = focus != null ? card.Consumers.FirstOrDefault(c => c.Key == focus) : null;

            if (focusedConsumer != null)
            {
                string rStr = focusedConsumer.ReadBps is { } rb ? HostUiHelpers.FormatBytesPerSec(rb) : "---";
                string wStr = focusedConsumer.WriteBps is { } wb ? HostUiHelpers.FormatBytesPerSec(wb) : "---";
                r.ReadingVal.Text = $"{rStr} / {wStr}";
                r.ReadingVal.Foreground = new SolidColorBrush(focusedConsumer.Color);
                r.ReadingSub.Text = L10n.Format("Host_Storage_FocusRw", focusedConsumer.Name);
            }
            else if (card.IsOffline && card.Consumers.Count == 0)
            {
                r.ReadingVal.Text = L10n.Get("Host_Disk_Offline");
                r.ReadingVal.Foreground = new SolidColorBrush(secondary);
                r.ReadingSub.Text = L10n.Get("Host_Storage_Offline");
            }
            else
            {
                string rStr = card.TotalReadBps is { } rb ? HostUiHelpers.FormatBytesPerSec(rb) : "---";
                string wStr = card.TotalWriteBps is { } wb ? HostUiHelpers.FormatBytesPerSec(wb) : "---";
                r.ReadingVal.Text = $"{rStr} / {wStr}";
                r.ReadingVal.Foreground = new SolidColorBrush(primary);
                r.ReadingSub.Text = L10n.Get("Host_Storage_TotalRw");
            }

            r.ShareRightText.Text = card.TotalBps is { } tb
                ? L10n.Format("Host_Storage_TotalThroughput", HostUiHelpers.FormatBytesPerSec(tb))
                : "---";

            double activeSum = card.Consumers.Sum(c => c.TotalBps ?? 0);
            for (int cIdx = 0; cIdx < card.Consumers.Count && cIdx < r.ShareCols.Count; cIdx++)
            {
                double weight = activeSum > 0
                    ? Math.Max(0.5, ((card.Consumers[cIdx].TotalBps ?? 0) / activeSum) * 100.0)
                    : 1.0;
                r.ShareCols[cIdx].Col.Width = new GridLength(weight, GridUnitType.Star);
            }

            double maxRowBps = Math.Max(1.0, card.Consumers.Max(c => c.TotalBps ?? 0));
            for (int rowIdx = 0; rowIdx < card.Consumers.Count && rowIdx < r.Rows.Count; rowIdx++)
            {
                var c = card.Consumers[rowIdx];
                var rr = r.Rows[rowIdx];
                double rw = (c.ReadBps ?? 0) / maxRowBps;
                double ww = (c.WriteBps ?? 0) / maxRowBps;
                rr.ReadBarCol.Width = new GridLength(Math.Max(0, rw * 100), GridUnitType.Star);
                rr.WriteBarCol.Width = new GridLength(Math.Max(0, ww * 100), GridUnitType.Star);
                rr.EmptyBarCol.Width = new GridLength(Math.Max(0.1, 100 - Math.Min(100, (rw + ww) * 100)), GridUnitType.Star);
                rr.ReadPillValue.Text = c.ReadBps is { } rb ? HostUiHelpers.FormatBytesPerSec(rb) : "---";
                rr.WritePillValue.Text = c.WriteBps is { } wb ? HostUiHelpers.FormatBytesPerSec(wb) : "---";
                if (rr.IopsPillValue != null)
                    rr.IopsPillValue.Text = c.Iops is { } iv ? iv.ToString() : "---";
                if (rr.QueuePillValue != null)
                    rr.QueuePillValue.Text = c.Queue is { } qv ? qv.ToString() : "---";
            }
        }

        for (int i = 0; i < legendConsumers.Count && i < legendChips.Count; i++)
        {
            legendChips[i].RateText.Text = legendConsumers[i].TotalBps is { } bps
                ? HostUiHelpers.FormatBytesPerSec(bps)
                : "---";
        }
    }

    private void AttachInteraction(FrameworkElement el, string key, Action<bool, bool>? onFocus)
    {
        focusTargets.Add((key, el, onFocus));
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

        foreach (var (key, el, onFocus) in focusTargets)
        {
            bool isFocused = hasFocus && key == focus;
            bool isDimmed = hasFocus && !isFocused;
            el.Opacity = isDimmed ? 0.25 : 1.0;
            onFocus?.Invoke(isFocused, isDimmed);
        }

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
            chip.Border.Background = isSelected || isHovered ? selectedBg : transparent;
            chip.Border.BorderBrush = isSelected || isHovered ? focusBorder : transparent;
            chip.NameText.FontWeight = isSelected || isHovered ? FontWeights.SemiBold : FontWeights.Normal;
            chip.Border.Opacity = hasFocus && focus != chip.Key ? 0.35 : 1.0;
        }
    }
}
