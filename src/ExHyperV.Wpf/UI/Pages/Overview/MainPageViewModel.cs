using ExHyperV.Application;
using ExHyperV.UI.Pages.Host;
using ExHyperV.UI.Pages.Pcie;
using ExHyperV.UI.Pages.Switches;
using ExHyperV.UI.Pages.VirtualMachines;
using ExHyperV.UI.Shared;
using ExHyperV.UI.Shared.Interaction;
using ExHyperV.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;

namespace ExHyperV.UI.Pages.Overview
{
    public partial class MainPageViewModel : PageViewModelBase
    {
        [ObservableProperty] private string? _caption;
        [ObservableProperty] private string? _oSArchitecture;
        [ObservableProperty] private string? _cpuModel;
        [ObservableProperty] private string? _memCap;
        [ObservableProperty] private string? _appVersion;

        public MainPageViewModel()
        {
            AppVersion = AppInfoService.Version;
            LoadSystemInfoAsync().SafeFireAndForget();
        }

        private async Task LoadSystemInfoAsync()
        {
            var info = await HostSystemInfoService.GetSystemInfoAsync();
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
                _ => null
            };

            if (pageType != null)
                Navigation.NavigateTo(pageType);
        }
    }
}
