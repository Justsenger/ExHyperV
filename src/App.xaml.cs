using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using WPFLocalizeExtension.Engine;
using WPFLocalizeExtension.Providers;

namespace ExHyperV;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        LocalizeDictionary.Instance.DefaultProvider = ResxLocalizationProvider.Instance;

        LocalizeDictionary.Instance.SetCurrentThreadCulture = false;

        LocalizeDictionary.Instance.Culture = CultureInfo.CurrentUICulture;

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
            // Ignore logging errors to prevent application crashes
        }
    }
}