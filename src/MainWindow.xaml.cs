using System.ComponentModel;
using System.Windows;
using ExHyperV.Services;
using ExHyperV.Views.Pages;

namespace ExHyperV;

public partial class MainWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += PagePreload;
        Closing += OnClosing;

        // Theme is already applied in App.xaml.cs OnStartup
        // No need to apply it again here
    }

    private void PagePreload(object sender, RoutedEventArgs e)
    {
        // Preload all sub-pages, multithreaded
        RootNavigation.Navigate(typeof(DdaPage));
        RootNavigation.Navigate(typeof(GpuPage));
        RootNavigation.Navigate(typeof(StatusPage));
        RootNavigation.Navigate(typeof(MainPage));

        // Start watching for system theme changes
        SystemThemeHandler.StartWatching(this);
    }

    private static void OnClosing(object? sender, CancelEventArgs e)
    {
        // Stop watching for system theme changes when closing
        SystemThemeHandler.StopWatching();
    }
}