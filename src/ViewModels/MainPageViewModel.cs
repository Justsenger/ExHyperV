using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Views.Pages;
using System.Windows;

namespace ExHyperV.ViewModels
{
    public partial class MainPageViewModel : ObservableObject
    {
        [ObservableProperty] private string? _caption;
        [ObservableProperty] private string? _oSArchitecture;
        [ObservableProperty] private string? _cpuModel;
        [ObservableProperty] private string? _memCap;
        [ObservableProperty] private string? _appVersion;
        [ObservableProperty] private string? _author;
        [ObservableProperty] private string? _buildDate;

        public MainPageViewModel()
        {
            AppVersion = Utils.Version;
            Author = Utils.Author;
            BuildDate = Utils.GetLinkerTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
            _ = LoadSystemInfoAsync();
        }

        private async Task LoadSystemInfoAsync()
        {
            var info = await new SystemInfoService().GetSystemInfoAsync();
            Caption = info.Caption;
            OSArchitecture = info.OSArchitecture;
            CpuModel = info.CpuModel;
            MemCap = info.MemCap;
        }

        [RelayCommand]
        private void OnNavigate(string parameter)
        {
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow == null) return;

            Type? pageType = parameter switch
            {
                "VM" => typeof(VirtualMachinesPage),
                "Host" => typeof(HostPage),
                "PCIe" => typeof(DDAPage),
                "Network" => typeof(SwitchPage),
                "USB" => typeof(USBPage),
                _ => null
            };

            if (pageType != null)
                mainWindow.RootNavigation.Navigate(pageType);
        }
    }
}