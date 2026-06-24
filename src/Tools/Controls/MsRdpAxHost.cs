using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MSTSCLib;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 直接托管系统 mstscax.dll 的 RDP ActiveX（CLSID = MsRdpClient9NotSafeForScripting），
    /// 不经 RoyalApps、不经 aximp 生成的 AxInterop——自己派生 <see cref="AxHost"/>。
    /// 标量属性走 IDispatch 晚绑定（dynamic），非脚本属性与事件走类型化 COM 接口。
    /// </summary>
    internal sealed class MsRdpAxHost : AxHost
    {
        private const string MsRdpClient9Clsid = "8b918b82-7985-4c24-89df-c33ad2bbfbcd";
        private bool _smartSizing;   // 当前 SmartSizing 状态缓存（SetSmartSizing 用，避免重复设值闪烁）

        public event Action? Connected;
        public event Action<int>? Disconnected;
        public event Action<int, int>? RemoteDesktopSizeChanged;
        public event Action? EnteredFullScreen;
        public event Action? LeftFullScreen;
        public event Action<int>? FatalError;
        public event Action? MinimizeRequested;   // 连接栏最小化按钮（容器处理全屏）
        public event Action? CloseRequested;       // 连接栏关闭按钮（容器处理全屏）

        public MsRdpAxHost() : base(MsRdpClient9Clsid) { }

        // AxHost 在底层 OCX 创建完成后调用此处——是订阅 COM 事件的规范时机。
        protected override void AttachInterfaces()
        {
            try
            {
                var evt = (IMsTscAxEvents_Event)GetOcx();
                // 每个处理都过 Safe()——COM 事件 sink 绝不能让异常逃回 native，否则 0xC000041D 进程秒退。
                evt.OnConnected += () => Safe(() => Connected?.Invoke());
                evt.OnDisconnected += reason => Safe(() => Disconnected?.Invoke(reason));
                evt.OnRemoteDesktopSizeChange += (w, h) => Safe(() => RemoteDesktopSizeChanged?.Invoke(w, h));
                // 容器处理全屏：热键/请求经 OnRequestGo/LeaveFullScreen（非 OnEnter/Leave，那是控件自身全屏才触发）
                evt.OnRequestGoFullScreen += () => Safe(() => EnteredFullScreen?.Invoke());
                evt.OnRequestLeaveFullScreen += () => Safe(() => LeftFullScreen?.Invoke());
                evt.OnFatalError += code => Safe(() => FatalError?.Invoke(code));
                // 容器处理全屏下，连接栏的最小化/关闭按钮经事件交给容器（窗口）处理
                evt.OnRequestContainerMinimize += () => Safe(() => MinimizeRequested?.Invoke());
                evt.OnConfirmClose += () => { Safe(() => CloseRequested?.Invoke()); return true; };  // 返回值=允许关闭
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Rdp] AttachInterfaces 失败: " + ex);
            }
        }

        public void ApplyAndConnect(RdpConnectionSettings s)
        {
            try
            {
                dynamic rdp = GetOcx();
                rdp.Server = s.Server;

                // ★ UI 父窗口句柄：控件弹出的子窗口需要有效父窗口，否则在框架回调里抛异常逃回 native → 0xC000041D。
                // COMReference(tlbimp) 把它生成成 set_UIParentWindowHandle(ref _RemotableHandle/wireHWND)，需手填：
                //   fContext = WDT_INPROC_CALL(0x48746457)，hInproc = HWND 低 32 位（USER 句柄恒在 32 位内）。
                TrySet("UIParentWindowHandle", () =>
                {
                    var h = new _RemotableHandle { fContext = 0x48746457 };
                    h.u.hInproc = GetAncestor(this.Handle, GA_ROOT).ToInt32();
                    ((IMsRdpClientNonScriptable3)GetOcx()).set_UIParentWindowHandle(ref h);
                });

                dynamic adv = rdp.AdvancedSettings9;
                adv.RDPPort = s.Port;
                adv.AuthenticationLevel = s.AuthenticationLevel;
                if (!string.IsNullOrEmpty(s.AuthenticationServiceClass))
                    adv.AuthenticationServiceClass = s.AuthenticationServiceClass;

                // CredSSP 与 NegotiateSecurityLayer 必须在同一个 NonScriptable3 上、先开 CredSSP 再关协商
                // （官方 VMConnect 示例的顺序；分到不同接口设会让 NegotiateSecurityLayer 报 E_INVALIDARG）。
                var ocx = (IMsRdpClientNonScriptable3)GetOcx();
                TrySet("EnableCredSspSupport", () => ocx.EnableCredSspSupport = s.NetworkLevelAuthentication);
                TrySet("NegotiateSecurityLayer", () => ocx.NegotiateSecurityLayer = s.NegotiateSecurityLayer);

                // DisableCredentialsDelegation 非强类型属性，经 IMsRdpExtendedSettings 字符串属性包设置——
                // 避免 reason=3848（凭据委派被拒）的关键，也是 stock typelib 查不到同名属性的原因。
                if (s.DisableCredentialsDelegation)
                    TrySet("DisableCredentialsDelegation", () =>
                    {
                        var ext = (IMsRdpExtendedSettings)GetOcx();
                        object on = true;
                        ext.set_Property("DisableCredentialsDelegation", ref on);
                    });

                // 初值原生不缩放：连上即清晰；之后由 SetSmartSizing 按"画面是否超出画面区"动态开关
                // （装得下原生清晰、超出才缩放铺满）。控件宽高比始终=画面宽高比，故缩放无 #CBCBCB 信箱、鼠标映射准。
                TrySet("SmartSizing", () => adv.SmartSizing = false);
                _smartSizing = false;
                TrySet("EnableAutoReconnect", () => adv.EnableAutoReconnect = true);
                // VMBus 无真实网络：关掉带宽/网络探测，避免连接栏"网络信息"弹窗取退化数据而原生崩溃。
                TrySet("BandwidthDetection", () => adv.BandwidthDetection = false);
                // 连接超时调短：localhost VMBus 正常连接 <1s，调短让连不上的会话(如不支持增强)快速放弃 → 快速回退。
                if (s.ConnectionTimeoutSeconds > 0)
                {
                    TrySet("singleConnectionTimeout", () => adv.singleConnectionTimeout = s.ConnectionTimeoutSeconds);
                    TrySet("overallConnectionTimeout", () => adv.overallConnectionTimeout = s.ConnectionTimeoutSeconds);
                }
                // 全屏与键鼠捕获（mstscax 原生）：容器处理全屏 → 热键时 fire OnRequestGo/LeaveFullScreen，由窗口全屏；
                // HotKeyFullScreen=可配置 vkey → Ctrl+Alt+<key>；KeyboardHookMode=1 → Win/Alt+Tab 等组合键只要画面有焦点就送 VM（窗口化也送，不止全屏；要切回宿主先点一下别处）。
                TrySet("ContainerHandledFullScreen", () => adv.ContainerHandledFullScreen = 1);   // 容器(WPF 窗口)处理全屏；mstscax 自己全屏会开独立窗口、关掉后残留主窗口
                TrySet("HotKeyFullScreen", () => adv.HotKeyFullScreen = s.FullScreenHotKeyVirtualKey);
                TrySet("KeyboardHookMode", () => rdp.SecuredSettings.KeyboardHookMode = 1);

                adv.PCB = s.PreConnectionBlob ?? string.Empty;

                if (s.DesktopWidth > 0 && s.DesktopHeight > 0)
                    TrySet("Desktop", () => { rdp.DesktopWidth = s.DesktopWidth; rdp.DesktopHeight = s.DesktopHeight; });

                rdp.Connect();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Rdp] ApplyAndConnect 异常: " + ex);
            }
        }

        public void DisconnectSafe()
        {
            try
            {
                dynamic rdp = GetOcx();
                if ((int)rdp.Connected != 0) rdp.Disconnect();
            }
            catch { /* 未连接 / OCX 未就绪 */ }
        }

        /// <summary>0=未连接 1=已连接 2=连接中（mstscax 的 Connected 取值）。</summary>
        public int ConnectionState
        {
            get { try { dynamic rdp = GetOcx(); return (int)rdp.Connected; } catch { return 0; } }
        }

        /// <summary>同步控件全屏状态（容器处理全屏下，按钮发起的全屏需回灌，使 mstscax 内部状态/键盘捕获与窗口一致）。</summary>
        public void SetFullScreen(bool fullScreen)
        {
            try { dynamic rdp = GetOcx(); rdp.FullScreen = fullScreen; }
            catch (Exception ex) { Debug.WriteLine("[Rdp] SetFullScreen 失败: " + ex.Message); }
        }

        /// <summary>动态开关 SmartSizing（基本会话用：VM 分辨率超出画面区时开=缩放铺满，否则关=原生 1:1 清晰）。带缓存避免重复设值闪烁。</summary>
        public void SetSmartSizing(bool on)
        {
            if (_smartSizing == on) return;
            _smartSizing = on;
            try { dynamic rdp = GetOcx(); rdp.AdvancedSettings9.SmartSizing = on; }
            catch (Exception ex) { Debug.WriteLine("[Rdp] SetSmartSizing 失败: " + ex.Message); }
        }

        /// <summary>增强会话改分辨率（不重连）。命名避开 Control.Resize 事件（否则 CS0108 隐藏告警）。</summary>
        public void SetResolution(int width, int height)
        {
            if (width <= 0 || height <= 0) return;
            try
            {
                dynamic rdp = GetOcx();
                // 参数复刻 VMConnect 的 RdpViewerControl：物理尺寸用毫米(非像素)、desktopScaleFactor=显示器 DPI%、
                // deviceScaleFactor=100。★旧代码末位传 1 是非法值(合法仅 100/140/180)，会让分辨率协商被拒 → 画面不随分辨率刷新+灰信箱。
                uint dpi = (uint)Math.Max(96, DeviceDpi);
                uint desktopScaleFactor = (uint)Math.Round(dpi / 96.0 * 100.0);
                uint physW = (uint)Math.Round(width * 25.4 / dpi);
                uint physH = (uint)Math.Round(height * 25.4 / dpi);
                rdp.UpdateSessionDisplaySettings((uint)width, (uint)height, physW, physH, 0u, desktopScaleFactor, 100u);
            }
            catch (Exception ex) { Debug.WriteLine("[Rdp] SetResolution 失败: " + ex.Message); }
        }

        /// <summary>基本会话缩放：设 mstscax 原生 ZoomLevel(百分比，如 100/150/200)。经 IMsRdpExtendedSettings 字符串属性包热设，
        /// 由控件内部缩放——这正是微软 VMConnect "查看→缩放" 的真正机制（SmartSizing 只能缩不能放，放大必须走这里）。
        /// 仅基本会话生效、全屏无效（调用方在全屏时传 100）。</summary>
        public void SetZoomLevel(uint percent)
        {
            try
            {
                var ext = (IMsRdpExtendedSettings)GetOcx();
                object v = percent;
                ext.set_Property("ZoomLevel", ref v);
            }
            catch (Exception ex) { Debug.WriteLine("[Rdp] SetZoomLevel 失败: " + ex.Message); }
        }

        private void TrySet(string what, Action set)
        {
            try { set(); }
            catch (Exception ex) { Debug.WriteLine($"[Rdp] 设 {what} 失败: {ex.GetType().Name} — {ex.Message}"); }
        }

        // COM 事件处理的护栏：异常绝不能逃回 native 回调方（否则 0xC000041D 致命回调异常、进程秒退）。
        private void Safe(Action handler)
        {
            try { handler(); }
            catch (Exception ex) { Debug.WriteLine("[Rdp] 事件处理异常(已拦截): " + ex); }
        }

        private const uint GA_ROOT = 2;
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    }
}
