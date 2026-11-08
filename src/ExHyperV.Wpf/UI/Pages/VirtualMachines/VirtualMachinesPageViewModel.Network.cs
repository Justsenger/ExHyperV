using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.UI.Pages.VirtualMachines
{
    public partial class VirtualMachinesPageViewModel
    {
        [ObservableProperty] private ObservableCollection<string> _availableSwitchNames = new();






        public List<object> VlanModeOptions { get; } = new()
{
    new { Value = VlanOperationMode.Access, Name = Properties.Resources.Net_Mode_Access },
    new { Value = VlanOperationMode.Trunk, Name = Properties.Resources.Net_Mode_Trunk },
    new { Value = VlanOperationMode.Private, Name = Properties.Resources.Net_Mode_Private }
};

        public List<object> PvlanModeOptions { get; } = new()
{
    new { Value = PvlanMode.Isolated, Name = Properties.Resources.Net_Pvlan_Isolated },
    new { Value = PvlanMode.Community, Name = Properties.Resources.Net_Pvlan_Community },
    new { Value = PvlanMode.Promiscuous, Name = Properties.Resources.Net_Pvlan_Promiscuous }
};

        public List<object> PortMirroringOptions { get; } = new()
{
    new { Value = PortMonitorMode.None, Name = Properties.Resources.Common_Disabled },
    new { Value = PortMonitorMode.Source, Name = Properties.Resources.Net_Mirror_Source },
    new { Value = PortMonitorMode.Destination, Name = Properties.Resources.Net_Mirror_Dest }
};

        [RelayCommand]
        private async Task GoToNetworkSettingsAsync()
        {
            var vm = SelectedVm;
            if (vm == null) return;

            CurrentViewType = VmDetailViewType.NetworkSettings;
            IsLoadingSettings = true;

            try
            {
                var switchesTask = VmNetworkService.GetAvailableSwitchesAsync();
                var adaptersTask = VmNetworkService.GetNetworkAdaptersAsync(vm.Id);

                await Task.WhenAll(switchesTask, adaptersTask);

                var switches = switchesTask.Result.RequireData();
                var adapters = adaptersTask.Result.RequireData();
                if (SelectedVm != vm) return;
                if (!AvailableSwitchNames.SequenceEqual(switches))
                {
                    AvailableSwitchNames = new ObservableCollection<string>(switches);
                }

                SyncNetworkAdaptersInternal(vm.NetworkAdapters, adapters);

                if (vm.IsRunning)
                    _ = RefreshNetworkIpsAsync(vm, vm.NetworkAdapters.ToArray());
                else
                    foreach (var adapter in vm.NetworkAdapters) adapter.IpAddresses = new List<string>();

            }
            catch (Exception ex)
            {
                ShowError($"{Properties.Resources.Error_Common_LoadFail}：{ex.Message}");
            }
            finally
            {
                await Task.Delay(300);
                IsLoadingSettings = false;
            }
        }

        private async Task RefreshNetworkIpsAsync(VmInstanceViewModel vm, VmNetworkAdapter[] adapters)
        {
            try
            {
                var result = await VmNetworkService.FillDynamicIpsAsync(vm.Id, adapters);
                result.ThrowIfFailed();
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
        }

        // 原位合并网卡列表以保持现有绑定。
        private void SyncNetworkAdaptersInternal(ObservableCollection<VmNetworkAdapter> currentList, List<VmNetworkAdapter> newList)
        {
            if (newList == null) return;

            var toRemove = currentList.Where(c => !newList.Any(n => n.DeviceId == c.DeviceId)).ToList();
            foreach (var item in toRemove)
            {
                currentList.Remove(item);
            }

            foreach (var newItem in newList)
            {
                var existingItem = currentList.FirstOrDefault(c => c.DeviceId == newItem.DeviceId);
                if (existingItem != null)
                {
                    existingItem.Name = newItem.Name;
                    existingItem.IsConnected = newItem.IsConnected;
                    existingItem.SwitchIdentity = newItem.SwitchIdentity;
                    existingItem.MacAddress = newItem.MacAddress;
                    existingItem.IsStaticMac = newItem.IsStaticMac;
                    if (newItem.PerformanceSample?.IsValid == true)
                    {
                        existingItem.SendBytesPerSecond = newItem.SendBytesPerSecond;
                        existingItem.ReceiveBytesPerSecond = newItem.ReceiveBytesPerSecond;
                    }
                    existingItem.HasNetworkSpeedSample = newItem.HasNetworkSpeedSample;
                    existingItem.PerformanceSample = newItem.PerformanceSample ?? new(PerformanceSampleState.NotSampled, default);

                    if (newItem.IpAddresses != null && newItem.IpAddresses.Count > 0)
                    {
                        existingItem.IpAddresses = newItem.IpAddresses;
                    }

                    // VLAN 设置
                    existingItem.VlanMode = newItem.VlanMode;
                    existingItem.AccessVlanId = newItem.AccessVlanId;
                    existingItem.NativeVlanId = newItem.NativeVlanId;
                    existingItem.TrunkAllowedVlanIds = newItem.TrunkAllowedVlanIds;
                    existingItem.PvlanMode = newItem.PvlanMode;
                    existingItem.PvlanPrimaryId = newItem.PvlanPrimaryId;
                    existingItem.PvlanSecondaryId = newItem.PvlanSecondaryId;

                    // 带宽与安全
                    existingItem.BandwidthLimit = newItem.BandwidthLimit;
                    existingItem.BandwidthReservation = newItem.BandwidthReservation;
                    existingItem.MacSpoofingAllowed = newItem.MacSpoofingAllowed;
                    existingItem.DhcpGuardEnabled = newItem.DhcpGuardEnabled;
                    existingItem.RouterGuardEnabled = newItem.RouterGuardEnabled;
                    existingItem.MonitorMode = newItem.MonitorMode;
                    existingItem.StormLimit = newItem.StormLimit;
                    existingItem.TeamingAllowed = newItem.TeamingAllowed;

                    // 硬件卸载
                    existingItem.VmqEnabled = newItem.VmqEnabled;
                    existingItem.SriovEnabled = newItem.SriovEnabled;
                    existingItem.IpsecOffloadEnabled = newItem.IpsecOffloadEnabled;
                }
                else
                {
                    currentList.Add(newItem);
                }
            }
        }

        // 网卡操作失败后重新读取后端状态，使界面回到实际值。
        private async Task RevertAdaptersFromBackendAsync(VmInstanceViewModel vm)
        {
            try
            {
                var fresh = (await VmNetworkService.GetNetworkAdaptersAsync(vm.Id)).RequireData();
                SyncNetworkAdaptersInternal(vm.NetworkAdapters, fresh);
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
        }

        [RelayCommand]
        private async Task AddNetworkAdapterAsync()
        {
            var vm = SelectedVm;
            if (vm == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.AddNetworkAdapterAsync(vm.Id);

                // 失败可能已经创建或撤销了资源；始终刷新实际列表，保留本次操作原始结果。
                await RevertAdaptersFromBackendAsync(vm);

                if (result.Success)
                {
                    ShowSuccess(Properties.Resources.Msg_Net_Added);
                }
                else
                {
                    var details = DescribeOperationResult(result.Error, result.CleanupError, [],
                        result.AdapterState == VmNetworkAdapterCreationState.Unknown ||
                        result.InitializationState == VmNetworkAdapterInitializationState.Unknown);
                    if (result.RetainedDeviceIds.Count > 0)
                        details += Environment.NewLine + string.Join(Environment.NewLine, result.RetainedDeviceIds);
                    ShowError($"{Properties.Resources.Error_Storage_AddFail}：{details}");
                }
            }
            catch (Exception ex)
            {
                ShowError($"{Properties.Resources.Error_Net_AddExc}：{FriendlyError.CleanLines(ex.Message)}");
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task RemoveNetworkAdapterAsync(VmNetworkAdapter adapter)
        {
            var vm = SelectedVm;
            if (vm == null || adapter == null) return;

            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.RemoveNetworkAdapterAsync(vm.Id, adapter);

                if (result.Success)
                {
                    ShowSuccess(Properties.Resources.Msg_Net_AdapterRemoved);
                    if (SelectedVm == vm) await GoToNetworkSettingsAsync();
                }
                else
                {
                    ShowError($"{Properties.Resources.Error_Storage_RemoveFail}：{FriendlyError.CleanLines(result.Error)}");
                }
            }
            catch (Exception ex)
            {
                ShowError($"{Properties.Resources.Error_Net_RemoveExc}：{FriendlyError.CleanLines(ex.Message)}");
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task UpdateAdapterConnectionAsync(VmNetworkAdapter adapter)
        {
            // 导航离开网络页时，交换机下拉(SelectionChanged)/连接开关(Toggled)会在卸载瞬间误触发本命令，
            // 静默改或断开网卡连接(运行态可热改、不报错，更隐蔽)。仅在仍处于网络页时执行。
            if (CurrentViewType != VmDetailViewType.NetworkSettings) return;
            var vm = SelectedVm;
            if (vm == null || adapter == null) return;

            // 未创建任何虚拟交换机时，网卡只有 setting、没有可创建的端口分配对象。
            // 开启操作在这里直接回滚并给出明确提示，避免落到 WMI 后显示误导性的“找不到分配对象”。
            if (VmNetworkService.GetConnectionError(adapter.IsConnected, AvailableSwitchNames.Count) is string connectionError)
            {
                adapter.IsConnected = false;
                ShowTip(connectionError);
                return;
            }

            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.UpdateConnectionAsync(vm.Id, adapter);
                if (!result.Success)
                {
                    ShowError(result.Error);
                    adapter.IsConnected = !adapter.IsConnected;
                }
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 应用 MAC 地址（改静态 MAC；输入空=改回动态）
        [RelayCommand]
        private async Task ApplyMacAddressAsync(VmNetworkAdapter adapter)
        {
            var vm = SelectedVm;
            if (vm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.SetMacAddressAsync(vm.Id, adapter, adapter.MacAddress);
                if (result.Success)
                {
                    ShowSuccess(Properties.Resources.Msg_Net_MacApplied);
                    await RevertAdaptersFromBackendAsync(vm);   // 重拉后端真实 MAC(规范化显示 + 静态/动态状态)
                }
                else ShowError($"{Properties.Resources.Error_Net_ApplyFail}：{result.Error}");
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task ApplyVlanSettingsAsync(VmNetworkAdapter adapter)
        {
            var vm = SelectedVm;
            if (vm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.ApplyVlanSettingsAsync(vm.Id, adapter);
                if (result.Success) ShowSuccess(Properties.Resources.Msg_Net_VlanApplied);
                else ShowError(result.Error);
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task ApplyQosSettingsAsync(VmNetworkAdapter adapter)
        {
            var vm = SelectedVm;
            if (vm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.ApplyBandwidthSettingsAsync(vm.Id, adapter);
                if (result.Success) ShowSuccess(Properties.Resources.Msg_Net_QosApplied);
                else ShowError(result.Error);
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task ApplySecuritySettingsAsync(VmNetworkAdapter adapter)
        {
            var vm = SelectedVm;
            if (vm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.ApplySecurityAndOffloadSettingsWithResultAsync(vm.Id, adapter);
                if (!result.Success)
                {
                    ShowError(result.Message);
                    await RevertAdaptersFromBackendAsync(vm);
                    return;
                }

                ShowSuccess(Properties.Resources.Msg_Common_Applied);
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task ToggleOffloadSettingAsync(VmNetworkAdapter adapter)
        {
            if (CurrentViewType != VmDetailViewType.NetworkSettings) return; // 同上：挡导航离开时开关卸载的误触发
            var vm = SelectedVm;
            if (vm == null || adapter == null) return;
            try
            {
                var result = await VmNetworkService.ApplyOffloadSettingsAsync(vm.Id, adapter);
                if (!result.Success)
                {
                    ShowError($"{Properties.Resources.Error_Net_ApplyFail}：{result.Error}");
                    await RevertAdaptersFromBackendAsync(vm);
                }
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
        }

        [RelayCommand]
        private async Task ToggleSecuritySettingAsync(VmNetworkAdapter adapter)
        {
            if (CurrentViewType != VmDetailViewType.NetworkSettings) return; // 同上：挡导航离开时开关卸载的误触发
            var vm = SelectedVm;
            if (vm == null || adapter == null) return;
            try
            {
                var result = await VmNetworkService.ApplySecuritySettingsAsync(vm.Id, adapter);
                if (!result.Success)
                {
                    ShowError($"{Properties.Resources.Error_Net_SecurityFail}：{result.Error}");
                    await RevertAdaptersFromBackendAsync(vm);
                }
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
        }



    }
}
