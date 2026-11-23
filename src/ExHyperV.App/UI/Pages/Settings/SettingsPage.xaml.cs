
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;

namespace ExHyperV.App.UI.Pages.Settings;

public sealed partial class SettingsPage : Page
{
    private readonly AppAppearance appearance = ((App)Application.Current).Appearance;
    private readonly List<(Button Button, FontIcon Check, string Color, string Key)> swatches = [];
    private bool updating = true;

    public SettingsPage()
    {
        InitializeComponent();
        foreach (var (key, hex) in new[]
        {
            ("Settings_Color_Blue", "#4CC2FF"), ("Settings_Color_Purple", "#B69CFF"), ("Settings_Color_Pink", "#F39BC1"),
            ("Settings_Color_Orange", "#FFB36B"), ("Settings_Color_Yellow", "#F2D66D"), ("Settings_Color_Green", "#7BDCB5"), ("Settings_Color_Cyan", "#64D8E4")
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
            button.Click += (_, _) => SetColor(hex);
            swatches.Add((button, check, hex, key));
            ColorSwatches.Children.Add(button);
        }
        ApplyLocalization();
        UpdateControls();
    }

    private void ApplyLocalization()
    {
        TitleHeader.Text = L10n.Get("Settings_Title");
        LanguageSectionHeader.Text = L10n.Get("Settings_LanguageGroup");
        LanguagePicker.Header = L10n.Get("Settings_Language");
        LangZhCnItem.Content = L10n.Get("Settings_Language_ZhCn");
        LangSystemItem.Content = L10n.Get("Settings_Language_System");
        AppearanceSectionHeader.Text = L10n.Get("Settings_Appearance");
        ThemePicker.Header = L10n.Get("Settings_Theme");
        ThemeSystemItem.Content = L10n.Get("Settings_Theme_System");
        ThemeLightItem.Content = L10n.Get("Settings_Theme_Light");
        ThemeDarkItem.Content = L10n.Get("Settings_Theme_Dark");
        AccentToggle.Header = L10n.Get("Settings_Accent");
        AccentToggle.OnContent = L10n.Get("Common_On");
        AccentToggle.OffContent = L10n.Get("Common_Off");
        AutomationProperties.SetName(ColorSwatches, L10n.Get("Settings_ColorPaletteName"));
        CustomColorButton.Content = L10n.Get("Settings_CustomColor");
        CancelColorButton.Content = L10n.Get("Common_Cancel");
        ApplyColorButton.Content = L10n.Get("Common_Apply");
        ResetColorButton.Content = L10n.Get("Settings_ResetColor");
        ColorHintText.Text = L10n.Get("Settings_ColorHint");
        ExpandedStateLabel.Text = L10n.Get("Settings_ExpandedState");
        foreach (var (button, _, _, key) in swatches)
        {
            string colorName = L10n.Get(key);
            AutomationProperties.SetName(button, colorName);
            ToolTipService.SetToolTip(button, colorName);
        }
        if (LanguageNotice.IsOpen) LanguageNotice.Message = L10n.Get("Settings_Language_RestartHint");
    }

    private void UpdateControls()
    {
        updating = true;
        var settings = appearance.Settings;
        LanguagePicker.SelectedIndex = L10n.NormalizePreference(settings.Language) switch
        {
            "zh-CN" => 1,
            "en" => 2,
            _ => 0
        };
        ThemePicker.SelectedIndex = settings.Theme switch { ElementTheme.Light => 1, ElementTheme.Dark => 2, _ => 0 };
        AccentToggle.IsOn = settings.AccentEnabled;
        foreach (var button in ColorControls.Children.OfType<StackPanel>().SelectMany(panel => panel.Children).OfType<Button>())
            button.IsEnabled = settings.AccentEnabled;
        ColorValue.Text = settings.AccentEnabled ? settings.AccentColor : L10n.Get("Settings_Gray");
        foreach (var (button, check, hex, _) in swatches)
        {
            bool selected = string.Equals(hex, settings.AccentColor, StringComparison.OrdinalIgnoreCase);
            check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(button, L10n.Get(selected ? "Settings_Selected" : "Settings_Unselected"));
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

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs args)
    {
        if (updating) return;
        string lang = (LanguagePicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "system";
        L10n.ApplyLanguage(lang);
        Apply(appearance.Settings with { Language = L10n.NormalizePreference(lang) });
        ApplyLocalization();
        UpdateControls();
        LanguageNotice.Message = L10n.Get("Settings_Language_RestartHint");
        LanguageNotice.IsOpen = true;
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
