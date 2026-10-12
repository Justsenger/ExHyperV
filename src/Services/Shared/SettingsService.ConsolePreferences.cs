using System.IO;
using System.Xml.Linq;

namespace ExHyperV.Services;

public static partial class SettingsService
{
    // Stable tokens, independent of UI language. Unknown values retain the old behavior.
    public static string GetConsolePowerAction() =>
        ReadConsolePreference("ConsolePowerAction") == "Save" ? "Save" : "Stop";

    public static bool GetOpenConsoleOnStart() =>
        bool.TryParse(ReadConsolePreference("OpenConsoleOnStart"), out bool enabled) && enabled;

    public static event Action? ConsolePowerActionChanged;

    public static bool SaveConsolePowerAction(string action)
    {
        if (action is not ("Stop" or "Save")) return false;
        if (!SaveConsolePreference("ConsolePowerAction", action)) return false;
        ConsolePowerActionChanged?.Invoke();
        return true;
    }

    public static bool SaveOpenConsoleOnStart(bool enabled) =>
        SaveConsolePreference("OpenConsoleOnStart", enabled ? "true" : "false");

    private static string? ReadConsolePreference(string key)
    {
        try { return File.Exists(ConfigFilePath) ? XDocument.Load(ConfigFilePath).Root?.Element(key)?.Value : null; }
        catch { return null; }
    }

    private static bool SaveConsolePreference(string key, string value)
    {
        try
        {
            var document = File.Exists(ConfigFilePath)
                ? XDocument.Load(ConfigFilePath)
                : new XDocument(new XElement("Config"));
            if (document.Root == null) return false;
            document.Root.SetElementValue(key, value);
            document.Save(ConfigFilePath);
            return true;
        }
        catch { return false; }
    }
}
