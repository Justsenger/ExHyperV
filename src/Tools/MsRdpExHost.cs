using System;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using RoyalApps.Community.Rdp.WinForms.Controls;
using RoyalApps.Community.Rdp.WinForms.Configuration;
using RdpColorDepth = RoyalApps.Community.Rdp.WinForms.Configuration.ColorDepth;
using RdpAuthLevel = RoyalApps.Community.Rdp.WinForms.Configuration.AuthenticationLevel;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading.Tasks;
using AxMSTSCLib;
using MSTSCLib;

namespace ExHyperV.Tools
{
    public class MsRdpExHost : WindowsFormsHost
    {
        private readonly RdpControl _rdpControl;
        private readonly System.Windows.Forms.Panel _curtain;
        private readonly System.Windows.Forms.Panel _winFormsContainer;

        private string? _lastConnectedId;
        private bool? _lastEnhancedMode;
        private int _lastReqW, _lastReqH;
        private DispatcherTimer? _fastResizeTimer;
        private DispatcherTimer? _layoutStabilizeTimer;
        private DispatcherTimer _configDebounceTimer;
        private Window? _parentWindow;
        private int _lastPixelW = 0, _lastPixelH = 0;
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
            _winFormsContainer = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill, BackColor = System.Drawing.Color.Black };
            _curtain = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill, BackColor = System.Drawing.Color.Black, Visible = true };
            _rdpControl = new RdpControl { Dock = System.Windows.Forms.DockStyle.Fill };

            _winFormsContainer.Controls.Add(_curtain);
            _winFormsContainer.Controls.Add(_rdpControl);
            _curtain.BringToFront();

            _configDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            _configDebounceTimer.Tick += (s, e) => { _configDebounceTimer.Stop(); _ = TriggerConnectAsync(); };

            _layoutStabilizeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(10) };
            _layoutStabilizeTimer.Tick += (s, e) => { _layoutStabilizeTimer.Stop(); ExecutePhysicalLayout(_pendingW, _pendingH); };

            _rdpControl.OnConnected += (s, e) => {
                int w = _rdpControl.RdpClient!.DesktopWidth, h = _rdpControl.RdpClient.DesktopHeight;
                if (!_isTransitioning) { ActualPixelsWidth = w; ActualPixelsHeight = h; ExecutePhysicalLayout(w, h); _curtain.Visible = false; }
                StartFastSniffer(); OnRdpConnected?.Invoke();
            };

            _rdpControl.OnDisconnected += (s, e) => {
                StopFastSniffer(); _layoutStabilizeTimer.Stop(); _curtain.Visible = true;
                _lastConnectedId = null; _lastEnhancedMode = null; _lastPixelW = 0; _lastPixelH = 0;
                OnRdpDisconnected?.Invoke(e.Description);
            };

            this.Child = _winFormsContainer;
            this.Loaded += (s, e) => {
                _parentWindow = Window.GetWindow(this);
                if (_parentWindow != null)
                {
                    // 核心哲学：全屏时如果失去焦点（比如按了Ctrl+Alt+Del），自动退出全屏
                    _parentWindow.Deactivated += ParentWindow_Deactivated;
                }
            };
            this.Unloaded += (s, e) => {
                if (_parentWindow != null) _parentWindow.Deactivated -= ParentWindow_Deactivated;
            };
        }

        private void ParentWindow_Deactivated(object? sender, EventArgs e)
        {
            if (this.DataContext is ViewModels.ConsoleViewModel vm && vm.IsFullScreen)
            {
                Log("检测到焦点丢失（可能是Ctrl+Alt+Del），自动恢复窗口模式");
                vm.IsFullScreen = false;
            }
        }

        public static readonly DependencyProperty IsVmRunningProperty = DependencyProperty.Register(nameof(IsVmRunning), typeof(bool), typeof(MsRdpExHost), new PropertyMetadata(false, OnIsVmRunningChanged));
        public bool IsVmRunning { get => (bool)GetValue(IsVmRunningProperty); set => SetValue(IsVmRunningProperty, value); }
        private static void OnIsVmRunningChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { if (d is MsRdpExHost host && (bool)e.NewValue) host._configDebounceTimer.Start(); }
        private static void OnConfigChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { if (d is MsRdpExHost host) host._configDebounceTimer.Start(); }

        private async Task TriggerConnectAsync()
        {
            if (string.IsNullOrEmpty(VmId) || !this.IsLoaded || !IsVmRunning) return;
            if (_isConnecting) return;
            bool isConnected = _rdpControl.RdpClient?.ConnectionState == RoyalApps.Community.Rdp.WinForms.Controls.ConnectionState.Connected;
            if (isConnected && _lastConnectedId == VmId && _lastEnhancedMode == IsEnhancedMode && _lastReqW == RequestWidth && _lastReqH == RequestHeight) return;

            _isConnecting = true; _isTransitioning = true; _curtain.Visible = true;
            try
            {
                while (_lastConnectedId != VmId || _lastEnhancedMode != IsEnhancedMode || _lastReqW != RequestWidth || _lastReqH != RequestHeight)
                {
                    _targetW = RequestWidth; _targetH = RequestHeight;
                    if (IsEnhancedMode && _targetW > 0) { ActualPixelsWidth = _targetW; ActualPixelsHeight = _targetH; }
                    if (_lastConnectedId != null || (_rdpControl.RdpClient != null && (short)_rdpControl.RdpClient.ConnectionState != 0))
                    {
                        try { _rdpControl.Disconnect(); } catch { }
                        await Task.Delay(50);
                    }
                    _lastConnectedId = VmId; _lastEnhancedMode = IsEnhancedMode; _lastReqW = _targetW; _lastReqH = _targetH;

                    var config = _rdpControl.RdpConfiguration;
                    config.Input.KeyboardHookMode = true; // 开启监狱模式，100%沉浸
                    config.Input.EnableWindowsKey = true;
                    config.Input.GrabFocusOnConnect = true;

                    config.HyperV.Instance = VmId.Trim().ToUpper();
                    config.HyperV.EnhancedSessionMode = IsEnhancedMode;
                    config.Server = "127.0.0.1";
                    config.Display.ResizeBehavior = ResizeBehavior.Scrollbars;
                    config.Display.ColorDepth = RdpColorDepth.ColorDepth32Bpp;
                    config.Display.AutoScaling = false;
                    config.Redirection.RedirectClipboard = true;
                    config.Redirection.RedirectDrives = true;
                    config.Redirection.RedirectDevices = true;

                    if (IsEnhancedMode && _targetW > 0) { config.Display.DesktopWidth = _targetW; config.Display.DesktopHeight = _targetH; }
                    _rdpControl.Connect(); await Task.Delay(10);
                }
            }
            catch (Exception ex) { Log($"Error: {ex.Message}"); }
            finally { _isConnecting = false; await Task.Delay(1000); _isTransitioning = false; _curtain.Visible = false; }
        }

        private void UpdateLayoutByPixels(string reason, int w, int h, bool force = false)
        {
            if (w < 400 || h < 400) return;
            if (_isTransitioning && (w != _targetW || h != _targetH)) return;
            _isTransitioning = false; _curtain.Visible = false;
            ActualPixelsWidth = w; ActualPixelsHeight = h;
            if (!force && w == _lastPixelW && h == _lastPixelH) return;
            _lastPixelW = w; _lastPixelH = h; _pendingW = w; _pendingH = h;
            _layoutStabilizeTimer.Stop(); _layoutStabilizeTimer.Start();
        }

        private void ExecutePhysicalLayout(int w, int h)
        {
            if (w < 400) return;
            Dispatcher.Invoke(() => {
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget == null) return;
                double dpiX = source.CompositionTarget.TransformToDevice.M11, dpiY = source.CompositionTarget.TransformToDevice.M22;
                Width = w / dpiX; Height = h / dpiY;
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
                if (_rdpControl.RdpClient?.ConnectionState != RoyalApps.Community.Rdp.WinForms.Controls.ConnectionState.Connected) return;
                IntPtr opHandle = GetOutputPresenterHandle(_rdpControl.Handle);
                int cw, ch;
                if (opHandle != IntPtr.Zero && GetClientRect(opHandle, out RECT rect)) { cw = rect.Right - rect.Left; ch = rect.Bottom - rect.Top; }
                else { cw = _rdpControl.RdpClient.DesktopWidth; ch = _rdpControl.RdpClient.DesktopHeight; }
                if (cw > 400 && (cw != _lastPixelW || ch != _lastPixelH)) UpdateLayoutByPixels("SNIFFER", cw, ch);
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
        private void Log(string msg) => Debug.WriteLine($"[MsRdpEx] {msg}");
        public void Disconnect() { StopFastSniffer(); _layoutStabilizeTimer.Stop(); _lastConnectedId = null; try { _rdpControl?.Disconnect(); } catch { } }
        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        public void SendCtrlAltDel()
        {
            try
            {
                if (_rdpControl.RdpClient == null || _rdpControl.RdpClient.ConnectionState != RoyalApps.Community.Rdp.WinForms.Controls.ConnectionState.Connected)
                    return;

                // 1. 物理聚焦渲染窗口 (必不可少，否则注入会被忽略)
                IntPtr opHandle = GetOutputPresenterHandle(_rdpControl.Handle);
                if (opHandle != IntPtr.Zero) SetFocus(opHandle);

                if (_rdpControl.RdpClient is AxMsRdpClient9NotSafeForScripting ax)
                {
                    var nonScriptable = (IMsRdpClientNonScriptable5)ax.GetOcx();

                    // 关键逻辑：在 RDP 中，发送 Ctrl(0x11) + Alt(0x12) + End(0x23) 
                    // 远程计算机会将其视为 Ctrl+Alt+Del
                    int VK_CONTROL = 0x11;
                    int VK_MENU = 0x12; // Alt
                    int VK_END = 0x23;  // 用 End 代替 Del

                    // 定义序列：按下 Ctrl, 按下 Alt, 按下 End
                    int[] keys = { VK_CONTROL, VK_MENU, VK_END };
                    bool[] down = { true, true, true };
                    bool[] up = { false, false, false };

                    // 发送按下
                    nonScriptable.SendKeys(3, ref down[0], ref keys[0]);
                    // 发送弹起
                    nonScriptable.SendKeys(3, ref up[0], ref keys[0]);

                    Log("通过模拟 Ctrl+Alt+End 发送了 SAS 信号");
                }
            }
            catch (Exception ex)
            {
                Log($"CAD 注入失败: {ex.Message}");
            }
        }
    }
}