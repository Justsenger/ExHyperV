using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Wpf.Ui.Appearance;
using WPFLocalizeExtension.Engine;
using WPFLocalizeExtension.Providers;

namespace ExHyperV;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Apply the application theme based on the system theme.
        // This is done here at the earliest point to prevent UI flickering.
        var systemTheme = SystemThemeManager.GetCachedSystemTheme();

        // Correctly convert from SystemTheme to ApplicationTheme before applying.
        var applicationTheme = systemTheme == SystemTheme.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(applicationTheme);

        // Initialize the localization provider.
        LocalizeDictionary.Instance.DefaultProvider = ResxLocalizationProvider.Instance;
        LocalizeDictionary.Instance.SetCurrentThreadCulture = false;

        // Set the initial culture based on the system's UI culture.
        var systemCulture = CultureInfo.CurrentUICulture;
        if (systemCulture.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase))
            LocalizeDictionary.Instance.Culture = new CultureInfo("zh-CN");
        else
            // Default to English for all other system languages.
            LocalizeDictionary.Instance.Culture = new CultureInfo("en-US");

        // Attach handlers for debugging localization issues.
        ResxLocalizationProvider.Instance.ProviderError += OnLocalizationProviderError;
        LocalizeDictionary.Instance.MissingKeyEvent += OnMissingKeyEvent;
    }

    private static void OnMissingKeyEvent(object sender, MissingKeyEventArgs e)
    {
        var errorMessage = $"Localization Missing Key: '{e.Key}'.";
        Debug.WriteLine(errorMessage);
    }

    private static void OnLocalizationProviderError(object sender, ProviderErrorEventArgs e)
    {
        var errorMessage =
            $"Localization Error: Key '{e.Key}' not found in assembly '{e.Object?.ToString() ?? "null"}'.";

        Debug.WriteLine(errorMessage);
        LogLocalizationError(errorMessage);
    }

    private static void LogLocalizationError(string errorMessage)
    {
        try
        {
            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExHyperV", "localization_errors.log");

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

            var logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {errorMessage}{Environment.NewLine}";
            File.AppendAllText(logPath, logEntry);
        }
        catch
        {
            // Ignore logging errors to prevent application crashes.
        }
    }
}