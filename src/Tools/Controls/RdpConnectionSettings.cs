namespace ExHyperV.Tools
{
    /// <summary>
    /// 通用 RDP 连接参数。不含任何 Hyper-V 专有逻辑——
    /// Hyper-V 控制台的配方（2179 端口 / PCB 塞 VM GUID / Virtual Console Service 认证）
    /// 由调用方拼装后塞进来，保持本类型与 <see cref="RdpClientHost"/> 的通用性。
    /// </summary>
    public sealed class RdpConnectionSettings
    {
        public string Server { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 3389;

        /// <summary>预连接 blob（PCB）。Hyper-V 用它承载 VM GUID（增强会话追加 ";EnhancedMode=1"）。</summary>
        public string? PreConnectionBlob { get; set; }

        /// <summary>0 = 不验证服务器（Hyper-V 控制台用 0）。</summary>
        public uint AuthenticationLevel { get; set; }
        public string? AuthenticationServiceClass { get; set; }
        public bool NetworkLevelAuthentication { get; set; }
        public bool NegotiateSecurityLayer { get; set; } = true;
        public bool DisableCredentialsDelegation { get; set; }

        /// <summary>增强会话可指定初始分辨率；&lt;=0 表示不设置。</summary>
        public int DesktopWidth { get; set; }
        public int DesktopHeight { get; set; }

        /// <summary>增强会话的初始桌面缩放百分比（100-500）；必须在 Connect 前设置。</summary>
        public uint DesktopScaleFactor { get; set; } = 100;

        /// <summary>增强会话的初始设备缩放（RDP 仅允许 100/140/180）；通常保持 100。</summary>
        public uint DeviceScaleFactor { get; set; } = 100;

        /// <summary>连接超时（秒）；&lt;=0 表示用 mstscax 默认（较长）。调短可让连不上的会话快速放弃。</summary>
        public int ConnectionTimeoutSeconds { get; set; }

        /// <summary>
        /// 全屏切换热键的虚拟键码（与 Ctrl+Alt 组合——mstscax 的 HotKeyFullScreen 固定带 Ctrl+Alt，只能配最后这个键）。
        /// 常用值：Enter=0x0D（默认）、Space=0x20、Break=0x03、Pause=0x13。
        /// </summary>
        public int FullScreenHotKeyVirtualKey { get; set; } = 0x0D;   // VK_RETURN → Ctrl+Alt+Enter
    }
}
