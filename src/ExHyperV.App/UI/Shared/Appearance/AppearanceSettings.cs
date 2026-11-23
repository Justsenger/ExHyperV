using System.Globalization;
using System.Text.Json;
using ExHyperV.App.Localization;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace ExHyperV.App.UI.Shared.Appearance;

internal sealed record AppearanceSettings
{
    public ElementTheme Theme { get; init; } = ElementTheme.Default;
    public bool AccentEnabled { get; init; } = true;
    public string AccentColor { get; init; } = "#4CC2FF";
    public bool LoggingEnabled { get; init; } = true;
    public bool VerboseDiagnostics { get; init; }
    public string Language { get; init; } = "system";
    public bool AutoOpenConsole { get; init; } = false;
    public string DefaultPowerAction { get; init; } = "Shutdown";

    internal static string NormalizePowerAction(string? value)
        => string.Equals(value, "Save", StringComparison.OrdinalIgnoreCase) ? "Save" : "Shutdown";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExHyperV", "appearance.json");

    private static string LegacyFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExHyperV", "WinUI3", "appearance.json");

    public static AppearanceSettings Load()
    {
        try
        {
            string path = File.Exists(FilePath) ? FilePath : LegacyFilePath;
            if (!File.Exists(path)) return new();
            var value = JsonSerializer.Deserialize(File.ReadAllText(path), DesktopJson.Default.AppearanceSettings);
            if (value == null || !Enum.IsDefined(value.Theme) || !TryParseColor(value.AccentColor, out _)) return new();
            return value with
            {
                Language = L10n.NormalizePreference(value.Language),
                DefaultPowerAction = NormalizePowerAction(value.DefaultPowerAction)
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or damaged preference must not prevent the application from opening.
            return new();
        }
    }

    public string? Save()
    {
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, DesktopJson.Default.AppearanceSettings));
            File.Move(temporary, FilePath, overwrite: true);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return L10n.Format("Settings_AppearanceSaveFailed", exception.Message);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover temporary file can be removed after the next successful save.
            }
        }
    }

    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (text is not { Length: 7 } || text[0] != '#' ||
            !uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
        color = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
