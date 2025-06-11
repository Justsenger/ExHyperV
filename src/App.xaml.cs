using System.Globalization;
using System.Windows;
using WPFLocalizeExtension.Engine;
using WPFLocalizeExtension.Providers;

namespace ExHyperV;

public partial class App
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPFLocalizeExtension setup
        LocalizeDictionary.Instance.SetCurrentThreadCulture = true;
        LocalizeDictionary.Instance.DefaultProvider = ResxLocalizationProvider.Instance;

        // Language setup
        var systemCulture = CultureInfo.CurrentUICulture;
        var supportedCulture = IsLanguageSupported(systemCulture.Name) ? systemCulture.Name : "en-US";

        LocalizeDictionary.Instance.Culture = new CultureInfo(supportedCulture);
    }

    private bool IsLanguageSupported(string languageCode)
    {
        return languageCode == "en-US" || languageCode == "zh-CN";
    }
}