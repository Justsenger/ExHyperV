using System.Windows;
using ExHyperV.Services;

namespace ExHyperV;

public partial class App
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Initialize localization
        LocalizationService.Initialize();

        // Apply theme settings
        ThemeService.ApplyCurrentTheme();
    }
}