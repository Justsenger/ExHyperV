using System;
using System.ComponentModel;
using System.Windows.Forms.Integration;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 通用 RDP 宿主控件：在 WPF 里直接托管系统 mstscax ActiveX，依赖原生事件而非轮询。
    /// 不含 Hyper-V 专有逻辑——连接配方由调用方通过 <see cref="RdpConnectionSettings"/> 注入；
    /// 不反向依赖 ViewModel（全屏等状态以事件/方法暴露，由消费方桥接）。
    /// 取代旧的 MsRdpExHost（491 行 + 20ms 轮询 + FindWindowEx + 键盘钩子轮询 + 手搓重连 + DataContext 倒置）。
    /// </summary>
    public class RdpClientHost : WindowsFormsHost
    {
        private readonly MsRdpAxHost _ax = new();
        private RdpConnectionSettings? _pending;
        private bool _ready;

        /// <summary>已连接（OnConnected）。</summary>
        public event Action? Connected;
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
            _ax.Connected += () => Connected?.Invoke();
            _ax.Disconnected += r => Disconnected?.Invoke(r);
            _ax.RemoteDesktopSizeChanged += (w, h) => RemoteSizeChanged?.Invoke(w, h);
            _ax.EnteredFullScreen += () => FullScreenRequested?.Invoke(true);
            _ax.LeftFullScreen += () => FullScreenRequested?.Invoke(false);
            _ax.MinimizeRequested += () => MinimizeRequested?.Invoke();
            _ax.CloseRequested += () => CloseRequested?.Invoke();
            _ax.FatalError += code => FatalError?.Invoke(code);

            // ★ 必须在 BeginInit/EndInit 之前订阅——EndInit 可能同步创建句柄，晚订阅会错过事件。
            _ax.HandleCreated += (s, e) =>
            {
                _ready = true;
                if (_pending is { } p) { _pending = null; _ax.ApplyAndConnect(p); }
            };

            // ActiveX 须经 ISupportInitialize 正确激活，否则 WPF 合成时崩 (UCEERR_RENDERTHREADFAILURE)。
            // ActiveX 用 Dock.Fill 由框架按 DPI 正确铺满宿主（手动定位会因 DPI 坐标不一致导致纵横比错算 → 双重信箱）。
            // 黑底容器（对齐旧实现 _winFormsContainer.BackColor = Black）：SmartSizing 关闭后画面原生不缩放，
            // 周围空白显示黑色、窗口/全屏一致。
            var panel = new System.Windows.Forms.Panel
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                BackColor = System.Drawing.Color.FromArgb(0x20, 0x20, 0x20),
            };
            _ax.Dock = System.Windows.Forms.DockStyle.Fill;
            _ax.BackColor = System.Drawing.Color.FromArgb(0x20, 0x20, 0x20);
            ((ISupportInitialize)_ax).BeginInit();
            panel.Controls.Add(_ax);
            ((ISupportInitialize)_ax).EndInit();
            Child = panel;

            if (_ax.IsHandleCreated) _ready = true;
        }

        /// <summary>用给定配方连接。OCX 未就绪时排队，句柄创建后自动补发。</summary>
        public void Connect(RdpConnectionSettings settings)
        {
            if (_ready || _ax.IsHandleCreated) _ax.ApplyAndConnect(settings);
            else _pending = settings;
        }

        public void Disconnect() => _ax.DisconnectSafe();

        /// <summary>增强会话改分辨率（不重连）。</summary>
        public void Resize(int width, int height) => _ax.Resize(width, height);

        /// <summary>增强会话发送 Ctrl+Alt+Del（基本会话请由消费方走 WMI）。</summary>
        public void SendCtrlAltDelViaRdp() => _ax.SendCtrlAltDelEnhanced();

        /// <summary>同步全屏状态给底层控件（容器处理全屏时，按钮发起的全屏需要回灌给 mstscax，
        /// 使其内部状态/键盘捕获与窗口一致；热键发起的无需，由控件自身切换）。</summary>
        public void SetFullScreen(bool fullScreen) => _ax.SetFullScreen(fullScreen);
    }
}
