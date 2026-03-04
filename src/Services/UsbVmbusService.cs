using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using ExHyperV.Tools;
using ExHyperV.Models;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace ExHyperV.Services
{
    public class UsbVmbusService
    {
        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern unsafe int recv(IntPtr s, void* buf, int len, int flags);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern unsafe int send(IntPtr s, void* buf, int len, int flags);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int closesocket(IntPtr s);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int setsockopt(IntPtr s, int level, int optname, ref int optval, int optlen);

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern int WSAIoctl(IntPtr s, uint dwIoControlCode, ref int lpvInBuffer, uint cbInBuffer, IntPtr lpvOutBuffer, uint cbOutBuffer, out uint lpcbBytesReturned, IntPtr lpOverlapped, IntPtr lpCompletionRoutine);

        private const uint SIO_TCP_SET_ACK_FREQUENCY = 0x98000017;


        private const int IPPROTO_TCP = 6;
        private const int TCP_NODELAY = 0x0001;
        private const int SOL_SOCKET = 0xFFFF;
        private const int SO_SNDBUF = 0x1001;
        private const int SO_RCVBUF = 0x1002;

        // 全局活动连接记录表 (BusId -> VMName)
        public static ConcurrentDictionary<string, string> ActiveTunnels { get; } = new ConcurrentDictionary<string, string>();

        // 运行中的任务控制
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCts = new();

        [DllImport("ws2_32.dll", SetLastError = true)]
        private static extern IntPtr socket(int af, int type, int protocol);

        private const int AF_HYPERV = 34;
        private const int SOCK_STREAM = 1;
        private const int HV_PROTOCOL_RAW = 1;
        private static readonly Guid ServiceId = Guid.Parse("45784879-7065-7256-5553-4250726F7879");

        private const int ProxyBufSize = 512 * 1024;

        public UsbVmbusService()
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.RealTime; } catch { }
        }

        private void Log(string msg) => Debug.WriteLine($"[ExHyperV-USB] [{DateTime.Now:HH:mm:ss.fff}] {msg}");

        // 停止隧道任务
        public async Task StopTunnelAsync(string busId)
        {
            // 1. 取消信号并移除记录
            if (_activeCts.TryRemove(busId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                Log($"[StopTunnel] 正在停止旧隧道: {busId}");
            }

            // 2. 显式等待 unbind 完成，确保宿主机重新获得控制权
            Log($"[StopTunnel] 强制执行 unbind: {busId}");
            await RunUsbIpCommand($"unbind --busid {busId}");

            // 给系统一点反应时间（usbipd 驱动释放需要时间）
            await Task.Delay(500);
        }

        // 自动恢复隧道 (由 Watchdog 调用)
        public async Task AutoRecoverTunnel(string busId, string vmName)
        {
            // 如果已经有任务在跑，需要判断目标是否一致（或者为了保险，切换时直接重建）
            if (_activeCts.ContainsKey(busId))
            {
                // 如果你的逻辑允许直接切换，这里需要先调用 Stop
                await StopTunnelAsync(busId);
            }

            var cts = new CancellationTokenSource();
            if (!_activeCts.TryAdd(busId, cts)) return;

            try
            {
                // 关键：在 bind 之前确保环境干净
                await RunUsbIpCommand($"unbind --busid {busId}");

                bool bound = await RunUsbIpCommand($"bind --busid {busId}");
                if (!bound) bound = await RunUsbIpCommand($"bind --busid {busId} --force");

                if (!bound) throw new Exception("usbipd bind failed");

                var vms = await GetRunningVMsAsync();
                var targetVm = vms.FirstOrDefault(v => v.Name == vmName);
                if (targetVm == null) throw new Exception($"VM {vmName} not found or not running");

                await StartTunnelAsync(targetVm.Id, busId, cts.Token);
            }
            catch (Exception ex)
            {
                Log($"[AutoRecover] 隧道建立失败: {ex.Message}");
                _activeCts.TryRemove(busId, out _);
            }
        }

        public async Task StartTunnelAsync(Guid vmId, string busId, CancellationToken ct)
        {
            // 1. 创建 VMBus 原生句柄
            IntPtr hvHandle = socket(AF_HYPERV, SOCK_STREAM, HV_PROTOCOL_RAW);
            if (hvHandle == (IntPtr)(-1)) throw new SocketException(Marshal.GetLastWin32Error());

            // 仅为了方便调用 Connect()，套一层外壳，但核心收发绝不使用它
            var hv = new Socket(new System.Net.Sockets.SafeSocketHandle(hvHandle, true));
            Socket tcp = null;

            var completion = new TaskCompletionSource<bool>();
            ct.Register(() => {
                closesocket(hvHandle);
                if (tcp != null) closesocket(tcp.SafeHandle.DangerousGetHandle());
                completion.TrySetResult(true);
            });

            try
            {
                Log($"Tunnel: Connecting VMBus {vmId}...");
                await Task.Run(() => hv.Connect(new HyperVEndPoint(vmId, ServiceId)), ct);

                // 阻塞模式
                hv.Blocking = true;
                hv.Send(Encoding.ASCII.GetBytes(busId));

                tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                Log("Tunnel: Connecting Local TCP 3240...");
                bool tcpOk = false;
                for (int i = 0; i < 5; i++)
                {
                    try { await tcp.ConnectAsync("127.0.0.1", 3240, ct); tcpOk = true; break; }
                    catch { await Task.Delay(500, ct); }
                }
                if (!tcpOk) throw new Exception("usbipd service unreachable");

                // 核心差异：提取 TCP 的原生句柄
                IntPtr tcpHandle = tcp.SafeHandle.DangerousGetHandle();

                // 核心差异：抛弃 C# 的 tcp.NoDelay，使用原汁原味的 C++ setsockopt 下发内核！
                int opt1 = 1;
                int optSmall = 8192; 
                int opt0 = 0;
                int ackFreq = 1;
                uint bytesReturned;
                WSAIoctl(tcpHandle, SIO_TCP_SET_ACK_FREQUENCY, ref ackFreq, 4, IntPtr.Zero, 0, out bytesReturned, IntPtr.Zero, IntPtr.Zero);

                setsockopt(tcpHandle, IPPROTO_TCP, TCP_NODELAY, ref opt1, 4);
                setsockopt(tcpHandle, SOL_SOCKET, SO_SNDBUF, ref optSmall, 4);
                setsockopt(tcpHandle, SOL_SOCKET, SO_RCVBUF, ref optSmall, 4);

                // 启动纯 Native 泵 (传入 IntPtr 而不是 Socket)
                StartNativePump(hvHandle, tcpHandle, "VMBUS_TO_TCP", () => completion.TrySetResult(false), ct);
                StartNativePump(tcpHandle, hvHandle, "TCP_TO_VMBUS", () => completion.TrySetResult(false), ct);

                Log("Tunnel: Established (Native Mode).");
                await completion.Task;
            }
            finally
            {
                // 交给 closesocket 处理，抑制 C# 的 Dispose 异常
                try { hv?.Dispose(); } catch { }
                try { tcp?.Dispose(); } catch { }
            }
        }

        // 核心差异：直接使用 IntPtr 和 unsafe void* 绕开 C# CLR 托管层！
        private unsafe void StartNativePump(IntPtr sIn, IntPtr sOut, string label, Action onFault, CancellationToken ct)
        {
            new Thread(() =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                void* bufferPtr = NativeMemory.AlignedAlloc(ProxyBufSize, 4096);

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        // 完美复刻 C++: int b = recv(sIn, buffer, PROXY_BUF_SIZE, 0);
                        int b = recv(sIn, bufferPtr, ProxyBufSize, 0);
                        if (b <= 0) break; // <--- C++ 怎么写，这里就怎么写！

                        // 完美复刻 C++: if (send(sOut, buffer, b, 0) <= 0) break;
                        if (send(sOut, bufferPtr, b, 0) <= 0) break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[{label}] Native Pump 断开: {ex.Message}");
                }
                finally
                {
                    NativeMemory.AlignedFree(bufferPtr);

                    // 完美复刻 C++: closesocket(sIn); closesocket(sOut);
                    // 这里暴力切断原生句柄，直接物理级解决死锁
                    closesocket(sIn);
                    closesocket(sOut);

                    onFault?.Invoke();
                }
            })
            { IsBackground = true, Name = $"NativePump_{label}" }.Start();
        }

        public async Task WatchdogLoopAsync(CancellationToken globalCt)
        {
            while (!globalCt.IsCancellationRequested)
            {
                foreach (var entry in ActiveTunnels)
                {
                    if (!_activeCts.ContainsKey(entry.Key))
                    {
                        _ = Task.Run(() => AutoRecoverTunnel(entry.Key, entry.Value));
                    }
                }
                await Task.Delay(2000, globalCt);
            }
        }

        public async Task<bool> EnsureDeviceSharedAsync(string busId)
        {
            return await RunUsbIpCommand($"bind --busid {busId}");
        }

        private async Task<bool> RunUsbIpCommand(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("usbipd", args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                var proc = Process.Start(psi);
                if (proc != null) { await proc.WaitForExitAsync(); return proc.ExitCode == 0; }
                return false;
            }
            catch { return false; }
        }

        public async Task<List<VmInfo>> GetRunningVMsAsync()
        {
            var list = new List<VmInfo>();
            try
            {
                // 直接运行原生命令，不需要拼字符串，直接返回对象
                string script = "Get-VM | Where-Object {$_.State -eq 'Running'} | Select-Object Name, Id";

                // 使用你的 Utils.Run2
                var results = await Utils.Run2(script);

                foreach (var psObj in results)
                {
                    // 直接从属性中提取值，SDK 会自动处理类型转换
                    // Name 是 String，Id 是 Guid (在 PowerShell 中实际上是个对象)
                    var name = psObj.Properties["Name"]?.Value?.ToString();
                    var idValue = psObj.Properties["Id"]?.Value;

                    if (!string.IsNullOrEmpty(name) && idValue != null)
                    {
                        list.Add(new VmInfo
                        {
                            Name = name,
                            // 处理可能出现的不同 ID 类型包装
                            Id = idValue is Guid guid ? guid : Guid.Parse(idValue.ToString())
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ExHyperV-USB] 获取虚拟机列表失败: {ex.Message}");
            }
            return list;
        }

        public async Task<List<UsbDeviceModel>> GetUsbIpDevicesAsync()
        {
            var list = new List<UsbDeviceModel>();
            try
            {
                var psi = new ProcessStartInfo("usbipd", "list") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.Default };
                using var proc = Process.Start(psi);
                string outStr = await proc.StandardOutput.ReadToEndAsync();
                var lines = outStr.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                bool foundSection = false;
                foreach (var line in lines)
                {
                    if (line.Contains("BUSID")) { foundSection = true; continue; }
                    if (foundSection && line.Trim().Length > 0 && char.IsDigit(line.Trim()[0]))
                    {
                        var m = Regex.Match(line.Trim(), @"^([0-9\-.]+)\s+([0-9a-fA-F:]+)\s+(.*?)\s{2,}");
                        if (m.Success) list.Add(new UsbDeviceModel { BusId = m.Groups[1].Value.Trim(), VidPid = m.Groups[2].Value.Trim(), Description = m.Groups[3].Value.Trim(), Status = "Ready" });
                    }
                }
            }
            catch { }
            return list;
        }

        public void EnsureServiceRegistered()
        {
            try
            {
                string regPath = $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\{ServiceId:B}";
                using var key = Registry.LocalMachine.CreateSubKey(regPath);
                key.SetValue("ElementName", "ExHyperV USB Proxy Infrastructure");
            }
            catch { }
        }
    }
}