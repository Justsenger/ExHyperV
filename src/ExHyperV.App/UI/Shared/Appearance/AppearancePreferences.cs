using System.Text.Json.Serialization;

namespace ExHyperV.App.UI.Shared.Appearance;

internal enum StartupMode { Off, Normal, Silent }
internal enum WindowCloseBehavior { Exit, Tray }
internal enum ConsoleSessionMode { RdpBasic, RdpEnhanced, External }

internal sealed partial record AppearanceSettings
{
    [JsonPropertyName("Theme")]
    public int ThemeValue { get; init; }
    public bool AccentEnabled { get; init; } = true;
    public string AccentColor { get; init; } = "#4CC2FF";
    public bool LoggingEnabled { get; init; } = true;
    public bool VerboseDiagnostics { get; init; }
    public string Language { get; init; } = "system";
    public bool AutoOpenConsole { get; init; } = false;
    public StartupMode AutoStart { get; init; }
    public WindowCloseBehavior CloseBehavior { get; init; }
    public ConsoleSessionMode DefaultConsoleSession { get; init; }
    public bool ShowExtensionEntries { get; init; } = true;
    public bool AutoCheckUpdate { get; init; } = true;
    public string DefaultPowerAction { get; init; } = "Shutdown";
}
