using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using RoyalApps.Community.Rdp.WinForms.Controls;
using RoyalApps.Community.Rdp.WinForms.Configuration;
using AxMSTSCLib;
using MSTSCLib;
using RdpColorDepth = RoyalApps.Community.Rdp.WinForms.Configuration.ColorDepth;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 封装了 RoyalApps RdpControl 的 WPF 控件，支持 Hyper-V 增强模式切换及自动布局
    /// </summary>
    public class MsRdpExHost : WindowsFormsHost
    {
        private readonly RdpControl _rdpControl;
        private string? _lastConnectedId;
        private bool? _lastEnhancedMode; // 记录上次连接的增强模式状态
        private DispatcherTimer? _fastResizeTimer;
        private Window? _parentWindow;
        private int _lastPixelW = 0;
        private int _lastPixelH = 0;

        #region Win32 API
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string? windowTitle);
        #endregion

        #region Dependency Properties

        // 虚拟机 ID (GUID)
        public static readonly DependencyProperty VmIdProperty =
            DependencyProperty.Register(nameof(VmId), typeof(string), typeof(MsRdpExHost),
                new PropertyMetadata(null, OnConnectConfigChanged));

        public string VmId
        {
            get => (string)GetValue(VmIdProperty);
            set => SetValue(VmIdProperty, value);
        }

        // 是否开启增强模式 (Enhanced Session Mode)
        public static readonly DependencyProperty IsEnhancedModeProperty =
            DependencyProperty.Register(nameof(IsEnhancedMode), typeof(bool), typeof(MsRdpExHost),
                new PropertyMetadata(false, OnConnectConfigChanged));

        public bool IsEnhancedMode
        {
            get => (bool)GetValue(IsEnhancedModeProperty);
            set => SetValue(IsEnhancedModeProperty, value);
        }

        // 实际像素宽度 (用于 UI 绑定显示)
        public static readonly DependencyProperty ActualPixelsWidthProperty =
            DependencyProperty.Register(nameof(ActualPixelsWidth), typeof(int), typeof(MsRdpExHost), new PropertyMetadata(0));

        public int ActualPixelsWidth
        {
            get => (int)GetValue(ActualPixelsWidthProperty);
            set => SetValue(ActualPixelsWidthProperty, value);
        }

        // 实际像素高度
        public static readonly DependencyProperty ActualPixelsHeightProperty =
            DependencyProperty.Register(nameof(ActualPixelsHeight), typeof(int), typeof(MsRdpExHost), new PropertyMetadata(0));

        public int ActualPixelsHeight
        {
            get => (int)GetValue(ActualPixelsHeightProperty);
            set => SetValue(ActualPixelsHeightProperty, value);
        }

        #endregion

        #region Events
        public event Action? OnRdpConnected;
        public event Action<string>? OnRdpDisconnected;
        #endregion

        public MsRdpExHost()
        {
            _rdpControl = new RdpControl { Dock = System.Windows.Forms.DockStyle.Fill };

            // 配置底层 RDP 客户端
            _rdpControl.RdpClientConfigured += (s, e) =>
            {
                if (_rdpControl.RdpClient is AxMsRdpClient9NotSafeForScripting ax)
                {
                    // 监听远程桌面尺寸变化信号
                    ax.OnRemoteDesktopSizeChange += (sender, args) =>
                    {
                        UpdateLayoutByPixels("SIGNAL", args.width, args.height);
                    };
                }
            };

            // 连接成功回调
            _rdpControl.OnConnected += (s, e) =>
            {
                if (_rdpControl.RdpClient != null)
                {
                    UpdateLayoutByPixels("INIT", _rdpControl.RdpClient.DesktopWidth, _rdpControl.RdpClient.DesktopHeight);
                }
                StartFastSniffer();
                OnRdpConnected?.Invoke();
            };

            // 断开连接回调
            _rdpControl.OnDisconnected += (s, e) =>
            {
                StopFastSniffer();
                _lastConnectedId = null;
                _lastEnhancedMode = null;
                _lastPixelW = 0;
                _lastPixelH = 0;
                OnRdpDisconnected?.Invoke(e.Description);
            };

            this.Child = _rdpControl;

            this.Loaded += (s, e) =>
            {
                _parentWindow = Window.GetWindow(this);
                if (_parentWindow != null)
                {
                    _parentWindow.StateChanged += ParentWindow_StateChanged;
                }
            };
        }

        private static void OnConnectConfigChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MsRdpExHost host && !string.IsNullOrEmpty(host.VmId))
            {
                // 当 VmId 或 IsEnhancedMode 改变时触发重连
                host.TriggerConnect(host.VmId);
            }
        }

        private void TriggerConnect(string vmid)
        {
            // 确保控件可见时再连接，避免 ActiveX 初始化失败
            if (!this.IsVisible)
            {
                Dispatcher.BeginInvoke(new Action(() => TriggerConnect(vmid)), DispatcherPriority.Loaded);
                return;
            }

            // 检查是否已经是当前连接状态（防止重复触发）
            if (_lastConnectedId == vmid && _lastEnhancedMode == IsEnhancedMode) return;

            try
            {
                _lastConnectedId = vmid;
                _lastEnhancedMode = IsEnhancedMode;

                // 如果正在连接，先切断
                _rdpControl.Disconnect();

                string cleanGuid = vmid.Trim().Replace("{", "").Replace("}", "").ToUpper();

                // 核心配置：Hyper-V 专用参数
                var config = _rdpControl.RdpConfiguration;
                config.Server = "127.0.0.1"; // Hyper-V 始终连接本地宿主机
                config.HyperV.Instance = cleanGuid;
                config.HyperV.HyperVPort = 2179; // Hyper-V 默认 RDP 端口

                // --- 增强模式切换逻辑 ---
                config.HyperV.EnhancedSessionMode = this.IsEnhancedMode;

                if (this.IsEnhancedMode)
                {
                    // 增强模式支持更多特性
                    config.Display.ResizeBehavior = ResizeBehavior.SmartReconnect;
                    config.Display.ColorDepth = RdpColorDepth.ColorDepth32Bpp;
                    config.Connection.DisableUdpTransport = true; // 增强模式建议走 TCP (VMBus)
                }
                else
                {
                    // 基本模式（视频流模式）
                    config.Display.ResizeBehavior = ResizeBehavior.SmartReconnect;
                    config.Display.ColorDepth = RdpColorDepth.ColorDepth32Bpp;
                }

                config.Display.AutoScaling = false; // 由本 Host 逻辑接管缩放

                _rdpControl.Connect();
            }
            catch (Exception ex)
            {
                OnRdpDisconnected?.Invoke($"Connect failed: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新布局：将 RDP 像素大小转换为 WPF 逻辑单位并同步给窗口
        /// </summary>
        private void UpdateLayoutByPixels(string reason, int pixelWidth, int pixelHeight, bool forceRefresh = false)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0) return;
            if (!forceRefresh && pixelWidth == _lastPixelW && pixelHeight == _lastPixelH) return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget == null) return;

                // 获取 DPI 缩放比例
                double dpiX = source.CompositionTarget.TransformToDevice.M11;
                double dpiY = source.CompositionTarget.TransformToDevice.M22;

                this.ActualPixelsWidth = pixelWidth;
                this.ActualPixelsHeight = pixelHeight;

                _lastPixelW = pixelWidth;
                _lastPixelH = pixelHeight;

                // 将像素转换为 WPF 逻辑单位
                this.Width = pixelWidth / dpiX;
                this.Height = pixelHeight / dpiY;

                if (_parentWindow != null && _parentWindow.WindowState == WindowState.Normal)
                {
                    // 临时开启 SizeToContent 以适配 RDP 分辨率，随后立刻恢复
                    _parentWindow.SizeToContent = SizeToContent.WidthAndHeight;
                    _parentWindow.UpdateLayout();
                    _parentWindow.SizeToContent = SizeToContent.Manual;
                }
                else if (_parentWindow != null && _parentWindow.WindowState == WindowState.Maximized)
                {
                    this.InvalidateArrange();
                }
            }), DispatcherPriority.Render);
        }

        private void ParentWindow_StateChanged(object? sender, EventArgs e)
        {
            if (_parentWindow == null) return;
            if (_parentWindow.WindowState == WindowState.Normal)
            {
                // 从最大化恢复时，强制刷新一次分辨率适配
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateLayoutByPixels("STATE_RESTORE", _lastPixelW, _lastPixelH, forceRefresh: true);
                }), DispatcherPriority.Loaded);
            }
        }

        #region Fast Size Sniffer (用于实时捕获 RDP 窗口真实大小)
        private void StartFastSniffer()
        {
            if (_fastResizeTimer != null) return;
            _fastResizeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
            _fastResizeTimer.Tick += (s, e) =>
            {
                if (_rdpControl.RdpClient == null || _rdpControl.RdpClient.ConnectionState != ConnectionState.Connected) return;

                // 尝试获取 RDP 内部 Presenter 窗口的真实大小
                IntPtr opHandle = GetOutputPresenterHandle(_rdpControl.Handle);
                int currentW, currentH;

                if (opHandle != IntPtr.Zero && GetClientRect(opHandle, out RECT rect))
                {
                    currentW = rect.Right - rect.Left;
                    currentH = rect.Bottom - rect.Top;
                }
                else
                {
                    currentW = _rdpControl.RdpClient.DesktopWidth;
                    currentH = _rdpControl.RdpClient.DesktopHeight;
                }

                if ((currentW > 0 && currentH > 0) && (currentW != _lastPixelW || currentH != _lastPixelH))
                {
                    UpdateLayoutByPixels("SNIFFER", currentW, currentH);
                }
            };
            _fastResizeTimer.Start();
        }

        private void StopFastSniffer() { _fastResizeTimer?.Stop(); _fastResizeTimer = null; }

        private IntPtr GetOutputPresenterHandle(IntPtr rdpHandle)
        {
            // 层级：UIMainClass -> UIContainerClass -> OPContainerClass -> OPWindowClass
            IntPtr h1 = FindWindowEx(rdpHandle, IntPtr.Zero, "UIMainClass", null);
            IntPtr h2 = FindWindowEx(h1, IntPtr.Zero, "UIContainerClass", null);
            IntPtr h3 = FindWindowEx(h2, IntPtr.Zero, "OPContainerClass", null);
            IntPtr h4 = FindWindowEx(h3, IntPtr.Zero, "OPWindowClass", null);
            if (h4 == IntPtr.Zero) h4 = FindWindowEx(h3, IntPtr.Zero, "OPWindowClass_mstscax", null);
            return h4;
        }
        #endregion

        public void Disconnect()
        {
            StopFastSniffer();
            _lastConnectedId = null;
            _lastEnhancedMode = null;
            try { _rdpControl?.Disconnect(); } catch { }
        }
    }
}