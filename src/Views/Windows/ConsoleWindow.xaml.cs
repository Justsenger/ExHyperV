using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Wpf.Ui.Controls;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.ViewModels;

namespace ExHyperV.Views
{
    /// <summary>
    /// 控制台窗口：窗口管理 + RDP 内容编排集中在这一个组件（单一 master，所有状态反应只在这里，无跨组件竞争）。
    /// 三态由 <see cref="ConsoleViewModel"/> 驱动：窗口化(可调整大小，画面=VM 尺寸居中周围黑底) /
    /// 最大化("窗口全屏"，工作区) / 全屏(WM_GETMINMAXINFO 铺满显示器、不切 WindowStyle 以免 ActiveX HWND 偏移)。
    /// 连接随 VM 运行状态走（复用 ViewModel 的状态轮询，断线/VM 重启自动重连，无额外定时器）。
    /// </summary>
    public partial class ConsoleWindow : FluentWindow
    {
        private const double TitleBarHeight = 42;   // 与 XAML ui:TitleBar 高度一致
        // 全屏热键配置点：Ctrl+Alt+<虚拟键>，默认 Enter(0x0D)。常用：Enter=0x0D / Space=0x20 / Break=0x03 / Pause=0x13。
        private const int FullScreenHotKeyVk = 0x0D;
        // 连接超时：localhost VMBus 正常连接 <1s，2s 余量足够；连不上(如不支持增强)即在此时限内放弃 → 快速回退基本会话。
        private const int ConnectTimeoutSeconds = 2;

        private readonly ConsoleViewModel _vm;
        private bool _isFullScreen;               // 供 WM_GETMINMAXINFO 判断最大化铺满显示器还是工作区
        private bool _syncingFs;                  // 防止 mstscax→VM→mstscax 全屏状态回灌
        private bool _weInitiatedDisconnect;      // 标记我方主动断开(模式切换/VM 停止)，以免被当作"非预期断开"
        private bool _reconnectPending;           // 模式切换：断开完成(OnDisconnected)后再连，避免立即连被 mstscax 拒
        private bool _enhancedConnecting;         // 本次连接是否在尝试增强会话——没连上就断 → 回退基本会话

        public ConsoleWindow(string vmId, string vmName)
        {
            _vm = new ConsoleViewModel(vmId, vmName);
            this.DataContext = _vm;
            InitializeComponent();
            this.Title = vmName;

            _vm.SendCadRequested += OnSendCadRequested;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.Polled += OnVmPolled;   // 每次状态轮询：让连接与 VM 运行状态一致（含断线/VM 重启后重连）

            // RDP 宿主事件（原生事件，取代旧实现的 20ms 轮询）
            RdpHost.Connected += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                _vm.IsLoading = false;
                _enhancedConnecting = false;   // 已连上（增强成功，或本就是基本）
            }));
            RdpHost.Disconnected += _ => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_weInitiatedDisconnect)
                {
                    _weInitiatedDisconnect = false;
                    if (_reconnectPending) { _reconnectPending = false; SyncConnection(forceReconnect: false); }   // 断完 → 重连
                    return;
                }
                if (_enhancedConnecting)   // 增强会话没连上就断 → 回退基本会话（并把顶部开关切回）
                {
                    _enhancedConnecting = false;
                    _vm.FallbackToBasicSession();   // 触发 IsEnhancedMode 变化 → SyncConnection 以基本会话重连
                    return;
                }
                // VM 停止 / 掉线：保持窗口、显示遮罩；由状态轮询在 VM 运行时自动重连。
                // 关闭控制台由用户点窗口关闭按钮完成（不从断开推断，避免 VM 停止误关）。
                _vm.IsLoading = true;
            }));
            RdpHost.FatalError += code => Dispatcher.BeginInvoke(new Action(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[Rdp] 致命错误 code={code}");
                _vm.IsLoading = true;   // 同断线处理，等轮询重连
            }));
            RdpHost.RemoteSizeChanged += (w, h) => Dispatcher.BeginInvoke(new Action(() =>
            {
                _vm.CurrentWidth = w; _vm.CurrentHeight = h;
                LayoutRdpHost();
            }));
            RdpHost.FullScreenRequested += fs => Dispatcher.BeginInvoke(new Action(() =>
            {
                _syncingFs = true; _vm.IsFullScreen = fs; _syncingFs = false;   // 源自 mstscax 热键，只反映到 VM，不回灌
            }));
            RdpHost.MinimizeRequested += () => Dispatcher.BeginInvoke(new Action(() => this.WindowState = WindowState.Minimized));
            RdpHost.CloseRequested += () => Dispatcher.BeginInvoke(new Action(this.Close));
        }

        // HWND 就绪后挂钩 WndProc（全屏铺满显示器 + 拖动结束协商分辨率）。
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        }

        // 状态轮询回调：让连接跟随 VM 运行状态。VM 停止时保持窗口等待，VM 一恢复即重连——复用既有 2s 轮询，无需额外定时器。
        // 经 Dispatcher 兜底确保在 UI 线程执行（SyncConnection 会碰 RdpHost）。
        private void OnVmPolled() => Dispatcher.BeginInvoke(new Action(() => SyncConnection(forceReconnect: false)));

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ConsoleViewModel.IsEnhancedMode):
                    SyncConnection(forceReconnect: true);            // 换 PCB，须断后重连
                    if (!_vm.IsFullScreen) ApplyWindowedLayout();
                    break;

                case nameof(ConsoleViewModel.RequestWidth):
                case nameof(ConsoleViewModel.RequestHeight):
                    if (_vm.IsEnhancedMode && _vm.RequestWidth > 0 && _vm.RequestHeight > 0)
                        RdpHost.Resize(_vm.RequestWidth, _vm.RequestHeight);   // 顶部分辨率下拉
                    break;

                case nameof(ConsoleViewModel.IsFullScreen):
                    if (_vm.IsFullScreen) EnterFullScreen(); else ExitFullScreen();  // 窗口
                    if (!_syncingFs) RdpHost.SetFullScreen(_vm.IsFullScreen);        // 按钮发起的才回灌 mstscax
                    LayoutRdpHost();                                                 // RDP 宿主：全屏铺满 / 窗口化缩到 VM 居中
                    break;

                case nameof(ConsoleViewModel.CurrentWidth):
                case nameof(ConsoleViewModel.CurrentHeight):
                    if (!_vm.IsFullScreen && !_vm.IsEnhancedMode)
                        FitToResolution(_vm.CurrentWidth, _vm.CurrentHeight);   // 基本会话：窗口跟随 VM 分辨率
                    break;
            }
        }

        // 让 RDP 连接与 VM 运行状态一致。forceReconnect=true 时即使已连也先断（会话模式切换换 PCB 用）。
        private void SyncConnection(bool forceReconnect)
        {
            if (forceReconnect && RdpHost.ConnectionState != 0)
            {
                // 已连接要换 PCB（模式切换）：先断，等 OnDisconnected 断完再连——立即连会被 mstscax 拒、拖到轮询。
                _weInitiatedDisconnect = true;
                _reconnectPending = true;
                RdpHost.Disconnect();
                return;
            }

            if (_vm.IsRunning)
            {
                if (RdpHost.ConnectionState == 0)   // 该连而未连（force+已连的已在上面断开并挂起重连）
                {
                    _vm.IsLoading = true;
                    _enhancedConnecting = _vm.IsEnhancedMode;   // 记下本次是否在尝试增强（失败则回退基本）
                    RdpHost.Connect(BuildHyperVSettings(_vm.VmId, _vm.IsEnhancedMode, _vm.CurrentWidth, _vm.CurrentHeight));
                }
            }
            else if (RdpHost.ConnectionState != 0)   // VM 停了但还连着 → 断（保持窗口，等轮询到 VM 重启再连）
            {
                _weInitiatedDisconnect = true;
                RdpHost.Disconnect();
            }
        }

        // Hyper-V 控制台连接配方（消费层组装；增强沿用当前分辨率作初始尺寸，避免切换跳变）。
        private static RdpConnectionSettings BuildHyperVSettings(string vmId, bool enhanced, int reuseWidth, int reuseHeight)
        {
            var id = (vmId ?? string.Empty).Trim().ToUpperInvariant();
            return new RdpConnectionSettings
            {
                Server = "localhost",
                Port = 2179,
                AuthenticationLevel = 0,
                AuthenticationServiceClass = "Microsoft Virtual Console Service",
                NetworkLevelAuthentication = true,
                NegotiateSecurityLayer = false,
                DisableCredentialsDelegation = true,
                FullScreenHotKeyVirtualKey = FullScreenHotKeyVk,
                ConnectionTimeoutSeconds = ConnectTimeoutSeconds,
                DesktopWidth = enhanced ? reuseWidth : 0,
                DesktopHeight = enhanced ? reuseHeight : 0,
                PreConnectionBlob = enhanced ? $"{id};EnhancedMode=1" : id,
            };
        }

        // ── 全屏 / 窗口尺寸 ─────────────────────────────────────────────────
        private void EnterFullScreen()
        {
            _isFullScreen = true;
            // 不动 WindowStyle（保持一致 chrome、客户区不位移 → 不触发 ActiveX HWND 偏移）；
            // 靠 WM_GETMINMAXINFO 让最大化铺满整个显示器。已最大化则先还原再最大化以重新取全屏尺寸。
            if (this.WindowState == WindowState.Maximized) this.WindowState = WindowState.Normal;
            this.WindowState = WindowState.Maximized;
            this.Topmost = true;
        }

        private void ExitFullScreen()
        {
            _isFullScreen = false;
            this.Topmost = false;
            this.WindowState = WindowState.Normal;
            ApplyWindowedLayout();
        }

        private void ApplyWindowedLayout()
        {
            if (_vm.IsFullScreen) return;
            this.ResizeMode = ResizeMode.CanResize;   // 窗口化恒可调整大小（原生双击最大化/拖动/贴边依赖于此）
            FitToResolution(_vm.CurrentWidth, _vm.CurrentHeight);
        }

        /// <summary>窗口尺寸设为正好容纳 VM 分辨率（直接设 Width/Height，不用 SizeToContent——后者与最大化/全屏冲突）。</summary>
        private void FitToResolution(int pixelWidth, int pixelHeight)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0) return;
            if (this.WindowState == WindowState.Maximized) return;   // 最大化时别把窗口顶回分辨率尺寸
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return;
            double dpiX = src.CompositionTarget.TransformToDevice.M11;
            double dpiY = src.CompositionTarget.TransformToDevice.M22;
            this.Width = pixelWidth / dpiX;
            this.Height = pixelHeight / dpiY + TitleBarHeight;
        }

        /// <summary>摆放 RDP 宿主：全屏铺满（mstscax 接管 + 顶部连接栏）；窗口化缩到 VM 原生尺寸居中，
        /// 周围露出 RdpArea 黑底。SmartSizing 已关、画面原生不缩放，RdpHost=VM 尺寸时正好填满、无内部信箱。</summary>
        private void LayoutRdpHost()
        {
            if (_vm.IsFullScreen)
            {
                RdpHost.HorizontalAlignment = HorizontalAlignment.Stretch;
                RdpHost.VerticalAlignment = VerticalAlignment.Stretch;
                RdpHost.Width = double.NaN;
                RdpHost.Height = double.NaN;
                return;
            }
            int vmW = _vm.CurrentWidth, vmH = _vm.CurrentHeight;
            if (vmW <= 0 || vmH <= 0) return;
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return;
            double dpiX = src.CompositionTarget.TransformToDevice.M11;
            double dpiY = src.CompositionTarget.TransformToDevice.M22;
            RdpHost.HorizontalAlignment = HorizontalAlignment.Center;
            RdpHost.VerticalAlignment = VerticalAlignment.Center;
            RdpHost.Width = vmW / dpiX;
            RdpHost.Height = vmH / dpiY;
        }

        /// <summary>增强会话：用户结束拖动窗口（WM_EXITSIZEMOVE）后，把当前画面区像素协商给 VM（桌面跟随窗口尺寸）。</summary>
        private void NegotiateResolution()
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return;
            double dpiX = src.CompositionTarget.TransformToDevice.M11;
            double dpiY = src.CompositionTarget.TransformToDevice.M22;
            int px = (int)Math.Round(RdpArea.ActualWidth * dpiX);
            int py = (int)Math.Round(RdpArea.ActualHeight * dpiY);
            if (px >= 200 && py >= 200 && (px != _vm.CurrentWidth || py != _vm.CurrentHeight))
                RdpHost.Resize(px, py);
        }

        // ── CAD / 关闭 ──────────────────────────────────────────────────────
        private void OnSendCadRequested(object? sender, EventArgs e)
        {
            if (_vm.IsEnhancedMode) RdpHost.SendCtrlAltDelViaRdp();   // 增强：COM 扫描码
            else _ = VmInputService.SendCtrlAltDelAsync(_vm.VmId);    // 基本：WMI
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            RdpHost.Disconnect();   // 断开 RDP 会话（否则 mstscax/VMBus 会话残留到 GC）
            _vm.SendCadRequested -= OnSendCadRequested;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.Polled -= OnVmPolled;
            _vm.Dispose();
        }

        // ── WndProc：WM_GETMINMAXINFO（全屏铺满整个显示器）+ WM_EXITSIZEMOVE（拖动结束 → 增强会话协商分辨率）──
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO && _isFullScreen)
            {
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref mi))
                    {
                        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                        mmi.ptMaxPosition.X = 0;   // 相对所在显示器左上角
                        mmi.ptMaxPosition.Y = 0;
                        mmi.ptMaxSize.X = mi.rcMonitor.Right - mi.rcMonitor.Left;   // 整个显示器（非工作区）
                        mmi.ptMaxSize.Y = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                        Marshal.StructureToPtr(mmi, lParam, true);
                        handled = true;
                    }
                }
            }
            else if (msg == WM_EXITSIZEMOVE && _vm.IsEnhancedMode && !_vm.IsFullScreen)
            {
                NegotiateResolution();   // 用户结束拖动 → 协商一次（事件驱动，无防抖定时器）
            }
            return IntPtr.Zero;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO { public POINT ptReserved; public POINT ptMaxSize; public POINT ptMaxPosition; public POINT ptMinTrackSize; public POINT ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);
        [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
    }
}
