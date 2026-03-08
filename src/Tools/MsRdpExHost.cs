using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using RoyalApps.Community.Rdp.WinForms.Controls;
using RoyalApps.Community.Rdp.WinForms.Configuration;
using RdpColorDepth = RoyalApps.Community.Rdp.WinForms.Configuration.ColorDepth;
using AxMSTSCLib;
using System.Runtime.InteropServices;

namespace ExHyperV.Tools
{
    public class MsRdpExHost : WindowsFormsHost
    {
        private readonly RdpControl _rdpControl;
        private string? _lastConnectedId;
        private DispatcherTimer? _fastResizeTimer;
        private Window? _parentWindow;

        private int _lastPixelW = 0;
        private int _lastPixelH = 0;

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        public event Action? OnRdpConnected;
        public event Action<string>? OnRdpDisconnected;

        public static readonly DependencyProperty VmIdProperty =
            DependencyProperty.Register(nameof(VmId), typeof(string), typeof(MsRdpExHost),
                new PropertyMetadata(null, OnVmIdChanged));

        public string VmId
        {
            get => (string)GetValue(VmIdProperty);
            set => SetValue(VmIdProperty, value);
        }

        public MsRdpExHost()
        {
            _rdpControl = new RdpControl { Dock = DockStyle.Fill };

            _rdpControl.RdpClientConfigured += (s, e) =>
            {
                if (_rdpControl.RdpClient is AxMsRdpClient9NotSafeForScripting ax)
                {
                    ax.OnRemoteDesktopSizeChange += (sender, args) =>
                    {
                        UpdateLayoutByPixels("【底层信号推送】", args.width, args.height);
                    };
                }
            };

            _rdpControl.OnConnected += (s, e) =>
            {
                UpdateLayoutByPixels("【连接成功初始同步】", _rdpControl.RdpClient!.DesktopWidth, _rdpControl.RdpClient.DesktopHeight);
                StartFastSniffer();
                OnRdpConnected?.Invoke();
            };

            _rdpControl.OnDisconnected += (s, e) =>
            {
                StopFastSniffer();
                _lastConnectedId = null;
                _lastPixelW = 0; _lastPixelH = 0;
                OnRdpDisconnected?.Invoke(e.Description);
            };

            this.Child = _rdpControl;

            this.Loaded += (s, e) =>
            {
                _parentWindow = Window.GetWindow(this);
                if (_parentWindow != null)
                {
                    Debug.WriteLine($"[RDP-DEBUG] 已挂载父窗口: {_parentWindow.Title}");
                    _parentWindow.StateChanged += ParentWindow_StateChanged;
                }
            };
        }

        private void ParentWindow_StateChanged(object? sender, EventArgs e)
        {
            if (_parentWindow == null) return;

            if (_parentWindow.WindowState == WindowState.Maximized)
            {
                // 1. 最大化时，必须关闭自动适配，否则会产生布局回环或渲染空洞
                _parentWindow.SizeToContent = SizeToContent.Manual;
                Debug.WriteLine("[RDP-STATE] 最大化：关闭 SizeToContent");
            }
            else if (_parentWindow.WindowState == WindowState.Normal)
            {
                // 2. 还原时，重新开启自动适配并强制刷新
                Debug.WriteLine("[RDP-STATE] 还原：重新开启 SizeToContent");
                UpdateLayoutByPixels("【窗口还原校准】", _lastPixelW, _lastPixelH, true);
            }
        }

        private void UpdateLayoutByPixels(string reason, int pixelWidth, int pixelHeight, bool forceRefresh = false)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0) return;
            if (!forceRefresh && pixelWidth == _lastPixelW && pixelHeight == _lastPixelH) return;

            Dispatcher.Invoke(() =>
            {
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget == null) return;

                double dpiX = source.CompositionTarget.TransformToDevice.M11;
                double dpiY = source.CompositionTarget.TransformToDevice.M22;

                _lastPixelW = pixelWidth;
                _lastPixelH = pixelHeight;

                this.Width = pixelWidth / dpiX;
                this.Height = pixelHeight / dpiY;

                if (_parentWindow != null && _parentWindow.WindowState == WindowState.Normal)
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        // 还原时：先设为 0 彻底打破 Windows 的恢复边界约束
                        _parentWindow.Width = 0;
                        _parentWindow.Height = 0;

                        // 重新进入 WidthAndHeight 模式，它会重新根据 Host 的新 Width/Height 撑开窗口
                        _parentWindow.SizeToContent = SizeToContent.WidthAndHeight;
                        _parentWindow.UpdateLayout();
                    }), DispatcherPriority.Background);
                }
            });
        }

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

                if ((currentW > 0 && currentH > 0) && (currentW != _lastPixelW || currentH != _lastPixelH))
                {
                    UpdateLayoutByPixels("【高频侦测变化】", currentW, currentH);
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

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string? windowTitle);

        private static void OnVmIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MsRdpExHost host && e.NewValue is string vmid && !string.IsNullOrEmpty(vmid))
            {
                if (host._lastConnectedId == vmid) return;
                host._lastConnectedId = vmid;
                host.TriggerConnect(vmid);
            }
        }

        private void TriggerConnect(string vmid)
        {
            if (!this.IsVisible) { Dispatcher.BeginInvoke(new Action(() => TriggerConnect(vmid)), DispatcherPriority.Loaded); return; }
            try
            {
                string cleanGuid = vmid.Trim().Replace("{", "").Replace("}", "").ToUpper();
                _rdpControl.RdpConfiguration.HyperV.Instance = cleanGuid;
                _rdpControl.RdpConfiguration.HyperV.EnhancedSessionMode = false;
                _rdpControl.RdpConfiguration.Server = "127.0.0.1";
                _rdpControl.RdpConfiguration.Display.ResizeBehavior = ResizeBehavior.SmartReconnect;
                _rdpControl.RdpConfiguration.Display.ColorDepth = RdpColorDepth.ColorDepth32Bpp;
                _rdpControl.RdpConfiguration.Display.AutoScaling = false;
                _rdpControl.Connect();
            }
            catch (Exception ex) { Debug.WriteLine($"[RDP-ERROR] {ex.Message}"); }
        }

        public void Disconnect() { StopFastSniffer(); _lastConnectedId = null; try { _rdpControl?.Disconnect(); } catch { } }
    }
}