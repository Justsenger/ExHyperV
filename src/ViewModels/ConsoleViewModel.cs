using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;

namespace ExHyperV.ViewModels
{
    public partial class ConsoleViewModel : ObservableObject, IDisposable
    {
        // ===== 字段 =====

        private readonly VmQueryService _queryService = new();
        private DispatcherTimer _statusTimer = null!;
        private bool _polling;   // 防止上一次轮询(WMI 慢)未完成时重入

        // ===== 基础属性 =====

        [ObservableProperty] private string _vmId;
        [ObservableProperty] private string _vmName;
        [ObservableProperty] private bool _isRunning;
        
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotBusy))]
        [NotifyCanExecuteChangedFor(nameof(StartVmCommand))]
        [NotifyCanExecuteChangedFor(nameof(ShutdownVmCommand))]
        [NotifyCanExecuteChangedFor(nameof(ResetVmCommand))]
        [NotifyCanExecuteChangedFor(nameof(PauseVmCommand))]
        [NotifyCanExecuteChangedFor(nameof(SaveVmCommand))]
        [NotifyCanExecuteChangedFor(nameof(TurnOffVmCommand))]
        private bool _isBusy = false;

        public bool IsNotBusy => !IsBusy;

        public event EventHandler? SendCadRequested;
        /// <summary>每次状态轮询完成后触发（供消费方按 VM 运行状态同步连接，无需额外定时器）。</summary>
        public event Action? Polled;

        // ===== 构造 =====

        public ConsoleViewModel(string vmId, string vmName)
        {
            VmId = vmId;
            VmName = vmName;
            StartStatusPolling();
        }
        // ===== 全屏 =====

        [ObservableProperty] private bool _isFullScreen = false;

        [RelayCommand]
        private void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

        // ===== 状态轮询 =====

        private void StartStatusPolling()
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += async (s, e) => await SyncVmStateAsync();
            _statusTimer.Start();
            _ = SyncVmStateAsync();
        }

        private async Task SyncVmStateAsync()
        {
            if (_polling) return;   // 上一次轮询(WMI 慢)未完成 → 跳过，避免重入与重叠查询
            _polling = true;
            try
            {
                var vms = await _queryService.GetVmListAsync();
                var currentVm = vms.FirstOrDefault(v =>
                    v.Id.ToString().Equals(VmId, StringComparison.OrdinalIgnoreCase) ||
                    v.Name.Equals(VmName, StringComparison.OrdinalIgnoreCase));

                if (currentVm != null)
                {
                    IsRunning = currentVm.IsRunning;                       // 更新运行状态
                    if (VmName != currentVm.Name) VmName = currentVm.Name; // 更新名称
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.Message);
            }
            finally
            {
                _polling = false;
            }
            Polled?.Invoke();   // 通知消费方按最新 VM 运行状态同步 RDP 连接（连/断/重连）
        }
        // ===== 电源控制 =====

        private bool CanExecutePowerAction() => !IsBusy;

        [RelayCommand(CanExecute = nameof(CanExecutePowerAction))]
        private async Task StartVmAsync() => await ExecutePowerActionAsync("Start");

        [RelayCommand(CanExecute = nameof(CanExecutePowerAction))]
        private async Task ShutdownVmAsync() => await ExecutePowerActionAsync("Stop");

        [RelayCommand(CanExecute = nameof(CanExecutePowerAction))]
        private async Task ResetVmAsync() => await ExecutePowerActionAsync("Restart");

        [RelayCommand(CanExecute = nameof(CanExecutePowerAction))]
        private async Task PauseVmAsync() => await ExecutePowerActionAsync("Suspend");

        [RelayCommand(CanExecute = nameof(CanExecutePowerAction))]
        private async Task SaveVmAsync() => await ExecutePowerActionAsync("Save");

        [RelayCommand(CanExecute = nameof(CanExecutePowerAction))]
        private async Task TurnOffVmAsync() => await ExecutePowerActionAsync("TurnOff");

        private async Task ExecutePowerActionAsync(string action)
        {
            try
            {
                IsBusy = true;
                await VmPowerService.ExecuteControlActionAsync(VmName, action);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(string.Format(Properties.Resources.ConsoleViewModel_OperationFailed, ex.Message));
            }
            finally
            {
                // 关键：无论成功还是失败，操作完成后都要同步一次状态并关闭 Busy 状态
                await SyncVmStateAsync();
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void SendCad()
        {
            Debug.WriteLine(Properties.Resources.ConsoleViewModel_LogSendCadActivated);
            SendCadRequested?.Invoke(this, EventArgs.Empty);
        }

        // ===== 分辨率 =====

        [ObservableProperty] private string _selectedResolution = "-";
        [ObservableProperty] private int _currentWidth;
        [ObservableProperty] private int _currentHeight;

        partial void OnCurrentWidthChanged(int value) => UpdateResolutionString();
        partial void OnCurrentHeightChanged(int value) => UpdateResolutionString();

        private void UpdateResolutionString()
        {
            if (CurrentWidth > 0 && CurrentHeight > 0 && IsRunning)
            {
                SelectedResolution = $"{CurrentWidth} x {CurrentHeight}";
            }
            else
            {
                SelectedResolution = "-";
            }
        }

        public ObservableCollection<string> Resolutions { get; } = new()
        {
            "3840 x 2160", "2560 x 1600", "2560 x 1440", "1920 x 1200",
            "1920 x 1080", "1680 x 1050", "1600 x 1200", "1600 x 900",
            "1440 x 900",  "1366 x 768",  "1280 x 1024", "1280 x 800",
            "1280 x 720",  "1152 x 864",  "1024 x 768",  "800 x 600"
        };

        // ===== 缩放（仅基本会话）=====
        // 基本会话是固定分辨率的合成显示，只能拉伸缩放（放大必糊）；增强会话画面已原生跟随窗口，无需缩放。
        // 档值：本地化"适应窗口" + 纯比例字符串。窗口端 LayoutRdpHost 解析后摆放 RdpHost + 开关滚动条。
        [ObservableProperty] private string _selectedZoom = "100%";

        public ObservableCollection<string> ZoomOptions { get; } = new()
        {
            Properties.Resources.ConsoleWindow_ZoomAuto,
            "500%", "400%", "300%", "200%", "150%", "125%", "100%", "75%", "50%", "25%"
        };

        [RelayCommand]
        private void ChangeZoom(string zoom)
        {
            if (!string.IsNullOrEmpty(zoom)) SelectedZoom = zoom;
        }

        // ===== 会话模式 =====

        [ObservableProperty] private string _selectedSessionMode = Properties.Resources.ConsoleViewModel_BasicSession;
        [ObservableProperty] private bool _isEnhancedMode = false;

        [RelayCommand]
        private void SwitchSessionMode(string mode) => SelectedSessionMode = mode;

        /// <summary>增强会话连接失败时回退到基本会话（顶部会话开关随之切回，并触发以基本会话重连）。</summary>
        public void FallbackToBasicSession() => SelectedSessionMode = Properties.Resources.ConsoleViewModel_BasicSession;

        partial void OnSelectedSessionModeChanged(string value)
        {
            IsEnhancedMode = (value == Properties.Resources.ConsoleViewModel_EnhancedSession);
            OnPropertyChanged(nameof(CanChangeResolution));
        }

        public bool CanChangeResolution => IsEnhancedMode;

        [ObservableProperty] private int _requestWidth;
        [ObservableProperty] private int _requestHeight;

        partial void OnIsRunningChanged(bool value)
        {
            // 如果虚拟机停止运行
            if (!value)
            {
                // 重置宽高
                _currentWidth = 0;
                _currentHeight = 0;
                // 直接设置字符串为 "-"
                SelectedResolution = "-";

                // 通知 UI 宽高已更改（如果 UI 有绑定这两个值）
                OnPropertyChanged(nameof(CurrentWidth));
                OnPropertyChanged(nameof(CurrentHeight));
            }
        }

        [RelayCommand]
        private void ChangeResolution(string resolutionText)
        {
            if (string.IsNullOrEmpty(resolutionText) || !IsEnhancedMode) return;
            var parts = resolutionText.Split('x');
            if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int w) && int.TryParse(parts[1].Trim(), out int h))
            {
                CurrentWidth = w;
                CurrentHeight = h;
                RequestWidth = w;
                RequestHeight = h;
            }
        }

        public void Dispose() => _statusTimer?.Stop();
    }
}