using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Services;

namespace ExHyperV.ViewModels;

public sealed record ConsolePreferenceOption(string Value, string Label);

/// <summary>Application preferences only; constructing this model never queries or changes Hyper-V.</summary>
public partial class ConsolePreferencesViewModel : ObservableObject
{
    private bool _loading;

    public IReadOnlyList<ConsolePreferenceOption> PowerActions { get; } = new[]
    {
        new ConsolePreferenceOption("Stop", Properties.Resources.Button_ShutDown),
        new ConsolePreferenceOption("Save", Properties.Resources.Preferences_Save)
    };

    [ObservableProperty] private string _powerAction = "Stop";
    [ObservableProperty] private bool _openConsoleOnStart;
    [ObservableProperty] private string _saveError = string.Empty;

    public ConsolePreferencesViewModel() => Reload();

    public void Reload()
    {
        _loading = true;
        try
        {
            PowerAction = SettingsService.GetConsolePowerAction();
            OpenConsoleOnStart = SettingsService.GetOpenConsoleOnStart();
        }
        finally { _loading = false; }
    }

    partial void OnPowerActionChanged(string value)
    {
        if (!_loading) Save(SettingsService.SaveConsolePowerAction(value));
    }

    partial void OnOpenConsoleOnStartChanged(bool value)
    {
        if (!_loading) Save(SettingsService.SaveOpenConsoleOnStart(value));
    }

    private void Save(bool success)
    {
        SaveError = success ? string.Empty : Properties.Resources.Preferences_SaveFailed;
        if (!success) Reload();
    }
}
