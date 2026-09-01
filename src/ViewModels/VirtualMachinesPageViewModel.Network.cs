using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
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
            if (SelectedVm == null) return;

            CurrentViewType = VmDetailViewType.NetworkSettings;
            IsLoadingSettings = true;

            try
            {
                var switchesTask = VmNetworkService.GetAvailableSwitchesAsync();
                var adaptersTask = VmNetworkService.GetNetworkAdaptersAsync(SelectedVm.Name);

                await Task.WhenAll(switchesTask, adaptersTask);

                if (!AvailableSwitchNames.SequenceEqual(switchesTask.Result))
                {
                    AvailableSwitchNames = new ObservableCollection<string>(switchesTask.Result);
                }

                SyncNetworkAdaptersInternal(SelectedVm.NetworkAdapters, adaptersTask.Result);

                // IP 探测：仅运行中向 guest 探 IP；关机则清掉运行时缓存的旧 IP——
                // GetNetworkAdapters 不带 IP、且 SyncNetworkAdaptersInternal 只在非空时更新 IP，
                // 不主动清的话关机后会一直显示上次运行时的 IPv4/IPv6。
                if (SelectedVm.IsRunning)
                {
                    _ = Task.Run(async () => {
                        await VmNetworkService.FillDynamicIpsAsync(SelectedVm.Name, SelectedVm.NetworkAdapters);
                    });
                }
                else
                {
                    foreach (var a in SelectedVm.NetworkAdapters)
                        a.IpAddresses = new List<string>();
                }
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

        // 原位合并网卡列表以保持现有绑定。
        private void SyncNetworkAdaptersInternal(ObservableCollection<VmNetworkAdapter> currentList, List<VmNetworkAdapter> newList)
        {
            if (newList == null) return;

            var toRemove = currentList.Where(c => !newList.Any(n => n.Id == c.Id)).ToList();
            foreach (var item in toRemove)
            {
                currentList.Remove(item);
            }

            foreach (var newItem in newList)
            {
                var existingItem = currentList.FirstOrDefault(c => c.Id == newItem.Id);
                if (existingItem != null)
                {
                    existingItem.Name = newItem.Name;
                    existingItem.IsConnected = newItem.IsConnected;
                    existingItem.SwitchName = newItem.SwitchName;
                    existingItem.MacAddress = newItem.MacAddress;
                    existingItem.IsStaticMac = newItem.IsStaticMac;

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
        private async Task RevertAdaptersFromBackendAsync()
        {
            if (SelectedVm == null) return;
            try
            {
                var fresh = await VmNetworkService.GetNetworkAdaptersAsync(SelectedVm.Name);
                SyncNetworkAdaptersInternal(SelectedVm.NetworkAdapters, fresh);
            }
            catch { /* 回滚是尽力而为：拉取失败则保持现状，离开网络页时会自然重对账 */ }
        }

        [RelayCommand]
        private async Task AddNetworkAdapterAsync()
        {
            if (SelectedVm == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.AddNetworkAdapterAsync(SelectedVm.Name);

                if (result.Success)
                {
                    ShowSuccess(Properties.Resources.Msg_Net_Added);
                    await GoToNetworkSettingsAsync();
                }
                else
                {
                    ShowError($"{Properties.Resources.Error_Storage_AddFail}：{FriendlyError.CleanLines(result.Message)}");
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
        private async Task RemoveNetworkAdapterAsync(string adapterId)
        {
            if (SelectedVm == null || string.IsNullOrEmpty(adapterId)) return;

            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.RemoveNetworkAdapterAsync(SelectedVm.Name, adapterId);

                if (result.Success)
                {
                    ShowSuccess(Properties.Resources.Msg_Net_AdapterRemoved);
                    await GoToNetworkSettingsAsync();
                }
                else
                {
                    ShowError($"{Properties.Resources.Error_Storage_RemoveFail}：{FriendlyError.CleanLines(result.Message)}");
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
            if (SelectedVm == null || adapter == null) return;

            // 未创建任何虚拟交换机时，网卡只有 setting、没有可创建的端口分配对象。
            // 开启操作在这里直接回滚并给出明确提示，避免落到 WMI 后显示误导性的“找不到分配对象”。
            if (adapter.IsConnected && AvailableSwitchNames.Count == 0)
            {
                adapter.IsConnected = false;
                ShowTip(Properties.Resources.Msg_Net_CreateSwitchFirst);
                return;
            }

            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.UpdateConnectionAsync(SelectedVm.Name, adapter);
                if (!result.Success)
                {
                    ShowError(result.Message);
                    adapter.IsConnected = !adapter.IsConnected;
                }
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 应用 MAC 地址（改静态 MAC；输入空=改回动态）
        [RelayCommand]
        private async Task ApplyMacAddressAsync(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.SetMacAddressAsync(SelectedVm.Name, adapter, adapter.MacAddress);
                if (result.Success)
                {
                    ShowSuccess(Properties.Resources.Msg_Net_MacApplied);
                    await RevertAdaptersFromBackendAsync();   // 重拉后端真实 MAC(规范化显示 + 静态/动态状态)
                }
                else ShowError($"{Properties.Resources.Error_Net_ApplyFail}：{result.Message}");
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task ApplyVlanSettingsAsync(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.ApplyVlanSettingsAsync(SelectedVm.Name, adapter);
                if (result.Success) ShowSuccess(Properties.Resources.Msg_Net_VlanApplied);
                else ShowError(result.Message);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task ApplyQosSettingsAsync(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.ApplyBandwidthSettingsAsync(SelectedVm.Name, adapter);
                if (result.Success) ShowSuccess(Properties.Resources.Msg_Net_QosApplied);
                else ShowError(result.Message);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task ApplySecuritySettingsAsync(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var secResult = await VmNetworkService.ApplySecuritySettingsAsync(SelectedVm.Name, adapter);
                if (!secResult.Success)
                {
                    ShowError(string.Format(Properties.Resources.Error_Net_Security, secResult.Message));
                    return;
                }

                var offloadResult = await VmNetworkService.ApplyOffloadSettingsAsync(SelectedVm.Name, adapter);
                if (!offloadResult.Success)
                {
                    ShowError(string.Format(Properties.Resources.Error_Net_Offload, offloadResult.Message));
                    return;
                }

                ShowSuccess(Properties.Resources.Msg_Common_Applied);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task ToggleOffloadSettingAsync(VmNetworkAdapter adapter)
        {
            if (CurrentViewType != VmDetailViewType.NetworkSettings) return; // 同上：挡导航离开时开关卸载的误触发
            if (SelectedVm == null || adapter == null) return;
            var result = await VmNetworkService.ApplyOffloadSettingsAsync(SelectedVm.Name, adapter);
            if (!result.Success)
            {
                ShowError($"{Properties.Resources.Error_Net_ApplyFail}：{result.Message}");
                await RevertAdaptersFromBackendAsync();   // 失败回滚开关，避免 UI 显示与后端不一致
            }
        }

        [RelayCommand]
        private async Task ToggleSecuritySettingAsync(VmNetworkAdapter adapter)
        {
            if (CurrentViewType != VmDetailViewType.NetworkSettings) return; // 同上：挡导航离开时开关卸载的误触发
            if (SelectedVm == null || adapter == null) return;
            var result = await VmNetworkService.ApplySecuritySettingsAsync(SelectedVm.Name, adapter);
            if (!result.Success)
            {
                ShowError($"{Properties.Resources.Error_Net_SecurityFail}：{result.Message}");
                await RevertAdaptersFromBackendAsync();   // 失败回滚开关，避免 UI 显示与后端不一致
            }
        }



    }
}
