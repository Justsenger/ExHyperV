using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using System.Windows.Threading;
using RoyalApps.Community.Rdp.WinForms.Controls;
using RoyalApps.Community.Rdp.WinForms.Configuration;

namespace ExHyperV.Tools
{
    public class MsRdpExHost : WindowsFormsHost
    {
        private readonly RdpControl _rdpControl;
        private string? _lastConnectedId; // 用于防抖，防止重复连接

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

            _rdpControl.OnConnected += (s, e) => {
                Debug.WriteLine("[RDP-OK] 连接成功，画面应显示");
                Dispatcher.Invoke(() => OnRdpConnected?.Invoke());
            };

            _rdpControl.OnDisconnected += (s, e) => {
                Debug.WriteLine($"[RDP-ERR] 连接断开: {e.Description} (Code: {e.DisconnectCode})");
                // 如果断开了，清空缓存以便下次重连
                _lastConnectedId = null;
                Dispatcher.Invoke(() => OnRdpDisconnected?.Invoke(e.Description));
            };

            // 核心接管：在库的所有自动化逻辑执行完后，我们做最后一次暴力覆盖
            _rdpControl.RdpClientConfigured += (s, e) =>
            {
                var client = _rdpControl.RdpClient;
                if (client != null)
                {
                    // 只有当你确定库默认设置有问题时才改这里
                    // 库默认会为 HyperV 设置 NLA = true，这通常是正确的
                    client.RedirectClipboard = false;
                }
            };
            this.Child = _rdpControl;
        }

        private static void OnVmIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MsRdpExHost host && e.NewValue is string vmid && !string.IsNullOrEmpty(vmid))
            {
                // 防抖：如果已经是这个 ID 在连接了，不要动
                if (host._lastConnectedId == vmid) return;
                host._lastConnectedId = vmid;

                host.TriggerConnect(vmid);
            }
        }

        private void TriggerConnect(string vmid)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // 1. 这里的 cleanGuid 保持原样
                    string cleanGuid = vmid.Trim().Replace("{", "").Replace("}", "").ToUpper();

                    var config = _rdpControl.RdpConfiguration;

                    // 2. 使用库内置的 HyperV 配置块（最重要！）
                    // 设置了 Instance 后，库会自动帮你处理：
                    // - Port (默认 2179)
                    // - PCB (设置到正确的控件属性)
                    // - AuthenticationServiceClass ("Microsoft Virtual Console Service")
                    // - NLA = true, Negotiate = false
                    config.HyperV.Instance = cleanGuid;
                    config.HyperV.EnhancedSessionMode = false; // 如果连不上，试着改 true/false 切换

                    // 3. 基础网络信息
                    config.Server = "127.0.0.1";

                    // 4. 身份信息：连接 Hyper-V 宿主机通常需要当前 Windows 令牌
                    // 注意：某些环境下 Username 需要留空或设为当前用户
                    config.Credentials.Username = "";

                    // 5. 性能优化
                    config.Display.ResizeBehavior = ResizeBehavior.SmartSizing;
                    config.UseMsRdc = false; // 保持使用 mstscax

                    Debug.WriteLine($"[RDP-准备] 发起 Hyper-V 连接: {cleanGuid}");
                    _rdpControl.Connect();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RDP-异常] {ex.Message}");
                }
            }), DispatcherPriority.Background);
        }
        public void Disconnect()
        {
            _lastConnectedId = null;
            try { _rdpControl?.Disconnect(); } catch { }
        }
    }
}