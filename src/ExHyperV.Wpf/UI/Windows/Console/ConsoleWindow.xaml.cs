using ExHyperV.Application;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using Wpf.Ui.Controls;
using ExHyperV.Services;

namespace ExHyperV.UI.Windows.Console
{
    /// <summary>
    /// 控制台窗口：窗口管理和显示偏好；图像、输入与剪贴板由 Core 的 FreeRDP 连接提供。
    /// 三态由 <see cref="ConsoleViewModel"/> 驱动：窗口化(可调整大小，画面=VM 尺寸居中周围黑底) /
    /// 最大化("窗口全屏"，工作区) / 全屏(WindowStyle=None + 最大化铺满显示器、WM_NCHITTEST 屏蔽缩放边、关 Mica + 去 DWM 边框消白边)。
    /// 连接随 VM 运行状态走（复用 ViewModel 的状态轮询，断线/VM 重启自动重连，无额外定时器）。
    /// </summary>
    public partial class ConsoleWindow : FluentWindow
    {
        private const double TitleBarHeight = 42;   // 与 XAML ui:TitleBar 高度一致
        // Ctrl+Alt+Enter 是本地窗口快捷键，不送入来宾。
        private const int FullScreenHotKeyVk = 0x0D;
        // 连接超时：localhost VMBus 正常连接 <1s，2s 余量足够；连不上(如不支持增强)即在此时限内放弃 → 快速回退基本会话。
        private const int ConnectTimeoutSeconds = 2;
        // 统一控制窗口缩放热区和增强会话画面左/右/底留边（DIP），不再叠加独立热区。
        // 当前以 2 DIP 验证边缘拖拽；画面留边和缩放热区使用同一宽度。
        private const double ConsoleResizeBorder = 2;
        private const double ConsoleResizeCornerLength = 12; // 沿现有边缘延长角落热区，不增加边宽或画面留白
        private bool _windowFrameHookAdded;

        private readonly ConsoleViewModel _vm;
        private bool _isFullScreen;               // 供 WM_GETMINMAXINFO 判断最大化铺满显示器还是工作区
        private bool _syncingFs;                  // 防止画面快捷键与窗口状态重复回灌
        private bool _weInitiatedDisconnect;      // 标记我方主动断开(模式切换/VM 停止)，以免被当作"非预期断开"
        private bool _reconnectPending;           // 模式切换：旧连接释放完成后再连
        private bool _enhancedConnecting;         // 本次连接是否在尝试增强会话——没连上就断 → 回退基本会话
        private bool _pendingEnhancedInset;       // 切到增强后：连上(Connected)时把窗口放大一圈，立刻露出可抓取缩放边（增强复用基本分辨率时无 RemoteSizeChange，故挂在 Connected）
        private bool _userResizing;               // 用户进入移动/缩放循环(WM_ENTER..EXITSIZEMOVE 之间)——期间 RdpArea.SizeChanged 不协商
        private int _moveSizeStartWidth;           // 进入移动/缩放循环时的窗口物理宽度；退出时据此区分纯移动与真实改大小
        private int _moveSizeStartHeight;
        private bool _windowFollowsResolution;    // 下拉改分辨率后置位：待画面真的变到新分辨率(RemoteSizeChanged)再让窗口跟随确认值；拖动会清掉(窗口归用户掌控)
        private WindowStyle _origWindowStyle;                     // 全屏前的 WindowStyle，退出恢复
        private WindowBackdropType _origBackdrop;                 // 全屏前的背景类型(Mica)，退出恢复
        private System.Windows.Media.Brush? _origBackground;      // 全屏前的窗口底色，退出恢复
        private bool _closing;                                    // 用户经连接栏关闭：抑制断开后的自动重连（避免"复活"）

        public ConsoleWindow(string vmId, string vmName)
        {
            _vm = new ConsoleViewModel(vmId, vmName);
            this.DataContext = _vm;
            InitializeComponent();
            if (App.PerformanceMode)
            {
                WindowBackdropType = WindowBackdropType.None;
                SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
            }
            this.Title = vmName;

            _vm.SendCadRequested += OnSendCadRequested;
            _vm.OperationFailed += OnOperationFailed;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            _vm.Polled += OnVmPolled;   // 每次状态轮询：让连接与 VM 运行状态一致（含断线/VM 重启后重连）
            _vm.ResolutionChangeRequested += (w, h) =>
            {
                if (!_vm.IsEnhancedMode || w <= 0 || h <= 0) return;
                SettingsService.SaveDefaultConsoleResolution(w, h); // 用户明确选择，立即保存；不等待来宾是否接受
                RequestEnhancedResolution(w, h, followWindow: true);
            };

            // WPF 适配器转交 Core 连接事件。
            RdpHost.Connected += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                _enhancedConnecting = false;   // 已连上（增强成功，或本就是基本）
                // 基本会话连上即应用当前缩放档（不能依赖 RemoteSizeChange——同分辨率重连时它不触发）。
                // 增强会话按真实远端尺寸适配；基本会话保留用户缩放档。
                if (!_vm.IsEnhancedMode) ApplyBasicZoom();
                else LayoutRdpHost();
                if (_pendingEnhancedInset && _vm.IsEnhancedMode && !_vm.IsFullScreen)
                {
                    LayoutRdpHost();              // 增强复用基本分辨率时不触发 RemoteSizeChange，这里主动按当前分辨率把画面居中
                    EnsureEnhancedResizeBorder(); // 放大窗口露出可抓取缩放边
                    // 布局完成前禁止按中间窗口尺寸协商分辨率。
                    Dispatcher.BeginInvoke(new Action(() => _pendingEnhancedInset = false),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
                else _pendingEnhancedInset = false;
            }));
            RdpHost.LoginCompleted += () => Dispatcher.BeginInvoke(new Action(() =>
            {
                OnEnhancedLoginCompleted();
            }));
            RdpHost.ResolutionRejected += () =>
            {
                if (_resolutionPending) _resolutionRequestAccepted = false;
            };
            RdpHost.Disconnected += reason => Dispatcher.BeginInvoke(new Action(() =>
            {
                ResetEnhancedResolution();
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
                // VM 停止或掉线时保留窗口并显示连接遮罩，状态轮询在 VM 恢复后重连。
                // 关闭控制台由用户点窗口关闭按钮完成（不从断开推断，避免 VM 停止误关）。
            }));
            RdpHost.FatalError += code => Dispatcher.BeginInvoke(new Action(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[Rdp] 致命错误 code={code}");
            }));
            RdpHost.RemoteSizeChanged += (w, h) => Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyRemoteDesktopSize(w, h);
            }));
            RdpHost.FullScreenRequested += fs => Dispatcher.BeginInvoke(new Action(() =>
            {
                _syncingFs = true; _vm.IsFullScreen = fs; _syncingFs = false;
            }));
            RdpHost.MinimizeRequested += () => Dispatcher.BeginInvoke(new Action(() => this.WindowState = WindowState.Minimized));
            RdpHost.CloseRequested += () => Dispatcher.BeginInvoke(new Action(() => { if (_closing) return; _closing = true; this.Close(); }));

            // 可用区域(RdpArea)变化 = 最大化/还原/全屏/退出全屏：重排画面对齐 + 增强会话按新区域重新协商分辨率填充。
            // 拖动期间不逐像素请求来宾改分辨率，拖完由 WM_EXITSIZEMOVE 协商一次。
            RdpArea.SizeChanged += (s, e) =>
            {
                LayoutRdpHost();
                if (_vm.IsEnhancedMode && !_userResizing
                    && (!_windowFollowsResolution || _vm.IsFullScreen || WindowState == WindowState.Maximized)
                    && !_pendingEnhancedInset && _vm.CurrentWidth > 0) NegotiateResolution();
            };
        }

        // HWND 就绪后挂钩 WndProc（全屏铺满显示器 + 拖动结束协商分辨率）。
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            SetOpaqueCompositionBackground();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        }

        // 晚于 TitleBar.Loaded 注册，避免其普通边缘判定覆盖四角的斜向判定。
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            SetOpaqueCompositionBackground();
            if (_windowFrameHookAdded) return;
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (source == null) return;
            source.AddHook(WindowFrameHook);
            _windowFrameHookAdded = true;
        }

        private IntPtr WindowFrameHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // None 表示整个窗口都是自绘客户区，WindowChrome 在这里也不修改矩形，
            // 但默认返回 WVR_REDRAW，导致缩放时整块 WPF 外壳失效，而 RDP 子窗口仍在。
            // 返回 0 保留旧客户区像素；WM_SIZE/WPF 布局及正常绘制继续处理。
            // 最大化/全屏/销毁及保留系统边框的情况仍完全交给 WindowChrome。
            if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero && !_closing
                && !_vm.IsFullScreen && WindowState == WindowState.Normal
                && WindowChrome.GetWindowChrome(this)?.NonClientFrameEdges == NonClientFrameEdges.None)
            {
                handled = true;
                return IntPtr.Zero;
            }

            if (msg != WM_NCHITTEST || _closing || _vm.IsFullScreen
                || WindowState != WindowState.Normal
                || (ResizeMode != ResizeMode.CanResize && ResizeMode != ResizeMode.CanResizeWithGrip))
                return IntPtr.Zero;
            if (!GetWindowRect(hwnd, out RECT rect)) return IntPtr.Zero;

            long position = lParam.ToInt64();
            int x = (short)(position & 0xFFFF) - rect.Left;
            int y = (short)((position >> 16) & 0xFFFF) - rect.Top;
            int hit = GetResizeCornerHit(x, y, rect.Right - rect.Left, rect.Bottom - rect.Top, GetDpiScale());
            if (hit == 0) return IntPtr.Zero; // 普通边缘、标题栏和客户区继续走原来的判定。
            handled = true;
            return (IntPtr)hit;
        }

        private static int GetResizeCornerHit(int x, int y, int width, int height, double dpiScale)
        {
            if (width <= 0 || height <= 0 || x < 0 || y < 0 || x >= width || y >= height
                || !double.IsFinite(dpiScale) || dpiScale <= 0) return 0;
            double border = Math.Ceiling(ConsoleResizeBorder * dpiScale);
            double cornerX = Math.Min(width / 2.0, Math.Ceiling(ConsoleResizeCornerLength * dpiScale));
            double cornerY = Math.Min(height / 2.0, Math.Ceiling(ConsoleResizeCornerLength * dpiScale));
            bool left = x < border, right = x >= width - border;
            bool top = y < border, bottom = y >= height - border;
            // L 形：只在 2 DIP 边缘内改为斜向拖拽，不把角落内部的 RDP 画面变成拖拽区。
            if ((left && y < cornerY) || (top && x < cornerX)) return HTTOPLEFT;
            if ((right && y < cornerY) || (top && x >= width - cornerX)) return HTTOPRIGHT;
            if ((left && y >= height - cornerY) || (bottom && x < cornerX)) return HTBOTTOMLEFT;
            if ((right && y >= height - cornerY) || (bottom && x >= width - cornerX)) return HTBOTTOMRIGHT;
            return 0;
        }

        // FluentWindow 会在背景类型变化时重建 WindowChrome；每次都应用同一个边宽，
        // 避免进入/退出全屏后又恢复库默认的 4 DIP。其余标题栏配置保持不变。
        protected override void SetWindowChrome()
        {
            base.SetWindowChrome();
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome != null)
            {
                if (chrome.IsFrozen) chrome = (WindowChrome)chrome.Clone();
                chrome.ResizeBorderThickness = new Thickness(ConsoleResizeBorder);
                // FluentWindow 的 None 仍使用非零玻璃边距，会令 WPF 合成底色透明。
                // 原生 RDP 子窗口需要真正不透明的宿主，不让拖动时露出玻璃背景。
                chrome.GlassFrameThickness = new Thickness(0);
                WindowChrome.SetWindowChrome(this, chrome);
            }
            SetOpaqueCompositionBackground();
        }

        private void SetOpaqueCompositionBackground()
        {
            if (PresentationSource.FromVisual(this) is HwndSource source && source.CompositionTarget != null)
                source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Black;
        }

        // 状态轮询回调：让连接跟随 VM 运行状态。VM 停止时保持窗口等待，VM 一恢复即重连——复用既有 2s 轮询，无需额外定时器。
        // SyncConnection 会访问 RdpHost，必须在 UI 线程执行。
        private void OnVmPolled() => Dispatcher.BeginInvoke(new Action(() => SyncConnection(forceReconnect: false)));

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ConsoleViewModel.IsEnhancedMode):
                    // 基本缩放与增强的来宾分辨率分开保存，切换时清除当前画面的基本缩放。
                    if (_vm.IsEnhancedMode && RdpHost.ConnectionState != 0) RdpHost.SetZoomLevel(100);
                    _pendingEnhancedInset = _vm.IsEnhancedMode;      // 进入增强：连上后放大窗口露出可抓取边
                    SyncConnection(forceReconnect: true);            // 换 PCB，须断后重连
                    if (!_vm.IsFullScreen) ApplyWindowedLayout();
                    break;

                case nameof(ConsoleViewModel.IsFullScreen):
                    if (_vm.IsFullScreen) EnterFullScreen(); else ExitFullScreen();  // 窗口
                    // 全屏后按新的可用区域应用同一缩放偏好。
                    if (_vm.IsFullScreen && !_vm.IsEnhancedMode) RdpHost.SetZoomLevel(100);
                    if (!_syncingFs) RdpHost.SetFullScreen(_vm.IsFullScreen);
                    if (!_vm.IsEnhancedMode) ApplyBasicZoom();                       // 基本会话：进全屏布局(缩放已 100)、退全屏还原缩放档（同 VMConnect）
                    else LayoutRdpHost();                                            // 增强：全屏铺满 / 窗口化缩到 VM 居中
                    break;

                case nameof(ConsoleViewModel.CurrentWidth):
                case nameof(ConsoleViewModel.CurrentHeight):
                    if (!_vm.IsEnhancedMode) ApplyBasicZoom();   // 基本会话：窗口跟随 VM 分辨率 × 当前缩放档（增强靠下拉/拖动两条专属路径）
                    break;

                case nameof(ConsoleViewModel.SelectedZoom):
                    ApplyBasicZoom();   // 基本会话缩放档变更 → 调整窗口尺寸(显式比例放大窗口) + 重排画面
                    break;
            }
        }

        // 让 RDP 连接与 VM 运行状态一致。forceReconnect=true 时即使已连也先断（会话模式切换换 PCB 用）。
        private void SyncConnection(bool forceReconnect)
        {
            if (_closing) return;   // 正在关闭：不再重连（避免连接栏关闭后被轮询重连"复活"）
            if (forceReconnect) ResetEnhancedResolution();
            if (forceReconnect && RdpHost.ConnectionState != 0)
            {
                // 模式切换先等旧连接释放，再建立新连接。
                _weInitiatedDisconnect = true;
                _reconnectPending = true;
                RdpHost.Disconnect();
                return;
            }

            if (_vm.IsRunning)
            {
                if (RdpHost.ConnectionState == 0)   // 该连而未连（force+已连的已在上面断开并挂起重连）
                {
                    ResetEnhancedResolution();
                    _enhancedConnecting = _vm.IsEnhancedMode;   // 记下本次是否在尝试增强（失败则回退基本）
                    uint desktopScale = (uint)Math.Clamp(Math.Round(GetDpiScale() * 100.0), 100, 500);
                    int initialWidth = _vm.IsEnhancedMode ? _vm.InitialEnhancedWidth : _vm.CurrentWidth;
                    int initialHeight = _vm.IsEnhancedMode ? _vm.InitialEnhancedHeight : _vm.CurrentHeight;
                    if (_vm.IsEnhancedMode)
                    {
                        _postLoginWidth = initialWidth;
                        _postLoginHeight = initialHeight;
                    }
                    RdpHost.Connect(BuildHyperVSettings(_vm.VmId, _vm.VmName, _vm.IsEnhancedMode, initialWidth, initialHeight, desktopScale));
                }
            }
            else if (RdpHost.ConnectionState != 0)   // VM 停了但还连着 → 断（保持窗口，等轮询到 VM 重启再连）
            {
                ResetEnhancedResolution();
                _weInitiatedDisconnect = true;
                RdpHost.Disconnect();
            }
        }

        // 窗口只提交目标和显示选择，Hyper-V 端口/PCB/认证规则均在 Core。
        private static RdpConnectionSettings BuildHyperVSettings(string vmId, string vmName, bool enhanced, int reuseWidth, int reuseHeight, uint desktopScale)
        {
            return new RdpConnectionSettings
            {
                VmId = Guid.Parse(vmId),
                Enhanced = enhanced,
                FullScreenHotKeyVirtualKey = FullScreenHotKeyVk,
                ConnectionTimeoutSeconds = ConnectTimeoutSeconds,
                DesktopWidth = enhanced ? reuseWidth & ~1 : 0,
                DesktopHeight = enhanced ? reuseHeight : 0,
                DesktopScaleFactor = enhanced ? desktopScale : 100
            };
        }

        // ── 全屏 / 窗口尺寸 ─────────────────────────────────────────────────
        private void EnterFullScreen()
        {
            _isFullScreen = true;
            // WPF 原生全屏：WindowStyle=None + 最大化（WM_GETMINMAXINFO 把尺寸顶到整个显示器 rcMonitor、隐藏任务栏）。
            // 保留 WindowChrome 的标题栏配置；全屏四周缩放边由 WndProc 的 WM_NCHITTEST 统一返回 HTCLIENT 屏蔽。
            // 关 Mica + 底色置黑 + 去 DWM 边框色 → 消除边缘白/灰。
            _origWindowStyle = this.WindowStyle;
            _origBackdrop = this.WindowBackdropType;
            _origBackground = this.Background;
            this.WindowBackdropType = WindowBackdropType.None;
            this.Background = System.Windows.Media.Brushes.Black;
            this.WindowStyle = WindowStyle.None;
            if (this.WindowState == WindowState.Maximized) this.WindowState = WindowState.Normal;
            this.WindowState = WindowState.Maximized;
            var hwnd = new WindowInteropHelper(this).Handle;
            uint noBorder = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(uint));
        }

        private void ExitFullScreen()
        {
            _isFullScreen = false;
            var hwnd = new WindowInteropHelper(this).Handle;
            uint defBorder = DWMWA_COLOR_DEFAULT;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref defBorder, sizeof(uint));   // 恢复 DWM 边框色
            this.WindowStyle = _origWindowStyle;
            this.WindowState = WindowState.Normal;
            this.Background = _origBackground;
            this.WindowBackdropType = _origBackdrop;
            ApplyWindowedLayout();
        }

        private void ApplyWindowedLayout()
        {
            if (_vm.IsFullScreen) return;
            this.ResizeMode = ResizeMode.CanResize;   // 窗口化恒可调整大小（原生双击最大化/拖动/贴边依赖于此）
            // 基本：窗口=VM 分辨率。增强：窗口尺寸不在此处动——初次进入由 Connected(EnsureEnhancedResizeBorder) 放大留边；
            // 退出全屏由 WPF 还原到全屏前尺寸、再经 RdpArea.SizeChanged 重新协商分辨率恢复留边。
            if (!_vm.IsEnhancedMode) FitToResolution(_vm.CurrentWidth, _vm.CurrentHeight);
        }

        /// <summary>窗口尺寸设为正好容纳 VM 分辨率（直接设 Width/Height，不用 SizeToContent——后者与最大化/全屏冲突）。</summary>
        private void FitToResolution(int pixelWidth, int pixelHeight)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0) return;
            if (this.WindowState == WindowState.Maximized) return;   // 最大化时别把窗口顶回分辨率尺寸
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return;
            double dpiX = GetDpiScale(), dpiY = dpiX;   // Win32 取真实 DPI，避开 TransformToDevice 首帧滞后成 100%
            double scrW = pixelWidth / dpiX, scrH = pixelHeight / dpiY;   // 画面 DIP 尺寸
            if (!_vm.IsEnhancedMode)   // 基本会话：钳到工作区并保宽高比——任一边超出承载区域就按比例缩小，画面由 SmartSizing 缩放铺满，不冲出壳子
            {
                var wa = SystemParameters.WorkArea;
                var overhead = GetWindowOverhead();
                double availableW = Math.Max(1, wa.Width - overhead.Width);
                double availableH = Math.Max(1, wa.Height - overhead.Height);
                double scale = Math.Min(1.0, Math.Min(availableW / scrW, availableH / scrH));
                scrW *= scale; scrH *= scale;
            }
            if (_vm.IsEnhancedMode)
            {
                // RdpArea 包含画面和可拖动边；窗口尺寸还需补偿 DWM/WPF 外壳。
                SetWindowForRdpArea(scrW + 2 * ConsoleResizeBorder, scrH + ConsoleResizeBorder);
            }
            else
            {
                SetWindowForRdpArea(scrW, scrH);
            }
            // 高缩放下将窗口位置限制在工作区内，确保标题栏和边角可见。
            var area = SystemParameters.WorkArea;
            if (this.Left + this.Width > area.Right) this.Left = Math.Max(area.Left, area.Right - this.Width);
            if (this.Top + this.Height > area.Bottom) this.Top = Math.Max(area.Top, area.Bottom - this.Height);
            if (this.Left < area.Left) this.Left = area.Left;
            if (this.Top < area.Top) this.Top = area.Top;
        }

        /// <summary>摆放 RDP 承载控件：全屏/增强铺满或贴合；基本会话按所选缩放档把画面缩放居中。
        /// 显式比例先调整窗口；超过可用区的放大画面由 WPF 滚动显示。</summary>
        private void LayoutRdpHost()
        {
            bool filled = _vm.IsFullScreen || WindowState == WindowState.Maximized;
            var content = GetRdpContentBounds(RdpArea.ActualWidth, RdpArea.ActualHeight, _vm.IsEnhancedMode, filled);
            // 用实际布局约束守住边缘，不能只在请求分辨率时扣除。
            // 先于尺寸有效性检查设置，连接前、拖动中以及远端拒绝改分辨率时同样有效。
            RdpHost.Margin = new Thickness(content.X, 0, content.X, content.X);
            RdpHost.MaxWidth = _vm.IsEnhancedMode ? content.Width : double.PositiveInfinity;
            RdpHost.MaxHeight = _vm.IsEnhancedMode ? content.Height : double.PositiveInfinity;
            if (_vm.IsEnhancedMode)
            {
                // 承载区铺满内框；适配器按实际帧尺寸保持画面比例和黑边。
                RdpHost.ConfigureDesktopPresentation(_enhancedDesktopConfirmed, _vm.CurrentWidth, _vm.CurrentHeight,
                    topAlignedWhenFits: !filled);
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
            double dpiX = GetDpiScale(), dpiY = dpiX;   // Win32 取真实 DPI，避开 TransformToDevice 首帧滞后成 100%
            if (!_vm.IsEnhancedMode)
            {
                // 基本会话按实际可容纳的比例摆放图像；放大档允许滚动。
                // 有效比例 = 缩放档≤100% 取 min(档, 画面区能装下的比例)、>100% 放大档取档本身：
                //   · ≤100%(含自适应)：用户要看「整幅画面」，承载区域比画面小时收缩到刚好放下——不溢出、不出滚动条
                //   · >100%：允许画面溢出并使用控件内滚动条。
                // 基本会话使用显式比例，不再套一层适应窗口。
                int areaW = (int)Math.Round(RdpArea.ActualWidth * dpiX);
                int areaH = (int)Math.Round(RdpArea.ActualHeight * dpiY);
                int userZoom = BasicZoomPercent();
                int fitZoom = Math.Max(1, (int)Math.Floor(Math.Min(
                    areaW * 100.0 / vmW,
                    areaH * 100.0 / vmH)));
                int effectiveZoom = userZoom <= 100 ? Math.Min(userZoom, fitZoom) : userZoom;
                double sc = effectiveZoom / 100.0;
                // 沿用整数百分比，向下取整避免边缘舍入导致意外滚动条。
                RdpHost.SetZoomLevel((uint)effectiveZoom);
                RdpHost.SetSmartSizing(false);
                RdpHost.HorizontalAlignment = HorizontalAlignment.Center;
                RdpHost.VerticalAlignment = VerticalAlignment.Center;
                RdpHost.Width = Math.Min(areaW, vmW * sc) / dpiX;
                RdpHost.Height = Math.Min(areaH, vmH * sc) / dpiY;
                return;
            }
        }

        /// <summary>基本会话缩放：显式比例 → 把窗口缩放到 原生×比例（FitToResolution 内部钳到工作区）；
        /// 适应窗口/最大化/全屏时只重排画面；超过工作区的放大档由适配器滚动显示。</summary>
        private void ApplyBasicZoom()
        {
            if (_vm.IsEnhancedMode) return;
            int pct = BasicZoomPercent();
            // ZoomLevel 不在此设——改由 LayoutRdpHost 按"实际能装下的有效比例"热设（窗口装不下时收缩、不溢出）。此处只把窗口撑到 原生×档。
            if (!_vm.IsFullScreen && this.WindowState == WindowState.Normal
                && _vm.CurrentWidth > 0 && _vm.CurrentHeight > 0)
            {
                // 调整窗口后由 SizeChanged 触发 LayoutRdpHost 重排。
                FitToResolution(_vm.CurrentWidth * pct / 100, _vm.CurrentHeight * pct / 100);
            }
            LayoutRdpHost();
        }

        /// <summary>当前基本会话缩放百分比(25–500)："自动"/空 → 后台算"不超屏的最大档"(见 AutoZoomPercent)；"N%" → N；兜底 100。
        /// 全屏仍沿用所选缩放档。
        /// Ctrl+Alt+Enter 可退出全屏。</summary>
        private int BasicZoomPercent()
        {
            string z = _vm.SelectedZoom;
            if (string.IsNullOrEmpty(z) || z == Properties.Resources.ConsoleWindow_ZoomAuto)
                return AutoZoomPercent();
            return int.TryParse(z.TrimEnd('%', ' '), out int p) ? ClampZoom(p) : 100;
        }

        // 自动缩放选择能完整放入工作区的最大档位，不逐档修改 UI。
        // 结果取自 ZoomOptions；分辨率未知时使用 100，最小档仍超出时使用 25。
        private static readonly int[] AutoZoomCandidates = { 500, 400, 300, 200, 150, 125, 100, 75, 50, 25 };
        private int AutoZoomPercent()
        {
            int vmW = _vm.CurrentWidth, vmH = _vm.CurrentHeight;
            if (vmW <= 0 || vmH <= 0) return 100;
            var src = PresentationSource.FromVisual(this);
            double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            var wa = SystemParameters.WorkArea;   // 工作区(DIP)，即时读、不触发布局
            var overhead = GetWindowOverhead();
            double availableW = Math.Max(1, wa.Width - overhead.Width);
            double availableH = Math.Max(1, wa.Height - overhead.Height);
            foreach (int p in AutoZoomCandidates)
            {
                double winW = vmW * (p / 100.0) / dpiX;
                double winH = vmH * (p / 100.0) / dpiY;
                if (winW <= availableW && winH <= availableH) return p;
            }
            return 25;
        }

        private static int ClampZoom(int pct) => Math.Max(25, Math.Min(500, pct));

        /// <summary>增强会话进入时：按照 ConsoleResizeBorder 统一设置画面边宽——
        /// 画面分辨率保持不变（避免来宾端把非标准分辨率吸附回标准值），靠窗口比画面大一圈来露出可抓取的缩放边。</summary>
        private void EnsureEnhancedResizeBorder()
        {
            if (this.WindowState == WindowState.Maximized) return;
            if (_vm.CurrentWidth <= 0 || _vm.CurrentHeight <= 0) return;
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return;
            double dpiX = GetDpiScale(), dpiY = dpiX;   // Win32 取真实 DPI，避开 TransformToDevice 首帧滞后成 100%
            // RdpArea 目标：画面 + 左右各一条边；高度为画面 + 底边（顶部贴标题栏、无上边）。
            // Window 到 RdpArea 之间的标题栏/DWM/WPF 外壳使用当前平台的实际测量值，不假定固定为 42px/0px。
            SetWindowForRdpArea(
                _vm.CurrentWidth / dpiX + 2 * ConsoleResizeBorder,
                _vm.CurrentHeight / dpiY + ConsoleResizeBorder);
        }

        /// <summary>当前窗口尺寸中不属于 RdpArea 的部分（标题栏、DWM/WPF 边框等，单位 DIP）。</summary>
        private Size GetWindowOverhead()
        {
            if (this.ActualWidth > 0 && this.ActualHeight > 0
                && RdpArea.ActualWidth > 0 && RdpArea.ActualHeight > 0)
            {
                return new Size(
                    Math.Max(0, this.ActualWidth - RdpArea.ActualWidth),
                    Math.Max(0, this.ActualHeight - RdpArea.ActualHeight));
            }

            // 首次布局完成后再使用远端尺寸校正。
            return new Size(0, TitleBarHeight);
        }

        /// <summary>按 RdpArea 的目标尺寸反推窗口尺寸，自动适配不同系统上的窗口外壳宽度。</summary>
        private void SetWindowForRdpArea(double targetWidth, double targetHeight)
        {
            var overhead = GetWindowOverhead();
            this.Width = targetWidth + overhead.Width;
            this.Height = targetHeight + overhead.Height;
        }

        /// <summary>增强会话：用户结束拖动窗口（WM_EXITSIZEMOVE）后，把当前画面区像素协商给 VM（桌面跟随窗口尺寸）。</summary>
        private void NegotiateResolution()
        {
            if (TryGetEnhancedViewport(out int px, out int py))
                RequestEnhancedResolution(px, py, followWindow: false);
        }

        private static Rect GetRdpContentBounds(double areaWidth, double areaHeight, bool enhanced, bool filled)
        {
            double border = enhanced && !filled ? ConsoleResizeBorder : 0;
            return new Rect(border, 0, Math.Max(0, areaWidth - 2 * border), Math.Max(0, areaHeight - border));
        }

        private bool TryGetEnhancedViewport(out int px, out int py)
        {
            px = py = 0;
            if (WindowState == WindowState.Minimized) return false;
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return false;
            double dpiX = GetDpiScale(), dpiY = dpiX;   // Win32 取真实 DPI，避开 TransformToDevice 首帧滞后成 100%
            // 全屏/最大化：画面占满 RdpArea、无可抓取边；普通窗口化：左右各留一条、底部留一条（顶部贴标题栏、无上边）。
            bool filled = _vm.IsFullScreen || this.WindowState == WindowState.Maximized;
            var content = GetRdpContentBounds(RdpArea.ActualWidth, RdpArea.ActualHeight, true, filled);
            px = (int)Math.Floor(content.Width * dpiX);
            py = (int)Math.Floor(content.Height * dpiY);
            return px >= 200 && py >= 200;
        }

        // ── CAD / 关闭 ──────────────────────────────────────────────────────
        private async void OnSendCadRequested(object? sender, EventArgs e)
        {
            try
            {
                var result = await VmInputService.SendCtrlAltDelAsync(_vm.TargetVmId);
                if (!result.Success) OnOperationFailed(result.Error);
            }
            catch (Exception ex) { OnOperationFailed(ex.Message); }
        }

        private void OnOperationFailed(string message)
        {
            if (!_closing)
                System.Windows.MessageBox.Show(this, FriendlyError.CleanLines(message),
                    Properties.Resources.Error_Common_OpFail,
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }

        private void OnConsolePickerClick(object sender, RoutedEventArgs e)
        {
            // Keyboard/UIA activation also raises Click. Keep the overflow host alive
            // while DropDownButton opens its nested menu; leaf actions still close it.
            e.Handled = true;
        }

        private void OnConsoleToolbarIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // Popups have their own HWND; explicitly close overflow when entering fullscreen.
            if (sender is System.Windows.Controls.ToolBar toolbar && !toolbar.IsVisible)
                toolbar.IsOverflowOpen = false;
        }

        // 标题栏虚拟机名称：文字会接住鼠标，需显式 DragMove 才能拖窗口（穿透在 ui:TitleBar 里拖不动，对齐 1.4.3）。
        private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        private bool _rdpTornDown;   // OnClosing 与 OnClosed 共用的幂等保护。
        private bool _closePending;
        private bool _windowClosed;

        // 不销毁仍在连接的窗口：先停止输入并异步等 Core 收尾，再完成这一次关闭。
        protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel || _rdpTornDown) return;
            e.Cancel = true;
            if (_closePending) return;
            _closePending = true;
            _closing = true;
            ResetEnhancedResolution();
            try { await RdpHost.ShutdownAndDisposeAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[FreeRDP] 窗口收尾失败：{ex}"); }
            if (_windowClosed || Dispatcher.HasShutdownStarted) return;
            _rdpTornDown = true;
            // 即使同步收尾也不重入 OnClosing；应用退出可能已强制关闭此窗口。
            _ = Dispatcher.BeginInvoke(new Action(() => { if (!_windowClosed) Close(); }));
        }

        protected override void OnClosed(EventArgs e)
        {
            _windowClosed = true;
            base.OnClosed(e);
            _closing = true;
            ResetEnhancedResolution();
            if (!_rdpTornDown) { _rdpTornDown = true; RdpHost.ShutdownAndDispose(); }
            _vm.SendCadRequested -= OnSendCadRequested;
            _vm.OperationFailed -= OnOperationFailed;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.Polled -= OnVmPolled;
            _vm.Dispose();

        }

        private void OnExitFullScreenClick(object sender, RoutedEventArgs e) => _vm.IsFullScreen = false;
        private void OnMinimizeConsoleClick(object sender, RoutedEventArgs e) => RdpHost.RequestMinimize();
        private void OnCloseConsoleClick(object sender, RoutedEventArgs e) => RdpHost.RequestClose();

        // ── WndProc：WM_GETMINMAXINFO（全屏铺满整个显示器）+ WM_EXITSIZEMOVE（拖动结束 → 增强会话协商分辨率）──
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;
        private const int WM_ENTERSIZEMOVE = 0x0231;
        private const int WM_EXITSIZEMOVE = 0x0232;
        private const int MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const int DWMWA_BORDER_COLOR = 34;          // Win11：窗口边框颜色（全屏置 None 去白边）
        private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;   // 不画边框
        private const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCHITTEST && _vm.IsFullScreen)
            {
                handled = true;
                return (IntPtr)HTCLIENT;   // 全屏：整窗算客户区，屏蔽缩放边 → 四周不可拖（TitleBar 全屏折叠、不竞争，本钩子结果即生效）
            }
            if (msg == WM_GETMINMAXINFO && _isFullScreen)
            {
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref mi))
                    {
                        // 最大化窗口客户区会被系统按边框(SM_CXSIZEFRAME+SM_CXPADDEDBORDER)内缩 ~8px/边，
                        // 致 RdpArea 比显示器小一圈、画面被 SmartSizing 缩糊/留缝。把窗口外扩这一圈、左上角负偏移 →
                        // 边框落到屏外、客户区正好=整显示器。
                        int fx = GetSystemMetrics(SM_CXSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
                        int fy = GetSystemMetrics(SM_CYSIZEFRAME) + GetSystemMetrics(SM_CXPADDEDBORDER);
                        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                        mmi.ptMaxPosition.X = -fx;
                        mmi.ptMaxPosition.Y = -fy;
                        mmi.ptMaxSize.X = (mi.rcMonitor.Right - mi.rcMonitor.Left) + 2 * fx;   // 整个显示器 + 两边边框
                        mmi.ptMaxSize.Y = (mi.rcMonitor.Bottom - mi.rcMonitor.Top) + 2 * fy;
                        Marshal.StructureToPtr(mmi, lParam, true);
                        handled = true;
                    }
                }
            }
            else if (msg == WM_ENTERSIZEMOVE)
            {
                _userResizing = true;    // 用户开始移动或改大小：期间不在 SizeChanged 协商
                if (GetWindowRect(hwnd, out RECT startRect))
                {
                    _moveSizeStartWidth = startRect.Right - startRect.Left;
                    _moveSizeStartHeight = startRect.Bottom - startRect.Top;
                }
                else
                {
                    _moveSizeStartWidth = _moveSizeStartHeight = 0;
                }
                _windowFollowsResolution = false;   // 拖动发起的协商不让窗口跟随(用户在掌控窗口尺寸)
            }
            else if (msg == WM_EXITSIZEMOVE)
            {
                _userResizing = false;
                bool sizeChanged = GetWindowRect(hwnd, out RECT endRect)
                    && (_moveSizeStartWidth <= 0
                        || endRect.Right - endRect.Left != _moveSizeStartWidth
                        || endRect.Bottom - endRect.Top != _moveSizeStartHeight);
                if (sizeChanged && _vm.IsEnhancedMode && !_vm.IsFullScreen)
                    NegotiateResolution();   // 只有真的拖边改大小才协商；拖标题栏移动窗口不改变分辨率
                _moveSizeStartWidth = _moveSizeStartHeight = 0;
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
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
        private const int SM_CXSIZEFRAME = 32, SM_CYSIZEFRAME = 33, SM_CXPADDEDBORDER = 92;   // 最大化全屏纠正客户区内缩用
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);   // 顶边缩放命中测试取窗口顶坐标
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);

        // GetDpiForWindow 可在首帧取得窗口所在显示器的 DPI，避免 WPF 转换矩阵尚未更新。
        private double GetDpiScale()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    uint dpi = GetDpiForWindow(hwnd);
                    if (dpi >= 48) return dpi / 96.0;
                }
            }
            catch { }
            var src = PresentationSource.FromVisual(this);
            return src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        }
    }
}
