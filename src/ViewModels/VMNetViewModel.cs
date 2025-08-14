using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

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
        private async Task AddNewSwitchAsync()
        {
            var addSwitchVm = new AddSwitchViewModel(Switches, _physicalAdapters);
            var addSwitchView = new AddSwitchView
            {
                DataContext = addSwitchVm
            };

            var createConfirmed = await DialogManager.ShowContentDialogAsync("新增虚拟交换机", addSwitchView);

            if (!createConfirmed)
            {
                return;
            }

            if (addSwitchVm.Validate())
            {
                IsBusy = true;
                try
                {
                    string typeForService = addSwitchVm.SelectedSwitchType;

                    await _networkService.CreateSwitchAsync(
                        addSwitchVm.SwitchName,
                        typeForService,
                        addSwitchVm.SelectedNetworkAdapter
                    );

                    await CoreRefreshLogicAsync();
                }
                catch (System.Exception ex)
                {
                    await DialogManager.ShowAlertAsync("创建失败", ex.Message);
                }
                finally
                {
                    IsBusy = false;
                }
            }
            else
            {
                await DialogManager.ShowAlertAsync("输入无效", addSwitchVm.ErrorMessage ?? "未知错误。");
            }
        }
        [RelayCommand]
        private async Task DeleteSwitchAsync(SwitchViewModel? switchToDelete)
        {
            if (switchToDelete == null || switchToDelete.IsDefaultSwitch)
            {
                return;
            }

            IsBusy = true;
            try
            {
                await _networkService.DeleteSwitchAsync(switchToDelete.SwitchName);
                await CoreRefreshLogicAsync();
            }
            catch (System.Exception ex)
            {
                await DialogManager.ShowAlertAsync("删除失败", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }


        [RelayCommand]
        private async Task LoadNetworkInfoAsync()
        {
            if (IsBusy) return;

            IsBusy = true;
            try
            {
                await CoreRefreshLogicAsync();
            }
            finally
            {
                IsBusy = false;
            }
        }

        private async Task CoreRefreshLogicAsync()
        {
            ErrorMessage = null;
            IsContentVisible = false;
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
                    foreach (var switchInfo in _rawSwitchInfos)
                    {
                        var switchVm = new SwitchViewModel(switchInfo, _networkService, _physicalAdapters, Switches);
                        switchVm.PropertyChanged += OnSwitchViewModelPropertyChanged;
                        Switches.Add(switchVm);
                    }
                    UpdateAllSwitchMenus();
                    IsContentVisible = true;
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"加载网络信息时出错: {ex.Message}";
                await DialogManager.ShowAlertAsync("错误", ErrorMessage);
            }
        }

        private async void OnSwitchViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not SwitchViewModel changedSwitch) return;

            if (changedSwitch.IsReverting || changedSwitch.IsLockedForInteraction) return;

            if (e.PropertyName == nameof(SwitchViewModel.SelectedNetworkMode) ||
                e.PropertyName == nameof(SwitchViewModel.SelectedUpstreamAdapter) ||
                e.PropertyName == nameof(SwitchViewModel.IsHostConnectionAllowed))
            {
                changedSwitch.IsLockedForInteraction = true;
                try
                {
                    await ApplyConfigurationChange(changedSwitch);
                }
                finally
                {
                    changedSwitch.IsLockedForInteraction = false;
                }
            }
        }

        private async Task ApplyConfigurationChange(SwitchViewModel changedSwitch)
        {
            var originalSwitchInfo = _rawSwitchInfos.FirstOrDefault(s => s.Id == changedSwitch.SwitchId);
            if (originalSwitchInfo == null) return;

            if (changedSwitch.SelectedNetworkMode == "NAT")
            {
                var otherNatSwitch = Switches.FirstOrDefault(s => s.SwitchId != changedSwitch.SwitchId && !s.IsDefaultSwitch && s.SelectedNetworkMode == "NAT");
                if (otherNatSwitch != null)
                {
                    await DialogManager.ShowAlertAsync("配置冲突", $"操作失败：系统只允许存在一个NAT网络。\n已有的NAT交换机是: '{otherNatSwitch.SwitchName}'");
                    await changedSwitch.RevertTo(originalSwitchInfo);
                    return;
                }
            }

            if ((changedSwitch.SelectedNetworkMode == "Bridge" || changedSwitch.SelectedNetworkMode == "NAT") && !string.IsNullOrEmpty(changedSwitch.SelectedUpstreamAdapter))
            {
                var conflictingSwitch = Switches.FirstOrDefault(s => s.SwitchId != changedSwitch.SwitchId && !string.IsNullOrEmpty(s.SelectedUpstreamAdapter) && s.SelectedUpstreamAdapter == changedSwitch.SelectedUpstreamAdapter);
                if (conflictingSwitch != null)
                {
                    await DialogManager.ShowAlertAsync("配置冲突", $"操作失败：物理网卡 '{changedSwitch.SelectedUpstreamAdapter}' 已被交换机 '{conflictingSwitch.SwitchName}' 使用。");
                    await changedSwitch.RevertTo(originalSwitchInfo);
                    return;
                }
            }

            if ((changedSwitch.SelectedNetworkMode == "Bridge" || changedSwitch.SelectedNetworkMode == "NAT") && string.IsNullOrEmpty(changedSwitch.SelectedUpstreamAdapter))
            {
                return;
            }

            IsBusy = true;
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
                await DialogManager.ShowAlertAsync("更新失败", $"更新交换机 '{changedSwitch.SwitchName}' 配置失败: {ex.InnerException?.Message ?? ex.Message}");
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
    }
}