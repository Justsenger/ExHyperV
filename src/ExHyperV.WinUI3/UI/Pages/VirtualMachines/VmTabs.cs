using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

internal sealed record VmTabIcon(string Glyph, string FontFamily = "Segoe Fluent Icons", double Rotation = 0);

/// <summary>Compact tabs with a short accent underline, shared by both dashboard tab levels.</summary>
public sealed class VmTabs : UserControl
{
    private readonly StackPanel buttons = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly List<Grid> tabs = [];
    private readonly ScrollViewer scroll = new()
    {
        HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        ZoomMode = ZoomMode.Disabled, IsHorizontalScrollChainingEnabled = false
    };
    private readonly List<Button> items = [];
    private readonly List<Border> indicators = [];
    private readonly Brush transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    private VmTabIcon[] icons = [];
    private readonly List<string> titles = [];
    private int selected;
    private bool visibilityQueued;
    private double? wheelTarget;
    private int keyboardFocus = -1;
    public event Action<int>? SelectionRequested;
    public int SelectedIndex => selected;

    public VmTabs()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Height = 36;
        buttons.Background = transparent;
        scroll.Content = buttons;
        Content = scroll;
        // Handle vertical wheels in the content before ScrollViewer sees the event.
        // Touch panning and horizontal wheels remain native ScrollViewer gestures.
        buttons.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), true);
        scroll.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => wheelTarget = null), true);
        scroll.ViewChanged += (_, args) =>
        {
            if (!args.IsIntermediate && wheelTarget is double target && Math.Abs(scroll.HorizontalOffset - target) < .5)
                wheelTarget = null;
        };
        Loaded += (_, _) => QueueSelectedIntoView();
        SizeChanged += (_, _) => QueueSelectedIntoView();
        buttons.SizeChanged += (_, _) => QueueSelectedIntoView();
        ActualThemeChanged += (_, _) => { UpdateLabels(); QueueSelectedIntoView(); };
    }

    public void SetItems(params string[] titles)
    {
        buttons.Children.Clear(); tabs.Clear(); items.Clear(); indicators.Clear(); wheelTarget = null;
        this.titles.Clear(); this.titles.AddRange(titles);
        for (int i = 0; i < titles.Length; i++)
        {
            int index = i;
            var button = new Button { Content = titles[i], MinWidth = 64, Height = 36, MinHeight = 36, Padding = new Thickness(12, 0, 12, 3),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"] };
            button.Click += (_, _) => SelectionRequested?.Invoke(index);
            button.Loaded += (_, _) => QueueSelectedIntoView();
            button.KeyDown += (_, e) =>
            {
                int next = e.Key switch { Windows.System.VirtualKey.Right => (selected + 1) % items.Count,
                    Windows.System.VirtualKey.Left => (selected + items.Count - 1) % items.Count,
                    Windows.System.VirtualKey.Home => 0, Windows.System.VirtualKey.End => items.Count - 1, _ => -1 };
                if (next < 0) return;
                e.Handled = true; keyboardFocus = next; SelectionRequested?.Invoke(next);
            };
            var indicator = new Border { Height = 3, Width = 32, CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Background = (Brush)Application.Current.Resources["VmAccentBrush"], IsHitTestVisible = false };
            var tab = new Grid();
            tab.SizeChanged += (_, _) => QueueSelectedIntoView();
            tab.Children.Add(button); tab.Children.Add(indicator);
            items.Add(button); indicators.Add(indicator); tabs.Add(tab); buttons.Children.Add(tab);
        }
        Select(Math.Min(selected, Math.Max(0, items.Count - 1)));
    }

    public void SetGlyphs(params string[] values) => SetIcons(values.Select(value => new VmTabIcon(value)).ToArray());
    internal void SetIcons(params VmTabIcon[] values) { icons = values; for (int i = 0; i < items.Count; i++) SetTitle(i, titles[i]); }
    public void SetTitle(int index, string title)
    {
        titles[index] = title;
        if (index < icons.Length && !string.IsNullOrEmpty(icons[index].Glyph))
        {
            var icon = icons[index];
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(new FontIcon
            {
                Glyph = icon.Glyph, FontSize = 16, FontFamily = new FontFamily(icon.FontFamily),
                RenderTransformOrigin = new Windows.Foundation.Point(.5, .5),
                RenderTransform = new RotateTransform { Angle = icon.Rotation }
            });
            content.Children.Add(new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            items[index].Content = content;
        }
        else items[index].Content = title;
        UpdateLabels();
        QueueSelectedIntoView();
    }
    public void Select(int index)
    {
        if (index < 0 || index >= items.Count) return;
        selected = index;
        if (keyboardFocus != index) keyboardFocus = -1;
        UpdateLabels();
        QueueSelectedIntoView();
    }

    private void QueueSelectedIntoView()
    {
        if (visibilityQueued) return;
        visibilityQueued = true;
        if (!DispatcherQueue.TryEnqueue(() => { visibilityQueued = false; EnsureSelectedVisible(); })) visibilityQueued = false;
    }

    private void EnsureSelectedVisible()
    {
        if (!IsLoaded || selected >= tabs.Count || scroll.ViewportWidth <= 0 || tabs[selected].ActualWidth <= 0) return;
        var tab = tabs[selected];
        double left = tab.TransformToVisual(buttons).TransformPoint(new Point()).X;
        double right = left + tab.ActualWidth;
        double offset = scroll.HorizontalOffset;
        if (left < offset || tab.ActualWidth > scroll.ViewportWidth) offset = left;
        else if (right > offset + scroll.ViewportWidth) offset = right - scroll.ViewportWidth;
        wheelTarget = null;
        scroll.ChangeView(Math.Clamp(offset, 0, scroll.ScrollableWidth), null, null, disableAnimation: true);
        if (keyboardFocus == selected)
        {
            items[selected].Focus(FocusState.Keyboard);
            keyboardFocus = -1;
        }
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (!IsEnabled || scroll.ScrollableWidth <= 0) return;
        var properties = args.GetCurrentPoint(buttons).Properties;
        if (properties.IsHorizontalMouseWheel)
        {
            wheelTarget = null;
            return;
        }
        if (properties.MouseWheelDelta == 0) return;
        // One wheel detent (120) moves approximately one tab; fractional deltas
        // from high-resolution wheels are preserved. Down moves right, up left.
        double target = Math.Clamp((wheelTarget ?? scroll.HorizontalOffset) - properties.MouseWheelDelta * (80d / 120), 0, scroll.ScrollableWidth);
        wheelTarget = target;
        if (!scroll.ChangeView(target, null, null, disableAnimation: true)) wheelTarget = null;
        args.Handled = true;
    }

    private void UpdateLabels()
    {
        for (int i = 0; i < items.Count; i++)
        {
            items[i].Foreground = (Brush)Application.Current.Resources[i == selected ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush"];
            items[i].Background = i == selected ? (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"] : transparent;
            indicators[i].Visibility = i == selected ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetName(items[i], titles[i] + (i == selected ? "，已选中" : ""));
        }
    }
}
