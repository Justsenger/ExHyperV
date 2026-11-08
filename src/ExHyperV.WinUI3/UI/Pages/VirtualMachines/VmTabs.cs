using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

/// <summary>Compact tabs with a short accent underline, shared by both dashboard tab levels.</summary>
public sealed class VmTabs : UserControl
{
    private readonly VmWrapPanel buttons = new() { Spacing = 4 };
    private readonly List<Button> items = [];
    private readonly List<Border> indicators = [];
    private readonly Brush transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    private string[] glyphs = [];
    private readonly List<string> titles = [];
    private int selected;
    public event Action<int>? SelectionRequested;
    public int SelectedIndex => selected;

    public VmTabs()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Content = buttons;
        ActualThemeChanged += (_, _) => UpdateLabels();
    }

    public void SetItems(params string[] titles)
    {
        buttons.Children.Clear(); items.Clear(); indicators.Clear(); this.titles.Clear(); this.titles.AddRange(titles);
        for (int i = 0; i < titles.Length; i++)
        {
            int index = i;
            var button = new Button { Content = titles[i], MinWidth = 64, Height = 36, MinHeight = 36, Padding = new Thickness(12, 0, 12, 3),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"] };
            button.Click += (_, _) => SelectionRequested?.Invoke(index);
            button.KeyDown += (_, e) =>
            {
                int next = e.Key switch { Windows.System.VirtualKey.Right => (selected + 1) % items.Count,
                    Windows.System.VirtualKey.Left => (selected + items.Count - 1) % items.Count,
                    Windows.System.VirtualKey.Home => 0, Windows.System.VirtualKey.End => items.Count - 1, _ => -1 };
                if (next < 0) return;
                e.Handled = true; items[next].Focus(FocusState.Keyboard); SelectionRequested?.Invoke(next);
            };
            var indicator = new Border { Height = 3, Width = 32, CornerRadius = new CornerRadius(1.5),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Background = (Brush)Application.Current.Resources["VmAccentBrush"], IsHitTestVisible = false };
            var tab = new Grid();
            tab.Children.Add(button); tab.Children.Add(indicator);
            items.Add(button); indicators.Add(indicator); buttons.Children.Add(tab);
        }
        Select(Math.Min(selected, Math.Max(0, items.Count - 1)));
    }

    public void SetGlyphs(params string[] values) { glyphs = values; for (int i = 0; i < items.Count; i++) SetTitle(i, titles[i]); }
    public void SetTitle(int index, string title)
    {
        titles[index] = title;
        if (index < glyphs.Length)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            content.Children.Add(new FontIcon { Glyph = glyphs[index], FontSize = 16, FontFamily = new FontFamily("Segoe Fluent Icons") });
            content.Children.Add(new TextBlock { Text = title, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
            items[index].Content = content;
        }
        else items[index].Content = title;
        UpdateLabels();
    }
    public void Select(int index)
    {
        if (index < 0 || index >= items.Count) return;
        selected = index;
        UpdateLabels();
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
