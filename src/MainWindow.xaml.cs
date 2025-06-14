using System.Windows;
using ExHyperV.Views.Pages;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ExHyperV;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += PagePreload;

        // This watcher listens for theme changes while the application is running.
        // The initial theme is now set in App.xaml.cs.
        Loaded += (sender, args) => { SystemThemeWatcher.Watch(this); };
    }

    private void PagePreload(object sender, RoutedEventArgs e)
    {
        // Preload all pages to avoid navigation lag on first access.
        RootNavigation.Navigate(typeof(DDAPage));
        RootNavigation.Navigate(typeof(GPUPage));
        RootNavigation.Navigate(typeof(StatusPage));
        RootNavigation.Navigate(typeof(MainPage));
    }
}