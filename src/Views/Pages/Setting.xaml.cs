using System.Globalization;
using System.Windows;
using System.Windows.Controls;
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
        if (ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark)
            ThemeComboBox.SelectedIndex = 1; // Dark
        else
            ThemeComboBox.SelectedIndex = 0; // Light

        // Language initialization based on current WPFLocalizeExtension culture
        var currentCulture = LocalizeDictionary.Instance.Culture?.Name ?? "en-US";
        switch (currentCulture)
        {
            case "en-US":
                Setcombo("English");
                break;
            case "zh-CN":
                Setcombo("中文");
                break;
            default:
                Setcombo("English");
                break;
        }

        isInitializing = false;
    }


    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;

        if (ThemeComboBox.SelectedIndex == 1) // Dark
            ApplicationThemeManager.Apply(ApplicationTheme.Dark);
        else // Light
            ApplicationThemeManager.Apply(ApplicationTheme.Light);
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;

        var selectedItem = LanguageComboBox.SelectedItem as ComboBoxItem;
        if (selectedItem != null)
        {
            var selectedLanguage = selectedItem.Content.ToString();

            if (selectedLanguage == "中文")
                SetLanguage("zh-CN"); // 设置为中文（简体）
            else if (selectedLanguage == "English") SetLanguage("en-US"); // 设置为英文
        }
    }

    private void SetLanguage(string languageCode)
    {
        try
        {
            // Dynamic language switching without application restart
            LocalizeDictionary.Instance.Culture = new CultureInfo(languageCode);

            // Force UI update
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                // Update all windows
                foreach (Window window in Application.Current.Windows)
                {
                    window.UpdateLayout();
                    window.InvalidateVisual();

                    // Force update all elements
                    if (window.Content is FrameworkElement content)
                    {
                        content.UpdateLayout();
                        content.InvalidateVisual();
                    }
                }
            }));
        }
        catch
        {
            // Ignore language switching errors
        }
    }

    private void Setcombo(string lang)
    {
        LanguageComboBox.SelectedItem = LanguageComboBox.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => item.Content.ToString() == lang);
    }
}