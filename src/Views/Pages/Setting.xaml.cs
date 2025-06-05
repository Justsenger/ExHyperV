namespace ExHyperV.Views.Pages;

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using Wpf.Ui.Appearance;


public partial class Setting
{
    private const string ConfigFilePath = "config.xml";
    private bool isInitializing = true; // 标志变量，用于避免死循环
    public string sp = "none"; //炫彩开关
    public Setting()
    {
        InitializeComponent();
        if (ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark)
        { 
            ThemeComboBox.SelectedItem = ThemeComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Content.ToString() == Properties.Resources.dark);
        }
        else
        {
            ThemeComboBox.SelectedItem = ThemeComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Content.ToString() == Properties.Resources.light);
        }

        XDocument configDoc = XDocument.Load(ConfigFilePath);
        string lang = configDoc.Root?.Element("Language")?.Value ?? "en-US"; //获取语言

        switch (lang)
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
    }


    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedItem = ThemeComboBox.SelectedItem as ComboBoxItem;
        if (selectedItem != null)
        {
            string theme = selectedItem.Content.ToString();

            if (theme == Properties.Resources.dark)
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
        var selectedItem = LanguageComboBox.SelectedItem as ComboBoxItem;
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


    private void OnSPskinSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedItem = SPskin.SelectedItem as ComboBoxItem; //获取选中的SP主题
        if (selectedItem != null)
        {
            string theme = selectedItem.Content.ToString();

            if (theme == "炫彩")
            {
                sp = "炫彩";
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
            }
            else
            {
                sp = "none";
                ApplicationThemeManager.Apply(ApplicationTheme.Light);
            }
        }
    }


    

    private void SetLanguage(string languageCode)
    {
        if (isInitializing)
        {
            isInitializing = false;
            return;
        }

        string configFilePath = "config.xml"; // 或者使用绝对路径
        if (File.Exists(configFilePath))
        {
            XDocument configDoc = XDocument.Load(configFilePath);
            var languageElement = configDoc.Root.Element("Language");

            if (languageElement != null)
            {
                languageElement.Value = languageCode;  // 设置新的语言代码
            }
            else
            {
                configDoc.Root.Add(new XElement("Language", languageCode)); // 如果没有则新增
            }
            configDoc.Save(configFilePath);
        }
        else
        {
            XDocument newConfig = new XDocument(
                new XElement("Config", new XElement("Language", languageCode))
            );
            newConfig.Save(configFilePath);
        }

        string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;

        // 启动新的应用程序实例
        System.Diagnostics.Process.Start(exePath);

        // 退出当前应用程序
        Application.Current.Shutdown();

    }

    private void Setcombo(string lang) {
        LanguageComboBox.SelectedItem = LanguageComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => item.Content.ToString() == lang);
    }

}
