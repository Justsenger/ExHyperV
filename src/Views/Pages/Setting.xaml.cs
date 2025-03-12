namespace ExHyperV.Views.Pages;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Appearance;


public partial class Setting
{
    public Setting()
    {
        InitializeComponent();
        if (ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark)
        { 
            ThemeComboBox.SelectedItem = ThemeComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Content.ToString() == "黑暗");
        }
        else
        {
            ThemeComboBox.SelectedItem = ThemeComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Content.ToString() == "明亮");
        }
    }


    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedItem = ThemeComboBox.SelectedItem as ComboBoxItem;
        if (selectedItem != null)
        {
            string theme = selectedItem.Content.ToString();

            if (theme == "黑暗")
            {
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
            }
            else
            {
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
            }
        }
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedItem = ThemeComboBox.SelectedItem as ComboBoxItem;
        if (selectedItem != null)
        {
            string theme = selectedItem.Content.ToString();

            if (theme == "中文")
            {
                SetLanguage("zh-CN");  // 设置为中文（简体）
            }
            else if (theme == "English")
            {
                SetLanguage("en-US");  // 设置为英文
            }
        }
    }

    private void SetLanguage(string cultureCode)
    {
        CultureInfo culture = new CultureInfo(cultureCode);
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }


}
