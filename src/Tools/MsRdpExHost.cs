using System;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using RoyalApps.Community.Rdp.WinForms.Controls;
using RoyalApps.Community.Rdp.WinForms.Configuration;
using RdpColorDepth = RoyalApps.Community.Rdp.WinForms.Configuration.ColorDepth;
using RdpAuthLevel = RoyalApps.Community.Rdp.WinForms.Configuration.AuthenticationLevel;
using AxMSTSCLib;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading.Tasks;
using MSTSCLib;

namespace ExHyperV.Tools
{
    public class MsRdpExHost : WindowsFormsHost
    {
        private readonly RdpControl _rdpControl;
        private readonly System.Windows.Forms.Panel _curtain; // 黑色遮罩幕布
        private readonly System.Windows.Forms.Panel _winFormsContainer; // 内部容器

        private string? _lastConnectedId;
        private bool? _lastEnhancedMode;
        private int _lastReqW, _lastReqH;

        private DispatcherTimer? _fastResizeTimer;
        private DispatcherTimer? _layoutStabilizeTimer;
        private DispatcherTimer _configDebounceTimer;

        private Window? _parentWindow;
        private int _lastPixelW = 0;
        private int _lastPixelH = 0;
        private int _pendingW, _pendingH;
        private bool _isConnecting = false;

        private bool _isTransitioning = false;
        private int _targetW, _targetH;

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string? windowTitle);

        public event Action? OnRdpConnected;
        public event Action<string>? OnRdpDisconnected;

        #region Dependency Properties
        public static readonly DependencyProperty VmIdProperty = DependencyProperty.Register(nameof(VmId), typeof(string), typeof(MsRdpExHost), new PropertyMetadata(null, OnConfigChanged));
        public string VmId { get => (string)GetValue(VmIdProperty); set => SetValue(VmIdProperty, value); }

        public static readonly DependencyProperty IsEnhancedModeProperty = DependencyProperty.Register(nameof(IsEnhancedMode), typeof(bool), typeof(MsRdpExHost), new PropertyMetadata(false, OnConfigChanged));
        public bool IsEnhancedMode { get => (bool)GetValue(IsEnhancedModeProperty); set => SetValue(IsEnhancedModeProperty, value); }

        public static readonly DependencyProperty ActualPixelsWidthProperty = DependencyProperty.Register(nameof(ActualPixelsWidth), typeof(int), typeof(MsRdpExHost), new PropertyMetadata(0));
        public int ActualPixelsWidth { get => (int)GetValue(ActualPixelsWidthProperty); set => SetValue(ActualPixelsWidthProperty, value); }

        public static readonly DependencyProperty ActualPixelsHeightProperty = DependencyProperty.Register(nameof(ActualPixelsHeight), typeof(int), typeof(MsRdpExHost), new PropertyMetadata(0));
        public int ActualPixelsHeight { get => (int)GetValue(ActualPixelsHeightProperty); set => SetValue(ActualPixelsHeightProperty, value); }

        public static readonly DependencyProperty RequestWidthProperty = DependencyProperty.Register(nameof(RequestWidth), typeof(int), typeof(MsRdpExHost), new PropertyMetadata(0, OnConfigChanged));
        public int RequestWidth { get => (int)GetValue(RequestWidthProperty); set => SetValue(RequestWidthProperty, value); }

        public static readonly DependencyProperty RequestHeightProperty = DependencyProperty.Register(nameof(RequestHeight), typeof(int), typeof(MsRdpExHost), new PropertyMetadata(0, OnConfigChanged));
        public int RequestHeight { get => (int)GetValue(RequestHeightProperty); set => SetValue(RequestHeightProperty, value); }
        #endregion

        public MsRdpExHost()
        {
            // 1. 初始化 WinForms 容器和幕布
            _winFormsContainer = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill, BackColor = System.Drawing.Color.Black };
            _curtain = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill, BackColor = System.Drawing.Color.Black, Visible = true };
            _rdpControl = new RdpControl { Dock = System.Windows.Forms.DockStyle.Fill };

            // 2. 层叠顺序：幕布在最上面
            _winFormsContainer.Controls.Add(_curtain);
            _winFormsContainer.Controls.Add(_rdpControl);
            _curtain.BringToFront();

            // 防抖定时器
            _configDebounceTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _configDebounceTimer.Interval = TimeSpan.FromMilliseconds(10);
            _configDebounceTimer.Tick += (s, e) => {
                _configDebounceTimer.Stop();
                _ = TriggerConnectAsync();
            };

            // 布局稳定定时器
            _layoutStabilizeTimer = new DispatcherTimer(DispatcherPriority.Render);
            _layoutStabilizeTimer.Interval = TimeSpan.FromMilliseconds(10);
            _layoutStabilizeTimer.Tick += (s, e) => {
                _layoutStabilizeTimer.Stop();
                ExecutePhysicalLayout(_pendingW, _pendingH);
            };

            _rdpControl.RdpClientConfigured += (s, e) => {
                if (_rdpControl.RdpClient is AxMsRdpClient9NotSafeForScripting ax)
                {
                    ax.OnRemoteDesktopSizeChange += (sender, args) => {
                        UpdateLayoutByPixels("SIGNAL", args.width, args.height);
                    };
                }
            };

            _rdpControl.OnConnected += (s, e) => {
                int w = _rdpControl.RdpClient!.DesktopWidth;
                int h = _rdpControl.RdpClient.DesktopHeight;
                Log($"CONNECTED REPORTED: {w}x{h}");

                if (!_isTransitioning)
                {
                    this.ActualPixelsWidth = w; this.ActualPixelsHeight = h;
                    ExecutePhysicalLayout(w, h);
                    _curtain.Visible = false; // 非过渡状态连接成功也关掉幕布
                }

                StartFastSniffer();
                OnRdpConnected?.Invoke();
            };

            _rdpControl.OnDisconnected += (s, e) => {
                StopFastSniffer();
                _layoutStabilizeTimer.Stop();
                _curtain.Visible = true; // 断开时拉上幕布
                _lastConnectedId = null; _lastEnhancedMode = null; _lastPixelW = 0; _lastPixelH = 0;
                OnRdpDisconnected?.Invoke(e.Description);
            };

            this.Child = _winFormsContainer;
            this.Loaded += (s, e) => _parentWindow = Window.GetWindow(this);
        }

        private static void OnConfigChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MsRdpExHost host)
            {
                host._configDebounceTimer.Stop();
                host._configDebounceTimer.Start();
            }
        }

        private async Task TriggerConnectAsync()
        {
            if (string.IsNullOrEmpty(this.VmId)) return;
            // 注意：不能检查 !this.IsVisible，因为隐藏时也需要后台准备
            if (!this.IsLoaded) return;

            if (_isConnecting) return;
            _isConnecting = true;
            _isTransitioning = true;

            // 【动作】拉上幕布
            _curtain.Visible = true;
            _curtain.BringToFront();

            try
            {
                while (_lastConnectedId != this.VmId || _lastEnhancedMode != IsEnhancedMode || _lastReqW != RequestWidth || _lastReqH != RequestHeight)
                {
                    string targetVmid = this.VmId;
                    bool targetEnh = IsEnhancedMode;
                    _targetW = RequestWidth;
                    _targetH = RequestHeight;

                    if (targetEnh && _targetW > 0 && _targetH > 0)
                    {
                        this.ActualPixelsWidth = _targetW;
                        this.ActualPixelsHeight = _targetH;
                    }

                    if (_lastConnectedId != null || (_rdpControl.RdpClient != null && (short)_rdpControl.RdpClient.ConnectionState != 0))
                    {
                        try { _rdpControl.Disconnect(); } catch { }
                        await Task.Delay(50);
                    }

                    _lastConnectedId = targetVmid;
                    _lastEnhancedMode = targetEnh;
                    _lastReqW = _targetW;
                    _lastReqH = _targetH;

                    var config = _rdpControl.RdpConfiguration;
                    config.HyperV.Instance = targetVmid.Trim().ToUpper();
                    config.HyperV.EnhancedSessionMode = targetEnh;
                    config.Server = "127.0.0.1";
                    config.Display.ResizeBehavior = ResizeBehavior.Scrollbars;
                    config.Display.ColorDepth = RdpColorDepth.ColorDepth32Bpp;
                    config.Display.AutoScaling = false;

                    config.Redirection.RedirectClipboard = true;    // 开启剪贴板共享（文字、图片）
                    config.Redirection.RedirectDrives = true;       // 开启驱动器重定向（文件拷贝的核心）
                    config.Redirection.RedirectDevices = true;      // 开启即插即用设备重定向

                    if (targetEnh && _targetW > 0)
                    {
                        config.Display.DesktopWidth = _targetW;
                        config.Display.DesktopHeight = _targetH;
                    }

                    _rdpControl.Connect();
                    await Task.Delay(10);
                }
            }
            catch (Exception ex) { Log($"Connect error: {ex.Message}"); }
            finally
            {
                _isConnecting = false;
                // 兜底：2000ms后如果还没对齐分辨率，强制揭开幕布
                await Task.Delay(2000);
                if (_isTransitioning)
                {
                    _isTransitioning = false;
                    _curtain.Visible = false;
                }
            }
        }

        private void UpdateLayoutByPixels(string reason, int pixelWidth, int pixelHeight, bool forceRefresh = false)
        {
            if (pixelWidth < 400 || pixelHeight < 400) return;

            if (_isTransitioning)
            {
                // 只有当底层上报的分辨率 = 我们请求的分辨率时，才认为“对齐了”
                if (pixelWidth == _targetW && pixelHeight == _targetH)
                {
                    _isTransitioning = false;
                    _curtain.Visible = false; // 【动作】揭开幕布
                }
                else
                {
                    Log($"[屏蔽期] 忽略不匹配的分辨率: {pixelWidth}x{pixelHeight}");
                    return;
                }
            }

            this.ActualPixelsWidth = pixelWidth;
            this.ActualPixelsHeight = pixelHeight;

            if (!forceRefresh && pixelWidth == _lastPixelW && pixelHeight == _lastPixelH) return;

            _lastPixelW = pixelWidth;
            _lastPixelH = pixelHeight;
            _pendingW = pixelWidth;
            _pendingH = pixelHeight;
            _layoutStabilizeTimer.Stop();
            _layoutStabilizeTimer.Start();
        }

        private void ExecutePhysicalLayout(int pixelWidth, int pixelHeight)
        {
            if (pixelWidth < 400) return;
            Dispatcher.Invoke(() => {
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget == null) return;
                double dpiX = source.CompositionTarget.TransformToDevice.M11;
                double dpiY = source.CompositionTarget.TransformToDevice.M22;

                this.Width = pixelWidth / dpiX;
                this.Height = pixelHeight / dpiY;

                if (_parentWindow != null)
                {
                    if (_parentWindow.WindowState == WindowState.Normal)
                    {
                        _parentWindow.Width = double.NaN; _parentWindow.Height = double.NaN;
                        _parentWindow.SizeToContent = SizeToContent.WidthAndHeight;
                        _parentWindow.UpdateLayout();
                        _parentWindow.SizeToContent = SizeToContent.Manual;
                    }
                    else
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
            _fastResizeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(20) };
            _fastResizeTimer.Tick += (s, e) => {
                if (_rdpControl.RdpClient == null || _rdpControl.RdpClient.ConnectionState != RoyalApps.Community.Rdp.WinForms.Controls.ConnectionState.Connected) return;
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

                if (currentW > 400 && (currentW != _lastPixelW || currentH != _lastPixelH))
                    UpdateLayoutByPixels("SNIFFER", currentW, currentH);
            };
            _fastResizeTimer.Start();
        }

        private void StopFastSniffer() { if (_fastResizeTimer != null) { _fastResizeTimer.Stop(); _fastResizeTimer = null; } }

        private IntPtr GetOutputPresenterHandle(IntPtr rdpHandle)
        {
            IntPtr h1 = FindWindowEx(rdpHandle, IntPtr.Zero, "UIMainClass", null);
            IntPtr h2 = FindWindowEx(h1, IntPtr.Zero, "UIContainerClass", null);
            IntPtr h3 = FindWindowEx(h2, IntPtr.Zero, "OPContainerClass", null);
            return FindWindowEx(h3, IntPtr.Zero, "OPWindowClass", null);
        }

        private void Log(string msg) => Debug.WriteLine($"[MsRdpEx] {msg}");

        public void Disconnect() { StopFastSniffer(); _layoutStabilizeTimer.Stop(); _lastConnectedId = null; try { _rdpControl?.Disconnect(); } catch { } }

        public void SendCtrlAltDel()
        {
            try
            {
                if (_rdpControl.RdpClient is AxMsRdpClient9NotSafeForScripting ax)
                {
                    // RDP ActiveX 控件发送 Ctrl+Alt+Del 有专门的接口
                    // 0x11 = Ctrl, 0x12 = Alt, 0x2E = Del
                    // 这种方式是通过控制台发送 SAS (Secure Attention Sequence) 信号
                    var nonScriptable = (IMsRdpClientNonScriptable5)ax.GetOcx();

                    // 定义按键数组
                    bool[] states = { true, true, true }; // 三个键都按下
                    int[] keys = { 0x11, 0x12, 0x2E };    // Ctrl, Alt, Del

                    // 注意：ActiveX 的 SendKeys 参数传递比较特殊
                    // 这里我们用一种最稳妥的动态调用或直接转换
                    nonScriptable.SendKeys(3, ref states[0], ref keys[0]);

                    Debug.WriteLine("[MsRdpEx] 已发送 Ctrl+Alt+Del 信号");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MsRdpEx] 发送 CAD 失败: {ex.Message}");
            }
        }

    }
}