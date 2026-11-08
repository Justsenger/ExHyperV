using ExHyperV.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.WinUI3.UI.Pages.Host;

public sealed partial class HostPage : Page
{
    private bool requested;

    public HostPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (requested) return;
        requested = true;
        try
        {
            var info = await HostSystemInfoService.GetSystemInfoAsync();
            OperatingSystem.Text = info.Caption;
            Processor.Text = info.CpuModel;
            Architecture.Text = HostProcessorPlatformService.GetArchitecture().ToString();
            Memory.Text = info.MemCap;
            Details.Visibility = Visibility.Visible;
        }
        catch (Exception exception)
        {
            QueryError.Message = exception.Message;
            QueryError.IsOpen = true;
            requested = false;
        }
        finally
        {
            LoadingIndicator.IsActive = false;
            LoadingIndicator.Visibility = Visibility.Collapsed;
        }
    }
}
