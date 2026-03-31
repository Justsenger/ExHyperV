using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using RoyalApps.Community.Rdp.WinForms.Controls;
using RoyalApps.Community.Rdp.WinForms.Configuration;
using RdpColorDepth = RoyalApps.Community.Rdp.WinForms.Configuration.ColorDepth;
using System.Runtime.InteropServices;
using MSTSCLib;
using System.Windows.Interop;
namespace ExHyperV.Tools
{
    public class MsRdpExHost : WindowsFormsHost
    {
        private readonly RdpControl _rdpControl;
        private readonly System.Windows.Forms.Panel _curtain;
        private readonly System.Windows.Forms.Panel _winFormsContainer;

        private string? _lastConnectedId;
        private bool? _lastEnhancedMode;
        private bool? _lastRelativeMouse; // 用于判定模式切换
        private bool _wasFullScreen; // 追踪上一周期的全屏状态
        private bool? _lastSyncState = null;
        private int _lastReqW, _lastReqH;
        private DispatcherTimer? _fastResizeTimer;
        private DispatcherTimer? _layoutStabilizeTimer;
        private readonly DispatcherTimer _configDebounceTimer;
        private Window? _parentWindow;
        private int _pendingW, _pendingH;
        private bool _isConnecting;
        private bool _isTransitioning;
        private int _targetW, _targetH;

        #region Win32 API
        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string className, string? windowTitle);
        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool ClipCursor(ref RECT lpRect);
        [DllImport("user32.dll")]
        private static extern bool ClipCursor(IntPtr lpRect);
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        #endregion

        public event Action? OnRdpConnected;
        public event Action<string>? OnRdpDisconnected;

        #region Dependency Properties
        public static readonly DependencyProperty VmIdProperty = DependencyProperty.Register(nameof(VmId), typeof(string), typeof(MsRdpExHost), new PropertyMetadata(null, OnConfigChanged));
        public string VmId { get => (string)GetValue(VmIdProperty); set => SetValue(VmIdProperty, value); }

        public static readonly DependencyProperty IsEnhancedModeProperty = DependencyProperty.Register(nameof(IsEnhancedMode), typeof(bool), typeof(MsRdpExHost), new PropertyMetadata(false, OnConfigChanged));
        public bool IsEnhancedMode { get => (bool)GetValue(IsEnhancedModeProperty); set => SetValue(IsEnhancedModeProperty, value); }

        // 新增：相对鼠标模式属性
        public static readonly DependencyProperty IsRelativeMouseModeProperty = DependencyProperty.Register(nameof(IsRelativeMouseMode), typeof(bool), typeof(MsRdpExHost), new PropertyMetadata(false, OnConfigChanged));
        public bool IsRelativeMouseMode { get => (bool)GetValue(IsRelativeMouseModeProperty); set => SetValue(IsRelativeMouseModeProperty, value); }

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
            _winFormsContainer = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill, BackColor = System.Drawing.Color.Black };
            _curtain = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill, BackColor = System.Drawing.Color.Black, Visible = true };
            _rdpControl = new RdpControl { Dock = System.Windows.Forms.DockStyle.Fill };

            _winFormsContainer.Controls.Add(_curtain);
            _winFormsContainer.Controls.Add(_rdpControl);
            _curtain.BringToFront();

            _configDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            _configDebounceTimer.Tick += (s, e) => { _configDebounceTimer.Stop(); _ = TriggerConnectAsync(); };

            _layoutStabilizeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
            _layoutStabilizeTimer.Tick += (s, e) => {
                // 如果检测到鼠标左键仍处于按下状态，说明用户可能正在拖动标题栏
                // 此时不执行布局调整，让 Timer 继续跳动，直到用户松开鼠标
                if ((GetAsyncKeyState(0x01) & 0x8000) != 0) return;

                _layoutStabilizeTimer.Stop();
                ExecutePhysicalLayout(_pendingW, _pendingH);
            };
            // 核心交互：点击画面即尝试捕获鼠标
            _rdpControl.OnClientAreaClicked += (s, e) => {
                if (IsRelativeMouseMode) TrapMouse();
            };


            _rdpControl.OnConnected += (s, e) => {
                int w = _rdpControl.RdpClient!.DesktopWidth, h = _rdpControl.RdpClient.DesktopHeight;
                if (!_isTransitioning) { ActualPixelsWidth = w; ActualPixelsHeight = h; ExecutePhysicalLayout(w, h); _curtain.Visible = false; }
                StartFastSniffer(); OnRdpConnected?.Invoke();
            };

            _rdpControl.OnDisconnected += (s, e) => {
                ReleaseMouse(); // 断开时释放鼠标
                StopFastSniffer(); _layoutStabilizeTimer.Stop(); _curtain.Visible = true;
                _lastConnectedId = null; _lastEnhancedMode = null; _lastRelativeMouse = null;
                OnRdpDisconnected?.Invoke(e.Description);
            };

            this.Child = _winFormsContainer;
            this.Loaded += (s, e) => {
                _parentWindow = Window.GetWindow(this);
                if (_parentWindow != null) _parentWindow.Deactivated += ParentWindow_Deactivated;
            };
            this.Unloaded += (s, e) => {
                ReleaseMouse();
                if (_parentWindow != null) _parentWindow.Deactivated -= ParentWindow_Deactivated;
            };
        }

        private void ParentWindow_Deactivated(object? sender, EventArgs e)
        {
            ReleaseMouse(); // 失去焦点自动释放鼠标
            if (this.DataContext is ViewModels.ConsoleViewModel vm && vm.IsFullScreen) vm.IsFullScreen = false;
        }

        private static void OnIsVmRunningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { if (d is MsRdpExHost host && (bool)e.NewValue) host._configDebounceTimer.Start(); }
        private static void OnConfigChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { if (d is MsRdpExHost host) host._configDebounceTimer.Start(); }

        private async Task TriggerConnectAsync()
        {
            if (string.IsNullOrEmpty(VmId) || !this.IsLoaded || !IsVmRunning) return;
            if (_isConnecting) return;

            double hostDpi = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null) hostDpi = source.CompositionTarget.TransformToDevice.M11;

            bool isConnected = _rdpControl.RdpClient?.ConnectionState == RoyalApps.Community.Rdp.WinForms.Controls.ConnectionState.Connected;

            if (isConnected && _lastConnectedId == VmId && _lastEnhancedMode == IsEnhancedMode &&
                _lastRelativeMouse == IsRelativeMouseMode && _lastReqW == RequestWidth && _lastReqH == RequestHeight) return;

            _isConnecting = true; _isTransitioning = true; _curtain.Visible = true;
            try
            {
                while (_lastConnectedId != VmId || _lastEnhancedMode != IsEnhancedMode ||
                       _lastRelativeMouse != IsRelativeMouseMode || _lastReqW != RequestWidth || _lastReqH != RequestHeight)
                {
                    _targetW = RequestWidth;
                    _targetH = RequestHeight;

                    System.Diagnostics.Debug.WriteLine(Properties.Resources.MsRdpExHost_1);
                    System.Diagnostics.Debug.WriteLine(string.Format(Properties.Resources.MsRdpExHost_2, hostDpi, IsEnhancedMode));
                    System.Diagnostics.Debug.WriteLine(string.Format(Properties.Resources.MsRdpExHost_3, _targetW, _targetH));

                    // 【关键点】删掉了这行：ActualPixelsWidth = _targetW; 
                    // 理由：不能在连接前就假定分辨率已经改变，否则会误导 Sniffer 的“From”逻辑

                    if (_lastConnectedId != null || (_rdpControl.RdpClient != null && (short)_rdpControl.RdpClient.ConnectionState != 0))
                    {
                        try { _rdpControl.Disconnect(); } catch { }
                        await Task.Delay(50);
                    }
                    _lastConnectedId = VmId; _lastEnhancedMode = IsEnhancedMode;
                    _lastRelativeMouse = IsRelativeMouseMode; _lastReqW = _targetW; _lastReqH = _targetH;

                    var config = _rdpControl.RdpConfiguration;
                    config.Input.KeyboardHookMode = true;
                    config.Input.KeyboardHookToggleShortcutEnabled = true;
                    config.Input.RelativeMouseMode = IsRelativeMouseMode;
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
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine(string.Format(Properties.Resources.MsRdpExHost_4, ex.Message)); }
            finally { _isConnecting = false; await Task.Delay(1000); _isTransitioning = false; _curtain.Visible = false; }
        }        
        #region Mouse Trap Logic
        public void TrapMouse()
        {
            Dispatcher.BeginInvoke(new Action(() => {
                try
                {
                    IntPtr opHandle = GetOutputPresenterHandle(_rdpControl.Handle);
                    if (opHandle == IntPtr.Zero) return;

                    SetFocus(opHandle);

                    // 获取 RDP 渲染窗口在屏幕上的物理矩形
                    if (GetWindowRect(opHandle, out RECT rect))
                    {
                        // 留出 1 像素内边距，防止鼠标滑出判定
                        rect.Left += 1; rect.Top += 1; rect.Right -= 1; rect.Bottom -= 1;
                        ClipCursor(ref rect);
                    }
                }
                catch { }
            }), DispatcherPriority.Render);
        }

        public void ReleaseMouse()
        {
            ClipCursor(IntPtr.Zero);
        }
        #endregion

        private void UpdateLayoutByPixels(int w, int h)
        {
            if (w < 400 || h < 400) return;
            if (_isTransitioning && (w != _targetW || h != _targetH)) return;
            _isTransitioning = false; _curtain.Visible = false;
            ActualPixelsWidth = w; ActualPixelsHeight = h;
            _pendingW = w; _pendingH = h;
            _layoutStabilizeTimer.Stop(); _layoutStabilizeTimer.Start();
        }

        private void ExecutePhysicalLayout(int w, int h)
        {
            if (w < 400) return;
            Dispatcher.Invoke(() => {
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget == null) return;
                double dpiX = source.CompositionTarget.TransformToDevice.M11, dpiY = source.CompositionTarget.TransformToDevice.M22;

                System.Diagnostics.Debug.WriteLine(Properties.Resources.MsRdpExHost_5);
                System.Diagnostics.Debug.WriteLine($"[RDP_LOG] 物理像素: {w}x{h}, DPI渲染比例: {dpiX}");

                // 还原逻辑：恢复为你原始的计算方式（去掉了我之前加的 0.5 余量）
                Width = (w / dpiX);
                Height = (h / dpiY);

                System.Diagnostics.Debug.WriteLine(string.Format(Properties.Resources.MsRdpExHost_7, Width, Height));

                if (_parentWindow?.WindowState == WindowState.Normal)
                {
                    _parentWindow.Width = double.NaN; _parentWindow.Height = double.NaN;
                    _parentWindow.SizeToContent = SizeToContent.WidthAndHeight;
                    _parentWindow.UpdateLayout(); _parentWindow.SizeToContent = SizeToContent.Manual;
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
                        _lastSyncState = currentUI;
                        client.KeyboardHookMode = currentUI ? 1 : 0;
                        _wasFullScreen = currentUI;
                        return;
                    }

                    if (currentHook != _lastSyncState)
                    {
                        vm.IsFullScreen = currentHook; // 强行拉动 UI 进入或退出全屏
                        if (!currentHook) ReleaseMouse();
                        _lastSyncState = currentHook;
                        _wasFullScreen = currentHook;
                    }
                    else if (currentUI != _lastSyncState)
                    {
                        client.KeyboardHookMode = currentUI ? 1 : 0; // 强行拉动 Hook 开启或关闭
                        if (!currentUI) ReleaseMouse();
                        _lastSyncState = currentUI;
                        _wasFullScreen = currentUI;
                    }
                }

                if (_rdpControl.RdpClient?.ConnectionState != RoyalApps.Community.Rdp.WinForms.Controls.ConnectionState.Connected) return;

                IntPtr opHandle = GetOutputPresenterHandle(_rdpControl.Handle);
                int cw, ch;

                // 获取当前 Win32 窗口真实的物理像素尺寸
                if (opHandle != IntPtr.Zero && GetClientRect(opHandle, out RECT rect))
                {
                    cw = rect.Right - rect.Left;
                    ch = rect.Bottom - rect.Top;
                }
                else
                {
                    cw = _rdpControl.RdpClient.DesktopWidth;
                    ch = _rdpControl.RdpClient.DesktopHeight;
                }

                // 如果探测到的物理尺寸 cw 跟我们记录的上次尺寸 ActualPixelsWidth 不同
                if (cw > 400 && (cw != ActualPixelsWidth || ch != ActualPixelsHeight))
                {
                    // 此时日志将正确显示：从 [旧的实际值] 变为 [探测到的新实际值]
                    System.Diagnostics.Debug.WriteLine($"[RDP_LOG] 分辨率真实变化: 从 {ActualPixelsWidth}x{ActualPixelsHeight} (记录) 变为 {cw}x{ch} (窗口)");

                    // 更新记录并触发布局调整
                    UpdateLayoutByPixels(cw, ch);
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

        public void Disconnect() { ReleaseMouse(); StopFastSniffer(); _layoutStabilizeTimer.Stop(); _lastConnectedId = null; try { _rdpControl?.Disconnect(); } catch { } }

        public void SendCtrlAltDel()
        {
            string targetVmId = this.VmId;
            bool isEnhanced = this.IsEnhancedMode;
            if (string.IsNullOrEmpty(targetVmId)) return;

            if (isEnhanced)
            {
                try
                {
                    object ocx = _rdpControl.RdpClient?.GetOcx();
                    if (ocx is IMsRdpClientNonScriptable5 nonScriptable)
                    {
                        IntPtr opHandle = GetOutputPresenterHandle(_rdpControl.Handle);
                        if (opHandle != IntPtr.Zero) SetFocus(opHandle);

                        int[] codes = { 0x1D, 0x38, 0x53 | 0x100 };
                        bool[] down = { false, false, false };
                        bool[] up = { true, true, true };

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
            else
            {
                Task.Run(async () => { await VmInputTool.SendCtrlAltDelAsync(targetVmId); });
            }
        }
    }
}