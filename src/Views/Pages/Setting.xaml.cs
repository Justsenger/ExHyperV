using System.Windows.Controls;
using ExHyperV.Services;

namespace ExHyperV.Views.Pages;

public partial class Setting
{
    // Private fields
    private bool _isInitializing = true; // 标志变量，用于避免死循环

    public Setting()
    {
        InitializeComponent();

        // Set theme ComboBox based on current preference
        var currentTheme = ThemeService.ThemePreference();
        var targetResource = currentTheme switch
        {
            "Auto" => Properties.Resources.auto,
            "Light" => Properties.Resources.light,
            "Dark" => Properties.Resources.dark,
            _ => Properties.Resources.auto
        };

        ThemeComboBox.SelectedItem = ThemeComboBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(x => x.Content.ToString() == targetResource);

        // Set language ComboBox based on current language
        var currentLanguage = LocalizationService.ReadLanguageFromConfig();
        SetLanguageCombo(currentLanguage);

        // Special skin functionality is not implemented - controls are disabled
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeComboBox.SelectedItem is not ComboBoxItem selectedItem) return;
        var selectedText = selectedItem.Content.ToString();

        var themePreference = selectedText switch
        {
            _ when selectedText == Properties.Resources.auto => "Auto",
            _ when selectedText == Properties.Resources.light => "Light",
            _ when selectedText == Properties.Resources.dark => "Dark",
            _ => "Auto"
        };

        // Save preference and apply theme
        ThemeService.SaveThemePreference(themePreference);
        ThemeService.ApplyTheme(themePreference);
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing)
        {
            _isInitializing = false;
            return;
        }

        if (LanguageComboBox.SelectedItem is not ComboBoxItem) return;

        // Map ComboBox index to language code
        var languageCode = LanguageComboBox.SelectedIndex switch
        {
            0 => "zh-CN", // Chinese
            1 => "en-US", // English
            _ => "en-US"
        };

        ChangeLanguageDynamic(languageCode);
    }

    private void OnSPskinSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Special skins are not implemented yet - control is disabled
    }

    /// <summary>
    ///     Changes language dynamically without application restart
    /// </summary>
    /// <param name="languageCode">Language code to change to</param>
    private static void ChangeLanguageDynamic(string languageCode)
    {
        // Change language dynamically - UI will update automatically
        LocalizationService.ChangeLanguage(languageCode);
    }

    private void SetLanguageCombo(string languageCode)
    {
        // Map language code to ComboBox index
        var index = languageCode switch
        {
            "zh-CN" => 0,
            "en-US" => 1,
            _ => 1 // Default to English
        };

        LanguageComboBox.SelectedIndex = index;
    }
}