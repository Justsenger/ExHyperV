using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Properties;
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

            var createConfirmed = await DialogManager.ShowContentDialogAsync(ExHyperV.Properties.Resources.Title_AddVirtualSwitch, addSwitchView);

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
                    await DialogManager.ShowAlertAsync(ExHyperV.Properties.Resources.Error_CreationFailed, ex.Message);
                }
                finally
                {
                    IsBusy = false;
                }
            }
            else
            {
                await DialogManager.ShowAlertAsync(ExHyperV.Properties.Resources.Validation_InputInvalid, addSwitchVm.ErrorMessage ?? Resources.Error_Unknown);
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
                await DialogManager.ShowAlertAsync(ExHyperV.Properties.Resources.Error_DeletionFailed, ex.Message);
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
                    ErrorMessage = ExHyperV.Properties.Resources.Info_NoSwitchesFound;
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
                ErrorMessage = string.Format(Properties.Resources.Error_LoadNetworkInfoFailed, ex.Message);
                await DialogManager.ShowAlertAsync(Resources.error, ErrorMessage);
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
                    await DialogManager.ShowAlertAsync(ExHyperV.Properties.Resources.Error_ConfigurationConflict, string.Format(Properties.Resources.Error_OnlyOneNatNetworkAllowed, otherNatSwitch.SwitchName));
                    await changedSwitch.RevertTo(originalSwitchInfo);
                    return;
                }
            }

            if ((changedSwitch.SelectedNetworkMode == "Bridge" || changedSwitch.SelectedNetworkMode == "NAT") && !string.IsNullOrEmpty(changedSwitch.SelectedUpstreamAdapter))
            {
                var conflictingSwitch = Switches.FirstOrDefault(s => s.SwitchId != changedSwitch.SwitchId && !string.IsNullOrEmpty(s.SelectedUpstreamAdapter) && s.SelectedUpstreamAdapter == changedSwitch.SelectedUpstreamAdapter);
                if (conflictingSwitch != null)
                {
                    await DialogManager.ShowAlertAsync(ExHyperV.Properties.Resources.Error_ConfigurationConflict, string.Format(Properties.Resources.Error_PhysicalAdapterInUse, changedSwitch.SelectedUpstreamAdapter, conflictingSwitch.SwitchName));
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
                await DialogManager.ShowAlertAsync(ExHyperV.Properties.Resources.UpdateFailed, string.Format(Properties.Resources.Error_UpdateSwitchConfigFailed, changedSwitch.SwitchName, ex.InnerException?.Message ?? ex.Message));
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