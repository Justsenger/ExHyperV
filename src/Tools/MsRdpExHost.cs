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

            Debug.WriteLine($"[RDP-STATE] 窗口状态变为: {_parentWindow.WindowState}");

            // 当从最大化还原时
            if (_parentWindow.WindowState == WindowState.Normal)
            {
                Debug.WriteLine("[RDP-STATE] 检测到窗口还原，准备强制重置外壳尺寸...");
                // 即使像素没变，也要调用一次以触发 SizeToContent 重置
                UpdateLayoutByPixels("【窗口状态还原校准】", _lastPixelW, _lastPixelH, true);
            }
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

        private void UpdateLayoutByPixels(string reason, int pixelWidth, int pixelHeight, bool forceRefresh = false)
        {
            if (pixelWidth <= 0 || pixelHeight <= 0) return;
            // 如果尺寸没变且不是强制刷新，则跳过
            if (!forceRefresh && pixelWidth == _lastPixelW && pixelHeight == _lastPixelH) return;

            Dispatcher.Invoke(() =>
            {
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget == null) return;

                double dpiX = source.CompositionTarget.TransformToDevice.M11;
                double dpiY = source.CompositionTarget.TransformToDevice.M22;

                _lastPixelW = pixelWidth;
                _lastPixelH = pixelHeight;

                double wpfW = pixelWidth / dpiX;
                double wpfH = pixelHeight / dpiY;

                Debug.WriteLine($">>>>> [RDP-SYNC-START] 原因: {reason}");
                Debug.WriteLine($"      目标物理像素: {pixelWidth}x{pixelHeight}");
                Debug.WriteLine($"      计算WPF尺寸: {wpfW}x{wpfH}");

                // 1. 设置 Host 尺寸
                this.Width = wpfW;
                this.Height = wpfH;

                if (_parentWindow != null)
                {
                    // 如果正在 Normal 模式下，强制执行外壳收缩
                    if (_parentWindow.WindowState == WindowState.Normal)
                    {
                        // 使用 Background 优先级，确保在还原动画完成后执行
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            Debug.WriteLine($"[RDP-SYNC-SHELL] 执行窗口自适应。当前窗口尺寸: {_parentWindow.ActualWidth}x{_parentWindow.ActualHeight}");

                            // 关键修复：清除可能被系统锁定的硬性尺寸
                            _parentWindow.Width = double.NaN;
                            _parentWindow.Height = double.NaN;

                            // 抖动 SizeToContent
                            var currentMode = _parentWindow.SizeToContent;
                            _parentWindow.SizeToContent = SizeToContent.Manual;
                            _parentWindow.SizeToContent = currentMode;

                            _parentWindow.UpdateLayout();
                            Debug.WriteLine($"[RDP-SYNC-SHELL] 完成。新窗口尺寸: {_parentWindow.ActualWidth}x{_parentWindow.ActualHeight}");
                        }), DispatcherPriority.Background);
                    }
                    else
                    {
                        Debug.WriteLine($"[RDP-SYNC-SHELL] 窗口处于 {_parentWindow.WindowState} 状态，跳过外壳收缩，仅保持内部居中。");
                    }
                }
            });
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