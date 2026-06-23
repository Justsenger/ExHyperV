using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Interaction;
using System.Collections.ObjectModel;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public record SchedulerMode(string Name, HyperVSchedulerType Type);

    public partial class HostPageViewModel : ObservableObject
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
        [ObservableProperty] private bool _isServerSystem;
        [ObservableProperty] private bool _isSystemSwitchEnabled = false;
        [ObservableProperty] private string _systemVersionDesc;
        [ObservableProperty] private bool _isNumaSpanningEnabled;
        [ObservableProperty] private HyperVSchedulerType _currentSchedulerType;

        public ObservableCollection<SchedulerMode> SchedulerModes { get; } = new()
        {
            new SchedulerMode(Properties.Resources.Scheduler_Classic, HyperVSchedulerType.Classic),
            new SchedulerMode(Properties.Resources.Scheduler_Core, HyperVSchedulerType.Core),
            new SchedulerMode(Properties.Resources.Scheduler_Root, HyperVSchedulerType.Root)
        };

        // ===== 构造与初始化检查 =====

        public HostPageViewModel() => _ = LoadInitialStatusAsync();

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
            InitializeProductType();
            await LoadAdvancedConfigAsync();
            IsGpuStrategyToggleEnabled = true;
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

        partial void OnIsNumaSpanningEnabledChanged(bool value)
        {
            if (!_isInitialized) return;
            _ = Task.Run(async () =>
            {
                var (ok, msg) = await HyperVNumaService.SetNumaSpanningEnabledAsync(value);
                if (!ok)
                {
                    ShowSnackbar(Properties.Resources.Status_Title_Error, msg, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
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
                    ShowSnackbar(Properties.Resources.Status_Title_Error, Properties.Resources.Error_Host_SchedulerFail, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
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
            ShowSnackbar(Properties.Resources.Status_Title_Info, Properties.Resources.HostPageViewModel_DisablingHyperV, ControlAppearance.Info, SymbolRegular.Settings24);
            bool ok = await HyperVHostService.DisableHyperVAsync();
            if (!ok)
            {
                ShowSnackbar(Properties.Resources.Status_Title_Error, Properties.Resources.HostPageViewModel_DisableFailed, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                return;
            }
            ShowRestartPrompt(Properties.Resources.HostPageViewModel_DisableSuccess);
        }

        [RelayCommand]
        private async Task EnableHyperVAsync()
        {
            ShowSnackbar(Properties.Resources.Status_Title_Info, Properties.Resources.Msg_Host_EnableHyperV, ControlAppearance.Info, SymbolRegular.Settings24);
            bool ok = await HyperVHostService.EnableHyperVAsync();
            if (!ok)
            {
                ShowSnackbar(Properties.Resources.Status_Title_Error, Properties.Resources.Error_Host_EnableFail, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
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
                    ShowSnackbar(Properties.Resources.Status_Title_Warning,
                        Properties.Resources.Status_Msg_RestartRequired,
                        ControlAppearance.Caution,
                        SymbolRegular.Warning24);
                    _isInitialized = false;
                    IsServerSystem = !toServer;
                    _isInitialized = true;
                    return;
                }

                string result = await Task.Run(() => SystemTypeService.ApplySwitch(toServer));
                if (result == "SUCCESS") ShowRestartPrompt(Properties.Resources.Status_Msg_RestartNow);
                else
                {
                    ShowSnackbar(Properties.Resources.Status_Title_Error, result, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    _isInitialized = false; IsServerSystem = !toServer; _isInitialized = true;
                }
            }
            finally { IsSystemSwitchEnabled = true; }
        }


        // ===== UI 辅助（Snackbar / 重启提示） =====

        private void ShowSnackbar(string title, string msg, ControlAppearance app, SymbolRegular icon)
            => Notifications.ShowSnackbar(title, msg, app, icon);

        private void ShowRestartPrompt(string message)
            => Notifications.ShowRestartPrompt(message);
    }

    // ===== 检查项状态子 VM =====

    public partial class CheckStatusViewModel : ObservableObject
    {
        [ObservableProperty] private bool _isChecking = true;
        [ObservableProperty] private string _statusText;
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