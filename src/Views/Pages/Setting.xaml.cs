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
            ThemeComboBox.SelectedItem = ThemeComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Content.ToString() == "�ڰ�");
        }
        else
        {
            ThemeComboBox.SelectedItem = ThemeComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Content.ToString() == "����");
        }
    }


    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedItem = ThemeComboBox.SelectedItem as ComboBoxItem;
        if (selectedItem != null)
        {
            string theme = selectedItem.Content.ToString();

            if (theme == "�ڰ�")
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

            if (theme == "����")
            {
                SetLanguage("zh-CN");  // ����Ϊ���ģ����壩
            }
            else if (theme == "English")
            {
                SetLanguage("en-US");  // ����ΪӢ��
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
