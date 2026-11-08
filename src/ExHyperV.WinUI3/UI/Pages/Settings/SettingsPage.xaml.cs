using ExHyperV.WinUI3.UI.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExHyperV.WinUI3.UI.Pages.Settings;

public sealed partial class SettingsPage : Page
{
    private readonly AppAppearance appearance = ((App)Application.Current).Appearance;
    private readonly List<(Button Button, FontIcon Check, string Color)> swatches = [];
    private bool updating = true;

    public SettingsPage()
    {
        InitializeComponent();
        foreach (var (name, hex) in new[]
        {
            ("蓝色", "#4CC2FF"), ("紫色", "#B69CFF"), ("粉色", "#F39BC1"),
            ("橙色", "#FFB36B"), ("黄色", "#F2D66D"), ("绿色", "#7BDCB5"), ("青色", "#64D8E4")
        })
        {
            AppearanceSettings.TryParseColor(hex, out var color);
            var check = new FontIcon { Glyph = "\uE73E", FontSize = 16, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black) };
            var button = new Button
            {
                Width = 36, Height = 36, CornerRadius = new(18), Padding = new(0),
                Background = new SolidColorBrush(color), Content = check
            };
            button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(color);
            button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(color);
            AutomationProperties.SetName(button, name);
            ToolTipService.SetToolTip(button, name);
            button.Click += (_, _) => SetColor(hex);
            swatches.Add((button, check, hex));
            ColorSwatches.Children.Add(button);
        }
        UpdateControls();
    }

    private void UpdateControls()
    {
        updating = true;
        var settings = appearance.Settings;
        ThemePicker.SelectedIndex = settings.Theme switch { ElementTheme.Light => 1, ElementTheme.Dark => 2, _ => 0 };
        AccentToggle.IsOn = settings.AccentEnabled;
        foreach (var button in ColorControls.Children.OfType<StackPanel>().SelectMany(panel => panel.Children).OfType<Button>())
            button.IsEnabled = settings.AccentEnabled;
        ColorValue.Text = settings.AccentEnabled ? settings.AccentColor : "灰色";
        foreach (var (button, check, hex) in swatches)
        {
            bool selected = string.Equals(hex, settings.AccentColor, StringComparison.OrdinalIgnoreCase);
            check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(button, selected ? "已选择" : "未选择");
        }
        updating = false;
    }

    private void Apply(AppearanceSettings settings)
    {
        string? error = appearance.Set(settings);
        SaveError.Message = error ?? string.Empty;
        SaveError.IsOpen = error != null;
        UpdateControls();
    }

    private void OnThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!updating) Apply(appearance.Settings with
        {
            Theme = ThemePicker.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default }
        });
    }
    private void OnAccentToggled(object sender, RoutedEventArgs args)
    {
        if (!updating) Apply(appearance.Settings with { AccentEnabled = AccentToggle.IsOn });
    }
    private void SetColor(string hex) => Apply(appearance.Settings with { AccentColor = hex });
    private void OnResetColor(object sender, RoutedEventArgs args) => SetColor(new AppearanceSettings().AccentColor);
    private void OnChooseColor(object sender, RoutedEventArgs args)
    {
        AppearanceSettings.TryParseColor(appearance.Settings.AccentColor, out var color);
        AccentPicker.Color = color;
    }
    private void OnApplyColor(object sender, RoutedEventArgs args)
    {
        SetColor(AppearanceSettings.ToHex(AccentPicker.Color));
        ColorFlyout.Hide();
    }
    private void OnCancelColor(object sender, RoutedEventArgs args) => ColorFlyout.Hide();
}
