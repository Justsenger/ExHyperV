using System.Globalization;
using System.Resources;

namespace ExHyperV.App.Localization;

internal static class L10n
{
    private static readonly CultureInfo InitialUiCulture = CultureInfo.CurrentUICulture;
    private static readonly CultureInfo ZhCnCulture = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly CultureInfo EnUsCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly ResourceManager Manager = new("ExHyperV.App.Localization.Strings", typeof(L10n).Assembly);

    internal static string CurrentPreference { get; private set; } = "system";
    internal static event EventHandler? LanguageChanged;
    private static CultureInfo uiCulture = InitialUiCulture;

    internal static void ApplyLanguage(string? preference)
    {
        var normalized = NormalizePreference(preference);
        bool changed = CurrentPreference != normalized;
        CurrentPreference = normalized;
        uiCulture = normalized switch
        {
            "zh-CN" => ZhCnCulture,
            "en" => EnUsCulture,
            _ => ResolveSystemCulture()
        };
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
        CultureInfo.CurrentUICulture = uiCulture;
        if (changed) LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    internal static string NormalizePreference(string? preference) => preference switch
    {
        "zh-CN" or "zh-Hans" or "zh" => "zh-CN",
        "en" or "en-US" => "en",
        _ => "system"
    };

    private static CultureInfo ResolveSystemCulture()
    {
        var os = InitialUiCulture;
        if (os.Name.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)
            || os.Name.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)
            || os.Name.Equals("zh-SG", StringComparison.OrdinalIgnoreCase)
            || os.Name.Equals("zh", StringComparison.OrdinalIgnoreCase))
            return ZhCnCulture;
        if (os.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase))
            return ZhCnCulture;
        return os;
    }

    internal static string Get(string key)
        => Manager.GetString(key, uiCulture) ?? key;

    internal static string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
