using ExHyperV.App.Localization;
using Microsoft.Win32;

namespace ExHyperV.App.UI.Shared.Appearance;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string LoginArgument = "--login";

    // Explorer skips Run entries that require elevation. Start a normal user process and let
    // ShellExecute request consent for the application. The executable path is data, never script.
    internal static void SetEnabled(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled)
        {
            run.DeleteValue("ExHyperV", throwOnMissingValue: false);
            return;
        }

        string? path = Environment.GetEnvironmentVariable("EXHYPERV_PORTABLE_LAUNCHER") ?? Environment.ProcessPath;
        if (!Path.IsPathFullyQualified(path ?? "") || !File.Exists(path))
            throw new IOException(L10n.Get("Settings_StartupPathUnavailable"));

        using var app = Registry.CurrentUser.CreateSubKey(@"Software\ExHyperV");
        app.SetValue("StartupPath", path, RegistryValueKind.String);
        string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        string command = $"\"{powershell}\" -NoProfile -NonInteractive -WindowStyle Hidden -Command " +
            $"\"Start-Process -Verb RunAs -FilePath (Get-ItemPropertyValue HKCU:\\Software\\ExHyperV StartupPath) -ArgumentList '{LoginArgument}'\"";
        run.SetValue("ExHyperV", command, RegistryValueKind.String);
    }
}
