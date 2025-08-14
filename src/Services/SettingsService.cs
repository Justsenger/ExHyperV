using System.IO;
using System.Xml.Linq;
using System.Windows;
using Wpf.Ui.Appearance;
using System.Diagnostics;
using ExHyperV.Properties; 

namespace ExHyperV.Services
{
    public static class SettingsService
    {
        private const string ConfigFilePath = "config.xml";

        // 从XML加载语言设置
        public static string GetLanguage()
        {
            if (!File.Exists(ConfigFilePath)) return "en-US"; // 默认英文

            try
            {
                XDocument configDoc = XDocument.Load(ConfigFilePath);
                return configDoc.Root?.Element("Language")?.Value ?? "en-US";
            }
            catch
            {
                return "en-US"; // 文件损坏则返回默认值
            }
        }

        // 保存语言设置并重启应用
        public static void SetLanguageAndRestart(string languageName)
        {
            string languageCode = languageName == "中文" ? "zh-CN" : "en-US";

            XDocument configDoc;
            if (File.Exists(ConfigFilePath))
            {
                configDoc = XDocument.Load(ConfigFilePath);
                var languageElement = configDoc.Root?.Element("Language");
                if (languageElement != null)
                {
                    languageElement.Value = languageCode;
                }
                else
                {
                    configDoc.Root?.Add(new XElement("Language", languageCode));
                }
            }
            else
            {
                configDoc = new XDocument(new XElement("Config", new XElement("Language", languageCode)));
            }
            configDoc.Save(ConfigFilePath);

            // 重启应用
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath != null)
            {
                Process.Start(exePath);
            }
            Application.Current.Shutdown();
        }

        // 获取当前主题
        public static string GetTheme()
        {
            return ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? Resources.dark : Resources.light;
        }

        // 应用新主题
        public static void ApplyTheme(string themeName)
        {
            var theme = themeName == Resources.dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(theme);
        }
    }
}