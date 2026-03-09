using System;
using System.Diagnostics;
using System.Linq;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;
using ExHyperV.Services; // 引入后端服务命名空间

namespace ExHyperV.ViewModels
{
    public partial class ConsoleViewModel : ObservableObject, IDisposable
    {
        // -------------------------------------------------------------------------
        // 后端服务
        // -------------------------------------------------------------------------
        private readonly VmPowerService _powerService = new();
        private readonly VmQueryService _queryService = new();
        private DispatcherTimer _statusTimer;

        // -------------------------------------------------------------------------
        // 基础属性
        // -------------------------------------------------------------------------
        [ObservableProperty] private string _vmId;
        [ObservableProperty] private string _vmName;
        [ObservableProperty] private bool _isLoading = true;

        // ★ 关键属性：控制 XAML 中“启动”和“关机/下拉菜单”的可见性切换
        [ObservableProperty] private bool _isRunning;

        private bool _isConnecting = false;

        // 向 View 层暴露 CAD 请求事件，View 监听到后调用 RDP 的 SendCAD()
        public event EventHandler SendCadRequested;

        // -------------------------------------------------------------------------
        // 构造函数与初始化
        // -------------------------------------------------------------------------
        public ConsoleViewModel(string vmId, string vmName)
        {
            VmId = vmId;
            VmName = vmName;

            // 启动状态轮询（每2秒检查一次当前虚拟机的状态）
            StartStatusPolling();
        }

        // 用于供无参情况下的设计时或反射实例化
        public ConsoleViewModel() { }

        // -------------------------------------------------------------------------
        // 虚拟机状态监控
        // -------------------------------------------------------------------------
        private void StartStatusPolling()
        {
            _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _statusTimer.Tick += async (s, e) => await SyncVmStateAsync();
            _statusTimer.Start();

            // 立即执行一次状态获取
            _ = SyncVmStateAsync();
        }

        private async Task SyncVmStateAsync()
        {
            try
            {
                // 1. 获取最新列表
                var vms = await _queryService.GetVmListAsync();

                // 2. 改进搜索逻辑：增加大小写忽略，确保在 ID 或名称变动时都能匹配上
                var currentVm = vms.FirstOrDefault(v =>
                    v.Id.ToString().Equals(VmId, StringComparison.OrdinalIgnoreCase) ||
                    v.Name.Equals(VmName, StringComparison.OrdinalIgnoreCase));

                if (currentVm != null)
                {
                    // ★ 修复重点：直接使用后端模型已经算好的 IsRunning 属性
                    // 不要去比较 currentVm.State == "Running"，因为 State 可能是中文
                    IsRunning = currentVm.IsRunning;

                    // 如果名称发生了变化（比如在主界面重命名了），同步更新标题
                    if (VmName != currentVm.Name) VmName = currentVm.Name;

                    IsLoading = false;
                }
                else
                {
                    // 如果列表里找不到这个 VM 了（可能被删除了）
                    Debug.WriteLine($"[Console] 找不到虚拟机: {VmName}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Console VM State Sync Error] {ex.Message}");
            }
        }
        // -------------------------------------------------------------------------
        // 核心功能：虚拟机电源与控制命令
        // -------------------------------------------------------------------------

        [RelayCommand]
        private async Task StartVmAsync() => await ExecutePowerActionAsync("Start");

        [RelayCommand]
        private async Task ShutdownVmAsync() => await ExecutePowerActionAsync("Stop"); // Stop 即“正常关机”

        [RelayCommand]
        private async Task ResetVmAsync() => await ExecutePowerActionAsync("Restart"); // 重启

        [RelayCommand]
        private async Task PauseVmAsync() => await ExecutePowerActionAsync("Suspend"); // 暂停

        [RelayCommand]
        private async Task SaveVmAsync() => await ExecutePowerActionAsync("Save"); // 保存状态[RelayCommand]
        private async Task TurnOffVmAsync() => await ExecutePowerActionAsync("TurnOff"); // 强制关闭电源

        [RelayCommand]
        private void SendCad()
        {
            // 触发事件交由 View (ConsoleWindow 或 VmConsoleView) 处理
            SendCadRequested?.Invoke(this, EventArgs.Empty);
            Debug.WriteLine("[Console] 请求发送 Ctrl+Alt+Del");
        }

        /// <summary>
        /// 统一下发电源命令的内部封装
        /// </summary>
        private async Task ExecutePowerActionAsync(string action)
        {
            try
            {
                // 乐观 UI 更新：点击后立刻改变状态，提升响应感
                if (action == "Start") IsRunning = true;
                if (action == "Stop" || action == "TurnOff" || action == "Save" || action == "Suspend") IsRunning = false;

                await _powerService.ExecuteControlActionAsync(VmName, action);

                // 执行完毕后强制同步一次准确状态
                await SyncVmStateAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Console Power Action Error] {ex.Message}");
                // 如果你有 Snackbar 服务，可以在这里弹窗提示错误
                // 恢复乐观更新带来的误差
                await SyncVmStateAsync();
            }
        }

        // -------------------------------------------------------------------------
        // RDP 分辨率与会话逻辑 (原样保留)
        // -------------------------------------------------------------------------

        [ObservableProperty] private string _selectedResolution = "等待连接...";
        [ObservableProperty] private int _currentWidth;
        [ObservableProperty] private int _currentHeight;

        partial void OnCurrentWidthChanged(int value) => UpdateResolutionString();
        partial void OnCurrentHeightChanged(int value) => UpdateResolutionString();

        private void UpdateResolutionString()
        {
            if (CurrentWidth > 0 && CurrentHeight > 0)
                SelectedResolution = $"{CurrentWidth} x {CurrentHeight}";
        }

        public ObservableCollection<string> Resolutions { get; } = new()
        {
            "3840 x 2160", "2560 x 1600", "2560 x 1440", "1920 x 1200",
            "1920 x 1080", "1680 x 1050", "1600 x 1200", "1600 x 900",
            "1440 x 900",  "1366 x 768",  "1280 x 1024", "1280 x 800",
            "1280 x 720",  "1152 x 864",  "1024 x 768",  "800 x 600"
        };

        [ObservableProperty] private string _selectedSessionMode = "基本会话";
        [ObservableProperty] private bool _isEnhancedMode = false;
        [ObservableProperty] private SymbolRegular _selectedSessionIcon = SymbolRegular.Broom24;

        [RelayCommand]
        private void SwitchSessionMode(string mode)
        {
            SelectedSessionMode = mode;
        }

        partial void OnSelectedSessionModeChanged(string value)
        {
            IsEnhancedMode = (value == "增强会话");
            SelectedSessionIcon = IsEnhancedMode ? SymbolRegular.Flash24 : SymbolRegular.Broom24;
            OnPropertyChanged(nameof(CanChangeResolution));
        }

        public bool CanChangeResolution => IsEnhancedMode;

        [ObservableProperty] private int _requestWidth;
        [ObservableProperty] private int _requestHeight;

        [RelayCommand]
        private void ChangeResolution(string resolutionText)
        {
            if (string.IsNullOrEmpty(resolutionText) || !IsEnhancedMode) return;

            var parts = resolutionText.Split('x');
            if (parts.Length == 2 &&
                int.TryParse(parts[0].Trim(), out int w) &&
                int.TryParse(parts[1].Trim(), out int h))
            {
                Debug.WriteLine($"[ViewModel] 请求切换分辨率至: {w}x{h}");
                CurrentWidth = w;
                CurrentHeight = h;
                RequestWidth = w;   // 这里触发 PropertyChanged，View 层通过绑定监听该变化去重连 RDP
                RequestHeight = h;
            }
        }

        // -------------------------------------------------------------------------
        // 资源释放
        // -------------------------------------------------------------------------
        public void Dispose()
        {
            _statusTimer?.Stop();
        }
    }
}