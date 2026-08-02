using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.ViewModels
{
    public partial class VirtualMachinesPageViewModel
    {
        private int _pcieLoadVersion;
        private ushort _appliedPcieTopology;

        [ObservableProperty]
        private bool _pcieSystemSettingsAvailable;
        [ObservableProperty] private bool _pcieEmulationEnabled;
        [ObservableProperty] private ushort _selectedPcieTopology;
        [ObservableProperty] private bool _bootPciExpressAvailable;
        [ObservableProperty] private bool _bootPciExpressEnabled;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPcieDevices))]
        [NotifyPropertyChangedFor(nameof(HasNoPcieDevices))]
        private ObservableCollection<VmPcieDeviceSetting> _pcieDevices = [];

        public bool HasPcieDevices => PcieDevices.Count > 0;
        public bool HasNoPcieDevices => !HasPcieDevices;

        public IReadOnlyList<VmPcieOption<ushort>> PcieTopologyOptions { get; } =
        [
            new(0, PcieText("VmPcie_TopologyDefault")),
            new(1, PcieText("VmPcie_TopologyHost")),
        ];

        public IReadOnlyList<VmPcieOption<VmPcieGuestMode>> PcieGuestModes { get; } =
        [
            new(VmPcieGuestMode.Paravirtualized, PcieText("VmPcie_Paravirtualized")),
            new(VmPcieGuestMode.Emulated, PcieText("VmPcie_Emulated")),
        ];

        public string VmPcieSystemSection => PcieText("VmPcie_SystemSection");
        public string VmPcieEmulationTitle => PcieText("VmPcie_EmulationTitle");
        public string VmPcieEmulationDesc => PcieText("VmPcie_EmulationDesc");
        public string VmPcieTopologyTitle => PcieText("VmPcie_TopologyTitle");
        public string VmPcieTopologyDesc => PcieText("VmPcie_TopologyDesc");
        public string VmPcieBootSection => PcieText("VmPcie_BootSection");
        public string VmPcieBootTitle => PcieText("VmPcie_BootTitle");
        public string VmPcieBootDesc => PcieText("VmPcie_BootDesc");
        public string VmPcieDevicesSection => PcieText("VmPcie_DevicesSection");
        public string VmPcieDeviceModeTitle => PcieText("VmPcie_DeviceModeTitle");
        public string VmPcieDeviceModeDesc => PcieText("VmPcie_DeviceModeDesc");
        public string VmPcieNoDevices => PcieText("VmPcie_NoDevices");
        [RelayCommand]
        private async Task GoToPcieSettingsAsync()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.PcieSettings;
            await LoadPcieSettingsAsync(SelectedVm);
        }

        private async Task LoadPcieSettingsAsync(VmInstanceViewModel vm)
        {
            int loadVersion = ++_pcieLoadVersion;
            IsLoadingSettings = true;
            try
            {
                var result = await VmPcieService.GetSettingsAsync(vm.Name);
                if (loadVersion != _pcieLoadVersion || SelectedVm != vm) return;
                if (!result.Success)
                {
                    ShowError($"{Properties.Resources.Error_Common_LoadFail}：{FriendlyError.CleanLines(result.Error)}");
                    return;
                }
                if (!result.HasData) return;

                using (SuppressApply())
                {
                    PcieSystemSettingsAvailable = result.Data!.SystemSettingsAvailable;
                    PcieEmulationEnabled = result.Data.EmulationEnabled;
                    SelectedPcieTopology = result.Data.Topology;
                    _appliedPcieTopology = result.Data.Topology;
                    BootPciExpressAvailable = result.Data.BootPciExpressAvailable;
                    BootPciExpressEnabled = result.Data.BootPciExpress;
                    PcieDevices = new ObservableCollection<VmPcieDeviceSetting>(result.Data.Devices);
                    foreach (var device in PcieDevices)
                        device.PropertyChanged += OnPcieDevicePropertyChanged;
                }
            }
            finally
            {
                if (loadVersion == _pcieLoadVersion)
                    IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task EnablePcieEmulationAsync()
        {
            if (SelectedVm == null || !PcieSystemSettingsAvailable || PcieEmulationEnabled) return;
            var vm = SelectedVm;
            bool confirmed = await ConfirmPermanentEmulationAsync();
            if (!confirmed)
            {
                OnPropertyChanged(nameof(PcieEmulationEnabled));
                return;
            }

            var result = await VmPcieService.SetSystemSettingsAsync(
                vm.Name, enableEmulation: true, SelectedPcieTopology);
            if (!result.Success)
            {
                OnPropertyChanged(nameof(PcieEmulationEnabled));
                ShowError(FriendlyError.CleanLines(result.Error));
                return;
            }

            if (SelectedVm == vm) PcieEmulationEnabled = true;
            ShowSuccess(PcieText("VmPcie_EmulationEnabledMessage"));
        }

        partial void OnSelectedPcieTopologyChanged(ushort value)
        {
            if (IsApplySuppressed
                || CurrentViewType != VmDetailViewType.PcieSettings
                || SelectedVm == null
                || !PcieSystemSettingsAvailable)
                return;

            _ = ApplyPcieTopologyAsync(value);
        }

        private async Task ApplyPcieTopologyAsync(ushort topology)
        {
            if (SelectedVm == null || !PcieSystemSettingsAvailable) return;
            var vm = SelectedVm;
            ushort previousTopology = _appliedPcieTopology;
            try
            {
                bool enablesEmulation = topology == 1 && !PcieEmulationEnabled;
                if (enablesEmulation && !await ConfirmPermanentEmulationAsync())
                {
                    RestorePcieTopology(vm, previousTopology);
                    return;
                }

                var result = await VmPcieService.SetSystemSettingsAsync(
                    vm.Name, PcieEmulationEnabled || enablesEmulation, topology);
                if (!result.Success)
                {
                    ShowError(FriendlyError.CleanLines(result.Error));
                    RestorePcieTopology(vm, previousTopology);
                    return;
                }

                if (SelectedVm == vm)
                {
                    _appliedPcieTopology = topology;
                    if (enablesEmulation) PcieEmulationEnabled = true;
                }
            }
            catch (Exception ex)
            {
                RestorePcieTopology(vm, previousTopology);
                ShowError(FriendlyError.CleanLines(ex.Message));
            }
        }

        private void RestorePcieTopology(VmInstanceViewModel vm, ushort topology)
        {
            if (SelectedVm != vm) return;
            using (SuppressApply())
                SelectedPcieTopology = topology;
        }

        partial void OnBootPciExpressEnabledChanged(bool value)
        {
            if (IsApplySuppressed || SelectedVm == null || !BootPciExpressAvailable) return;
            _ = ApplyBootPciExpressAsync(value);
        }

        private async Task ApplyBootPciExpressAsync(bool value)
        {
            if (SelectedVm == null) return;
            var vm = SelectedVm;
            var result = await VmPcieService.SetBootPciExpressAsync(vm.Name, value);
            if (result.Success)
            {
                ShowSuccess(PcieText("VmPcie_AppliedMessage"));
                return;
            }

            if (SelectedVm == vm)
                using (SuppressApply()) BootPciExpressEnabled = !value;
            ShowError(FriendlyError.CleanLines(result.Error));
        }

        private void OnPcieDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(VmPcieDeviceSetting.GuestMode)
                || IsApplySuppressed
                || CurrentViewType != VmDetailViewType.PcieSettings
                || sender is not VmPcieDeviceSetting device
                || SelectedVm == null
                || !device.GuestModeAvailable)
                return;

            _ = ApplyPcieDeviceModeAsync(device);
        }

        private async Task ApplyPcieDeviceModeAsync(VmPcieDeviceSetting device)
        {
            if (SelectedVm == null || device == null || !device.GuestModeAvailable) return;
            var vm = SelectedVm;
            try
            {
                if (device.GuestMode == VmPcieGuestMode.Emulated && !PcieEmulationEnabled)
                {
                    if (!await ConfirmPermanentEmulationAsync())
                    {
                        RestorePcieDeviceMode(device);
                        return;
                    }

                    var enable = await VmPcieService.SetSystemSettingsAsync(
                        vm.Name, enableEmulation: true, SelectedPcieTopology);
                    if (!enable.Success)
                    {
                        RestorePcieDeviceMode(device);
                        ShowError(FriendlyError.CleanLines(enable.Error));
                        return;
                    }
                    if (SelectedVm == vm) PcieEmulationEnabled = true;
                }

                var result = await VmPcieService.SetDeviceModeAsync(device.WmiInstanceId, device.GuestMode);
                if (!result.Success)
                {
                    RestorePcieDeviceMode(device);
                    ShowError(FriendlyError.CleanLines(result.Error));
                    return;
                }

                device.AppliedGuestMode = device.GuestMode;
            }
            catch (Exception ex)
            {
                RestorePcieDeviceMode(device);
                ShowError(FriendlyError.CleanLines(ex.Message));
            }
        }

        private void RestorePcieDeviceMode(VmPcieDeviceSetting device)
        {
            using (SuppressApply())
                device.GuestMode = device.AppliedGuestMode;
        }

        private static Task<bool> ConfirmPermanentEmulationAsync()
            => Interaction.Dialogs.ShowConfirmAsync(
                PcieText("VmPcie_ConfirmTitle"),
                PcieText("VmPcie_ConfirmMessage"),
                PcieText("VmPcie_Enable"),
                Properties.Resources.Button_Cancel,
                isDanger: true,
                showIcon: false,
                maxWidth: 340);

        private static string PcieText(string key)
            => Properties.Resources.ResourceManager.GetString(key)
               ?? key;
    }
}
