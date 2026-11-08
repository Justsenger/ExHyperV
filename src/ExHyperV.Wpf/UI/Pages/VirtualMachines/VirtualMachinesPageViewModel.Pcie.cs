using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.UI.Pages.VirtualMachines
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
            new(0, Properties.Resources.VmPcie_TopologyDefault),
            new(1, Properties.Resources.VmPcie_TopologyHost),
        ];

        public IReadOnlyList<VmPcieOption<VmPcieGuestMode>> PcieGuestModes { get; } =
        [
            new(VmPcieGuestMode.Paravirtualized, Properties.Resources.VmPcie_Paravirtualized),
            new(VmPcieGuestMode.Emulated, Properties.Resources.VmPcie_Emulated),
        ];

        public string VmPcieSystemSection => Properties.Resources.VmPcie_SystemSection;
        public string VmPcieEmulationTitle => Properties.Resources.VmPcie_EmulationTitle;
        public string VmPcieEmulationDesc => Properties.Resources.VmPcie_EmulationDesc;
        public string VmPcieTopologyTitle => Properties.Resources.VmPcie_TopologyTitle;
        public string VmPcieTopologyDesc => Properties.Resources.VmPcie_TopologyDesc;
        public string VmPcieBootSection => Properties.Resources.VmPcie_BootSection;
        public string VmPcieBootTitle => Properties.Resources.VmPcie_BootTitle;
        public string VmPcieBootDesc => Properties.Resources.VmPcie_BootDesc;
        public string VmPcieDevicesSection => Properties.Resources.VmPcie_DevicesSection;
        public string VmPcieDeviceModeTitle => Properties.Resources.VmPcie_DeviceModeTitle;
        public string VmPcieDeviceModeDesc => Properties.Resources.VmPcie_DeviceModeDesc;
        public string VmPcieNoDevices => Properties.Resources.VmPcie_NoDevices;
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
                var result = await VmPcieService.GetSettingsAsync(vm.Id);
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
                    foreach (var device in PcieDevices) device.PropertyChanged -= OnPcieDevicePropertyChanged;
                    PcieDevices = new ObservableCollection<VmPcieDeviceSetting>(result.Data.Devices);
                    foreach (var device in PcieDevices)
                        device.PropertyChanged += OnPcieDevicePropertyChanged;
                }
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
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
            var change = new VmPcieChangeSession(VmPcieChangeKind.EnableEmulation,
                vm.Id, PcieEmulationEnabled, SelectedPcieTopology);
            var result = await change.ApplyAsync(!change.RequiresConfirmation || await ConfirmPermanentEmulationAsync());
            if (result.Cancelled)
            {
                OnPropertyChanged(nameof(PcieEmulationEnabled));
                return;
            }

            if (!result.Success)
            {
                OnPropertyChanged(nameof(PcieEmulationEnabled));
                ShowError(FriendlyError.CleanLines(result.Error));
                return;
            }

            if (SelectedVm == vm) PcieEmulationEnabled = true;
            ShowSuccess(Properties.Resources.VmPcie_EmulationEnabledMessage);
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
                var change = new VmPcieChangeSession(VmPcieChangeKind.Topology,
                    vm.Id, PcieEmulationEnabled, topology);
                var result = await change.ApplyAsync(!change.RequiresConfirmation || await ConfirmPermanentEmulationAsync());
                if (result.Cancelled)
                {
                    RestorePcieTopology(vm, previousTopology);
                    return;
                }

                if (!result.Success)
                {
                    ShowError(FriendlyError.CleanLines(result.Error));
                    RestorePcieTopology(vm, previousTopology);
                    return;
                }

                if (SelectedVm == vm)
                {
                    _appliedPcieTopology = topology;
                    if (change.EmulationEnabled) PcieEmulationEnabled = true;
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
            var result = await VmPcieService.SetBootPciExpressAsync(vm.Id, value);
            if (result.Success)
            {
                ShowSuccess(Properties.Resources.VmPcie_AppliedMessage);
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
            var requestedMode = device.GuestMode;
            var change = new VmPcieChangeSession(VmPcieChangeKind.DeviceMode,
                vm.Id, PcieEmulationEnabled, SelectedPcieTopology, device, requestedMode);
            try
            {
                var result = await change.ApplyAsync(!change.RequiresConfirmation || await ConfirmPermanentEmulationAsync());
                if (result.Cancelled)
                {
                    RestorePcieDeviceMode(device);
                    return;
                }
                if (!result.Success)
                {
                    RestorePcieDeviceMode(device);
                    ShowError(FriendlyError.CleanLines(result.Error));
                    return;
                }

                device.AppliedGuestMode = requestedMode;
            }
            catch (Exception ex)
            {
                RestorePcieDeviceMode(device);
                ShowError(FriendlyError.CleanLines(ex.Message));
            }
            finally
            {
                if (SelectedVm == vm && change.EmulationEnabled) PcieEmulationEnabled = true;
            }
        }

        private void RestorePcieDeviceMode(VmPcieDeviceSetting device)
        {
            using (SuppressApply())
                device.GuestMode = device.AppliedGuestMode;
        }

        private static Task<bool> ConfirmPermanentEmulationAsync()
            => ExHyperV.UI.Shared.Interaction.Dialogs.ShowConfirmAsync(
                Properties.Resources.VmPcie_ConfirmTitle,
                Properties.Resources.VmPcie_ConfirmMessage,
                Properties.Resources.VmPcie_Enable,
                Properties.Resources.Button_Cancel,
                isDanger: true,
                showIcon: false,
                maxWidth: 340);
    }
}
