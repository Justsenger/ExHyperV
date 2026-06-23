using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Interaction;
using ExHyperV.Services;
using ExHyperV.Views;

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
            AppVersion = AppInfoService.Version;
            Author = AppInfoService.Author;
            BuildDate = AppInfoService.BuildTime.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
            _ = LoadSystemInfoAsync();
        }

        private async Task LoadSystemInfoAsync()
        {
            var info = await SystemInfoService.GetSystemInfoAsync();
            Caption = info.Caption;
            OSArchitecture = info.OSArchitecture;
            CpuModel = info.CpuModel;
            MemCap = info.MemCap;
        }

        [RelayCommand]
        private void OnNavigate(string parameter)
        {
            Type? pageType = parameter switch
            {
                "VM" => typeof(VirtualMachinesPage),
                "Host" => typeof(HostPage),
                "PCIe" => typeof(PCIePage),
                "Network" => typeof(SwitchPage),
                "USB" => typeof(USBPage),
                _ => null
            };

            if (pageType != null)
                Navigation.NavigateTo(pageType);
        }
    }
}