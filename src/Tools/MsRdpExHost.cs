using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using RoyalApps.Community.Rdp.WinForms.Controls;
using RoyalApps.Community.Rdp.WinForms.Configuration;
using RdpColorDepth = RoyalApps.Community.Rdp.WinForms.Configuration.ColorDepth;
using RdpAuthLevel = RoyalApps.Community.Rdp.WinForms.Configuration.AuthenticationLevel;
using AxMSTSCLib;

namespace ExHyperV.Tools
{
    public class MsRdpExHost : WindowsFormsHost
    {
        private readonly RdpControl _rdpControl;
        private string? _lastConnectedId;
        private bool? _lastEnhancedMode;
        private DispatcherTimer? _fastResizeTimer;
        private Window? _parentWindow;
        private int _lastPixelW = 0;
        private int _lastPixelH = 0;

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string? windowTitle);

        public event Action? OnRdpConnected;
        public event Action<string>? OnRdpDisconnected;

        #region Dependency Properties

        public static readonly DependencyProperty VmIdProperty =
            DependencyProperty.Register(nameof(VmId), typeof(string), typeof(MsRdpExHost),
                new PropertyMetadata(null, OnConnectConfigChanged));

        public string VmId { get => (string)GetValue(VmIdProperty); set => SetValue(VmIdProperty, value); }

        public static readonly DependencyProperty IsEnhancedModeProperty =
            DependencyProperty.Register(nameof(IsEnhancedMode), typeof(bool), typeof(MsRdpExHost),
                new PropertyMetadata(false, OnConnectConfigChanged));

        public bool IsEnhancedMode { get => (bool)GetValue(IsEnhancedModeProperty); set => SetValue(IsEnhancedModeProperty, value); }

        public static readonly DependencyProperty ActualPixelsWidthProperty =
            DependencyProperty.Register(nameof(ActualPixelsWidth), typeof(int), typeof(MsRdpExHost), new PropertyMetadata(0));

        public int ActualPixelsWidth { get => (int)GetValue(ActualPixelsWidthProperty); set => SetValue(ActualPixelsWidthProperty, value); }

        public static readonly DependencyProperty ActualPixelsHeightProperty =
            DependencyProperty.Register(nameof(ActualPixelsHeight), typeof(int), typeof(MsRdpExHost), new PropertyMetadata(0));

        public int ActualPixelsHeight { get => (int)GetValue(ActualPixelsHeightProperty); set => SetValue(ActualPixelsHeightProperty, value); }

        #endregion

        public MsRdpExHost()
        {
            _rdpControl = new RdpControl { Dock = System.Windows.Forms.DockStyle.Fill };

            _rdpControl.RdpClientConfigured += (s, e) =>
            {
                if (_rdpControl.RdpClient is AxMsRdpClient9NotSafeForScripting ax)
                {
                    ax.OnRemoteDesktopSizeChange += (sender, args) =>
                    {
                        // 过滤掉切换分辨率时的极小瞬时值（白屏保护）
                        if (args.width > 300 && args.height > 300)
                            UpdateLayoutByPixels("SIGNAL", args.width, args.height);
                    };
                }
            };

            _rdpControl.OnConnected += (s, e) =>
            {
                Log($"CONNECTED: {_rdpControl.RdpClient!.DesktopWidth}x{_rdpControl.RdpClient.DesktopHeight}");
                UpdateLayoutByPixels("INIT", _rdpControl.RdpClient!.DesktopWidth, _rdpControl.RdpClient.DesktopHeight);
                StartFastSniffer();
                OnRdpConnected?.Invoke();
            };

            _rdpControl.OnDisconnected += (s, e) =>
            {
                Log($"DISCONNECTED: {e.Description}");
                StopFastSniffer();
                _lastConnectedId = null;
                _lastEnhancedMode = null;
                _lastPixelW = 0; _lastPixelH = 0;
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
                host.TriggerConnect(host.VmId);
            }
        }

        private void TriggerConnect(string vmid)
        {
            if (!this.IsVisible) { Dispatcher.BeginInvoke(new Action(() => TriggerConnect(vmid)), DispatcherPriority.Loaded); return; }

            // 状态锁：只有当 ID 或 模式 真正改变时才重连，防止分辨率变化触发此逻辑
            if (_lastConnectedId == vmid && _lastEnhancedMode == IsEnhancedMode) return;

            try
            {
                Log($"TRIGGER CONNECT: VM={vmid}, Enhanced={IsEnhancedMode}");

                if (_rdpControl.RdpClient != null && _rdpControl.RdpClient.ConnectionState != ConnectionState.Disconnected)
                {
                    _rdpControl.Disconnect();
                    _lastConnectedId = null;
                    Dispatcher.BeginInvoke(new Action(() => TriggerConnect(vmid)), DispatcherPriority.Background);
                    return;
                }

                _lastConnectedId = vmid;
                _lastEnhancedMode = IsEnhancedMode;

                string cleanGuid = vmid.Trim().Replace("{", "").Replace("}", "").ToUpper();
                var config = _rdpControl.RdpConfiguration;

                config.HyperV.Instance = cleanGuid;
                config.HyperV.EnhancedSessionMode = this.IsEnhancedMode;
                config.Server = "127.0.0.1";
                config.HyperV.HyperVPort = 2179;

                config.Security.AuthenticationLevel = RdpAuthLevel.NoAuthenticationOfServer;

                // 增强模式必须关闭 UDP 走 VMBus
                if (IsEnhancedMode) config.Connection.DisableUdpTransport = true;
                else config.Connection.DisableUdpTransport = false;

                config.Display.ResizeBehavior = ResizeBehavior.SmartReconnect;
                config.Display.ColorDepth = RdpColorDepth.ColorDepth32Bpp;
                config.Display.AutoScaling = false;

                _rdpControl.Connect();
            }
            catch (Exception ex)
            {
                Log($"Connect Error: {ex.Message}");
            }
        }

        private void UpdateLayoutByPixels(string reason, int pixelWidth, int pixelHeight, bool forceRefresh = false)
        {
            if (pixelWidth <= 300 || pixelHeight <= 300) return; // 再次加固：忽略无效的分辨率
            if (!forceRefresh && pixelWidth == _lastPixelW && pixelHeight == _lastPixelH) return;

            Dispatcher.Invoke(() =>
            {
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget == null) return;

                double dpiX = source.CompositionTarget.TransformToDevice.M11;
                double dpiY = source.CompositionTarget.TransformToDevice.M22;

                // --- 同步分辨率给 UI 按钮 ---
                this.ActualPixelsWidth = pixelWidth;
                this.ActualPixelsHeight = pixelHeight;

                _lastPixelW = pixelWidth;
                _lastPixelH = pixelHeight;

                this.Width = pixelWidth / dpiX;
                this.Height = pixelHeight / dpiY;

                if (_parentWindow != null)
                {
                    if (_parentWindow.WindowState == WindowState.Normal)
                    {
                        _parentWindow.Width = double.NaN;
                        _parentWindow.Height = double.NaN;
                        _parentWindow.SizeToContent = SizeToContent.WidthAndHeight;
                        _parentWindow.UpdateLayout();
                        _parentWindow.SizeToContent = SizeToContent.Manual;
                    }
                    else if (_parentWindow.WindowState == WindowState.Maximized)
                    {
                        this.InvalidateArrange();
                        _parentWindow.UpdateLayout();
                    }
                }
            });
        }

        private void ParentWindow_StateChanged(object? sender, EventArgs e)
        {
            if (_parentWindow == null) return;
            if (_parentWindow.WindowState == WindowState.Normal)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateLayoutByPixels("STATE_RESTORE", _lastPixelW, _lastPixelH, forceRefresh: true);
                }), DispatcherPriority.Loaded);
            }
        }

        private void Log(string msg) => Debug.WriteLine($"[MsRdpExHost] {msg}");

        #region Fast Sniffer
        private void StartFastSniffer()
        {
            if (_fastResizeTimer != null) return;
            _fastResizeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
            _fastResizeTimer.Tick += (s, e) =>
            {
                if (_rdpControl.RdpClient == null || _rdpControl.RdpClient.ConnectionState != ConnectionState.Connected) return;
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

                if (currentW > 300 && currentH > 300 && (currentW != _lastPixelW || currentH != _lastPixelH))
                {
                    UpdateLayoutByPixels("SNIFFER", currentW, currentH);
                }
            };
            _fastResizeTimer.Start();
        }

        private void StopFastSniffer() { _fastResizeTimer?.Stop(); _fastResizeTimer = null; }

        private IntPtr GetOutputPresenterHandle(IntPtr rdpHandle)
        {
            IntPtr h1 = FindWindowEx(rdpHandle, IntPtr.Zero, "UIMainClass", null);
            IntPtr h2 = FindWindowEx(h1, IntPtr.Zero, "UIContainerClass", null);
            IntPtr h3 = FindWindowEx(h2, IntPtr.Zero, "OPContainerClass", null);
            return FindWindowEx(h3, IntPtr.Zero, "OPWindowClass", null);
        }
        #endregion

        public void Disconnect() { StopFastSniffer(); _lastConnectedId = null; _lastEnhancedMode = null; try { _rdpControl?.Disconnect(); } catch { } }
    }
}