using ExHyperV.WinUI3.Localization;
using ExHyperV.WinUI3.UI.Shell;
using ExHyperV.WinUI3.UI.Shared;
using Microsoft.UI.Xaml;

namespace ExHyperV.WinUI3;

public partial class App : Microsoft.UI.Xaml.Application
{
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
