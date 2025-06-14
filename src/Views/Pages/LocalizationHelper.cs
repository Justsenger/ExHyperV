using WPFLocalizeExtension.Engine;

namespace ExHyperV.Views.Pages;

/// <summary>
///     Static helper for getting localized strings in C# code.
///     Replaces the misuse of LocalizationKeyConverter for non-XAML scenarios.
/// </summary>
public static class LocalizationHelper
{
    /// <summary>
    ///     Gets a localized string by key.
    /// </summary>
    /// <param name="key">The resource key</param>
    /// <param name="fallback">Fallback value if key is not found. If null, the key itself will be returned.</param>
    /// <returns>Localized string or fallback value</returns>
    public static string GetString(string key, string? fallback = null)
    {
        var result = LocalizeDictionary.Instance.GetLocalizedObject("ExHyperV", "Resources", key,
            LocalizeDictionary.Instance.Culture);
        return result?.ToString() ?? fallback ?? key;
    }
}