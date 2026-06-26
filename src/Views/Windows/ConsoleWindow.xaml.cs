using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
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
    /// 最大化("窗口全屏"，工作区) / 全屏(WindowStyle=None + 最大化铺满显示器、WM_NCHITTEST 屏蔽缩放边、关 Mica + 去 DWM 边框消白边)。
    /// 连接随 VM 运行状态走（复用 ViewModel 的状态轮询，断线/VM 重启自动重连，无额外定时器）。
    /// </summary>
    public partial class ConsoleWindow : FluentWindow
    {
        private const double TitleBarHeight = 42;   // 与 XAML ui:TitleBar 高度一致
        // 全屏热键配置点：Ctrl+Alt+<虚拟键>，默认 Enter(0x0D)。常用：Enter=0x0D / Space=0x20 / Break=0x03 / Pause=0x13。
        private const int FullScreenHotKeyVk = 0x0D;
        // 连接超时：localhost VMBus 正常连接 <1s，2s 余量足够；连不上(如不支持增强)即在此时限内放弃 → 快速回退基本会话。
        private const int ConnectTimeoutSeconds = 2;
        // 增强会话：画面四周留出的可抓取缩放边（DIP）。mstscax 画面是 airspace、会盖住窗口边缘的缩放热区，
        // 留这点边让边缘是 WPF(RdpArea)、能抓住拖动缩放。值越小越窄，但太小会抓不到缩放热区。
        private const double EnhancedResizeBorder = 3;

        private readonly ConsoleViewModel _vm;
        private bool _isFullScreen;               // 供 WM_GETMINMAXINFO 判断最大化铺满显示器还是工作区
        private bool _syncingFs;                  // 防止 mstscax→VM→mstscax 全屏状态回灌
        private bool _weInitiatedDisconnect;      // 标记我方主动断开(模式切换/VM 停止)，以免被当作"非预期断开"
        private bool _reconnectPending;           // 模式切换：断开完成(OnDisconnected)后再连，避免立即连被 mstscax 拒
        private bool _enhancedConnecting;         // 本次连接是否在尝试增强会话——没连上就断 → 回退基本会话
        private bool _pendingEnhancedInset;       // 切到增强后：连上(Connected)时把窗口放大一圈，立刻露出可抓取缩放边（增强复用基本分辨率时无 RemoteSizeChange，故挂在 Connected）
        private bool _userResizing;               // 用户正拖动改窗口大小(WM_ENTER..EXITSIZEMOVE 之间)——期间 RdpArea.SizeChanged 不协商，拖完在 WM_EXITSIZEMOVE 协商一次，避免每像素刷新
        private bool _windowFollowsResolution;    // 下拉改分辨率后置位：待画面真的变到新分辨率(RemoteSizeChanged)再让窗口跟随确认值；拖动会清掉(窗口归用户掌控)
        private WindowStyle _origWindowStyle;                     // 全屏前的 WindowStyle，退出恢复
        private WindowBackdropType _origBackdrop;                 // 全屏前的背景类型(Mica)，退出恢复
        private System.Windows.Media.Brush? _origBackground;      // 全屏前的窗口底色，退出恢复
        private bool _closing;                                    // 用户经连接栏关闭：抑制断开后的自动重连（避免"复活"）
        private bool _topHookAdded;                                // 顶边缩放钩子是否已在 ContentRendered 注册（只挂一次）

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
                _enhancedConnecting = false;   // 已连上（增强成功，或本就是基本）
                // 基本会话连上即应用当前缩放档（不能依赖 RemoteSizeChange——同分辨率重连时它不触发）。
                // 增强会话连上后【绝不】碰 ZoomLevel：mid-session 设 ZoomLevel 会和动态分辨率(UpdateSessionDisplaySettings)
                // 打架，致画面不随分辨率刷新、还被缩成灰信箱。进增强前的归零已在 IsEnhancedMode 分支(断开重连之前)做好。
                if (!_vm.IsEnhancedMode) ApplyBasicZoom();
                if (_pendingEnhancedInset && _vm.IsEnhancedMode && !_vm.IsFullScreen)
                {
                    _pendingEnhancedInset = false;
                    LayoutRdpHost();              // 增强复用基本分辨率时不触发 RemoteSizeChange，这里主动按当前分辨率把画面居中
                    EnsureEnhancedResizeBorder(); // 放大窗口露出可抓取缩放边
                }
            }));
            RdpHost.Disconnected += reason => Dispatcher.BeginInvoke(new Action(() =>
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
                if (reason == 1)   // reason=1=本地主动断开，且非我方发起(weInit)/非增强探测 → 用户点了连接栏关闭按钮，关闭控制台（否则被轮询重连"复活"）
                {
                    _closing = true;
                    this.Close();
                    return;
                }
                // VM 停止 / 掉线：保持窗口、黑布盖住（RdpClientHost 在断开时自动盖布）；由状态轮询在 VM 运行时自动重连。
                // 关闭控制台由用户点窗口关闭按钮完成（不从断开推断，避免 VM 停止误关）。
            }));
            RdpHost.FatalError += code => Dispatcher.BeginInvoke(new Action(() =>
            {
                System.Diagnostics.Debug.WriteLine($"[Rdp] 致命错误 code={code}");   // 黑布由 RdpClientHost 在断开时自动盖住，等轮询重连
            }));
            RdpHost.RemoteSizeChanged += (w, h) => Dispatcher.BeginInvoke(new Action(() =>
            {
                _vm.CurrentWidth = w; _vm.CurrentHeight = h;
                // 画面"真的"变到新分辨率了：若此前是下拉发起的协商，现在才让窗口跟随这个确认值(增强、窗口化)。
                // 拖动发起的协商不在此跟随(标记已在 WM_ENTERSIZEMOVE 清掉)，窗口仍由用户掌控。
                if (_windowFollowsResolution)
                {
                    _windowFollowsResolution = false;
                    if (_vm.IsEnhancedMode && !_vm.IsFullScreen && this.WindowState == WindowState.Normal)
                        FitToResolution(w, h);
                }
                LayoutRdpHost();
            }));
            RdpHost.FullScreenRequested += fs => Dispatcher.BeginInvoke(new Action(() =>
            {
                _syncingFs = true; _vm.IsFullScreen = fs; _syncingFs = false;   // 源自 mstscax 热键，只反映到 VM，不回灌（画面分辨率由 RdpArea.SizeChanged 协商）
            }));
            RdpHost.MinimizeRequested += () => Dispatcher.BeginInvoke(new Action(() => this.WindowState = WindowState.Minimized));
            RdpHost.CloseRequested += () => Dispatcher.BeginInvoke(new Action(() => { _closing = true; this.Close(); }));

            // 可用区域(RdpArea)变化 = 最大化/还原/全屏/退出全屏：重排画面对齐 + 增强会话按新区域重新协商分辨率填充。
            // 拖动改大小期间(_userResizing)不在此协商（避免每像素刷新 mstscax），拖完由 WM_EXITSIZEMOVE 协商一次。
            RdpArea.SizeChanged += (s, e) =>
            {
                LayoutRdpHost();
                if (_vm.IsEnhancedMode && !_userResizing && _vm.CurrentWidth > 0) NegotiateResolution();
            };
        }

        // HWND 就绪后挂钩 WndProc（全屏铺满显示器 + 拖动结束协商分辨率）。
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        }

        // 顶边缩放钩子在 ContentRendered（晚于 TitleBar 的 Loaded）注册 → 处于 FIFO 末位、末位 handled 取胜，
        // 才能用 HTTOP 压过 TitleBar 对顶部空白区返回的 HTCAPTION（日志证实早注册会被 TitleBar 覆盖）。
        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            if (_topHookAdded) return;
            _topHookAdded = true;
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(TopResizeHook);
        }

        // Ctrl+Alt+Enter 全屏切换的 WPF 兜底：用过缩放下拉后焦点落在工具栏(WPF)上时，mstscax 的 HotKeyFullScreen 收不到键
        // （它只在画面有焦点时拦截）→ 在窗口级补捕获，使焦点在 WPF 侧也能切全屏。画面有焦点时按键直达 mstscax 的 HWND、不进此处，
        // 交由其自身热键处理，二者不冲突。Alt 参与组合时 e.Key 会是 Key.System、真实键在 e.SystemKey，需还原。
        protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            var key = (e.Key == Key.System) ? e.SystemKey : e.Key;
            if (key == Key.Enter &&
                (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt))
            {
                _vm.IsFullScreen = !_vm.IsFullScreen;
                e.Handled = true;
            }
        }

        // 增强 + 窗口化时，窗口顶部 TopResizeGrip 像素内 → HTTOP，使顶边可上下拉动改分辨率（底边被任务栏盖住时的退路）。
        private IntPtr TopResizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_NCHITTEST && _vm.IsEnhancedMode && !_vm.IsFullScreen)
            {
                int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                if (GetWindowRect(hwnd, out RECT r) && y >= r.Top && y < r.Top + TopResizeGrip)
                {
                    handled = true;
                    return (IntPtr)HTTOP;
                }
            }
            return IntPtr.Zero;
        }

        // 状态轮询回调：让连接跟随 VM 运行状态。VM 停止时保持窗口等待，VM 一恢复即重连——复用既有 2s 轮询，无需额外定时器。
        // 经 Dispatcher 兜底确保在 UI 线程执行（SyncConnection 会碰 RdpHost）。
        private void OnVmPolled() => Dispatcher.BeginInvoke(new Action(() => SyncConnection(forceReconnect: false)));

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ConsoleViewModel.IsEnhancedMode):
                    // 切到增强【前】把 OCX 的 ZoomLevel 归 100：此刻仍是已连的基本会话、马上要断开重连，在这里设安全。
                    // 必须早于进入增强——增强靠动态分辨率，不能带基本会话残留的缩放；且一旦进了增强再 mid-session 设
                    // ZoomLevel 会和动态分辨率打架(画面不随分辨率刷新+灰信箱)，故归零只能在断开重连之前做、之后绝不碰。
                    if (_vm.IsEnhancedMode) RdpHost.SetZoomLevel(100);
                    _pendingEnhancedInset = _vm.IsEnhancedMode;      // 进入增强：连上后放大窗口露出可抓取边
                    SyncConnection(forceReconnect: true);            // 换 PCB，须断后重连
                    if (!_vm.IsFullScreen) ApplyWindowedLayout();
                    break;

                case nameof(ConsoleViewModel.RequestWidth):
                case nameof(ConsoleViewModel.RequestHeight):
                    if (_vm.IsEnhancedMode && _vm.RequestWidth > 0 && _vm.RequestHeight > 0)
                    {
                        // 只发协商请求；窗口【不在此】跟着改——否则登录界面等协商被忽略时，窗口空撑大、画面顶格留黑边。
                        // 置标记：等画面真的变到新分辨率(RemoteSizeChanged)再让窗口跟随确认值。
                        _windowFollowsResolution = true;
                        RdpHost.Resize(_vm.RequestWidth, _vm.RequestHeight);          // 顶部分辨率下拉 → mstscax 协商新分辨率
                    }
                    break;

                case nameof(ConsoleViewModel.IsFullScreen):
                    if (_vm.IsFullScreen) EnterFullScreen(); else ExitFullScreen();  // 窗口
                    // 进全屏：必须在 mstscax 进入全屏(SetFullScreen)之前把缩放归 100。连接栏的布局只在"进全屏那一刻"计算，
                    // 若此刻 ZoomLevel≠100，连接栏会按缩放态布局而消失、退不出去；事后再设 100 也救不回。退全屏不在此动(由 ApplyBasicZoom 还原档)。
                    if (_vm.IsFullScreen && !_vm.IsEnhancedMode) RdpHost.SetZoomLevel(100);
                    if (!_syncingFs) RdpHost.SetFullScreen(_vm.IsFullScreen);        // 按钮发起的才回灌 mstscax
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
            // WPF 原生全屏：WindowStyle=None + 最大化（WM_GETMINMAXINFO 把尺寸顶到整个显示器 rcMonitor、隐藏任务栏）。
            // 全程不碰 WindowChrome → 退出后标题栏拖动不丢；全屏四周可拖的缩放边由 WndProc 的 WM_NCHITTEST 统一返回 HTCLIENT 屏蔽。
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
            double dpiX = src.CompositionTarget.TransformToDevice.M11;
            double dpiY = src.CompositionTarget.TransformToDevice.M22;
            double scrW = pixelWidth / dpiX, scrH = pixelHeight / dpiY;   // 画面 DIP 尺寸
            if (!_vm.IsEnhancedMode)   // 基本会话：钳到工作区并保宽高比——任一边超出宿主就按比例缩小，画面由 SmartSizing 缩放铺满，不冲出壳子
            {
                var wa = SystemParameters.WorkArea;
                double scale = Math.Min(1.0, Math.Min(wa.Width / scrW, (wa.Height - TitleBarHeight) / scrH));
                scrW *= scale; scrH *= scale;
            }
            this.Width = scrW;
            this.Height = scrH + TitleBarHeight;
            // 高缩放下窗口接近工作区大小，原位置不变会冲出屏幕(标题栏/边角够不到、即"冲烂") → 钳回工作区内保持完整可见
            var area = SystemParameters.WorkArea;
            if (this.Left + this.Width > area.Right) this.Left = Math.Max(area.Left, area.Right - this.Width);
            if (this.Top + this.Height > area.Bottom) this.Top = Math.Max(area.Top, area.Bottom - this.Height);
            if (this.Left < area.Left) this.Left = area.Left;
            if (this.Top < area.Top) this.Top = area.Top;
        }

        /// <summary>摆放 RDP 宿主：全屏/增强铺满或贴合；基本会话按所选缩放档把画面缩放居中。
        /// 显式比例的"放大"由 ApplyBasicZoom 改窗口尺寸实现；此处只把画面缩到画面区内（mstscax 是 airspace、无法滚动，故不溢出）。</summary>
        private void LayoutRdpHost()
        {
            if (_vm.IsFullScreen && _vm.IsEnhancedMode)
            {
                // 增强全屏：画面已协商到显示器分辨率，宿主铺满。SmartSizing 必须关——否则从"最大化被吸附"态
                // (SmartSizing 开)进全屏会残留缩放，把正好 1:1 的全屏画面也磨糊。
                RdpHost.SetSmartSizing(false);
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
            if (!_vm.IsEnhancedMode)
            {
                // 基本会话：缩放走 mstscax 原生 ZoomLevel，此处按"实际能装下的有效比例"热设 + 把宿主控件摆成对应尺寸居中。
                // 有效比例 = 缩放档≤100% 取 min(档, 画面区能装下的比例)、>100% 放大档取档本身：
                //   · ≤100%(含自适应)：用户要看「整幅画面」，宿主工作区比画面小时收缩到刚好放下——不溢出、不出滚动条
                //     （回归旧 SmartSizing 行为；此前固定 ZoomLevel=档 致大画面遇小窗口溢出+自带滚动条）；
                //   · >100%：用户要「放大看局部」，画面本就该比窗口大，允许溢出+控件内滚动条（VMConnect 同款）。
                // SmartSizing 必须关：缩放走 ZoomLevel，二者互斥（留着会打架、并在控件大于画面时糊上 mstscax 的 #CBCBCB 信箱）。
                int areaW = (int)Math.Round(RdpArea.ActualWidth * dpiX);
                int areaH = (int)Math.Round(RdpArea.ActualHeight * dpiY);
                double userScale = BasicZoomPercent() / 100.0;
                double fitScale = Math.Min(areaW / (double)vmW, areaH / (double)vmH);
                double sc = userScale <= 1.0 ? Math.Min(userScale, fitScale) : userScale;
                RdpHost.SetZoomLevel((uint)Math.Max(1, (int)Math.Round(sc * 100)));   // 有效比例（MsRdpAxHost 内缓存去重，仅真变时穿透 OCX）
                RdpHost.SetSmartSizing(false);
                RdpHost.HorizontalAlignment = HorizontalAlignment.Center;
                RdpHost.VerticalAlignment = VerticalAlignment.Center;
                RdpHost.Width = Math.Min(areaW, vmW * sc) / dpiX;
                RdpHost.Height = Math.Min(areaH, vmH * sc) / dpiY;
                return;
            }
            // 增强 + 窗口化/最大化：画面原生。但若来宾把分辨率吸附到比画面区大的标准值
            // （如最大化要 1920×990、来宾回 1920×1080），居中摆放会向上溢出盖住标题栏 →
            // 此时开 SmartSizing 把画面缩进画面区、保宽高比、居中（标题栏安全、整体可见、鼠标映射准）。
            int eAreaW = (int)Math.Round(RdpArea.ActualWidth * dpiX);
            int eAreaH = (int)Math.Round(RdpArea.ActualHeight * dpiY);
            bool eFits = vmW <= eAreaW + 2 && vmH <= eAreaH + 2;
            RdpHost.SetSmartSizing(!eFits);
            RdpHost.HorizontalAlignment = HorizontalAlignment.Center;
            if (!eFits)
            {
                double scale = Math.Min(eAreaW / (double)vmW, eAreaH / (double)vmH);
                RdpHost.VerticalAlignment = VerticalAlignment.Center;
                RdpHost.Width = vmW * scale / dpiX;
                RdpHost.Height = vmH * scale / dpiY;
            }
            else
            {
                // 画面 ≤ 画面区，原生。普通窗口化：顶贴标题栏（无上间隙，可抓取边在左/右/底）；最大化：居中。
                bool topFlush = this.WindowState == WindowState.Normal;
                RdpHost.VerticalAlignment = topFlush ? VerticalAlignment.Top : VerticalAlignment.Center;
                RdpHost.Width = vmW / dpiX;
                RdpHost.Height = vmH / dpiY;
            }
        }

        /// <summary>基本会话缩放：显式比例 → 把窗口缩放到 原生×比例（FitToResolution 内部钳到工作区）；
        /// 适应窗口/最大化/全屏 → 不动窗口、仅按当前画面区重排。放大靠撑大窗口实现——mstscax 是 airspace、无法用 ScrollViewer 滚动。</summary>
        private void ApplyBasicZoom()
        {
            if (_vm.IsEnhancedMode) return;
            int pct = BasicZoomPercent();
            // ZoomLevel 不在此设——改由 LayoutRdpHost 按"实际能装下的有效比例"热设（窗口装不下时收缩、不溢出）。此处只把窗口撑到 原生×档。
            if (!_vm.IsFullScreen && this.WindowState == WindowState.Normal
                && _vm.CurrentWidth > 0 && _vm.CurrentHeight > 0)
            {
                // 窗口随缩放（VMConnect 同款；MinWidth/MinHeight 兜底防过小；FitToResolution 内部再钳到工作区）→ SizeChanged → LayoutRdpHost 重排。
                FitToResolution(_vm.CurrentWidth * pct / 100, _vm.CurrentHeight * pct / 100);
            }
            LayoutRdpHost();
        }

        /// <summary>当前基本会话缩放百分比(25–500)："自动"/空 → 后台算"不超屏的最大档"(见 AutoZoomPercent)；"N%" → N；兜底 100。
        /// 全屏不再强制 100：进全屏瞬间先归 100 让 mstscax 把连接栏布局好(见 IsFullScreen 分支)、SetFullScreen 后再 bump 回此档，
        /// 试验"全屏既缩放又留连接栏"。逃生键 Ctrl+Alt+Enter 始终可退、不怕困住。</summary>
        private int BasicZoomPercent()
        {
            string z = _vm.SelectedZoom;
            if (string.IsNullOrEmpty(z) || z == Properties.Resources.ConsoleWindow_ZoomAuto)
                return AutoZoomPercent();
            return int.TryParse(z.TrimEnd('%', ' '), out int p) ? ClampZoom(p) : 100;
        }

        // "自动"缩放：纯内存算术挑档——从大到小遍历缩放档，取第一个"画面×该档 + 标题栏"能完整放进工作区(不超屏)的。
        // 只读 VM 分辨率/工作区/DPI 几个即时值在内存里比大小，绝不逐档应用到 UI（无闪烁、不撑窗口、不触发布局往返），微秒级返回。
        // 结果恒落在 ZoomOptions 现有档位上(非连续魔法数)；VM 分辨率未知回落 100，连最小档都塞不下用 25。
        private static readonly int[] AutoZoomCandidates = { 500, 400, 300, 200, 150, 125, 100, 75, 50, 25 };
        private int AutoZoomPercent()
        {
            int vmW = _vm.CurrentWidth, vmH = _vm.CurrentHeight;
            if (vmW <= 0 || vmH <= 0) return 100;
            var src = PresentationSource.FromVisual(this);
            double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            var wa = SystemParameters.WorkArea;   // 工作区(DIP)，即时读、不触发布局
            foreach (int p in AutoZoomCandidates)
            {
                double winW = vmW * (p / 100.0) / dpiX;
                double winH = vmH * (p / 100.0) / dpiY + TitleBarHeight;
                if (winW <= wa.Width && winH <= wa.Height) return p;
            }
            return 25;
        }

        private static int ClampZoom(int pct) => Math.Max(25, Math.Min(500, pct));   // 实测 mstscax 支持 >200，上限放到 500

        /// <summary>增强会话进入时：若画面四周余量不足 EnhancedResizeBorder，放大窗口补足——
        /// 画面分辨率保持不变（避免来宾端把非标准分辨率吸附回标准值），靠窗口比画面大一圈来露出可抓取的缩放边。</summary>
        private void EnsureEnhancedResizeBorder()
        {
            if (this.WindowState == WindowState.Maximized) return;
            if (_vm.CurrentWidth <= 0 || _vm.CurrentHeight <= 0) return;
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return;
            double dpiX = src.CompositionTarget.TransformToDevice.M11;
            double dpiY = src.CompositionTarget.TransformToDevice.M22;
            // 确定性设值（不读 RdpArea.ActualWidth，异步布局可能是旧值）：
            // 宽 = 画面 + 左右各一条边；高 = 画面 + 仅底部一条边 + 标题栏（顶部贴标题栏、无间隙）。
            this.Width = _vm.CurrentWidth / dpiX + 2 * EnhancedResizeBorder;
            this.Height = _vm.CurrentHeight / dpiY + EnhancedResizeBorder + TitleBarHeight;
        }

        /// <summary>增强会话：用户结束拖动窗口（WM_EXITSIZEMOVE）后，把当前画面区像素协商给 VM（桌面跟随窗口尺寸）。</summary>
        private void NegotiateResolution()
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget == null) return;
            double dpiX = src.CompositionTarget.TransformToDevice.M11;
            double dpiY = src.CompositionTarget.TransformToDevice.M22;
            // 全屏/最大化：画面占满 RdpArea、无可抓取边；普通窗口化：左右各留一条、底部留一条（顶部贴标题栏、无上边）。
            bool filled = _vm.IsFullScreen || this.WindowState == WindowState.Maximized;
            double bw = filled ? 0 : EnhancedResizeBorder;
            int px = (int)Math.Round((RdpArea.ActualWidth - 2 * bw) * dpiX);
            int py = (int)Math.Round((RdpArea.ActualHeight - bw) * dpiY);
            if (px >= 200 && py >= 200 && (px != _vm.CurrentWidth || py != _vm.CurrentHeight))
                RdpHost.Resize(px, py);
        }

        // ── CAD / 关闭 ──────────────────────────────────────────────────────
        private void OnSendCadRequested(object? sender, EventArgs e)
        {
            // CAD 按钮仅基本会话显示（增强会话 RDP 无法程序化发 SAS、按钮已隐藏）→ 这里只走基本会话的 WMI 硬件键盘。
            _ = VmInputService.SendCtrlAltDelAsync(_vm.VmId);
        }

        // 标题栏虚拟机名称：文字会接住鼠标，需显式 DragMove 才能拖窗口（穿透在 ui:TitleBar 里拖不动，对齐 1.4.3）。
        private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                this.DragMove();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _closing = true;        // 抑制 Dispose 期间可能再触发的 OnDisconnected 走自动重连
            RdpHost.Disconnect();   // 断开 RDP 会话（否则 mstscax/VMBus 会话残留到 GC）
            _vm.SendCadRequested -= OnSendCadRequested;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.Polled -= OnVmPolled;
            _vm.Dispose();
            // 确定性释放 mstscax COM 控件：WindowsFormsHost.Dispose → 容器 → AxHost → 释放底层 OCX。
            // WPF 不会在窗口关闭时自动 Dispose WindowsFormsHost（已知泄漏点）；不显式释放则反复开关控制台累积 COM/句柄。
            RdpHost.Dispose();
        }

        // ── WndProc：WM_GETMINMAXINFO（全屏铺满整个显示器）+ WM_EXITSIZEMOVE（拖动结束 → 增强会话协商分辨率）──
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 1;
        private const int HTTOP = 12;
        private const int TopResizeGrip = 10;   // 顶边缩放热区高度（物理像素）
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
                _userResizing = true;    // 用户开始拖动改大小：期间不在 SizeChanged 协商
                _windowFollowsResolution = false;   // 拖动发起的协商不让窗口跟随(用户在掌控窗口尺寸)
            }
            else if (msg == WM_EXITSIZEMOVE)
            {
                _userResizing = false;
                if (_vm.IsEnhancedMode && !_vm.IsFullScreen) NegotiateResolution();   // 拖完 → 协商一次
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
    }
}
