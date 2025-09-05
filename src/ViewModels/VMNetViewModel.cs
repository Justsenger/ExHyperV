using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Properties;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Views;

namespace ExHyperV.ViewModels
{
    public partial class VMNetViewModel : ObservableObject
    {
        private readonly INetworkService _networkService;
        // --- 新增：配置服务和配置数据持有者 ---
        private readonly ConfigurationService _configService;
        private AppConfig _appConfig;

        [ObservableProperty] private bool _isBusy = false;
        [ObservableProperty] private bool _isContentVisible = true;
        [ObservableProperty] private string? _errorMessage;

        public ObservableCollection<SwitchViewModel> Switches { get; } = new();

        private List<PhysicalAdapterInfo> _physicalAdapters = new();
        private List<SwitchInfo> _rawSwitchInfos = new();

        public VMNetViewModel()
        {
            _networkService = new NetworkService();
            // --- 新增：初始化配置服务 ---
            _configService = new ConfigurationService();
            _appConfig = new AppConfig(); // 初始化为空配置

            _ = LoadNetworkInfoAsync();
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

            if (!addSwitchVm.Validate())
            {
                await DialogManager.ShowAlertAsync(ExHyperV.Properties.Resources.Validation_InputInvalid, addSwitchVm.ErrorMessage ?? Resources.Error_Unknown);
                return;
            }

            IsBusy = true;
            try
            {
                await _networkService.CreateSwitchAsync(
                    addSwitchVm.SwitchName,
                    addSwitchVm.SelectedSwitchType,
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

            foreach (var s in Switches)
            {
                s.PropertyChanged -= OnSwitchViewModelPropertyChanged;
                s.RequestSave -= OnRequestSave; // --- 新增：取消订阅保存请求事件 ---
            }
            Switches.Clear();

            try
            {
                // --- 新增：在刷新时加载配置文件 ---
                _appConfig = _configService.LoadConfiguration();

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
                        // --- 修改：查找并传递配置给 SwitchViewModel ---
                        var config = _appConfig.Switches.FirstOrDefault(c => c.Id == switchInfo.Id);
                        if (config == null)
                        {
                            // 如果配置不存在，为这个新交换机创建一个默认配置
                            config = new SwitchConfig { Id = switchInfo.Id };
                            _appConfig.Switches.Add(config);
                        }

                        var switchVm = new SwitchViewModel(switchInfo, config, _networkService, _physicalAdapters, Switches);

                        switchVm.PropertyChanged += OnSwitchViewModelPropertyChanged;
                        switchVm.RequestSave += OnRequestSave; // --- 新增：订阅保存请求事件 ---

                        Switches.Add(switchVm);
                    }
                    UpdateAllSwitchMenus();
                    await SaveConfigurationAsync(); // --- 新增：刷新后保存一次，以清理掉不存在的交换机配置
                    IsContentVisible = true;
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = string.Format(Properties.Resources.Error_LoadNetworkInfoFailed, ex.Message);
                await DialogManager.ShowAlertAsync(Resources.error, ErrorMessage);
            }
        }

        // --- 新增：事件处理器，当子VM请求保存时触发 ---
        private async void OnRequestSave(object? sender, EventArgs e)
        {
            await SaveConfigurationAsync();
        }

        // --- 新增：核心的保存逻辑 ---
        private async Task SaveConfigurationAsync()
        {
            // 从所有 SwitchViewModel 收集最新的配置
            foreach (var vm in Switches)
            {
                var config = _appConfig.Switches.FirstOrDefault(c => c.Id == vm.SwitchId);
                if (config != null)
                {
                    // 更新配置对象
                    vm.UpdateConfig(config);
                }
            }

            // 移除那些已经不存在的交换机的配置
            _appConfig.Switches.RemoveAll(c => !_rawSwitchInfos.Any(s => s.Id == c.Id));

            // 保存到文件
            _configService.SaveConfiguration(_appConfig);

            await Task.CompletedTask;
        }

        private async void OnSwitchViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not SwitchViewModel changedSwitch || changedSwitch.IsReverting || changedSwitch.IsLockedForInteraction)
            {
                return;
            }

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

            if (changedSwitch.IsConnected && !string.IsNullOrEmpty(changedSwitch.SelectedUpstreamAdapter))
            {
                var conflictingSwitch = Switches.FirstOrDefault(s => s.SwitchId != changedSwitch.SwitchId && s.IsConnected && s.SelectedUpstreamAdapter == changedSwitch.SelectedUpstreamAdapter);
                if (conflictingSwitch != null)
                {
                    await DialogManager.ShowAlertAsync(ExHyperV.Properties.Resources.Error_ConfigurationConflict, string.Format(Properties.Resources.Error_PhysicalAdapterInUse, changedSwitch.SelectedUpstreamAdapter, conflictingSwitch.SwitchName));
                    await changedSwitch.RevertTo(originalSwitchInfo);
                    return;
                }
            }

            IsBusy = true;
            try
            {
                await _networkService.UpdateSwitchConfigurationAsync(
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