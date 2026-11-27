
using ExHyperV.App.Diagnostics;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.ConsoleHost;
using ExHyperV.ConsoleIpc;
using ExHyperV.App.UI.Shell;
using ExHyperV.Services;
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
    private readonly AppearanceSettings settings;
    private readonly ConsoleIpcClient ipcClient;
    private int shutdownRequested;
    internal Microsoft.UI.WindowId MainWindowId => window?.AppWindow.Id
        ?? throw new InvalidOperationException(L10n.Get("Main_WindowNotCreated"));

    internal AppAppearance Appearance { get; private set; } = null!;

    public App()
    {
        ipcClient = new ConsoleIpcClient(new ConsoleIpcDiscovery(), 1);
        ConsoleExtensionCatalog.Local.Register(new ConsoleIpcHostConnector(ipcClient));
        settings = AppearanceSettings.Load();
        AppDiagnostics.Start(settings);
        UnhandledException += (_, args) => AppDiagnostics.Crash("WinUIUnhandledException", args.Exception);
        L10n.ApplyLanguage(settings.Language);
        AppDiagnostics.StartupHeader(settings);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Application.Current's resource projection is available once application construction has finished.
        bool login = Environment.GetCommandLineArgs().Contains(StartupRegistration.LoginArgument, StringComparer.Ordinal);
        if (login && settings.AutoStart == StartupMode.Off) { Shutdown(); return; }
        Appearance = new AppAppearance(settings);
        NativeTheme.Sync(0, settings.Theme);
        window = new MainWindow();
        if (!login || settings.AutoStart != StartupMode.Silent || !window.TryHideToTray())
            window.Activate();
        if (Appearance.Settings.AutoCheckUpdate) window.CheckForStartupUpdate();
    }

    internal void Shutdown()
    {
        if (Interlocked.Exchange(ref shutdownRequested, 1) != 0) return;
        try { ipcClient.Dispose(); } catch (Exception) { }
        try { SharedBackgroundServices.ShutdownAsync().GetAwaiter().GetResult(); } catch (Exception) { }
        try { Exit(); } catch (Exception) { }
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(2500);
            AppDiagnostics.Shutdown();
            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "ExHyperV shutdown guard"
        };
        watchdog.Start();
    }
}
