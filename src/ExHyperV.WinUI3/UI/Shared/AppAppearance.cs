using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace ExHyperV.WinUI3.UI.Shared;

/// <summary>Client-only appearance preferences and live brushes shared by existing and newly opened pages.</summary>
internal sealed class AppAppearance
{
    private readonly Dictionary<string, SolidColorBrush> brushes = new();
    private readonly HashSet<string> aliases = [];
    private readonly UISettings systemColors = new();
    private bool dark;
    private bool highContrast;

    public AppearanceSettings Settings { get; private set; } = AppearanceSettings.Load();
    public event EventHandler? Changed;

    public AppAppearance() => UpdatePalette(ElementTheme.Dark);

    public string? Set(AppearanceSettings settings)
    {
        Settings = settings;
        UpdatePalette(dark ? ElementTheme.Dark : ElementTheme.Light);
        Changed?.Invoke(this, EventArgs.Empty);
        return Settings.Save();
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
        Color onAccent = highContrast ? background : ReadableForeground(accent);
        Color header = Settings.AccentEnabled || highContrast ? accent : Rgb(dark ? 57 : 233);
        Color onHeader = Settings.AccentEnabled || highContrast ? onAccent : foreground;
        Color textAccent = accent;
        // Accent used as text/lines needs contrast with the page, unlike a large filled header.
        for (int i = 0; i < 12 && Contrast(textAccent, background) < 4.5; i++)
            textAccent = Mix(textAccent, foreground, .18);

        SetBrush("AccentFillColorDefaultBrush", accent);
        SetBrush("AccentFillColorSecondaryBrush", StateColor(accent, background, .10, onAccent));
        SetBrush("AccentFillColorTertiaryBrush", StateColor(accent, background, .20, onAccent));
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
        SetBrush("VmHeaderCheckedHoverBrush", StateColor(header, foreground, .07, onHeader));
        SetBrush("VmHeaderCheckedPressedBrush", StateColor(header, background, .15, onHeader));
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
    private static Color Mix(Color a, Color b, double amount) => Color.FromArgb(255,
        (byte)Math.Round(a.R + (b.R - a.R) * amount), (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));
    private static double Luminance(Color color)
    {
        static double Channel(byte value) => value <= 10 ? value / 3294.6 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }
    private static double Contrast(Color a, Color b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
    private static Color ReadableForeground(Color background) =>
        Contrast(background, Microsoft.UI.Colors.Black) >= Contrast(background, Microsoft.UI.Colors.White)
            ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
    private static Color StateColor(Color original, Color target, double amount, Color foreground)
    {
        var candidate = Mix(original, target, amount);
        return Contrast(candidate, foreground) >= 4.5 ? candidate : original;
    }
}
