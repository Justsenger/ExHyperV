using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Interaction;
using ExHyperV.Tools;
using System.Collections.ObjectModel;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public record SchedulerMode(string Name, HyperVSchedulerType Type);

    public partial class HostPageViewModel : PageViewModelBase
    {
        // ===== 字段与状态 =====

        private bool _isInitialized = false;

        // ===== 绑定属性 =====

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
        [ObservableProperty] private bool _isServerSystem;
        [ObservableProperty] private bool _isSystemSwitchEnabled = false;

        // 有挂起的版本切换任务（重启前不可再切，开关保持禁用）
        private bool _hasPendingSwitch = false;
        [ObservableProperty] private bool _isNumaSpanningEnabled;
        [ObservableProperty] private HyperVSchedulerType _currentSchedulerType;

        public ObservableCollection<SchedulerMode> SchedulerModes { get; } = new()
        {
            new SchedulerMode(Properties.Resources.Scheduler_Classic, HyperVSchedulerType.Classic),
            new SchedulerMode(Properties.Resources.Scheduler_Core, HyperVSchedulerType.Core),
            new SchedulerMode(Properties.Resources.Scheduler_Root, HyperVSchedulerType.Root)
        };

        // ===== 构造与初始化检查 =====

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
            InitializeProductType();
            await LoadAdvancedConfigAsync();
            IsGpuStrategyToggleEnabled = true;
            IsNativeNvmeToggleEnabled = true;
            // 切换服务器版本(黑魔法)仅对特定客户端 SKU 生效；真 Server/家庭版/标准专业版/企业版等不适用，开关置灰。
            // 判定走 EditionID(真实 SKU)而非 ProductType——后者正是黑魔法改的值，用它会致被切的客户端版无法切回。
            // 已有挂起切换任务时同样置灰：挂起的替换无法取消也无法覆盖，重启生效前不可再切。
            IsSystemSwitchEnabled = !_hasPendingSwitch && HyperVHostService.IsServerSwitchApplicable();
        }

        private async Task LoadAdvancedConfigAsync()
        {
            try
            {
                bool numa = await HyperVNumaService.GetNumaSpanningEnabledAsync();
                var sched = await Task.Run(() => HyperVSchedulerService.GetSchedulerType());
                IsNumaSpanningEnabled = numa;
                CurrentSchedulerType = sched == HyperVSchedulerType.Unknown ? HyperVSchedulerType.Classic : sched;
            }
            catch { }
        }

        // ===== 属性变更处理 =====

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

        partial void OnIsNumaSpanningEnabledChanged(bool value)
        {
            if (!_isInitialized) return;
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

        partial void OnIsServerSystemChanged(bool value)
        {
            if (!_isInitialized) return;
            SwitchSystemVersion(value);
        }

        // ===== 命令 =====

        [RelayCommand]
        private async Task DisableHyperVAsync()
        {
            ShowTip(Properties.Resources.HostPageViewModel_DisablingHyperV);
            bool ok = await HyperVHostService.DisableHyperVAsync();
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
            bool ok = await HyperVHostService.EnableHyperVAsync();
            if (!ok)
            {
                ShowError(Properties.Resources.Error_Host_EnableFail);
                return;
            }
            ShowRestartPrompt(Properties.Resources.Msg_Host_EnableSuccess);
        }

        // ===== 系统版本切换 =====

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

                // 危险操作：切换前二次确认（同「彻底删除虚拟机」的红色确认弹窗）。取消则回拨开关、重新启用，不执行切换。
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
                if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    _isInitialized = false; IsServerSystem = !toServer; _isInitialized = true;
                    IsSystemSwitchEnabled = HyperVHostService.IsServerSwitchApplicable();
                    return;
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

    // ===== 检查项状态子 VM =====

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