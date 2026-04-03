using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Interop;
using System.Windows.Threading;
using MSTSCLib;
using RoyalApps.Community.Rdp.WinForms.Configuration;
using RoyalApps.Community.Rdp.WinForms.Controls;
using RdpColorDepth = RoyalApps.Community.Rdp.WinForms.Configuration.ColorDepth;
using WinForms = System.Windows.Forms;

namespace ExHyperV.Tools
{
    public class MsRdpExHost : WindowsFormsHost
    {
        #region Fields
        private readonly RdpControl _rdpControl;                       // RDP 核心渲染控件
        private readonly WinForms.Panel _curtain;                      // 遮盖闪烁的黑色幕布
        private readonly WinForms.Panel _winFormsContainer;            // WinForms 容器

        private string? _lastConnectedId;                              // VM ID 缓存
        private bool? _lastEnhancedMode;                               // 增强模式缓存
        private bool? _lastSyncState = null;                           // 状态同步标志位
        private bool _wasFullScreen;                                   // 全屏追踪
        private int _lastReqW, _lastReqH;                              // 上次请求的分辨率
        private int _pendingW, _pendingH;                              // 布局挂起宽高
        private int _targetW, _targetH;                                // 目标分辨率

        private bool _isConnecting;                                    // 连接锁
        private bool _isTransitioning;                                 // 过渡期锁
        private bool _isUserResizingOrMoving = false;                  // 用户操作窗口锁
        private bool _isLayoutPending = false;                         // 布局待处理标志

        private DispatcherTimer? _fastResizeTimer;                     // 像素嗅探器
        private DispatcherTimer? _layoutStabilizeTimer;                // 布局稳定器
        private readonly DispatcherTimer _configDebounceTimer;         // 配置防抖器
        private Window? _parentWindow;                                 // 父级窗口引用
        private int _hookChangeConfirmCount = 0;                           // Hook 变化双采样计数器
        #endregion

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

        public static readonly DependencyProperty IsVmRunningProperty = DependencyProperty.Register(nameof(IsVmRunning), typeof(bool), typeof(MsRdpExHost), new PropertyMetadata(false, OnIsVmRunningChanged));
        public bool IsVmRunning { get => (bool)GetValue(IsVmRunningProperty); set => SetValue(IsVmRunningProperty, value); }
        #endregion

        public MsRdpExHost()
        {
            _winFormsContainer = new WinForms.Panel { Dock = WinForms.DockStyle.Fill, BackColor = System.Drawing.Color.Black };
            _curtain = new WinForms.Panel { Dock = WinForms.DockStyle.Fill, BackColor = System.Drawing.Color.Black, Visible = true };
            _rdpControl = new RdpControl { Dock = WinForms.DockStyle.Fill };

            _winFormsContainer.Controls.Add(_curtain);
            _winFormsContainer.Controls.Add(_rdpControl);
            _curtain.BringToFront();

            _configDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            _configDebounceTimer.Tick += (s, e) => { _configDebounceTimer.Stop(); _ = TriggerConnectAsync(); };

            _layoutStabilizeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
            _layoutStabilizeTimer.Tick += (s, e) => {
                if ((GetAsyncKeyState(0x01) & 0x8000) != 0) return;
                _layoutStabilizeTimer.Stop();
                ExecutePhysicalLayout(_pendingW, _pendingH);
            };

            _rdpControl.OnConnected += (s, e) => {
                var client = _rdpControl.RdpClient;
                if (client == null) return;
                ActualPixelsWidth = client.DesktopWidth;
                ActualPixelsHeight = client.DesktopHeight;
                ExecutePhysicalLayout(ActualPixelsWidth, ActualPixelsHeight);
                _curtain.Visible = false;
                StartFastSniffer();
                OnRdpConnected?.Invoke();
            };

            _rdpControl.OnDisconnected += (s, e) => {
                StopFastSniffer();
                _layoutStabilizeTimer.Stop();
                _curtain.Visible = true;
                _lastConnectedId = null; _lastEnhancedMode = null; _lastSyncState = null;
                OnRdpDisconnected?.Invoke(e.Description);
            };

            this.Child = _winFormsContainer;
            this.Loaded += (s, e) => {
                _parentWindow = Window.GetWindow(this);
                if (_parentWindow != null)
                {
                    _parentWindow.Deactivated += ParentWindow_Deactivated;
                    var helper = new WindowInteropHelper(_parentWindow);
                    HwndSource.FromHwnd(helper.Handle)?.AddHook(WndProc);
                }
            };
            this.Unloaded += (s, e) => {
                if (_parentWindow != null) _parentWindow.Deactivated -= ParentWindow_Deactivated;
            };
        }

        private async Task TriggerConnectAsync()
        {
            if (string.IsNullOrEmpty(VmId) || !this.IsLoaded || !IsVmRunning) return;
            if (_isConnecting) return;

            bool isConnected = _rdpControl.RdpClient?.ConnectionState == RoyalApps.Community.Rdp.WinForms.Controls.ConnectionState.Connected;
            if (isConnected && _lastConnectedId == VmId && _lastEnhancedMode == IsEnhancedMode &&
                _lastReqW == RequestWidth && _lastReqH == RequestHeight) return;

            _isConnecting = true; _isTransitioning = true; _curtain.Visible = true;
            try
            {
                while (_lastConnectedId != VmId || _lastEnhancedMode != IsEnhancedMode ||
                       _lastReqW != RequestWidth || _lastReqH != RequestHeight)
                {
                    _targetW = RequestWidth; _targetH = RequestHeight;
                    if (_lastConnectedId != null || (_rdpControl.RdpClient != null && (short)_rdpControl.RdpClient.ConnectionState != 0))
                    {
                        try { _rdpControl.Disconnect(); } catch { }
                        await Task.Delay(50);
                    }
                    _lastConnectedId = VmId; _lastEnhancedMode = IsEnhancedMode;
                    _lastReqW = _targetW; _lastReqH = _targetH;

                    var config = _rdpControl.RdpConfiguration;
                    config.Input.KeyboardHookMode = true;
                    config.Input.KeyboardHookToggleShortcutEnabled = true;
                    config.Input.AllowBackgroundInput = true;
                    config.Input.EnableWindowsKey = true;
                    config.Input.GrabFocusOnConnect = true;
                    config.HyperV.Instance = VmId.Trim().ToUpper();
                    config.HyperV.EnhancedSessionMode = IsEnhancedMode;
                    config.Server = "127.0.0.1";
                    config.Display.ResizeBehavior = ResizeBehavior.Scrollbars;
                    config.Display.ColorDepth = RdpColorDepth.ColorDepth32Bpp;
                    config.Redirection.RedirectClipboard = true;

                    if (IsEnhancedMode)
                    {
                        config.Display.DesktopWidth = _targetW;
                        config.Display.DesktopHeight = _targetH;
                    }
                    _rdpControl.Connect();
                    await Task.Delay(10);
                }
            }
            catch { }
            finally
            {
                _isConnecting = false; await Task.Delay(1000); _isTransitioning = false; _curtain.Visible = false;
            }
        }

        private void ExecutePhysicalLayout(int w, int h)
        {
            if (w < 400 || h < 400 || _isUserResizingOrMoving)
            {
                if (_isUserResizingOrMoving) { _pendingW = w; _pendingH = h; _isLayoutPending = true; }
                return;
            }
            Dispatcher.Invoke(() => {
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget == null || _parentWindow == null) return;
                double dpiX = source.CompositionTarget.TransformToDevice.M11;
                double dpiY = source.CompositionTarget.TransformToDevice.M22;
                _rdpControl.Size = new System.Drawing.Size(w, h);
                _winFormsContainer.Size = new System.Drawing.Size(w, h);
                this.Width = w / dpiX; this.Height = h / dpiY;
                if (_parentWindow.WindowState == WindowState.Normal)
                {
                    _parentWindow.SizeToContent = SizeToContent.Manual;
                    _parentWindow.InvalidateMeasure(); _parentWindow.UpdateLayout();
                    _parentWindow.SizeToContent = SizeToContent.WidthAndHeight;
                    Dispatcher.BeginInvoke(new Action(() => {
                        if (!_isUserResizingOrMoving) _parentWindow.SizeToContent = SizeToContent.Manual;
                    }), DispatcherPriority.Background);
                }
            });
        }

        private void StartFastSniffer()
        {
            if (_fastResizeTimer != null) return;
            _fastResizeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(20) };
            _fastResizeTimer.Tick += (s, e) => {
                var client = _rdpControl.RdpClient;
                if (client?.ConnectionState != RoyalApps.Community.Rdp.WinForms.Controls.ConnectionState.Connected) return;

                if (this.DataContext is ViewModels.ConsoleViewModel vm)
                {
                    bool currentUI = vm.IsFullScreen;
                    bool currentHook = client.KeyboardHookMode != 0;
                    if (_lastSyncState == null)
                    {
                        _lastSyncState = currentUI; client.KeyboardHookMode = currentUI ? 1 : 0;
                    }
                    else if (currentHook != _lastSyncState)
                    {
                        if (_hookChangeConfirmCount == 0)
                        {
                            _hookChangeConfirmCount = 1; 
                        }
                        else
                        {
                            vm.IsFullScreen = currentHook;
                            _lastSyncState = currentHook;
                            _hookChangeConfirmCount = 0;
                        }
                    }
                    else if (currentUI != _lastSyncState)
                    {
                        client.KeyboardHookMode = currentUI ? 1 : 0;
                        _lastSyncState = currentUI;
                        _hookChangeConfirmCount = 0;
                    }
                }

                if (_isTransitioning) return;
                int dw = client.DesktopWidth; int dh = client.DesktopHeight;
                if (dw > 400 && (dw != ActualPixelsWidth || dh != ActualPixelsHeight))
                {
                    UpdateLayoutByPixels(dw, dh); return;
                }
                IntPtr opHandle = GetOutputPresenterHandle(_rdpControl.Handle);
                if (opHandle != IntPtr.Zero && GetClientRect(opHandle, out RECT rect))
                {
                    int cw = rect.Right - rect.Left; int ch = rect.Bottom - rect.Top;
                    if (cw > 400 && (cw != ActualPixelsWidth || ch != ActualPixelsHeight))
                    {
                        if (IsEnhancedMode || !_isTransitioning) UpdateLayoutByPixels(cw, ch);
                    }
                }
            };
            _fastResizeTimer.Start();
        }

        private void UpdateLayoutByPixels(int w, int h)
        {
            if (w < 400 || h < 400 || (_isTransitioning && (w != _targetW || h != _targetH))) return;
            _isTransitioning = false; _curtain.Visible = false;
            ActualPixelsWidth = w; ActualPixelsHeight = h; _pendingW = w; _pendingH = h;
            if (_isUserResizingOrMoving) _isLayoutPending = true;
            else { _layoutStabilizeTimer?.Stop(); _layoutStabilizeTimer?.Start(); }
        }

        private void ParentWindow_Deactivated(object? sender, EventArgs e)
        {
            if (this.DataContext is ViewModels.ConsoleViewModel vm && vm.IsFullScreen) vm.IsFullScreen = false;
        }

        private static void OnIsVmRunningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { if (d is MsRdpExHost host && (bool)e.NewValue) host._configDebounceTimer.Start(); }
        private static void OnConfigChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { if (d is MsRdpExHost host) host._configDebounceTimer.Start(); }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_ENTERSIZEMOVE = 0x0231; const int WM_EXITSIZEMOVE = 0x0232;
            if (msg == WM_ENTERSIZEMOVE) _isUserResizingOrMoving = true;
            else if (msg == WM_EXITSIZEMOVE)
            {
                _isUserResizingOrMoving = false;
                if (_isLayoutPending) { _isLayoutPending = false; ExecutePhysicalLayout(_pendingW, _pendingH); }
            }
            return IntPtr.Zero;
        }

        private IntPtr GetOutputPresenterHandle(IntPtr rdpHandle)
        {
            IntPtr h1 = FindWindowEx(rdpHandle, IntPtr.Zero, "UIMainClass", null);
            IntPtr h2 = FindWindowEx(h1, IntPtr.Zero, "UIContainerClass", null);
            IntPtr h3 = FindWindowEx(h2, IntPtr.Zero, "OPContainerClass", null);
            return FindWindowEx(h3, IntPtr.Zero, "OPWindowClass", null);
        }

        public void Disconnect() { StopFastSniffer(); _layoutStabilizeTimer.Stop(); _lastConnectedId = null; try { _rdpControl?.Disconnect(); } catch { } }

        public void SendCtrlAltDel()
        {
            string targetVmId = this.VmId;
            if (string.IsNullOrEmpty(targetVmId)) return;
            if (IsEnhancedMode)
            {
                try
                {
                    if (_rdpControl.RdpClient?.GetOcx() is IMsRdpClientNonScriptable5 nonScriptable)
                    {
                        IntPtr opHandle = GetOutputPresenterHandle(_rdpControl.Handle);
                        if (opHandle != IntPtr.Zero) SetFocus(opHandle);
                        int[] codes = { 0x1D, 0x38, 0x53 | 0x100 };
                        bool[] down = { false, false, false }, up = { true, true, true };
                        for (int i = 0; i < 10; i++)
                        {
                            nonScriptable.SendKeys(3, ref down[0], ref codes[0]);
                            System.Threading.Thread.Sleep(10);
                            nonScriptable.SendKeys(3, ref up[0], ref codes[0]);
                            System.Threading.Thread.Sleep(30);
                        }
                    }
                }
                catch { }
            }
            else Task.Run(async () => await VmInputTool.SendCtrlAltDelAsync(targetVmId));
        }

        private void StopFastSniffer() { _fastResizeTimer?.Stop(); _fastResizeTimer = null; }

        #region Win32 API
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string? windowTitle);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        #endregion
    }
}