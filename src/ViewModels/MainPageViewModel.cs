using CommunityToolkit.Mvvm.ComponentModel;
using ExHyperV.Services;

namespace ExHyperV.ViewModels
{
    public partial class MainPageViewModel : ObservableObject
    {
        [ObservableProperty] private string? _caption;
        [ObservableProperty] private string? _oSArchitecture;
        [ObservableProperty] private string? _cpuModel;
        [ObservableProperty] private string? _memCap;
        [ObservableProperty] private string? _appVersion;

        public MainPageViewModel()
        {
            AppVersion = AppInfoService.Version;
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
    }
}
