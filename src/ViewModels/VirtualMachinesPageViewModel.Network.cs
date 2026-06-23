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
        // ===== 视图模型属性 - 网络设置 =====
        [ObservableProperty] private ObservableCollection<string> _availableSwitchNames = new();


        // ===== 网络设置模块 =====



        // ===== 网络模式映射选项 (用于翻译) =====

        // 1. VLAN 主模式映射
        public List<object> VlanModeOptions { get; } = new()
{
    new { Value = VlanOperationMode.Access, Name = Properties.Resources.Net_Mode_Access },
    new { Value = VlanOperationMode.Trunk, Name = Properties.Resources.Net_Mode_Trunk },
    new { Value = VlanOperationMode.Private, Name = Properties.Resources.Net_Mode_Private }
};

        // 2. Private VLAN 类型 (角色) 映射
        public List<object> PvlanModeOptions { get; } = new()
{
    new { Value = PvlanMode.Isolated, Name = Properties.Resources.Net_Pvlan_Isolated },
    new { Value = PvlanMode.Community, Name = Properties.Resources.Net_Pvlan_Community },
    new { Value = PvlanMode.Promiscuous, Name = Properties.Resources.Net_Pvlan_Promiscuous }
};

        // 3. 端口镜像模式映射
        public List<object> PortMirroringOptions { get; } = new()
{
    new { Value = PortMonitorMode.None, Name = Properties.Resources.Common_Disabled },
    new { Value = PortMonitorMode.Source, Name = Properties.Resources.Net_Mirror_Source },
    new { Value = PortMonitorMode.Destination, Name = Properties.Resources.Net_Mirror_Dest }
};

        // 导航至网络设置
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

                var firstAdapter = adaptersTask.Result.FirstOrDefault();
                System.Diagnostics.Debug.WriteLine($"[DEBUG] GoToNetworkSettingsAsync is syncing. IsConnected = {firstAdapter?.IsConnected}");
                SyncNetworkAdaptersInternal(SelectedVm.NetworkAdapters, adaptersTask.Result);

                // IP 探测
                if (SelectedVm.IsRunning)
                {
                    _ = Task.Run(async () => {
                        await VmNetworkService.FillDynamicIpsAsync(SelectedVm.Name, SelectedVm.NetworkAdapters);
                    });
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Common_LoadFail, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                await Task.Delay(300);
                IsLoadingSettings = false;
            }
        }

        // 智能同步网卡列表，避免 UI 闪烁
        private void SyncNetworkAdaptersInternal(ObservableCollection<VmNetworkAdapter> currentList, List<VmNetworkAdapter> newList)
        {
            if (newList == null) return;

            // 1. 移除已经不存在的网卡
            var toRemove = currentList.Where(c => !newList.Any(n => n.Id == c.Id)).ToList();
            foreach (var item in toRemove)
            {
                currentList.Remove(item);
            }

            // 2. 更新现有的 或 添加新的
            foreach (var newItem in newList)
            {
                var existingItem = currentList.FirstOrDefault(c => c.Id == newItem.Id);
                if (existingItem != null)
                {
                    // === 存在则更新属性 ===
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

        // 网卡操作失败后从后端重新拉取真实状态覆盖 UI（回滚"撒谎"的开关；复用智能同步避免闪烁）
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

        // 添加新的网络适配器
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
                    ShowSnackbar(Properties.Resources.Msg_Common_AddSuccess, Properties.Resources.Msg_Net_Added, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                    await GoToNetworkSettingsAsync();
                }
                else
                {
                    ShowSnackbar(Properties.Resources.Error_Storage_AddFail, FriendlyError.CleanLines(result.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Net_AddExc, FriendlyError.CleanLines(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 移除网络适配器
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
                    ShowSnackbar(Properties.Resources.Msg_Net_Removed, Properties.Resources.Msg_Net_AdapterRemoved, ControlAppearance.Success, SymbolRegular.Delete24);
                    await GoToNetworkSettingsAsync();
                }
                else
                {
                    ShowSnackbar(Properties.Resources.Error_Storage_RemoveFail, FriendlyError.CleanLines(result.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Net_RemoveExc, FriendlyError.CleanLines(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 更新网卡连接状态
        [RelayCommand]
        private async Task UpdateAdapterConnectionAsync(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.UpdateConnectionAsync(SelectedVm.Name, adapter);
                if (!result.Success)
                {
                    ShowSnackbar(Properties.Resources.Error_Common_OpFail, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    adapter.IsConnected = !adapter.IsConnected;
                }
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 应用 VLAN 设置
        [RelayCommand]
        private async Task ApplyVlanSettingsAsync(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.ApplyVlanSettingsAsync(SelectedVm.Name, adapter);
                if (result.Success) ShowSnackbar(Properties.Resources.Common_Success, Properties.Resources.Msg_Net_VlanApplied, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                else ShowSnackbar(Properties.Resources.Common_Failed, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 应用 QoS 设置
        [RelayCommand]
        private async Task ApplyQosSettingsAsync(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmNetworkService.ApplyBandwidthSettingsAsync(SelectedVm.Name, adapter);
                if (result.Success) ShowSnackbar(Properties.Resources.Common_Success, Properties.Resources.Msg_Net_QosApplied, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                else ShowSnackbar(Properties.Resources.Common_Failed, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 应用安全与监控设置
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
                    ShowSnackbar(Properties.Resources.Common_Failed, string.Format(Properties.Resources.Error_Net_Security, secResult.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    return;
                }

                var offloadResult = await VmNetworkService.ApplyOffloadSettingsAsync(SelectedVm.Name, adapter);
                if (!offloadResult.Success)
                {
                    ShowSnackbar(Properties.Resources.Common_Failed, string.Format(Properties.Resources.Error_Net_Offload, offloadResult.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    return;
                }

                ShowSnackbar(Properties.Resources.Common_Success, Properties.Resources.Msg_Common_Applied, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 切换硬件加速设置
        [RelayCommand]
        private async Task ToggleOffloadSettingAsync(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            var result = await VmNetworkService.ApplyOffloadSettingsAsync(SelectedVm.Name, adapter);
            if (!result.Success)
            {
                ShowSnackbar(Properties.Resources.Error_Net_ApplyFail, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                await RevertAdaptersFromBackendAsync();   // 失败回滚开关，避免 UI 显示与后端不一致
            }
        }

        // 切换安全防护设置
        [RelayCommand]
        private async Task ToggleSecuritySettingAsync(VmNetworkAdapter adapter)
        {
            if (SelectedVm == null || adapter == null) return;
            var result = await VmNetworkService.ApplySecuritySettingsAsync(SelectedVm.Name, adapter);
            if (!result.Success)
            {
                ShowSnackbar(Properties.Resources.Error_Net_SecurityFail, result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                await RevertAdaptersFromBackendAsync();   // 失败回滚开关，避免 UI 显示与后端不一致
            }
        }



    }
}
