using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Xml.Linq;
namespace ExHyperV;

public partial class App
{
    private const string DefaultLanguage = "en-US";
    private const string ConfigFilePath = "config.xml";
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string targetLanguage;

        if (File.Exists(ConfigFilePath))
        {
            var configLanguage = ReadLanguageFromConfig();
            if (IsLanguageSupported(configLanguage))
            {
                targetLanguage = configLanguage;
            }
            else
            {
                targetLanguage = GetValidSystemLanguage();
                WriteLanguageToConfig(targetLanguage);
            }
        }
        else
        {
            targetLanguage = GetValidSystemLanguage();
            WriteLanguageToConfig(targetLanguage);
        }

        SetLanguage(targetLanguage);
    }

    private string GetValidSystemLanguage()
    {
        var systemLang = GetSystemLanguageViaAPI();
        return IsLanguageSupported(systemLang) ? systemLang : DefaultLanguage;
    }


    private bool IsLanguageSupported(string languageCode)
    {
        return languageCode == "en-US" || languageCode == "zh-CN";
    }

    private string GetSystemLanguageViaAPI()
    {
        var localeName = new StringBuilder(85);
        var result = GetUserDefaultLocaleName(localeName, localeName.Capacity);
        return result > 0 ? localeName.ToString().Substring(0, result - 1) : DefaultLanguage;
    }

    private void SetLanguage(string cultureCode)
    {
        var culture = new CultureInfo(cultureCode);
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    private string ReadLanguageFromConfig()
    {
        try
        {
            var configDoc = XDocument.Load(ConfigFilePath);
            return configDoc.Root?.Element("Language")?.Value ?? DefaultLanguage;
        }
        catch
        {
            return DefaultLanguage;
        }
    }

    private void WriteLanguageToConfig(string cultureCode)
    {
        var configDoc = File.Exists(ConfigFilePath)
            ? XDocument.Load(ConfigFilePath)
            : new XDocument(new XElement("Config"));

        var root = configDoc.Root;
        var langElement = root?.Element("Language");

        if (langElement == null)
            root?.Add(new XElement("Language", cultureCode));
        else
            langElement.Value = cultureCode;

        configDoc.Save(ConfigFilePath);
    }

    // Windows API 声明
    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern int GetUserDefaultLocaleName(
        [Out] StringBuilder lpLocaleName,
        int cchLocaleName
    );
}