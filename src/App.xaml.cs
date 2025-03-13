using System.Configuration;
using System.Data;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Xml.Linq;
using ExHyperV.Views.Pages;
using System.Runtime.InteropServices;

namespace ExHyperV
{
    public partial class App : Application
    {
        private const string DefaultLanguage = "en-US";
        private const string ConfigFilePath = "config.xml";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string targetLanguage;
            bool configExists = File.Exists(ConfigFilePath);

            if (configExists)
            {
                // 尝试读取配置语言
                string configLanguage = ReadLanguageFromConfig();

                // 验证配置语言有效性
                if (IsLanguageSupported(configLanguage))
                {
                    targetLanguage = configLanguage;
                }
                else
                {
                    // 配置无效时获取系统语言并更新配置
                    targetLanguage = GetValidSystemLanguage();
                    WriteLanguageToConfig(targetLanguage);
                }
            }
            else
            {
                // 首次启动获取系统语言并创建配置
                targetLanguage = GetValidSystemLanguage();
                WriteLanguageToConfig(targetLanguage);
            }

            // 应用最终确定的语言
            SetLanguage(targetLanguage);
        }

        private string GetValidSystemLanguage()
        {
            string systemLang = GetSystemLanguageViaAPI();
            return IsLanguageSupported(systemLang) ? systemLang : DefaultLanguage;
        }

        private bool IsLanguageSupported(string languageCode)
        {
            return languageCode == "en-US" || languageCode == "zh-CN";
        }

        private string GetSystemLanguageViaAPI()
        {
            System.Text.StringBuilder localeName = new System.Text.StringBuilder(85);
            int result = GetUserDefaultLocaleName(localeName, localeName.Capacity);
            return result > 0 ?
                localeName.ToString().Substring(0, result - 1) :
                DefaultLanguage;
        }

        private void SetLanguage(string cultureCode)
        {
            CultureInfo culture = new CultureInfo(cultureCode);
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;
        }

        private string ReadLanguageFromConfig()
        {
            try
            {
                XDocument configDoc = XDocument.Load(ConfigFilePath);
                return configDoc.Root?.Element("Language")?.Value ?? DefaultLanguage;
            }
            catch
            {
                return DefaultLanguage;
            }
        }

        private void WriteLanguageToConfig(string cultureCode)
        {
            XDocument configDoc = File.Exists(ConfigFilePath) ?
                XDocument.Load(ConfigFilePath) :
                new XDocument(new XElement("Config"));

            var root = configDoc.Root;
            var langElement = root?.Element("Language");

            if (langElement == null)
            {
                root?.Add(new XElement("Language", cultureCode));
            }
            else
            {
                langElement.Value = cultureCode;
            }

            configDoc.Save(ConfigFilePath);
        }

        // Windows API 声明
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern int GetUserDefaultLocaleName(
            [Out] System.Text.StringBuilder lpLocaleName,
            int cchLocaleName
        );
    }
}
