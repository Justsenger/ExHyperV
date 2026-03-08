using System.Windows;
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
        private int _lastPixelH = 0; [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect); [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string? windowTitle);

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
                        UpdateLayoutByPixels("SIGNAL", args.width, args.height);
                    };
                }
            };

            _rdpControl.OnConnected += (s, e) =>
            {
                UpdateLayoutByPixels("INIT", _rdpControl.RdpClient!.DesktopWidth, _rdpControl.RdpClient.DesktopHeight);
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
            };
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

                if (_parentWindow != null)
                {
                    if (_parentWindow.WindowState == WindowState.Normal)
                    {
                        _parentWindow.Width = double.NaN;
                        _parentWindow.Height = double.NaN;

                        // 用完即弃策略
                        // 1. 瞬间开启自适应：告诉窗口“请抱紧里面的 RDP 控件”
                        _parentWindow.SizeToContent = SizeToContent.WidthAndHeight;

                        // 2. 强迫 WPF 在当前帧立刻算出大小（不留到下一帧，防止异步延迟）
                        _parentWindow.UpdateLayout();

                        // 3. 算完立刻撒手,恢复自由身。
                        // 这样窗口在 99.99% 的生命周期里都是 Manual 状态，无论用户多快点击最大化都不会缩水
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
            catch { }
        }

        public void Disconnect() { StopFastSniffer(); _lastConnectedId = null; try { _rdpControl?.Disconnect(); } catch { } }
    }
}