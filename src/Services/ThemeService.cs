using System.IO;
using System.Xml.Linq;
using Wpf.Ui.Appearance;

namespace ExHyperV.Services;

/// <summary>
///     Service for managing application theme settings and automatic system theme following
/// </summary>
public static class ThemeService
{
    private const string DefaultTheme = "Auto";

    private static readonly string ConfigFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ExHyperV",
        "config.xml");

    /// <summary>
    ///     Gets the current theme preference from configuration
    /// </summary>
    /// <returns>Theme preference: "Auto", "Light", or "Dark"</returns>
    public static string ThemePreference()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
                return DefaultTheme;

            var configDoc = XDocument.Load(ConfigFilePath);
            return configDoc.Root?.Element("Theme")?.Value ?? DefaultTheme;
        }
        catch
        {
            return DefaultTheme;
        }
    }

    /// <summary>
    ///     Saves theme preference to configuration
    /// </summary>
    /// <param name="theme">Theme preference: "Auto", "Light", or "Dark"</param>
    public static void SaveThemePreference(string theme)
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
            var themeElement = root?.Element("Theme");

            if (themeElement is null)
                root?.Add(new XElement("Theme", theme));
            else
                themeElement.Value = theme;

            configDoc.Save(ConfigFilePath);
        }
        catch
        {
            // Silently fail if unable to save configuration
        }
    }

    /// <summary>
    ///     Applies theme based on preference
    /// </summary>
    /// <param name="themePreference">Theme preference: "Auto", "Light", or "Dark"</param>
    public static void ApplyTheme(string themePreference)
    {
        var targetTheme = themePreference switch
        {
            "Light" => ApplicationTheme.Light,
            "Dark" => ApplicationTheme.Dark,
            _ => SystemThemeManager.GetCachedSystemTheme() == SystemTheme.Dark
                ? ApplicationTheme.Dark
                : ApplicationTheme.Light
        };

        ApplicationThemeManager.Apply(targetTheme);
    }

    /// <summary>
    ///     Applies theme based on current preference from configuration
    /// </summary>
    public static void ApplyCurrentTheme()
    {
        var preference = ThemePreference();
        ApplyTheme(preference);
    }

    /// <summary>
    ///     Handles system theme change when preference is set to "Auto"
    /// </summary>
    public static void OnSystemThemeChanged()
    {
        var preference = ThemePreference();
        if (preference == "Auto") ApplyTheme("Auto");
    }
}