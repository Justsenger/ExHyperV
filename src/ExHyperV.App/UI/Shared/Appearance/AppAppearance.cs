
using ExHyperV.App.Diagnostics;
using ExHyperV.App.Localization;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;
using Windows.UI;

namespace ExHyperV.App.UI.Shared.Appearance;

/// <summary>Client-only appearance preferences and live brushes shared by existing and newly opened pages.</summary>
internal sealed class AppAppearance
{
    private readonly Dictionary<string, SolidColorBrush> brushes = new();
    private readonly HashSet<string> aliases = [];
    private readonly UISettings systemColors = new();
    private bool dark;
    private bool highContrast;

    public AppearanceSettings Settings { get; private set; }
    public event EventHandler? Changed;

    public AppAppearance(AppearanceSettings settings)
    {
        Settings = settings;
        UpdatePalette(ElementTheme.Dark);
    }

    public string? Set(AppearanceSettings settings)
    {
        var previous = Settings;
        bool registrationChanged = previous.AutoStart != settings.AutoStart;
        try
        {
            if (registrationChanged) StartupRegistration.SetEnabled(settings.AutoStart != StartupMode.Off);
            if (settings.Save() is { } error)
            {
                if (registrationChanged) StartupRegistration.SetEnabled(previous.AutoStart != StartupMode.Off);
                return error;
            }
        }
        catch (Exception exception)
        {
            AppDiagnostics.Failure("Settings", "StartupRegistrationFailed", exception);
            return L10n.Format("Settings_StartupFailed", exception.Message);
        }
        Settings = settings;
        if (settings.LoggingEnabled != previous.LoggingEnabled || settings.VerboseDiagnostics != previous.VerboseDiagnostics)
            AppDiagnostics.Configure(settings);
        if (settings.Language != previous.Language) L10n.ApplyLanguage(settings.Language);
        if (settings.Theme != previous.Theme || settings.AccentColor != previous.AccentColor || settings.AccentEnabled != previous.AccentEnabled)
            UpdatePalette(dark ? ElementTheme.Dark : ElementTheme.Light);
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    public void UpdatePalette(ElementTheme theme, bool? useHighContrast = null)
    {
        dark = theme == ElementTheme.Dark;
        if (useHighContrast.HasValue) highContrast = useHighContrast.Value;
        AppearanceSettings.TryParseColor(Settings.AccentColor, out var selected);
        Color foreground = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        Color background = Rgb(dark ? 32 : 255);
        Color accent = Settings.AccentEnabled ? selected : Rgb(dark ? 190 : 90);
        if (highContrast)
        {
            foreground = systemColors.GetColorValue(UIColorType.Foreground);
            background = systemColors.GetColorValue(UIColorType.Background);
            accent = foreground;
        }
        Color onAccent = highContrast ? background : ThemeHelper.ContrastingForeground(accent);
        Color header = Settings.AccentEnabled || highContrast ? accent : Rgb(dark ? 57 : 233);
        Color onHeader = Settings.AccentEnabled || highContrast ? onAccent : foreground;
        Color textAccent = accent;
        // Accent used as text/lines needs contrast with the page, unlike a large filled header.
        for (int i = 0; i < 12 && ThemeHelper.Contrast(textAccent, background) < 4.5; i++)
            textAccent = ThemeHelper.Mix(textAccent, foreground, .18);

        SetBrush("AccentFillColorDefaultBrush", accent);
        SetBrush("AccentFillColorSecondaryBrush", ThemeHelper.StateColor(accent, background, .10, onAccent));
        SetBrush("AccentFillColorTertiaryBrush", ThemeHelper.StateColor(accent, background, .20, onAccent));
        SetBrush("TextOnAccentFillColorPrimaryBrush", onAccent);
        SetBrush("TextOnAccentFillColorSecondaryBrush", onAccent);
        SetBrush("TextOnAccentFillColorSelectedTextBrush", onAccent);
        SetBrush("AccentTextFillColorPrimaryBrush", textAccent);
        SetBrush("AccentTextFillColorSecondaryBrush", textAccent);
        SetBrush("AccentTextFillColorTertiaryBrush", textAccent);
        SetBrush("SystemControlHighlightAccentBrush", textAccent);
        SetBrush("SystemControlForegroundAccentBrush", textAccent);
        SetBrush("NavigationViewSelectionIndicatorForeground", textAccent);
        SetBrush("VmAccentBrush", textAccent);
        SetBrush("VmChartFillBrush", textAccent, .25);
        SetBrush("VmGpuChartFillBrush", textAccent, .4);
        SetBrush("VmHeaderCheckedBrush", header);
        SetBrush("VmHeaderCheckedHoverBrush", ThemeHelper.StateColor(header, foreground, .07, onHeader));
        SetBrush("VmHeaderCheckedPressedBrush", ThemeHelper.StateColor(header, background, .15, onHeader));
        SetBrush("VmHeaderForegroundBrush", onHeader);

        // These WinUI control aliases are StaticResources in the built-in dictionary.
        // Own their brush references too, so changing a color updates already-created controls.
        Alias("AccentButtonBackground", "AccentFillColorDefaultBrush");
        Alias("ProgressBarForeground", "AccentFillColorDefaultBrush");
        Alias("AccentButtonBackgroundPointerOver", "AccentFillColorSecondaryBrush");
        Alias("AccentButtonBackgroundPressed", "AccentFillColorTertiaryBrush");
        Alias("AccentButtonForeground", "TextOnAccentFillColorPrimaryBrush");
        Alias("AccentButtonForegroundPointerOver", "TextOnAccentFillColorPrimaryBrush");
        Alias("AccentButtonForegroundPressed", "TextOnAccentFillColorPrimaryBrush");
        foreach (string key in new[] { "ListViewItemSelectionIndicatorBrush", "ListViewItemSelectionIndicatorPointerOverBrush", "ListViewItemSelectionIndicatorPressedBrush" })
            Alias(key, "AccentFillColorDefaultBrush");
        foreach (string key in new[]
        {
            "ToggleSwitchFillOn", "ToggleSwitchStrokeOn", "SliderTrackValueFill", "SliderThumbBackground",
            "RadioButtonOuterEllipseCheckedStroke", "RadioButtonOuterEllipseCheckedFill",
            "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeIndeterminate",
            "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillIndeterminate", "ToggleButtonBackgroundChecked"
        })
        {
            Alias(key, "AccentFillColorDefaultBrush");
            Alias(key + "PointerOver", "AccentFillColorSecondaryBrush");
            Alias(key + "Pressed", "AccentFillColorTertiaryBrush");
        }
        foreach (string key in new[]
        {
            "ToggleSwitchKnobFillOn", "RadioButtonCheckGlyphFill", "CheckBoxCheckGlyphForegroundChecked",
            "CheckBoxCheckGlyphForegroundIndeterminate", "ToggleButtonForegroundChecked"
        })
        {
            Alias(key, "TextOnAccentFillColorPrimaryBrush");
            Alias(key + "PointerOver", "TextOnAccentFillColorPrimaryBrush");
            Alias(key + "Pressed", "TextOnAccentFillColorPrimaryBrush");
        }
        foreach (string suffix in new[] { "", "PointerOver", "Pressed" })
            Alias("HyperlinkButtonForeground" + suffix, "AccentTextFillColorPrimaryBrush");
    }

    private void SetBrush(string key, Color color, double opacity = 1)
    {
        if (brushes.TryGetValue(key, out var brush)) brush.Color = color;
        else Application.Current.Resources[key] = brushes[key] = new SolidColorBrush(color) { Opacity = opacity };
    }

    private void Alias(string key, string source)
    {
        if (aliases.Add(key)) Application.Current.Resources[key] = brushes[source];
    }
    private static Color Rgb(byte value) => Color.FromArgb(255, value, value, value);
    private static Color Rgb(int value) => Rgb((byte)value);
}
