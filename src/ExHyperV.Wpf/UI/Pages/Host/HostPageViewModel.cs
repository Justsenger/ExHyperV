using ExHyperV.UI.Shared;
using ExHyperV.UI.Shared.Interaction;
using ExHyperV.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using System.Collections.ObjectModel;
using Wpf.Ui.Controls;

namespace ExHyperV.UI.Pages.Host
{
    public record SchedulerMode(string Name, HyperVSchedulerType Type);

    public partial class HostPageViewModel : PageViewModelBase
    {

        private bool _isInitialized = false;
        private bool _isChangingHyperV;
        private bool _hyperVRequiresRestart;
        private RestartReason[] _hyperVRestartReasons = [];
        private bool? _hyperVPendingEnabled;
        private int _hyperVStatusVersion;

        public ConsolePreferencesViewModel Preferences { get; } = new();


        public CheckStatusViewModel SystemStatus { get; } = new("");
        public CheckStatusViewModel CpuStatus { get; } = new("");
        public CheckStatusViewModel HyperVStatus { get; } = new("");
        public CheckStatusViewModel VersionStatus { get; } = new("");
        public CheckStatusViewModel IommuStatus { get; } = new("");

        // 待重启时按已确认的配置目标提供反向操作，图标和详情仍表示实际运行状态。
        public bool IsHyperVEnableVisible => (_hyperVPendingEnabled ?? HyperVStatus.IsSuccess) != true;
        public bool IsHyperVDisableVisible => !IsHyperVEnableVisible;

        // IOMMU 在 ARM 上叫 SMMU（System MMU），按架构显示正确名称；检测逻辑（DeviceGuard DMA 保护）跨架构通用。
        public string IommuLabel =>
            System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? Properties.Resources.Menu_Iommu_Smmu
                : Properties.Resources.Menu_Iommu;

        [ObservableProperty] private bool _isGpuStrategyEnabled;
        [ObservableProperty] private bool _isGpuStrategyToggleEnabled = false;
        [ObservableProperty] private bool _isNativeNvmeEnabled;
        [ObservableProperty] private bool _isNativeNvmeToggleEnabled = false;
        [ObservableProperty] private bool _isNativeNvmeSupported;
        [ObservableProperty] private bool _isOpenHclFirmwareFileEnabled;
        [ObservableProperty] private bool _isVolumeAutoMountEnabled;
        [ObservableProperty] private bool _isVolumeAutoMountToggleEnabled;
        [ObservableProperty] private bool _isServerSystem;
        [ObservableProperty] private bool _isSystemSwitchEnabled = false;
        [ObservableProperty] private string _systemSwitchStatus = string.Empty;

        [ObservableProperty] private bool _isNumaSpanningEnabled;
        [ObservableProperty] private bool _isNumaSpanningToggleEnabled;
        [ObservableProperty] private bool _isEnhancedSessionModeEnabled;
        [ObservableProperty] private bool _isEnhancedSessionModeToggleEnabled;
        [ObservableProperty] private string _defaultVirtualMachinePath = string.Empty;
        [ObservableProperty] private string _defaultVirtualHardDiskPath = string.Empty;
        [ObservableProperty] private bool _areDefaultPathsEnabled;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ApplyDynamicMacRangeCommand))]
        private string _minimumDynamicMacAddress = string.Empty;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ApplyDynamicMacRangeCommand))]
        private string _maximumDynamicMacAddress = string.Empty;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ApplyDynamicMacRangeCommand))]
        private bool _isDynamicMacRangeEnabled;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(ApplyDynamicMacRangeCommand))]
        private bool _isApplyingDynamicMacRange;
        [ObservableProperty] private HyperVSchedulerType _configuredSchedulerType = HyperVSchedulerType.Unknown;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SchedulerStatusText))]
        private HyperVSchedulerType _runningSchedulerType = HyperVSchedulerType.Unknown;
        [ObservableProperty] private bool _isSchedulerSelectionEnabled;
        private HyperVSchedulerType _confirmedConfiguredSchedulerType = HyperVSchedulerType.Unknown;
        [ObservableProperty] private Guid? _currentPowerPlanId;

        public string SchedulerStatusText
        {
            get
            {
                string current = string.Format(Properties.Resources.Scheduler_RunningFormat, GetSchedulerName(RunningSchedulerType));
                if (_confirmedConfiguredSchedulerType == HyperVSchedulerType.Unknown)
                    return current + Environment.NewLine + Properties.Resources.Scheduler_ConfigurationUnknown;
                return RunningSchedulerType != HyperVSchedulerType.Unknown && RunningSchedulerType != _confirmedConfiguredSchedulerType
                    ? current + Environment.NewLine + string.Format(Properties.Resources.Scheduler_NextBootFormat, GetSchedulerName(_confirmedConfiguredSchedulerType))
                    : current;
            }
        }

        private static string GetSchedulerName(HyperVSchedulerType type) => type switch
        {
            HyperVSchedulerType.Classic => Properties.Resources.Scheduler_Classic,
            HyperVSchedulerType.Core => Properties.Resources.Scheduler_Core,
            HyperVSchedulerType.Root => Properties.Resources.Scheduler_Root,
            _ => Properties.Resources.Common_Unknown,
        };

        public ObservableCollection<SchedulerMode> SchedulerModes { get; } = new()
        {
            new SchedulerMode(Properties.Resources.Scheduler_Classic, HyperVSchedulerType.Classic),
            new SchedulerMode(Properties.Resources.Scheduler_Core, HyperVSchedulerType.Core),
            new SchedulerMode(Properties.Resources.Scheduler_Root, HyperVSchedulerType.Root)
        };

        public ObservableCollection<HostPowerPlan> PowerPlans { get; } = new();


        public HostPageViewModel() => LoadInitialStatusAsync().SafeFireAndForget();

        private async Task LoadInitialStatusAsync()
        {
            await Task.WhenAll(CheckSystemInfoAsync(), CheckCpuInfoAsync(), CheckHyperVInfoAsync(), CheckServerInfoAsync(), CheckIommuAsync());
            await InitializeVersionPolicyAsync();
            _isInitialized = true;
            IsSchedulerSelectionEnabled = true;
        }

        private async Task CheckSystemInfoAsync() => await Task.Run(() =>
        {
            int buildNumber = Environment.OSVersion.Version.Build;
            VersionStatus.IsSuccess = HyperVHostService.IsGpuPartitioningVersionSupported(buildNumber);
            VersionStatus.StatusText = buildNumber.ToString();
            VersionStatus.IsChecking = false;
        });

        private async Task CheckCpuInfoAsync()
        {
            CpuStatus.IsSuccess = await Task.Run(() => HyperVHostService.IsVirtualizationEnabled());
            CpuStatus.IsChecking = false;
        }

        private async Task CheckHyperVInfoAsync()
        {
            if (_hyperVRequiresRestart) return;
            int version = ++_hyperVStatusVersion;
            var result = await HyperVHostService.GetHyperVDiagnosticsAsync();
            // 初始加载可能晚于用户提交的启停请求完成，不能覆盖其结果或待重启提示。
            if (version != _hyperVStatusVersion || _hyperVRequiresRestart) return;
            HyperVStatus.IsSuccess = result.IsReady;
            HyperVStatus.StatusText = result.StatusText;
            HyperVStatus.IsChecking = false;
            NotifyHyperVCommands();
        }

        private async Task CheckIommuAsync()
        {
            IommuStatus.IsSuccess = await Task.Run(() => HyperVHostService.IsIommuEnabled());
            IommuStatus.IsChecking = false;
        }

        private async Task CheckServerInfoAsync()
        {
            SystemStatus.IsSuccess = await Task.Run(() => HostProductTypeService.IsServerSystem());
            SystemStatus.IsChecking = false;
        }

        private async Task InitializeVersionPolicyAsync()
        {
            var gpuStrategy = await Task.Run(() => HyperVGpuPolicyService.GetGpuStrategyEnabled());
            IsGpuStrategyEnabled = gpuStrategy.HasData && gpuStrategy.Data;
            IsGpuStrategyToggleEnabled = gpuStrategy.HasData;
            if (!gpuStrategy.Success) ShowError(gpuStrategy.Error);
            IsNativeNvmeSupported = HostNvmeService.IsNativeNvmeSupported();
            var nativeNvme = await Task.Run(() => HostNvmeService.IsNativeNvmeEnabled());
            IsNativeNvmeEnabled = nativeNvme.HasData && nativeNvme.Data;
            IsNativeNvmeToggleEnabled = IsNativeNvmeSupported && nativeNvme.HasData;
            if (!nativeNvme.Success) ShowError(nativeNvme.Error);
            IsOpenHclFirmwareFileEnabled = await Task.Run(() => HostOpenHclService.IsFirmwareLoadFromFileEnabled());
            bool canSwitch = InitializeProductType();
            await LoadAdvancedConfigAsync();
            // 当前放行全部 SKU；挂起的替换无法取消或覆盖，重启生效前不可再切。
            IsSystemSwitchEnabled = canSwitch;
        }

        private async Task LoadAdvancedConfigAsync()
        {
            var autoMount = await Task.Run(HostVolumeAutoMountService.GetState);
            IsVolumeAutoMountEnabled = autoMount.HasData && autoMount.Data;
            IsVolumeAutoMountToggleEnabled = autoMount.HasData;
            if (!autoMount.Success) ShowError(autoMount.Error);

            try
            {
                var numaTask = HyperVNumaService.GetNumaSpanningEnabledAsync();
                var hostSettingsTask = HyperVHostSettingsService.GetAsync();
                var powerPlansTask = Task.Run(() =>
                    (Plans: HostPowerPlanService.GetPowerPlans(), ActiveId: HostPowerPlanService.GetActivePowerPlanId()));
                await RefreshSchedulerAsync();
                var numa = await numaTask;
                HyperVHostSettings? hostSettings = await hostSettingsTask;
                var powerPlans = await powerPlansTask;
                IsNumaSpanningEnabled = numa.HasData && numa.Data;
                IsNumaSpanningToggleEnabled = HyperVStatus.IsSuccess == true && numa.HasData;
                if (!numa.Success) ShowError(numa.Error);
                IsEnhancedSessionModeEnabled = hostSettings?.EnhancedSessionModeEnabled ?? false;
                IsEnhancedSessionModeToggleEnabled = HyperVStatus.IsSuccess == true && hostSettings != null;
                DefaultVirtualMachinePath = hostSettings?.DefaultVirtualMachinePath ?? string.Empty;
                DefaultVirtualHardDiskPath = hostSettings?.DefaultVirtualHardDiskPath ?? string.Empty;
                AreDefaultPathsEnabled = HyperVStatus.IsSuccess == true && hostSettings != null;
                MinimumDynamicMacAddress = FormatDynamicMacAddress(hostSettings?.MinimumMacAddress);
                MaximumDynamicMacAddress = FormatDynamicMacAddress(hostSettings?.MaximumMacAddress);
                IsDynamicMacRangeEnabled = HyperVStatus.IsSuccess == true && hostSettings != null;
                PowerPlans.Clear();
                foreach (HostPowerPlan plan in powerPlans.Plans)
                    PowerPlans.Add(plan);
                CurrentPowerPlanId = powerPlans.ActiveId;
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }


        partial void OnIsGpuStrategyEnabledChanged(bool value)
        {
            if (!_isInitialized || !IsGpuStrategyToggleEnabled) return;
            var result = value ? HyperVGpuPolicyService.AllowUnsupportedGpuAssignment() : HyperVGpuPolicyService.ResetGpuAssignmentPolicy();
            if (!result.Success)
            {
                ShowError(result.Error);
                _isInitialized = false;
                try { IsGpuStrategyEnabled = !value; }
                finally { _isInitialized = true; }
            }
        }

        partial void OnIsNativeNvmeEnabledChanged(bool value)
        {
            if (!_isInitialized || !IsNativeNvmeToggleEnabled) return;
            var result = value ? HostNvmeService.EnableNativeNvme() : HostNvmeService.DisableNativeNvme();
            if (!result.Success)
            {
                ShowError(result.Error);
                _isInitialized = false;
                try { IsNativeNvmeEnabled = !value; }
                finally { _isInitialized = true; }
                return;
            }
            ShowRestartPrompt(Properties.Resources.Msg_Host_NativeNvmeChanged);
        }

        partial void OnIsOpenHclFirmwareFileEnabledChanged(bool value)
        {
            if (!_isInitialized) return;

            var result = HostOpenHclService.SetFirmwareLoadFromFileEnabled(value);
            if (result.Success) return;

            ShowError(string.Format(Properties.Resources.Error_Host_OpenHclRegistryChangeFailed, result.Error));
            _isInitialized = false;
            IsOpenHclFirmwareFileEnabled = !value;
            _isInitialized = true;
        }

        partial void OnIsVolumeAutoMountEnabledChanged(bool value)
        {
            if (!_isInitialized || !IsVolumeAutoMountToggleEnabled) return;
            ApplyVolumeAutoMountStateAsync(value).SafeFireAndForget();
        }

        private async Task ApplyVolumeAutoMountStateAsync(bool enabled)
        {
            IsVolumeAutoMountToggleEnabled = false;
            var result = await HostVolumeAutoMountService.SetEnabledAsync(enabled);
            var actual = HostVolumeAutoMountService.GetState();

            _isInitialized = false;
            IsVolumeAutoMountEnabled = actual.HasData ? actual.Data : !enabled;
            IsVolumeAutoMountToggleEnabled = actual.HasData;
            _isInitialized = true;

            if (!result.Success)
                ShowError(string.Format(Properties.Resources.Error_Host_AutoMountChangeFailed, result.Error));
            if (!actual.Success) ShowError(actual.Error);
        }

        partial void OnIsNumaSpanningEnabledChanged(bool value)
        {
            if (!_isInitialized || !IsNumaSpanningToggleEnabled) return;
            _ = Task.Run(async () =>
            {
                var result = await HyperVNumaService.SetNumaSpanningEnabledAsync(value);
                if (!result.Success)
                {
                    ShowError(result.Error);
                    WpfApplication.Current.Dispatcher.Invoke(() =>
                    {
                        _isInitialized = false;
                        IsNumaSpanningEnabled = !value;
                        _isInitialized = true;
                    });
                }
            });
        }

        partial void OnIsEnhancedSessionModeEnabledChanged(bool value)
        {
            if (!_isInitialized || !IsEnhancedSessionModeToggleEnabled) return;
            _ = Task.Run(async () =>
            {
                var result = await HyperVHostSettingsService.SetEnhancedSessionModeEnabledAsync(value);
                if (!result.Success)
                {
                    ShowError(result.Error);
                    WpfApplication.Current.Dispatcher.Invoke(() =>
                    {
                        _isInitialized = false;
                        IsEnhancedSessionModeEnabled = !value;
                        _isInitialized = true;
                    });
                }
            });
        }

        partial void OnConfiguredSchedulerTypeChanged(HyperVSchedulerType value)
        {
            if (!_isInitialized || IsApplySuppressed || !IsSchedulerSelectionEnabled) return;
            if (value is not (HyperVSchedulerType.Classic or HyperVSchedulerType.Core or HyperVSchedulerType.Root)) return;
            ApplySchedulerTypeAsync(value).SafeFireAndForget();
        }

        private async Task ApplySchedulerTypeAsync(HyperVSchedulerType value)
        {
            IsSchedulerSelectionEnabled = false;
            try
            {
                var result = await HyperVSchedulerService.SetSchedulerTypeAsync(value);
                await RefreshSchedulerAsync();
                if (result.Success)
                    ShowRestartPrompt(Properties.Resources.Msg_Host_SchedulerChanged);
                else
                    ShowError(string.IsNullOrWhiteSpace(result.Error) ? Properties.Resources.Error_Host_SchedulerFail : result.Error);
            }
            catch (Exception error)
            {
                ShowError(error.Message);
                // 无法读回时不把刚才的选择当成已写入的配置。
                SetSchedulerState(RunningSchedulerType, HyperVSchedulerType.Unknown);
            }
            finally { IsSchedulerSelectionEnabled = true; }
        }

        private async Task RefreshSchedulerAsync()
        {
            var state = await Task.Run(() => (Running: HyperVSchedulerService.GetSchedulerType(),
                Configured: HyperVSchedulerService.GetConfiguredSchedulerType()));
            SetSchedulerState(state.Running, state.Configured);
        }

        private void SetSchedulerState(HyperVSchedulerType running, HyperVSchedulerType configured)
        {
            using (SuppressApply())
            {
                _confirmedConfiguredSchedulerType = configured;
                RunningSchedulerType = running;
                ConfiguredSchedulerType = configured;
                OnPropertyChanged(nameof(SchedulerStatusText));
            }
        }

        partial void OnCurrentPowerPlanIdChanged(Guid? value)
        {
            if (!_isInitialized || !value.HasValue) return;
            _ = Task.Run(() =>
            {
                try
                {
                    HostPowerPlanService.SetActivePowerPlan(value.Value);
                }
                catch (Exception ex)
                {
                    ShowError(string.Format(Properties.Resources.Error_Host_PowerPlanFail, ex.Message));
                    Guid? actual = HostPowerPlanService.GetActivePowerPlanId();
                    WpfApplication.Current.Dispatcher.Invoke(() =>
                    {
                        _isInitialized = false;
                        CurrentPowerPlanId = actual;
                        _isInitialized = true;
                    });
                }
            });
        }

        partial void OnIsServerSystemChanged(bool value)
        {
            if (!_isInitialized) return;
            SwitchSystemVersion(value);
        }

        [RelayCommand]
        private async Task BrowseDefaultVirtualMachinePathAsync()
        {
            string? picked = Dialogs.PickFolder(
                Properties.Resources.HostPage_SelectDefaultVirtualMachinePath,
                string.IsNullOrWhiteSpace(DefaultVirtualMachinePath) ? null : DefaultVirtualMachinePath);
            if (picked == null || string.Equals(picked, DefaultVirtualMachinePath, StringComparison.OrdinalIgnoreCase)) return;

            var result = await HyperVHostSettingsService.SetDefaultVirtualMachinePathAsync(picked);
            if (result.Success)
                DefaultVirtualMachinePath = picked;
            else
                ShowError(result.Error);
        }

        [RelayCommand]
        private async Task BrowseDefaultVirtualHardDiskPathAsync()
        {
            string? picked = Dialogs.PickFolder(
                Properties.Resources.HostPage_SelectDefaultVirtualHardDiskPath,
                string.IsNullOrWhiteSpace(DefaultVirtualHardDiskPath) ? null : DefaultVirtualHardDiskPath);
            if (picked == null || string.Equals(picked, DefaultVirtualHardDiskPath, StringComparison.OrdinalIgnoreCase)) return;

            var result = await HyperVHostSettingsService.SetDefaultVirtualHardDiskPathAsync(picked);
            if (result.Success)
                DefaultVirtualHardDiskPath = picked;
            else
                ShowError(result.Error);
        }

        private bool CanApplyDynamicMacRange() => IsDynamicMacRangeEnabled && !IsApplyingDynamicMacRange;

        [RelayCommand(CanExecute = nameof(CanApplyDynamicMacRange))]
        private async Task ApplyDynamicMacRangeAsync()
        {
            var (minimum, maximum, error) = HyperVHostSettingsService.ValidateDynamicMacAddressRange(
                MinimumDynamicMacAddress, MaximumDynamicMacAddress);
            if (error != null)
            {
                ShowError(error);
                return;
            }

            IsApplyingDynamicMacRange = true;
            try
            {
                var result = await HyperVHostSettingsService.SetDynamicMacAddressRangeAsync(minimum!, maximum!);
                if (!result.Success)
                {
                    ShowError(result.Error);
                    return;
                }

                MinimumDynamicMacAddress = FormatDynamicMacAddress(minimum);
                MaximumDynamicMacAddress = FormatDynamicMacAddress(maximum);
                ShowSuccess(Properties.Resources.Msg_Host_DynamicMacRangeApplied);
            }
            finally
            {
                IsApplyingDynamicMacRange = false;
            }
        }

        private static string FormatDynamicMacAddress(string? value)
        {
            string? normalized = HyperVHostSettingsService.NormalizeDynamicMacAddress(value);
            return normalized == null
                ? value ?? string.Empty
                : string.Join("-", Enumerable.Range(0, 6).Select(i => normalized.Substring(i * 2, 2)));
        }


        private bool CanChangeHyperV() => !_isChangingHyperV;

        [RelayCommand(CanExecute = nameof(CanChangeHyperV))]
        private Task DisableHyperVAsync() => ChangeHyperVAsync(false);

        [RelayCommand(CanExecute = nameof(CanChangeHyperV))]
        private Task EnableHyperVAsync() => ChangeHyperVAsync(true);

        private async Task ChangeHyperVAsync(bool enabled)
        {
            if (!CanChangeHyperV()) return;
            _isChangingHyperV = true;
            ++_hyperVStatusVersion;
            NotifyHyperVCommands();
            HyperVStatus.IsChecking = true;
            ShowTip(enabled ? Properties.Resources.Msg_Host_EnableHyperV : Properties.Resources.HostPageViewModel_DisablingHyperV);
            try
            {
                var result = await HyperVHostService.SetHyperVEnabledAsync(enabled);
                if (!result.Success)
                {
                    bool launchConfirmed = result.Steps.Any(step => step.Name == "SetLaunchType"
                        && step.State == OperationStepState.Succeeded);
                    if (launchConfirmed && !result.RestartReasons.Contains(RestartReason.BootConfiguration))
                    {
                        // 新启动配置已经读回确认；后续运行检查失败不能继续提示旧启动目标的重启需求。
                        _hyperVRestartReasons = _hyperVRestartReasons.Where(reason => reason != RestartReason.BootConfiguration).ToArray();
                        _hyperVRequiresRestart = _hyperVRestartReasons.Length != 0;
                    }
                    _hyperVRequiresRestart |= result.RequiresRestart;
                    _hyperVRestartReasons = _hyperVRestartReasons.Concat(result.RestartReasons).Distinct().ToArray();
                    // 写入或读回之后的失败都可能使旧目标失效，包括配置已改但运行检查未通过。
                    if (result.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.Conflict
                        || result.Steps.Any(step => step.Name == "SetLaunchType" && step.State != OperationStepState.NotStarted))
                        _hyperVPendingEnabled = null;
                    string error = string.IsNullOrWhiteSpace(result.Error)
                        ? enabled ? Properties.Resources.Error_Host_EnableFail : Properties.Resources.HostPageViewModel_DisableFailed
                        : result.Error;
                    if (_hyperVRequiresRestart)
                        error += Environment.NewLine + HyperVRestartMessage();
                    HyperVStatus.IsSuccess = false;
                    HyperVStatus.StatusText = error;
                    // 有重启要求也不能把失败包装成成功；保留失败原因和已发生的配置变更提示。
                    ShowError(error);
                    return;
                }
                // 反向修改启动项可以消除该项的重启需求，但不能完成此前组件登记或功能变更。
                _hyperVRestartReasons = _hyperVRestartReasons.Where(reason => reason != RestartReason.BootConfiguration)
                    .Concat(result.RestartReasons).Distinct().ToArray();
                _hyperVRequiresRestart = result.RequiresRestart || _hyperVRestartReasons.Length != 0;
                _hyperVPendingEnabled = _hyperVRequiresRestart ? enabled : null;
                if (_hyperVRequiresRestart)
                {
                    string message = enabled ? Properties.Resources.Msg_Host_EnableSuccess : Properties.Resources.HostPageViewModel_DisableSuccess;
                    string reasons = HyperVRestartReasonsText();
                    if (reasons.Length != 0) message += Environment.NewLine + reasons;
                    HyperVStatus.IsSuccess = null;
                    HyperVStatus.StatusText = message;
                    ShowRestartPrompt(message);
                    return;
                }

                await CheckHyperVInfoAsync();
                if (enabled && HyperVStatus.IsSuccess != true)
                    ShowError(HyperVStatus.StatusText);
                else
                    ShowSuccess(Properties.Resources.Msg_Common_Applied);
            }
            catch (Exception error)
            {
                _hyperVPendingEnabled = null;
                string message = error.Message;
                if (_hyperVRequiresRestart)
                    message += Environment.NewLine + HyperVRestartMessage();
                HyperVStatus.IsSuccess = false;
                HyperVStatus.StatusText = message;
                ShowError(message);
            }
            finally
            {
                _isChangingHyperV = false;
                HyperVStatus.IsChecking = false;
                NotifyHyperVCommands();
            }
        }

        private string HyperVRestartMessage()
        {
            string reasons = HyperVRestartReasonsText();
            return Properties.Resources.Status_Msg_RestartRequired
                + (reasons.Length == 0 ? string.Empty : Environment.NewLine + reasons);
        }

        private string HyperVRestartReasonsText() => string.Join(Environment.NewLine,
            _hyperVRestartReasons.Select(reason => reason switch
            {
                RestartReason.PackageRegistration => Properties.Resources.Host_HyperVRestartPackage,
                RestartReason.WindowsFeatureChange => Properties.Resources.Host_HyperVRestartFeature,
                RestartReason.BootConfiguration => Properties.Resources.Host_HyperVRestartBoot,
                _ => string.Empty
            }).Where(text => text.Length != 0));

        private void NotifyHyperVCommands()
        {
            OnPropertyChanged(nameof(IsHyperVEnableVisible));
            OnPropertyChanged(nameof(IsHyperVDisableVisible));
            EnableHyperVCommand.NotifyCanExecuteChanged();
            DisableHyperVCommand.NotifyCanExecuteChanged();
        }


        private bool InitializeProductType()
        {
            // 有挂起的切换任务时，开关显示"重启后的目标状态"而非当前 ProductType——
            // 灰在目标位置传达"操作已被接受、等重启"；方向未知(外部替换)则保守停在当前值。
            var pending = HostProductTypeService.GetPendingSwitch();
            IsServerSystem = pending.HasData && pending.Data.IsConfirmed ? pending.Data.TargetType switch
            {
                "ServerNT" => true,
                "WinNT" => false,
                _ => HostProductTypeService.IsServerSystem(),
            } : HostProductTypeService.IsServerSystem();
            SystemSwitchStatus = !pending.Success ? pending.Error
                : pending.HasData ? (pending.Data.IsConfirmed
                    ? Properties.Resources.Status_Msg_RestartRequired
                    : Properties.Resources.Host_ProductTypePendingUnconfirmed)
                    + Environment.NewLine + string.Join(Environment.NewLine, pending.Data.RecoveryPaths)
                : string.Empty;
            if (!pending.Success) ShowError(pending.Error);
            return pending.Success && HostProductTypeService.CanApplySwitch(pending.HasData ? pending.Data : null);
        }

        private async void SwitchSystemVersion(bool toServer)
        {
            try
            {
                IsSystemSwitchEnabled = false;

                var pending = HostProductTypeService.GetPendingSwitch();
                if (!pending.Success)
                {
                    ShowError(pending.Error);
                    ShowPendingState(null, toServer);
                    return;
                }
                if (!HostProductTypeService.CanApplySwitch(pending.HasData ? pending.Data : null))
                {
                    if (pending.Data!.IsConfirmed)
                        ShowTip(Properties.Resources.Status_Msg_RestartRequired);
                    else
                        await Dialogs.ShowAlertAsync(Properties.Resources.Error_Common_OpFail,
                            Properties.Resources.Host_ProductTypePendingUnconfirmed + Environment.NewLine + string.Join(Environment.NewLine, pending.Data.RecoveryPaths));
                    SystemSwitchStatus = (pending.Data.IsConfirmed ? Properties.Resources.Status_Msg_RestartRequired : Properties.Resources.Host_ProductTypePendingUnconfirmed)
                        + Environment.NewLine + string.Join(Environment.NewLine, pending.Data.RecoveryPaths);
                    ShowPendingState(pending.Data.IsConfirmed ? pending.Data.TargetType : null, toServer);
                    return;   // 挂起任务无法取消或覆盖，重启前保持禁用
                }

                // 危险操作：仅「切到服务器版本」前二次确认（红色弹窗，同「彻底删除虚拟机」）。取消则回拨开关、重新启用。
                // 切回客户端（工作站）时直接执行。
                if (toServer)
                {
                    var confirm = new Wpf.Ui.Controls.MessageBox
                    {
                        Title = Properties.Resources.SwitchServer_ConfirmTitle,
                        Content = new System.Windows.Controls.TextBlock
                        {
                            Text = Properties.Resources.SwitchServer_ConfirmMsg,
                            TextWrapping = System.Windows.TextWrapping.Wrap,
                        },
                        PrimaryButtonText = Properties.Resources.SwitchServer_ConfirmBtn,
                        PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,
                        CloseButtonText = Properties.Resources.Button_Cancel,
                    };
                    ExHyperV.UI.Shared.Interaction.Dialogs.ForceDangerButtonWhiteForeground(confirm);   // Danger 主按钮亮色主题下红底黑字，强制刷白
                    if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
                    {
                        RefreshProductType();
                        return;
                    }
                }

                var result = await Task.Run(() => HostProductTypeService.ApplySwitch(toServer));
                if (result.Success && result.State == HostProductTypeSwitchState.SubmittedAwaitingRestart)
                {
                    SystemSwitchStatus = Properties.Resources.Status_Msg_RestartRequired + Environment.NewLine + string.Join(Environment.NewLine, result.RecoveryPaths);
                    ShowRestartPrompt(Properties.Resources.Status_Msg_RestartNow);
                    return;   // 开关停在目标位置并保持禁用（待重启态）
                }
                if (result.Success && result.State == HostProductTypeSwitchState.AlreadyPending)
                {
                    SystemSwitchStatus = Properties.Resources.Status_Msg_RestartRequired + Environment.NewLine + string.Join(Environment.NewLine, result.RecoveryPaths);
                    ShowTip(Properties.Resources.Status_Msg_RestartRequired);
                    ShowPendingState(result.TargetType, toServer);
                    return;
                }

                var details = new List<string> { result.Response.Error };
                details.AddRange(result.RecoveryPaths);
                if (result.MountedHiveKey != null) details.Add("HKLM\\" + result.MountedHiveKey);
                if (result.RequiresRestart) details.Add(Properties.Resources.Status_Msg_RestartRequired);
                SystemSwitchStatus = string.Join(Environment.NewLine, details);
                await Dialogs.ShowAlertAsync(Properties.Resources.Error_Common_OpFail, SystemSwitchStatus);
                if (result.RequiresRestart || result.State == HostProductTypeSwitchState.CleanupRequired)
                    ShowPendingState(result.ReplacementSubmitted ? result.TargetType : null, toServer);
                else
                    RefreshProductType();
            }
            catch (Exception ex)
            {
                // async void：未捕获异常会直接崩溃 UI 线程；兜底上报并回滚开关状态
                ShowError(ex.Message);
                RefreshProductType();
            }
        }

        private void RefreshProductType()
        {
            _isInitialized = false;
            try { IsSystemSwitchEnabled = InitializeProductType(); }
            finally { _isInitialized = true; }
        }

        // 挂起态：开关摆到真实目标位置（方向未知则回滚到拨动前），不触发再次切换、保持禁用
        private void ShowPendingState(string? pendingTarget, bool attempted)
        {
            _isInitialized = false;
            IsServerSystem = pendingTarget switch
            {
                "ServerNT" => true,
                "WinNT" => false,
                _ => !attempted,
            };
            _isInitialized = true;
        }


    }


    public partial class CheckStatusViewModel : ObservableObject
    {
        [ObservableProperty] private bool _isChecking = true;
        [ObservableProperty] private string _statusText = string.Empty;
        [ObservableProperty] private bool? _isSuccess;
        public string IconGlyph => IsSuccess switch { true => "\uEC61", false => "\uEB90", _ => "\uE946" };
        public System.Windows.Media.Brush IconColor => IsSuccess switch
        {
            true => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(255, 0, 138, 23)),
            false => System.Windows.Media.Brushes.Red,
            _ => System.Windows.Media.Brushes.Gray
        };
        public CheckStatusViewModel(string initialText) => _statusText = initialText;
        partial void OnIsSuccessChanged(bool? value) { OnPropertyChanged(nameof(IconGlyph)); OnPropertyChanged(nameof(IconColor)); }
    }
}
