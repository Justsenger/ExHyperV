using ExHyperV.WinUI3.UI.Shell;
using ExHyperV.WinUI3.UI.Shared;
using Microsoft.UI.Xaml;

namespace ExHyperV.WinUI3;

public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? window;
    internal Microsoft.UI.WindowId MainWindowId => window?.AppWindow.Id
        ?? throw new InvalidOperationException("主窗口尚未创建。");

    internal AppAppearance Appearance { get; private set; } = null!;

    public App()
    {
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
