
using ExHyperV.App.Diagnostics;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shell;
using Microsoft.UI.Xaml;

namespace ExHyperV.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void SetResourceDirectory()
    {
        // MRT must resolve resources.pri and ms-appx URIs in the extracted bundle.
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY_PID",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private MainWindow? window;
    internal Microsoft.UI.WindowId MainWindowId => window?.AppWindow.Id
        ?? throw new InvalidOperationException(L10n.Get("Main_WindowNotCreated"));

    internal AppAppearance Appearance { get; private set; } = null!;

    public App()
    {
        var settings = AppearanceSettings.Load();
        AppDiagnostics.Start(settings);
        UnhandledException += (_, args) => AppDiagnostics.Crash("WinUIUnhandledException", args.Exception);
        L10n.ApplyLanguage(settings.Language);
        AppDiagnostics.StartupHeader(settings);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Application.Current's resource projection is available once application construction has finished.
        Appearance = new AppAppearance();
        window = new MainWindow();
        window.Activate();
    }
}
