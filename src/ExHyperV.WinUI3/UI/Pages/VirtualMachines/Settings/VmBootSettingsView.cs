using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmBootSettingsView : VmSettingsView
{
    private readonly StackPanel rowsPanel = new() { Spacing = 10, Margin = new Thickness(0, 4, 0, 24) };
    private readonly List<BootEntry> entries = [];
    private readonly List<Border> cards = [];
    private bool canEdit;

    private Border? dragCard;
    private int dragIndex = -1;
    private int currentTarget = -1;
    private double dragStartY;
    private double dragStride = 62;
    private bool isDragging;

    public VmBootSettingsView(Guid vmId) : base(vmId)
    {
        SetBody(rowsPanel);
    }

    private void RenderRows()
    {
        dragCard = null;
        isDragging = false;
        rowsPanel.Children.Clear();
        cards.Clear();

        var defaultBg = Brush("ControlFillColorDefaultBrush");
        var primaryBg = Brush("ControlFillColorSecondaryBrush");
        var primaryHoverBg = Brush("ControlFillColorTertiaryBrush");
        var borderDefault = Brush("CardStrokeColorDefaultBrush");
        var accentBrush = Brush("VmAccentBrush");
        var secondaryText = Brush("TextFillColorSecondaryBrush");
        var iconFont = new FontFamily("Segoe Fluent Icons");

        for (int i = 0; i < entries.Count; i++)
        {
            int index = i;
            var entry = entries[i];
            bool isPrimary = i == 0;

            var innerGrid = new Grid
            {
                Padding = new Thickness(18, 15, 18, 15),
                ColumnSpacing = 14
            };
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leadingIcon = new FontIcon
            {
                Glyph = entry.Glyph,
                FontFamily = iconFont,
                FontSize = 18,
                VerticalAlignment = VerticalAlignment.Center
            };
            innerGrid.Children.Add(leadingIcon);

            var textBlock = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            textBlock.Inlines.Add(new Run
            {
                Text = entry.Name,
                FontSize = 14.5,
                FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Medium
            });
            if (!string.IsNullOrWhiteSpace(entry.Description))
            {
                textBlock.Inlines.Add(new Run { Text = "   ", FontSize = 14.5 });
                textBlock.Inlines.Add(new Run
                {
                    Text = entry.Description,
                    FontSize = 13.5,
                    Foreground = secondaryText
                });
            }
            Grid.SetColumn(textBlock, 1);
            innerGrid.Children.Add(textBlock);

            var handleIcon = new FontIcon
            {
                Glyph = "\uE700",
                FontFamily = iconFont,
                FontSize = 18,
                Foreground = secondaryText,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(handleIcon, 2);
            innerGrid.Children.Add(handleIcon);

            var rootGrid = new Grid();
            if (isPrimary)
            {
                rootGrid.Children.Add(new Border
                {
                    Width = 3,
                    CornerRadius = new CornerRadius(1.5),
                    Margin = new Thickness(0, 12, 0, 12),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = accentBrush
                });
            }
            rootGrid.Children.Add(innerGrid);

            var card = new Border
            {
                Background = isPrimary ? primaryBg : defaultBg,
                BorderBrush = borderDefault,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                RenderTransform = new CompositeTransform(),
                RenderTransformOrigin = new Point(0.5, 0.5),
                Child = rootGrid
            };

            card.PointerEntered += (_, _) =>
            {
                if (!canEdit || dragCard != null) return;
                card.Background = isPrimary ? primaryHoverBg : primaryBg;
            };
            card.PointerExited += (_, _) =>
            {
                if (dragCard == card) return;
                card.Background = isPrimary ? primaryBg : defaultBg;
            };
            card.PointerPressed += (_, e) =>
            {
                if (!canEdit || Updating || IsBusy || IsStopped || dragCard != null) return;
                if (!e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
                dragCard = card;
                dragIndex = index;
                currentTarget = index;
                isDragging = false;
                dragStartY = e.GetCurrentPoint(rowsPanel).Position.Y;
                dragStride = cards.Count > 1 && cards[1].ActualOffset.Y > cards[0].ActualOffset.Y
                    ? cards[1].ActualOffset.Y - cards[0].ActualOffset.Y
                    : (card.ActualHeight > 0 ? card.ActualHeight + 10 : 62);
                card.CapturePointer(e.Pointer);
                e.Handled = true;
            };
            card.PointerMoved += (_, e) =>
            {
                if (dragCard != card) return;
                double dy = e.GetCurrentPoint(rowsPanel).Position.Y - dragStartY;
                if (!isDragging)
                {
                    if (Math.Abs(dy) < 5) return;
                    isDragging = true;
                    Canvas.SetZIndex(card, 30);
                    card.Background = primaryHoverBg;
                    card.BorderBrush = accentBrush;
                    if (card.RenderTransform is CompositeTransform dt)
                    {
                        dt.ScaleX = 1.012;
                        dt.ScaleY = 1.012;
                    }
                }

                double minDy = -dragIndex * dragStride - 10;
                double maxDy = (cards.Count - 1 - dragIndex) * dragStride + 10;
                double clampedDy = Math.Clamp(dy, minDy, maxDy);
                if (card.RenderTransform is CompositeTransform dragTransform)
                    dragTransform.TranslateY = clampedDy;

                int target = Math.Clamp(dragIndex + (int)Math.Round(clampedDy / dragStride), 0, cards.Count - 1);
                if (target != currentTarget)
                {
                    currentTarget = target;
                    UpdateSiblingOffsets();
                }
                e.Handled = true;
            };
            card.PointerReleased += async (_, e) =>
            {
                if (dragCard != card) return;
                int from = dragIndex;
                int to = currentTarget;
                bool movedByDrag = isDragging;
                dragCard = null;
                isDragging = false;
                ResetRowTransforms();
                card.ReleasePointerCapture(e.Pointer);
                e.Handled = true;

                if (!movedByDrag)
                {
                    if (from > 0 && from < entries.Count)
                    {
                        var clicked = entries[from];
                        entries.RemoveAt(from);
                        entries.Insert(0, clicked);
                        RenderRows();
                        await SubmitOrderAsync();
                    }
                    else
                    {
                        card.Background = isPrimary ? primaryBg : defaultBg;
                    }
                    return;
                }

                if (to != from && from >= 0 && from < entries.Count && to >= 0 && to < entries.Count)
                {
                    var moved = entries[from];
                    entries.RemoveAt(from);
                    entries.Insert(to, moved);
                    RenderRows();
                    await SubmitOrderAsync();
                }
                else
                {
                    card.Background = isPrimary ? primaryBg : defaultBg;
                    card.BorderBrush = borderDefault;
                }
            };
            card.PointerCanceled += (_, _) => AbortDrag(card);
            card.PointerCaptureLost += (_, _) => AbortDrag(card);

            cards.Add(card);
            rowsPanel.Children.Add(card);
        }

        rowsPanel.Opacity = canEdit ? 1.0 : 0.6;
        rowsPanel.IsHitTestVisible = canEdit;
    }

    private void UpdateSiblingOffsets()
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (i == dragIndex) continue;
            double shift = 0;
            if (dragIndex < currentTarget && i > dragIndex && i <= currentTarget)
                shift = -dragStride;
            else if (dragIndex > currentTarget && i >= currentTarget && i < dragIndex)
                shift = dragStride;
            if (cards[i].RenderTransform is CompositeTransform transform)
                transform.TranslateY = shift;
        }
    }

    private void ResetRowTransforms()
    {
        var borderDefault = Brush("CardStrokeColorDefaultBrush");
        var defaultBg = Brush("ControlFillColorDefaultBrush");
        var primaryBg = Brush("ControlFillColorSecondaryBrush");
        for (int i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            Canvas.SetZIndex(card, 0);
            card.BorderBrush = borderDefault;
            card.Background = i == 0 ? primaryBg : defaultBg;
            if (card.RenderTransform is CompositeTransform transform)
            {
                transform.TranslateY = 0;
                transform.ScaleX = 1;
                transform.ScaleY = 1;
            }
        }
    }

    private void AbortDrag(Border card)
    {
        if (dragCard != card) return;
        dragCard = null;
        isDragging = false;
        ResetRowTransforms();
    }

    private Task SubmitOrderAsync()
    {
        var order = entries.Select(e => e.Item).ToList();
        return RunAsync(async () =>
        {
            var result = await VmBootService.SetBootOrderAsync(VmId, order);
            if (result.Success) NotifyChanged();
            await ReloadAfterWriteAsync(result);
        });
    }

    protected override async Task LoadAsync()
    {
        var contextTask = ReadContextAsync();
        var orderTask = VmBootService.GetBootOrderAsync(VmId);
        await Task.WhenAll(contextTask, orderTask);
        var context = await contextTask;
        var result = await orderTask;
        if (!result.HasData) throw new InvalidOperationException(result.Error);
        if (IsStopped) return;
        Updating = true;
        try
        {
            entries.Clear();
            foreach (var item in result.Data!)
                entries.Add(new BootEntry(item));
            var condition = VmBootService.GetBootOrderModificationCondition(context.Generation, context.State);
            canEdit = condition.CanModify;
            RenderRows();
            if (!canEdit) ShowInformation(condition.Reason);
        }
        finally { Updating = false; }
    }

    private sealed class BootEntry(BootOrderItem item)
    {
        public BootOrderItem Item { get; } = item;
        public string Name => Item.Name;
        public string Description => Item.Description;
        public string Glyph => Item.Kind switch
        {
            BootDeviceKind.Floppy => "\uE74E",
            BootDeviceKind.OpticalDrive => "\uE958",
            BootDeviceKind.HardDisk => "\uEDA2",
            BootDeviceKind.Network => "\uE774",
            BootDeviceKind.FirmwareFile => "\uE74C",
            BootDeviceKind.Firmware => "\uE713",
            _ => "\uE9CE"
        };
    }
}
