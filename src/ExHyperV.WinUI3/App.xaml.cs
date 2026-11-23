using ExHyperV.WinUI3.Localization;
using ExHyperV.WinUI3.UI.Shell;
using ExHyperV.WinUI3.UI.Shared;
using Microsoft.UI.Xaml;

namespace ExHyperV.WinUI3;

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

    public Composition.ProductConfiguration Product { get; private set; } = new();
    partial void ConfigureProduct(ref Composition.ProductConfiguration product);
    private MainWindow? window;
    internal Microsoft.UI.WindowId MainWindowId => window?.AppWindow.Id
        ?? throw new InvalidOperationException(L10n.Get("Main_WindowNotCreated"));

    internal AppAppearance Appearance { get; private set; } = null!;

    public App()
    {
        L10n.ApplyLanguage(AppearanceSettings.Load().Language);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var product = Product;
        ConfigureProduct(ref product);
        Product = product;
        // Application.Current's resource projection is available once application construction has finished.
        Appearance = new AppAppearance();
        window = new MainWindow();
        window.Activate();
    }
}
