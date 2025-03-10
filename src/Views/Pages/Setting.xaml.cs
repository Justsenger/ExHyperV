namespace ExHyperV.Views.Pages;
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

}
