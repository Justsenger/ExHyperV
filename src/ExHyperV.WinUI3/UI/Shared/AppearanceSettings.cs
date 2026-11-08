using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace ExHyperV.WinUI3.UI.Shared;

internal sealed record AppearanceSettings
{
    public ElementTheme Theme { get; init; } = ElementTheme.Default;
    public bool AccentEnabled { get; init; } = true;
    public string AccentColor { get; init; } = "#4CC2FF";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ExHyperV", "WinUI3", "appearance.json");

    public static AppearanceSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var value = JsonSerializer.Deserialize<AppearanceSettings>(File.ReadAllText(FilePath));
            if (value == null || !Enum.IsDefined(value.Theme) || !TryParseColor(value.AccentColor, out _)) return new();
            return value;
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
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, FilePath, overwrite: true);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "外观已在本次运行中生效，但未能保存：" + exception.Message;
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
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
