using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Interaction;
using ExHyperV.Tools;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public record SchedulerMode(string Name, HyperVSchedulerType Type);

    public partial class HostPageViewModel : PageViewModelBase
    {

        private bool _isInitialized = false;


        public CheckStatusViewModel SystemStatus { get; } = new("");
        public CheckStatusViewModel CpuStatus { get; } = new("");
        public CheckStatusViewModel HyperVStatus { get; } = new("");
        public CheckStatusViewModel VersionStatus { get; } = new("");
        public CheckStatusViewModel IommuStatus { get; } = new("");
        public CheckStatusViewModel UsbStatus { get; } = new("");

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

        // 有挂起的版本切换任务（重启前不可再切，开关保持禁用）
        private bool _hasPendingSwitch = false;
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
        [ObservableProperty] private HyperVSchedulerType _currentSchedulerType;
        [ObservableProperty] private Guid? _currentPowerPlanId;

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
            await Task.WhenAll(CheckSystemInfoAsync(), CheckCpuInfoAsync(), CheckHyperVInfoAsync(), CheckServerInfoAsync(), CheckIommuAsync(), CheckUsbInfoAsync());
            await InitializeVersionPolicyAsync();
            _isInitialized = true;
        }

        private async Task CheckSystemInfoAsync() => await Task.Run(() =>
        {
            int buildNumber = Environment.OSVersion.Version.Build;
            string baseVersion = buildNumber.ToString();
            const int MinimumBuild = 17134;
            if (buildNumber >= MinimumBuild)
            {
                VersionStatus.IsSuccess = true;
                VersionStatus.StatusText = baseVersion;
            }
            else
            {
                VersionStatus.IsSuccess = false;
                VersionStatus.StatusText = baseVersion;   // 红叉+“GPU-PV 要求”标题已表意,不再拼“(不支持 GPU-PV)”
            }
            VersionStatus.IsChecking = false;
        });

        private async Task CheckCpuInfoAsync()
        {
            CpuStatus.IsSuccess = await Task.Run(() => HyperVHostService.IsVirtualizationEnabled());
            CpuStatus.IsChecking = false;
        }

        private async Task CheckHyperVInfoAsync()
        {
            var (isReady, _, statusText) = await HyperVHostService.GetHyperVStatusAsync();
            HyperVStatus.IsSuccess = isReady;
            HyperVStatus.StatusText = statusText;
            HyperVStatus.IsChecking = false;
        }

        private async Task CheckIommuAsync()
        {
            IommuStatus.IsSuccess = await Task.Run(() => HyperVHostService.IsIommuEnabled());
            IommuStatus.IsChecking = false;
        }

        private async Task CheckServerInfoAsync()
        {
            SystemStatus.IsSuccess = await Task.Run(() => HyperVHostService.IsServerSystem());
            SystemStatus.IsChecking = false;
        }

        private async Task CheckUsbInfoAsync()
        {
            UsbStatus.IsSuccess = await Task.Run(() => UsbVmbusService.IsUsbipdInstalled());
            UsbStatus.IsChecking = false;
        }

        private async Task InitializeVersionPolicyAsync()
        {
            IsGpuStrategyEnabled = await Task.Run(() => HyperVHostService.GetGpuStrategyEnabled());
            IsNativeNvmeSupported = Environment.OSVersion.Version.Build >= 26100; // WS2025 / Win11 24H2 起才有原生 NVMe
            IsNativeNvmeEnabled = await Task.Run(() => HostNvmeService.IsNativeNvmeEnabled());
            IsOpenHclFirmwareFileEnabled = await Task.Run(() => HostOpenHclService.IsFirmwareLoadFromFileEnabled());
            InitializeProductType();
            await LoadAdvancedConfigAsync();
            IsGpuStrategyToggleEnabled = true;
            IsNativeNvmeToggleEnabled = IsNativeNvmeSupported;   // 不支持的系统(Win10 等)开关置灰而非隐藏
            // 切换服务器版本(黑魔法)仅对特定客户端 SKU 生效；真 Server/家庭版/标准专业版/企业版等不适用，开关置灰。
            // 判定走 EditionID(真实 SKU)而非 ProductType——后者正是黑魔法改的值，用它会致被切的客户端版无法切回。
            // 已有挂起切换任务时同样置灰：挂起的替换无法取消也无法覆盖，重启生效前不可再切。
            IsSystemSwitchEnabled = !_hasPendingSwitch && HyperVHostService.IsServerSwitchApplicable();
        }

        private async Task LoadAdvancedConfigAsync()
        {
            VolumeAutoMountState autoMount = await Task.Run(HostVolumeAutoMountService.GetState);
            IsVolumeAutoMountEnabled = autoMount.Enabled;
            IsVolumeAutoMountToggleEnabled = autoMount.Success;

            try
            {
                var numaTask = HyperVNumaService.GetNumaSpanningEnabledAsync();
                var hostSettingsTask = HyperVHostSettingsService.GetAsync();
                var powerPlansTask = Task.Run(() =>
                    (Plans: HostPowerPlanService.GetPowerPlans(), ActiveId: HostPowerPlanService.GetActivePowerPlanId()));
                var sched = await Task.Run(() => HyperVSchedulerService.GetSchedulerType());
                bool? numa = await numaTask;
                HyperVHostSettings? hostSettings = await hostSettingsTask;
                var powerPlans = await powerPlansTask;
                IsNumaSpanningEnabled = numa ?? false;
                IsNumaSpanningToggleEnabled = HyperVStatus.IsSuccess == true && numa.HasValue;
                IsEnhancedSessionModeEnabled = hostSettings?.EnhancedSessionModeEnabled ?? false;
                IsEnhancedSessionModeToggleEnabled = HyperVStatus.IsSuccess == true && hostSettings != null;
                DefaultVirtualMachinePath = hostSettings?.DefaultVirtualMachinePath ?? string.Empty;
                DefaultVirtualHardDiskPath = hostSettings?.DefaultVirtualHardDiskPath ?? string.Empty;
                AreDefaultPathsEnabled = HyperVStatus.IsSuccess == true && hostSettings != null;
                MinimumDynamicMacAddress = FormatDynamicMacAddress(hostSettings?.MinimumMacAddress);
                MaximumDynamicMacAddress = FormatDynamicMacAddress(hostSettings?.MaximumMacAddress);
                IsDynamicMacRangeEnabled = HyperVStatus.IsSuccess == true && hostSettings != null;
                CurrentSchedulerType = sched == HyperVSchedulerType.Unknown ? HyperVSchedulerType.Classic : sched;
                PowerPlans.Clear();
                foreach (HostPowerPlan plan in powerPlans.Plans)
                    PowerPlans.Add(plan);
                CurrentPowerPlanId = powerPlans.ActiveId;
            }
            catch { }
        }


        partial void OnIsGpuStrategyEnabledChanged(bool value)
        {
            if (!_isInitialized) return;
            if (value) HyperVGpuPolicyService.AllowUnsupportedGpuAssignment(); else HyperVGpuPolicyService.ResetGpuAssignmentPolicy();
        }

        partial void OnIsNativeNvmeEnabledChanged(bool value)
        {
            if (!_isInitialized) return;
            if (value) HostNvmeService.EnableNativeNvme(); else HostNvmeService.DisableNativeNvme();
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
            VolumeAutoMountState actual = HostVolumeAutoMountService.GetState();

            _isInitialized = false;
            IsVolumeAutoMountEnabled = actual.Success ? actual.Enabled : !enabled;
            IsVolumeAutoMountToggleEnabled = actual.Success;
            _isInitialized = true;

            if (!result.Success)
                ShowError(string.Format(Properties.Resources.Error_Host_AutoMountChangeFailed, result.Error));
        }

        partial void OnIsNumaSpanningEnabledChanged(bool value)
        {
            if (!_isInitialized || !IsNumaSpanningToggleEnabled) return;
            _ = Task.Run(async () =>
            {
                var (ok, msg) = await HyperVNumaService.SetNumaSpanningEnabledAsync(value);
                if (!ok)
                {
                    ShowError(msg);
                    Application.Current.Dispatcher.Invoke(() =>
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
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _isInitialized = false;
                        IsEnhancedSessionModeEnabled = !value;
                        _isInitialized = true;
                    });
                }
            });
        }

        partial void OnCurrentSchedulerTypeChanged(HyperVSchedulerType value)
        {
            if (!_isInitialized) return;
            _ = Task.Run(async () =>
            {
                if (await HyperVSchedulerService.SetSchedulerTypeAsync(value))
                    ShowRestartPrompt(Properties.Resources.Msg_Host_SchedulerChanged);
                else
                {
                    ShowError(Properties.Resources.Error_Host_SchedulerFail);
                    var actual = HyperVSchedulerService.GetSchedulerType();
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _isInitialized = false;
                        CurrentSchedulerType = actual;
                        _isInitialized = true;
                    });
                }
            });
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
                    Application.Current.Dispatcher.Invoke(() =>
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
            string? minimum = NormalizeDynamicMacAddress(MinimumDynamicMacAddress);
            string? maximum = NormalizeDynamicMacAddress(MaximumDynamicMacAddress);
            if (minimum == null || maximum == null)
            {
                ShowError(Properties.Resources.Error_Host_DynamicMacRangeInvalid);
                return;
            }

            if (string.CompareOrdinal(minimum, maximum) > 0)
            {
                ShowError(Properties.Resources.Error_Host_DynamicMacRangeOrder);
                return;
            }

            IsApplyingDynamicMacRange = true;
            try
            {
                var result = await HyperVHostSettingsService.SetDynamicMacAddressRangeAsync(minimum, maximum);
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

        private static string? NormalizeDynamicMacAddress(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string trimmed = value.Trim();
            if (!Regex.IsMatch(trimmed, "^(?:[0-9A-Fa-f]{12}|(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2})$"))
                return null;

            return Regex.Replace(trimmed, "[:-]", string.Empty).ToUpperInvariant();
        }

        private static string FormatDynamicMacAddress(string? value)
        {
            string? normalized = NormalizeDynamicMacAddress(value);
            return normalized == null
                ? value ?? string.Empty
                : string.Join("-", Enumerable.Range(0, 6).Select(i => normalized.Substring(i * 2, 2)));
        }


        [RelayCommand]
        private async Task DisableHyperVAsync()
        {
            ShowTip(Properties.Resources.HostPageViewModel_DisablingHyperV);
            var op = HyperVHostService.DisableHyperVAsync();
            await Task.WhenAll(op, Task.Delay(1000));   // "操作中"提示至少停留 1s，不被结果一闪而过
            bool ok = await op;
            if (!ok)
            {
                ShowError(Properties.Resources.HostPageViewModel_DisableFailed);
                return;
            }
            ShowRestartPrompt(Properties.Resources.HostPageViewModel_DisableSuccess);
        }

        [RelayCommand]
        private async Task EnableHyperVAsync()
        {
            ShowTip(Properties.Resources.Msg_Host_EnableHyperV);
            var op = HyperVHostService.EnableHyperVAsync();
            await Task.WhenAll(op, Task.Delay(1000));   // "操作中"提示至少停留 1s，不被结果一闪而过
            bool ok = await op;
            if (!ok)
            {
                ShowError(Properties.Resources.Error_Host_EnableFail);
                return;
            }
            ShowRestartPrompt(Properties.Resources.Msg_Host_EnableSuccess);
        }


        private void InitializeProductType()
        {
            // 有挂起的切换任务时，开关显示"重启后的目标状态"而非当前 ProductType——
            // 灰在目标位置传达"操作已被接受、等重启"；方向未知(外部替换)则保守停在当前值。
            string? pending = SystemTypeService.GetPendingTarget();
            _hasPendingSwitch = pending != null;
            IsServerSystem = pending switch
            {
                "ServerNT" => true,
                "WinNT" => false,
                _ => HyperVHostService.IsServerSystem(),
            };
        }

        private async void SwitchSystemVersion(bool toServer)
        {
            try
            {
                IsSystemSwitchEnabled = false;

                string? pending = SystemTypeService.GetPendingTarget();
                if (pending != null)
                {
                    ShowTip(Properties.Resources.Status_Msg_RestartRequired);
                    ShowPendingState(pending, toServer);
                    return;   // 挂起任务无法取消或覆盖，重启前保持禁用
                }

                // 危险操作：仅「切到服务器版本」前二次确认（红色弹窗，同「彻底删除虚拟机」）。取消则回拨开关、重新启用。
                // 切回客户端（工作站）无此风险，直接执行、不打扰。
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
                    Interaction.Dialogs.ForceDangerButtonWhiteForeground(confirm);   // Danger 主按钮亮色主题下红底黑字，强制刷白
                    if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
                    {
                        _isInitialized = false; IsServerSystem = !toServer; _isInitialized = true;
                        IsSystemSwitchEnabled = HyperVHostService.IsServerSwitchApplicable();
                        return;
                    }
                }

                string result = await Task.Run(() => SystemTypeService.ApplySwitch(toServer));
                if (result == "SUCCESS")
                {
                    _hasPendingSwitch = true;
                    ShowRestartPrompt(Properties.Resources.Status_Msg_RestartNow);
                    return;   // 开关停在目标位置并保持禁用（待重启态）
                }
                if (result == "PENDING")
                {
                    ShowTip(Properties.Resources.Status_Msg_RestartRequired);
                    ShowPendingState(SystemTypeService.GetPendingTarget(), toServer);
                    return;
                }

                ShowError(result);
                _isInitialized = false; IsServerSystem = !toServer; _isInitialized = true;
                IsSystemSwitchEnabled = HyperVHostService.IsServerSwitchApplicable();
            }
            catch (Exception ex)
            {
                // async void：未捕获异常会直接崩溃 UI 线程；兜底上报并回滚开关状态
                ShowError(ex.Message);
                _isInitialized = false; IsServerSystem = !toServer; _isInitialized = true;
                IsSystemSwitchEnabled = HyperVHostService.IsServerSwitchApplicable();
            }
        }

        // 挂起态：开关摆到真实目标位置（方向未知则回滚到拨动前），不触发再次切换、保持禁用
        private void ShowPendingState(string? pendingTarget, bool attempted)
        {
            _hasPendingSwitch = true;
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
