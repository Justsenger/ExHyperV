using System.Globalization;
using System.Windows.Controls;
using ExHyperV.Models;
using Wpf.Ui.Appearance;
using WPFLocalizeExtension.Engine;

namespace ExHyperV.Views.Pages;

public partial class Setting
{
    private readonly bool isInitializing = true; // 标志变量，用于避免死循环
    public string sp = "none"; //炫彩开关

    public Setting()
    {
        InitializeComponent();

        // Theme initialization
        var currentTheme = ApplicationThemeManager.GetAppTheme();
        var targetThemeType = currentTheme == ApplicationTheme.Dark ? ThemeType.Dark : ThemeType.Light;

        // Find and select the ComboBoxItem with the matching Tag
        foreach (ComboBoxItem item in ThemeComboBox.Items)
            if (item.Tag is ThemeType themeType && themeType == targetThemeType)
            {
                ThemeComboBox.SelectedItem = item;
                break;
            }

        // Force refresh theme to ensure proper color inheritance
        ApplicationThemeManager.Apply(currentTheme);

        // Language initialization based on current WPFLocalizeExtension culture
        var currentCulture = LocalizeDictionary.Instance.Culture?.Name ?? "en-US";

        // Find and select the ComboBoxItem with the matching Tag
        foreach (ComboBoxItem item in LanguageComboBox.Items)
            if (item.Tag?.ToString() == currentCulture)
            {
                LanguageComboBox.SelectedItem = item;
                break;
            }

        isInitializing = false;
    }


    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;

        var selectedItem = ThemeComboBox.SelectedItem as ComboBoxItem;
        if (selectedItem?.Tag is ThemeType themeType)
        {
            var applicationTheme = themeType == ThemeType.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(applicationTheme);
        }
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;

        var selectedItem = LanguageComboBox.SelectedItem as ComboBoxItem;
        if (selectedItem?.Tag != null)
        {
            var languageCode = selectedItem.Tag.ToString();
            SetLanguage(languageCode);
        }
    }

    private void SetLanguage(string languageCode)
    {
        if (isInitializing) return;

        LocalizeDictionary.Instance.Culture = new CultureInfo(languageCode);
    }
}