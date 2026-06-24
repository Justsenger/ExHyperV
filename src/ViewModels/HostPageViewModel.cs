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

        [ObservableProperty] private bool _isGpuStrategyEnabled;
        [ObservableProperty] private bool _isGpuStrategyToggleEnabled = false;
        [ObservableProperty] private bool _isNativeNvmeEnabled;
        [ObservableProperty] private bool _isNativeNvmeToggleEnabled = false;
        [ObservableProperty] private bool _isNativeNvmeSupported;
        [ObservableProperty] private bool _isServerSystem;
        [ObservableProperty] private bool _isSystemSwitchEnabled = false;
        [ObservableProperty] private string _systemVersionDesc = string.Empty;
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
            await Task.WhenAll(CheckSystemInfoAsync(), CheckCpuInfoAsync(), CheckHyperVInfoAsync(), CheckServerInfoAsync(), CheckIommuAsync());
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
                VersionStatus.StatusText = baseVersion + Properties.Resources.Status_Msg_GpuPvNotSupported;
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

        private async Task InitializeVersionPolicyAsync()
        {
            IsGpuStrategyEnabled = await Task.Run(() => HyperVHostService.GetGpuStrategyEnabled());
            IsNativeNvmeSupported = Environment.OSVersion.Version.Build >= 26100; // WS2025 / Win11 24H2 起才有原生 NVMe
            IsNativeNvmeEnabled = await Task.Run(() => HostNvmeService.IsNativeNvmeEnabled());
            InitializeProductType();
            await LoadAdvancedConfigAsync();
            IsGpuStrategyToggleEnabled = true;
            IsNativeNvmeToggleEnabled = true;
            IsSystemSwitchEnabled = true;
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
            IsServerSystem = HyperVHostService.IsServerSystem();
            UpdateSystemDesc(IsServerSystem);
        }

        private void UpdateSystemDesc(bool isServer) =>
            SystemVersionDesc = $"{Properties.Resources.Status_Msg_CurrentVer}: {(isServer ? Properties.Resources.Status_Edition_Server : Properties.Resources.Status_Edition_Workstation)}";

        private async void SwitchSystemVersion(bool toServer)
        {
            try
            {
                IsSystemSwitchEnabled = false;

                if (SystemTypeService.HasPendingTask())
                {
                    ShowTip(Properties.Resources.Status_Msg_RestartRequired);
                    _isInitialized = false;
                    IsServerSystem = !toServer;
                    _isInitialized = true;
                    return;
                }

                string result = await Task.Run(() => SystemTypeService.ApplySwitch(toServer));
                if (result == "SUCCESS") ShowRestartPrompt(Properties.Resources.Status_Msg_RestartNow);
                else
                {
                    ShowError(result);
                    _isInitialized = false; IsServerSystem = !toServer; _isInitialized = true;
                }
            }
            catch (Exception ex)
            {
                // async void：未捕获异常会直接崩溃 UI 线程；兜底上报并回滚开关状态
                ShowError(ex.Message);
                _isInitialized = false; IsServerSystem = !toServer; _isInitialized = true;
            }
            finally { IsSystemSwitchEnabled = true; }
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