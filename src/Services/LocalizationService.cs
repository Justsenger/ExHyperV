using System.Globalization;
using System.IO;
using System.Xml.Linq;
using ExHyperV.Properties;

namespace ExHyperV.Services;

/// <summary>
///     Simple localization service with dynamic language switching
/// </summary>
public static class LocalizationService
{
    private const string DefaultLanguage = "en-US";

    private static readonly string ConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ExHyperV",
        "config.xml");

    /// <summary>
    ///     Supported languages in the application
    /// </summary>
    private static readonly Dictionary<string, string> SupportedLanguagesInternal = new()
    {
        { "en-US", "English" },
        { "zh-CN", "中文" }
    };

    /// <summary>
    ///     Event fired when language changes
    /// </summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    ///     Initializes localization on application startup
    /// </summary>
    public static void Initialize()
    {
        var targetLanguage = DetermineTargetLanguage();
        ApplyLanguage(targetLanguage);
    }

    /// <summary>
    ///     Changes the application language dynamically without restart
    /// </summary>
    /// <param name="languageCode">Language code (e.g., "en-US", "zh-CN")</param>
    public static void ChangeLanguage(string languageCode)
    {
        if (!IsLanguageSupported(languageCode))
            return;

        SaveLanguageToConfig(languageCode);
        ApplyLanguage(languageCode);

        // Notify UI about language change
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    ///     Reads language setting from configuration file
    /// </summary>
    /// <returns>Language code from config or default language</returns>
    public static string ReadLanguageFromConfig()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return DefaultLanguage;

            var configDoc = XDocument.Load(ConfigFilePath);
            var language = configDoc.Root?.Element("Language")?.Value;
            return IsLanguageSupported(language) ? language! : DefaultLanguage;
        }
        catch
        {
            return DefaultLanguage;
        }
    }

    /// <summary>
    ///     Checks if a language is supported
    /// </summary>
    /// <param name="languageCode">Language code to check</param>
    /// <returns>True if supported</returns>
    private static bool IsLanguageSupported(string? languageCode)
    {
        return !string.IsNullOrEmpty(languageCode) && SupportedLanguagesInternal.ContainsKey(languageCode);
    }

    /// <summary>
    ///     Gets the system language if supported, otherwise returns default
    /// </summary>
    /// <returns>Valid system language or default language</returns>
    private static string ValidSystemLanguage()
    {
        var systemLanguage = SystemLanguage();
        return IsLanguageSupported(systemLanguage) ? systemLanguage : DefaultLanguage;
    }

    /// <summary>
    ///     Saves language setting to configuration file
    /// </summary>
    /// <param name="languageCode">Language code to save</param>
    private static void SaveLanguageToConfig(string languageCode)
    {
        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(ConfigFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory)) Directory.CreateDirectory(directory);

            var configDoc = File.Exists(ConfigFilePath)
                ? XDocument.Load(ConfigFilePath)
                : new XDocument(new XElement("Config"));

            var root = configDoc.Root;
            if (root is null)
            {
                root = new XElement("Config");
                configDoc.Add(root);
            }

            var langElement = root.Element("Language");
            if (langElement is null)
                root.Add(new XElement("Language", languageCode));
            else
                langElement.Value = languageCode;

            configDoc.Save(ConfigFilePath);
        }
        catch
        {
            // Silently fail - not critical
        }
    }

    private static string DetermineTargetLanguage()
    {
        // Try to read from config first
        var configLanguage = ReadLanguageFromConfig();
        if (IsLanguageSupported(configLanguage))
            return configLanguage;

        // Use system language if config doesn't exist or is invalid
        var systemLanguage = ValidSystemLanguage();
        SaveLanguageToConfig(systemLanguage);
        return systemLanguage;
    }

    private static void ApplyLanguage(string languageCode)
    {
        var culture = new CultureInfo(languageCode);

        // Set culture for current thread
        Thread.CurrentThread.CurrentCulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;

        // Set default culture for new threads
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        // Update resource culture
        Resources.Culture = culture;
    }

    private static string SystemLanguage()
    {
        try
        {
            return CultureInfo.CurrentUICulture.Name;
        }
        catch
        {
            return DefaultLanguage;
        }
    }
}