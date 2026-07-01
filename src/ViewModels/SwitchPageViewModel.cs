using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Interaction;
using ExHyperV.Views;

namespace ExHyperV.ViewModels
{
    public partial class SwitchPageViewModel : PageViewModelBase
    {
        // ===== 属性 =====

        [ObservableProperty] private bool _isBusy = false;
        [ObservableProperty] private string? _errorMessage;

        public ObservableCollection<SwitchViewModel> Switches { get; } = new();

        private List<string> _physicalAdapters = new();
        private List<string> _bridgeableAdapters = new();
        private List<SwitchInfo> _rawSwitchInfos = new();

        // ===== 构造 =====

        public SwitchPageViewModel()
        {
            LoadNetworkInfoCommand.Execute(null);
        }

        // ===== 命令 =====

        [RelayCommand]
        private async Task AddNewSwitchAsync()
        {
            _bridgeableAdapters = await HyperVSwitchService.GetBridgeableAdaptersAsync();
            var addSwitchVm = new AddSwitchViewModel(Switches, _physicalAdapters, _bridgeableAdapters);
            var addSwitchView = new AddSwitchView
            {
                DataContext = addSwitchVm
            };

            var createConfirmed = await Dialogs.ShowContentDialogAsync(Properties.Resources.Title_AddVirtualSwitch, addSwitchView);

            if (!createConfirmed)
            {
                return;
            }

            if (addSwitchVm.Validate())
            {
                IsBusy = true;
                try
                {
                    var typeForService = addSwitchVm.SelectedSwitchType;

                    await HyperVSwitchService.CreateSwitchAsync(
                        addSwitchVm.SwitchName,
                        typeForService,
                        addSwitchVm.SelectedNetworkAdapter
                    );

                    await CoreRefreshLogicAsync();
                }
                catch (System.Exception ex)
                {
                    await Dialogs.ShowAlertAsync(Properties.Resources.Error_CreationFailed, ex.Message);
                }
                finally
                {
                    IsBusy = false;
                }
            }
            else
            {
                await Dialogs.ShowAlertAsync(Properties.Resources.Validation_InputInvalid, addSwitchVm.ErrorMessage ?? Properties.Resources.Error_Unknown);
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
                await HyperVSwitchService.DeleteSwitchAsync(switchToDelete.SwitchName);
                await CoreRefreshLogicAsync();
            }
            catch (System.Exception ex)
            {
                await Dialogs.ShowAlertAsync(Properties.Resources.Error_DeletionFailed, ex.Message);
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

        // ===== 内部刷新逻辑 =====

        private async Task CoreRefreshLogicAsync()
        {
            ErrorMessage = null;
            // 退订旧 SwitchViewModel 的事件再清空，避免被丢弃的实例仍响应 PropertyChanged 触发误配置
            foreach (var oldVm in Switches)
                oldVm.PropertyChanged -= OnSwitchViewModelPropertyChanged;
            Switches.Clear();

            try
            {
                var (switches, adapters) = await HyperVSwitchService.GetNetworkInfoAsync();
                _rawSwitchInfos = switches;
                _physicalAdapters = adapters;
                _bridgeableAdapters = await HyperVSwitchService.GetBridgeableAdaptersAsync();

                if (!_rawSwitchInfos.Any())
                {
                    ErrorMessage = Properties.Resources.Info_NoSwitchesFound;
                }
                else
                {
                    foreach (var switchInfo in _rawSwitchInfos)
                    {
                        var switchVm = new SwitchViewModel(switchInfo, _physicalAdapters, _bridgeableAdapters);
                        switchVm.PropertyChanged += OnSwitchViewModelPropertyChanged;
                        Switches.Add(switchVm);
                    }
                    UpdateAllSwitchMenus();
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = string.Format(Properties.Resources.Error_LoadNetworkInfoFailed, ex.Message);
                await Dialogs.ShowAlertAsync(Properties.Resources.Error_Title, ErrorMessage);
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

            if (changedSwitch.SelectedNetworkMode == SwitchMode.NAT)
            {
                var otherNatSwitch = Switches.FirstOrDefault(s => s.SwitchId != changedSwitch.SwitchId && !s.IsDefaultSwitch && s.SelectedNetworkMode == SwitchMode.NAT);
                if (otherNatSwitch != null)
                {
                    await Dialogs.ShowAlertAsync(Properties.Resources.Error_ConfigurationConflict, string.Format(Properties.Resources.Error_OnlyOneNatNetworkAllowed, otherNatSwitch.SwitchName));
                    await changedSwitch.RevertTo(originalSwitchInfo);
                    return;
                }
            }

            if ((changedSwitch.SelectedNetworkMode == SwitchMode.Bridge || changedSwitch.SelectedNetworkMode == SwitchMode.NAT) && !string.IsNullOrEmpty(changedSwitch.SelectedUpstreamAdapter))
            {
                var conflictingSwitch = Switches.FirstOrDefault(s => s.SwitchId != changedSwitch.SwitchId && !string.IsNullOrEmpty(s.SelectedUpstreamAdapter) && s.SelectedUpstreamAdapter == changedSwitch.SelectedUpstreamAdapter);
                if (conflictingSwitch != null)
                {
                    await Dialogs.ShowAlertAsync(Properties.Resources.Error_ConfigurationConflict, string.Format(Properties.Resources.Error_PhysicalAdapterInUse, changedSwitch.SelectedUpstreamAdapter, conflictingSwitch.SwitchName));
                    await changedSwitch.RevertTo(originalSwitchInfo);
                    return;
                }
            }

            if ((changedSwitch.SelectedNetworkMode == SwitchMode.Bridge || changedSwitch.SelectedNetworkMode == SwitchMode.NAT) && string.IsNullOrEmpty(changedSwitch.SelectedUpstreamAdapter))
            {
                return;
            }

            IsBusy = true;
            try
            {
                await HyperVSwitchService.UpdateSwitchConfigurationAsync(
                    changedSwitch.SwitchName,
                    changedSwitch.SelectedNetworkMode,
                    changedSwitch.SelectedUpstreamAdapter,
                    changedSwitch.IsHostConnectionAllowed
                );

                await RefreshDataModels();
                UpdateAllSwitchMenus();
            }
            catch (Exception ex)
            {
                await Dialogs.ShowAlertAsync(Properties.Resources.UpdateFailed, string.Format(Properties.Resources.Error_UpdateSwitchConfigFailed, changedSwitch.SwitchName, ex.InnerException?.Message ?? ex.Message));
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
            var (switches, adapters) = await HyperVSwitchService.GetNetworkInfoAsync();
            _rawSwitchInfos = switches;
            _physicalAdapters = adapters;
            _bridgeableAdapters = await HyperVSwitchService.GetBridgeableAdaptersAsync();

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