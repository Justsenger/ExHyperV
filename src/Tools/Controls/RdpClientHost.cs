using System;
using System.ComponentModel;
using System.Windows.Forms.Integration;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 通用 RDP 承载控件：在 WPF 里直接托管系统 mstscax ActiveX，依赖原生事件而非轮询。
    /// 不含 Hyper-V 专有逻辑——连接配方由调用方通过 <see cref="RdpConnectionSettings"/> 注入；
    /// 不反向依赖 ViewModel（全屏等状态以事件/方法暴露，由消费方桥接）。
    /// </summary>
    public class RdpClientHost : WindowsFormsHost
    {
        private readonly MsRdpAxHost _ax = new();
        private readonly BlackSurfacePanel _surface = new();
        private readonly BlackSurfacePanel _axWrapper = new();
        // WinForms 遮罩在连接完成前覆盖 mstscax，避免 ActiveX 初始化时闪出白底。
        // 必须盖在"包裹了 _ax 的容器(axWrapper)"上，而不是和裸 _ax 当兄弟——裸 ActiveX 会盖过兄弟控件。
        private readonly System.Windows.Forms.Panel _curtain = new()
        {
            Dock = System.Windows.Forms.DockStyle.Fill,
            BackColor = System.Drawing.Color.Black,
            Visible = true,
        };
        private RdpConnectionSettings? _pending;
        private bool _ready;
        private bool _manageDesktopPresentation;
        private bool _preferNativePixels;
        private int _desktopPixelWidth;
        private int _desktopPixelHeight;
        private bool _presentationQueued;
        private bool _topAlignedWhenFits;

        /// <summary>已连接（OnConnected）。</summary>
        public event Action? Connected;
        /// <summary>远程用户已通过 Windows 登录界面并完成登录。</summary>
        public event Action? LoginCompleted;
        /// <summary>已断开（OnDisconnected），参数为 RDP 断开原因码。</summary>
        public event Action<int>? Disconnected;
        /// <summary>远端桌面尺寸变化（OnRemoteDesktopSizeChange）——取代旧实现 20ms 像素嗅探。</summary>
        public event Action<int, int>? RemoteSizeChanged;
        /// <summary>RDP 控件自身请求进/出全屏（OnEnter/LeaveFullScreenMode）——取代旧实现键盘钩子轮询。</summary>
        public event Action<bool>? FullScreenRequested;
        /// <summary>连接栏最小化按钮请求（容器处理全屏下）。</summary>
        public event Action? MinimizeRequested;
        /// <summary>连接栏关闭按钮请求（容器处理全屏下）。</summary>
        public event Action? CloseRequested;
        /// <summary>底层控件致命错误（OnFatalError），参数为错误码。</summary>
        public event Action<int>? FatalError;

        /// <summary>0=未连接 1=已连接 2=连接中。</summary>
        public int ConnectionState => _ax.ConnectionState;

        public RdpClientHost()
        {
            // 同时设置 WindowsFormsHost 的适配层背景，避免布局时露出默认背景。
            Background = System.Windows.Media.Brushes.Black;
            _surface.SizeChanged += (_, _) => UpdateDesktopBounds();
            _ax.Connected += () => { _curtain.Visible = false; Connected?.Invoke(); };        // 连接成功后隐藏遮罩
            _ax.LoginCompleted += () => LoginCompleted?.Invoke();
            _ax.Disconnected += r => { _curtain.Visible = true; Disconnected?.Invoke(r); };   // 断开或重连时显示遮罩
            _ax.RemoteDesktopSizeChanged += (w, h) => RemoteSizeChanged?.Invoke(w, h);
            _ax.EnteredFullScreen += () => FullScreenRequested?.Invoke(true);
            _ax.LeftFullScreen += () => FullScreenRequested?.Invoke(false);
            _ax.MinimizeRequested += () => MinimizeRequested?.Invoke();
            _ax.CloseRequested += () => CloseRequested?.Invoke();
            _ax.SizeChanged += (_, _) => QueueDesktopPresentation();
            _ax.FatalError += code => FatalError?.Invoke(code);

            // 必须在 BeginInit/EndInit 之前订阅——EndInit 可能同步创建句柄，晚订阅会错过事件。
            _ax.HandleCreated += (s, e) =>
            {
                _ready = true;
                if (_pending is { } p) { _pending = null; _ax.ApplyAndConnect(p); }
            };

            // ActiveX 须经 ISupportInitialize 正确激活，否则 WPF 合成时崩 (UCEERR_RENDERTHREADFAILURE)。
            // ActiveX 用 Dock.Fill 由框架按 DPI 正确铺满承载控件（手动定位会因 DPI 坐标不一致导致纵横比错算 → 双重信箱）。
            // 黑底容器（对齐旧实现 _winFormsContainer.BackColor = Black）：SmartSizing 关闭后画面原生不缩放，
            // 周围空白显示黑色、窗口/全屏一致。
            _surface.Dock = System.Windows.Forms.DockStyle.Fill;
            _axWrapper.Dock = System.Windows.Forms.DockStyle.Fill;
            _ax.Dock = System.Windows.Forms.DockStyle.Fill;
            _ax.BackColor = System.Drawing.Color.Black;
            ((ISupportInitialize)_ax).BeginInit();
            _axWrapper.Controls.Add(_ax);
            ((ISupportInitialize)_ax).EndInit();
            _surface.Controls.Add(_curtain);
            _surface.Controls.Add(_axWrapper);   // 同一原生黑底容器负责画面外留黑区域
            _curtain.BringToFront();
            Child = _surface;

            if (_ax.IsHandleCreated) _ready = true;
        }

        /// <summary>用给定配方连接。OCX 未就绪时排队，句柄创建后自动补发。</summary>
        public void Connect(RdpConnectionSettings settings)
        {
            _manageDesktopPresentation = false;
            _axWrapper.Dock = System.Windows.Forms.DockStyle.Fill;
            _curtain.Visible = true;
            if (_ready || _ax.IsHandleCreated) _ax.ApplyAndConnect(settings);
            else _pending = settings;
        }

        public void Disconnect() => _ax.DisconnectSafe();

        /// <summary>
        /// 关窗前安全拆除：断开 → 泵消息等会话静默(ConnectionState→0) → Dispose。
        /// 须在窗口销毁前(OnClosing)调用：此时 UI 线程仍能泵消息，OnDisconnected 得以落地、会话线程退出，
        /// 随后 Dispose 走到 AxHost.InPlaceDeactivate 时控件已静默、无需等内部状态，不会死锁。
        /// 改在 OnClosed(WmDestroy 期)Dispose 则线程不泵消息且会话未静默，InPlaceDeactivate 死等 → UI 线程死锁。
        /// </summary>
        public void ShutdownAndDispose()
        {
            try
            {
                _ax.DisconnectSafe();
                var deadline = DateTime.UtcNow.AddSeconds(3);   // localhost VMBus 断开通常 <1s，3s 为上限
                while (_ax.ConnectionState != 0 && DateTime.UtcNow < deadline)
                {
                    System.Windows.Forms.Application.DoEvents();   // 泵 Win32 消息，让 OnDisconnected(COM 事件)落地
                    System.Threading.Thread.Sleep(15);
                }
            }
            catch { /* OCX 未就绪/已断开 */ }
            Dispose();   // 会话已静默，InPlaceDeactivate 可干净完成
        }

        /// <summary>增强会话改分辨率（不重连）。</summary>
        public bool Resize(int width, int height, double dpiScale) => _ax.SetResolution(width, height, dpiScale);

        public bool TryGetRemoteDesktopSize(out int width, out int height) => _ax.TryGetRemoteDesktopSize(out width, out height);

        /// <summary>动态开关 SmartSizing（基本会话：VM 分辨率超出画面区时开=缩放铺满，否则关=原生清晰）。</summary>
        public void SetSmartSizing(bool on)
        {
            _manageDesktopPresentation = false;
            _axWrapper.Dock = System.Windows.Forms.DockStyle.Fill;
            _ax.SetSmartSizing(on);
        }

        /// <summary>已确认远端尺寸且原生承载区恰好等大时使用 1:1；其余情况保留防裁切适配。</summary>
        public void ConfigureDesktopPresentation(bool preferNativePixels, int desktopWidth, int desktopHeight, bool topAlignedWhenFits = false)
        {
            _manageDesktopPresentation = true;
            _preferNativePixels = preferNativePixels;
            _desktopPixelWidth = desktopWidth;
            _desktopPixelHeight = desktopHeight;
            _topAlignedWhenFits = topAlignedWhenFits;
            QueueDesktopPresentation();
        }

        private void QueueDesktopPresentation()
        {
            if (!_manageDesktopPresentation || _presentationQueued) return;
            _presentationQueued = true;
            // 合并同一轮布局的中间尺寸，只按最终原生像素决定是否缩放。
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
            {
                _presentationQueued = false;
                if (!_ax.IsDisposed) ApplyDesktopPresentation();
            }));
        }

        private void ApplyDesktopPresentation()
        {
            if (!_manageDesktopPresentation) return;
            UpdateDesktopBounds();
            // 使用 WinForms 原生客户区像素，而非 WPF DIP 或请求值。
            // SizeChanged 再核对一次，避免尚未完成布局时误判为 1:1。
            bool native = _preferNativePixels && _desktopPixelWidth > 0 && _desktopPixelHeight > 0
                && _ax.ClientSize.Width == _desktopPixelWidth
                && _ax.ClientSize.Height == _desktopPixelHeight;
            _ax.SetSmartSizing(!native);
        }

        private void UpdateDesktopBounds()
        {
            if (!_manageDesktopPresentation || _axWrapper.IsDisposed) return;
            // 全部采用同一个原生容器的物理像素，避免 WPF/DPI 两次换算。
            var bounds = GetDesktopBounds(_surface.ClientSize.Width, _surface.ClientSize.Height,
                _desktopPixelWidth, _desktopPixelHeight, _topAlignedWhenFits);
            _axWrapper.Dock = System.Windows.Forms.DockStyle.None;
            if (_axWrapper.Bounds != bounds) _axWrapper.Bounds = bounds;
        }

        private static System.Drawing.Rectangle GetDesktopBounds(int width, int height, int desktopWidth, int desktopHeight, bool topAlignedWhenFits)
        {
            if (width <= 0 || height <= 0) return System.Drawing.Rectangle.Empty;
            if (desktopWidth <= 0 || desktopHeight <= 0) return new(0, 0, width, height);
            double scale = Math.Min(1.0, Math.Min(width / (double)desktopWidth, height / (double)desktopHeight));
            int w = Math.Min(width, Math.Max(1, (int)Math.Floor(desktopWidth * scale)));
            int h = Math.Min(height, Math.Max(1, (int)Math.Floor(desktopHeight * scale)));
            bool fits = desktopWidth <= width && desktopHeight <= height;
            return new((width - w) / 2, topAlignedWhenFits && fits ? 0 : (height - h) / 2, w, h);
        }

        private sealed class BlackSurfacePanel : System.Windows.Forms.Panel
        {
            public BlackSurfacePanel()
            {
                BackColor = System.Drawing.Color.Black;
                SetStyle(System.Windows.Forms.ControlStyles.UserPaint
                    | System.Windows.Forms.ControlStyles.AllPaintingInWmPaint
                    | System.Windows.Forms.ControlStyles.OptimizedDoubleBuffer
                    | System.Windows.Forms.ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaintBackground(System.Windows.Forms.PaintEventArgs e)
            {
                e.Graphics.FillRectangle(System.Drawing.Brushes.Black, e.ClipRectangle);
            }
        }

        /// <summary>基本会话缩放：设 mstscax 原生 ZoomLevel(百分比)。复刻 VMConnect 的 IMsRdpExtendedSettings("ZoomLevel")。</summary>
        public void SetZoomLevel(uint percent) => _ax.SetZoomLevel(percent);

        /// <summary>同步全屏状态给底层控件（容器处理全屏时，按钮发起的全屏需要回灌给 mstscax，
        /// 使其内部状态/键盘捕获与窗口一致；热键发起的无需，由控件自身切换）。</summary>
        public void SetFullScreen(bool fullScreen) => _ax.SetFullScreen(fullScreen);
    }
}
