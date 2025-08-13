using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace ExHyperV.ViewModels
{
    public partial class VMNetViewModel : ObservableObject
    {
        private readonly INetworkService _networkService;

        [ObservableProperty] private bool _isBusy = false;
        [ObservableProperty] private bool _isContentVisible = true;
        [ObservableProperty] private string? _errorMessage;

        public ObservableCollection<SwitchViewModel> Switches { get; } = new();

        private List<PhysicalAdapterInfo> _physicalAdapters = new();
        private List<SwitchInfo> _rawSwitchInfos = new();

        public VMNetViewModel()
        {
            _networkService = new NetworkService();
            LoadNetworkInfoCommand.Execute(null);
        }

        [RelayCommand]
        private async Task LoadNetworkInfoAsync()
        {
            if (IsBusy) return;
            IsBusy = true;
            ErrorMessage = null;
            IsContentVisible = false; // 初次加载时隐藏内容
            Switches.Clear();

            try
            {
                var (switches, adapters) = await _networkService.GetNetworkInfoAsync();
                _rawSwitchInfos = switches;
                _physicalAdapters = adapters;

                if (!_rawSwitchInfos.Any())
                {
                    ErrorMessage = "未找到任何 Hyper-V 虚拟交换机。\n请确保 Hyper-V 功能已安装并正在运行。";
                }
                else
                {
                    var initTasks = new List<Task>();
                    foreach (var switchInfo in _rawSwitchInfos)
                    {
                        var switchVm = new SwitchViewModel(switchInfo, _networkService, _physicalAdapters, Switches);
                        switchVm.PropertyChanged += OnSwitchViewModelPropertyChanged;
                        Switches.Add(switchVm);
                    }
                    UpdateAllSwitchMenus();
                    IsContentVisible = true; // 初次加载结束后显示内容
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"加载网络信息时出错: {ex.Message}";
                MessageBox.Show(ErrorMessage, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async void OnSwitchViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not SwitchViewModel changedSwitch) return;

            if (e.PropertyName == nameof(SwitchViewModel.SelectedNetworkMode) ||
                e.PropertyName == nameof(SwitchViewModel.SelectedUpstreamAdapter) ||
                e.PropertyName == nameof(SwitchViewModel.IsHostConnectionAllowed))
            {
                await Task.Delay(100);
                await ApplyConfigurationChange(changedSwitch);
            }
        }

        private async Task ApplyConfigurationChange(SwitchViewModel changedSwitch)
        {
            var originalSwitchInfo = _rawSwitchInfos.FirstOrDefault(s => s.Id == changedSwitch.SwitchId);
            if (originalSwitchInfo == null) return;

            if ((changedSwitch.SelectedNetworkMode == "Bridge" || changedSwitch.SelectedNetworkMode == "NAT") && string.IsNullOrEmpty(changedSwitch.SelectedUpstreamAdapter))
            {
                return;
            }

            if (changedSwitch.SelectedNetworkMode == "NAT")
            {
                var otherNatSwitch = Switches.FirstOrDefault(s => s.SwitchId != changedSwitch.SwitchId && !s.IsDefaultSwitch && s.SelectedNetworkMode == "NAT");
                if (otherNatSwitch != null)
                {
                    MessageBox.Show($"操作失败：系统只允许存在一个NAT网络。\n已有的NAT交换机是: '{otherNatSwitch.SwitchName}'", "配置冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
                    await RevertSwitchState(changedSwitch, originalSwitchInfo);
                    return;
                }
            }

            if ((changedSwitch.SelectedNetworkMode == "Bridge" || changedSwitch.SelectedNetworkMode == "NAT") && !string.IsNullOrEmpty(changedSwitch.SelectedUpstreamAdapter))
            {
                var conflictingSwitch = Switches.FirstOrDefault(s => s.SwitchId != changedSwitch.SwitchId && !string.IsNullOrEmpty(s.SelectedUpstreamAdapter) && s.SelectedUpstreamAdapter == changedSwitch.SelectedUpstreamAdapter);
                if (conflictingSwitch != null)
                {
                    MessageBox.Show($"操作失败：物理网卡 '{changedSwitch.SelectedUpstreamAdapter}' 已被交换机 '{conflictingSwitch.SwitchName}' 使用。", "配置冲突", MessageBoxButton.OK, MessageBoxImage.Warning);
                    await RevertSwitchState(changedSwitch, originalSwitchInfo);
                    return;
                }
            }

            IsBusy = true;
            // **** 唯一的修正点在这里：移除了对 IsContentVisible 的控制 ****
            try
            {
                await _networkService.UpdateSwitchConfigurationAsync(
                    changedSwitch.SwitchName,
                    changedSwitch.SelectedNetworkMode,
                    changedSwitch.SelectedUpstreamAdapter,
                    changedSwitch.IsHostConnectionAllowed,
                    false
                );

                await RefreshDataModels();
                UpdateAllSwitchMenus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"更新交换机 '{changedSwitch.SwitchName}' 配置失败: {ex.InnerException?.Message ?? ex.Message}", "更新失败", MessageBoxButton.OK, MessageBoxImage.Error);
                await RefreshDataModels();
                UpdateAllSwitchMenus();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task RefreshDataModels()
        {
            var (switches, adapters) = await _networkService.GetNetworkInfoAsync();
            _rawSwitchInfos = switches;
            _physicalAdapters = adapters;

            var updateTasks = new List<Task>();
            foreach (var vm in Switches)
            {
                var latestInfo = _rawSwitchInfos.FirstOrDefault(s => s.Id == vm.SwitchId);
                if (latestInfo != null)
                {
                    updateTasks.Add(vm.RevertTo(latestInfo));
                }
            }
            await Task.WhenAll(updateTasks);
        }

        private void UpdateAllSwitchMenus()
        {
            foreach (var vm in Switches)
            {
                vm.UpdateMenuItems();
            }
        }

        private async Task RevertSwitchState(SwitchViewModel switchVm, SwitchInfo originalInfo)
        {
            switchVm.PropertyChanged -= OnSwitchViewModelPropertyChanged;
            await switchVm.RevertTo(originalInfo);
            switchVm.PropertyChanged += OnSwitchViewModelPropertyChanged;
        }
    }
}