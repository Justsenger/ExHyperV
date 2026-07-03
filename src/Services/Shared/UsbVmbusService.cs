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
    public static class UsbVmbusService
    {
        public static ConcurrentDictionary<string, string> ActiveTunnels { get; } = new();
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCts = new();

        private static readonly Guid ServiceId = Guid.Parse("45784879-7065-7256-5553-4250726F7879");
        private const int ProxyBufSize = 512 * 1024;

        static UsbVmbusService()
        {
            // High 足以保证 USB 转发低延迟；RealTime 会饿死系统输入/磁盘线程，有整机卡死风险，绝不用于用户态进程
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.High; } catch { }
        }

        private static void Log(string msg) => Debug.WriteLine($"[ExHyperV-USB] [{DateTime.Now:HH:mm:ss.fff}] {msg}");

        public static async Task StopTunnelAsync(string busId)
        {
            if (_activeCts.TryRemove(busId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                Log(string.Format(Properties.Resources.UsbVmbus_LogStoppingTunnel, busId));
            }
            Log(string.Format(Properties.Resources.UsbVmbus_LogEnforceUnbind, busId));
            await RunUsbIpCommand($"unbind --busid {busId}");
            await Task.Delay(500);
        }

        public static async Task AutoRecoverTunnel(string busId, string vmName)
        {
            if (_activeCts.ContainsKey(busId)) await StopTunnelAsync(busId);

            var cts = new CancellationTokenSource();
            if (!_activeCts.TryAdd(busId, cts)) return;

            try
            {
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
                Log(string.Format(Properties.Resources.UsbVmbus_LogTunnelFailed, ex.Message));
                _activeCts.TryRemove(busId, out _);
            }
        }

        public static async Task StartTunnelAsync(Guid vmId, string busId, CancellationToken ct)
        {
            // 创建 VMBus socket
            var hvResp = VmbusApi.CreateVmbusSocket();
            if (!hvResp.Success)
                throw new SocketException(hvResp.Code);

            nint hvHandle = hvResp.Data;
            var hv = VmbusApi.WrapHandle(hvHandle);
            Socket? tcp = null;

            // 句柄唯一所有者是托管 Socket(hv/tcp)：取消与收尾统一走 Dispose(幂等)，泵线程只借用
            // 裸句柄、断线只报告不关闭——句柄号会被系统复用，多方 closesocket 会误关进程内无关连接。
            var completion = new TaskCompletionSource<bool>();
            ct.Register(() =>
            {
                try { hv.Dispose(); } catch { }
                try { tcp?.Dispose(); } catch { }
                completion.TrySetResult(true);
            });

            try
            {
                Log($"Tunnel: Connecting VMBus {vmId}...");
                await Task.Run(() => hv.Connect(new HyperVEndPoint(vmId, ServiceId)), ct);

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

                tcp.Blocking = true;

                nint tcpHandle = tcp.SafeHandle.DangerousGetHandle();

                // 配置同步缓冲固定 8KB，勿改
                int optSmall = 8192;
                VmbusApi.SetAckFrequency(tcpHandle, 1);
                VmbusApi.SetNoDelay(tcpHandle);
                VmbusApi.SetSendBuffer(tcpHandle, optSmall);
                VmbusApi.SetReceiveBuffer(tcpHandle, optSmall);

                StartNativePump(hvHandle, tcpHandle, "VMBUS_TO_TCP", () => completion.TrySetResult(false), ct);
                StartNativePump(tcpHandle, hvHandle, "TCP_TO_VMBUS", () => completion.TrySetResult(false), ct);

                Log("Tunnel: Established (Native Mode).");
                await completion.Task;
            }
            finally
            {
                try { hv?.Dispose(); } catch { }
                try { tcp?.Dispose(); } catch { }
            }
        }

        private static unsafe void StartNativePump(nint sIn, nint sOut, string label, Action onFault, CancellationToken ct)
        {
            new Thread(() =>
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
                void* bufferPtr = NativeMemory.AlignedAlloc(ProxyBufSize, 4096);

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        int b = VmbusApi.Recv(sIn, bufferPtr, ProxyBufSize);
                        if (b <= 0) break;
                        if (VmbusApi.Send(sOut, bufferPtr, b) <= 0) break;
                    }
                }
                catch (Exception ex) { Debug.WriteLine($"[{label}] Error: {ex.Message}"); }
                finally
                {
                    NativeMemory.AlignedFree(bufferPtr);
                    onFault?.Invoke();   // 只报告断线；句柄由 StartTunnelAsync 收尾统一 Dispose
                }
            })
            { IsBackground = true, Name = $"NativePump_{label}" }.Start();
        }

        public static async Task WatchdogLoopAsync(CancellationToken globalCt)
        {
            while (!globalCt.IsCancellationRequested)
            {
                foreach (var entry in ActiveTunnels)
                {
                    if (!_activeCts.ContainsKey(entry.Key))
                        _ = Task.Run(() => AutoRecoverTunnel(entry.Key, entry.Value));
                }
                await Task.Delay(2000, globalCt);
            }
        }

        public static async Task<bool> EnsureDeviceSharedAsync(string busId)
            => await RunUsbIpCommand($"bind --busid {busId}");

        private static async Task<bool> RunUsbIpCommand(string args)
        {
            try
            {
                var psi = new ProcessStartInfo("usbipd", args)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                var proc = Process.Start(psi);
                if (proc != null) { await proc.WaitForExitAsync(); return proc.ExitCode == 0; }
                return false;
            }
            catch { return false; }
        }

        public static async Task<List<UsbTargetVm>> GetRunningVMsAsync()
        {
            var resp = await WmiApi.QueryAsync(
                "SELECT Name, ElementName FROM Msvm_ComputerSystem WHERE EnabledState = 2 AND Name <> ElementName",
                obj => new UsbTargetVm
                {
                    Name = obj["ElementName"]?.ToString() ?? "",
                    Id = Guid.TryParse(obj["Name"]?.ToString(), out var g) ? g : Guid.Empty
                },
                WmiScope.HyperV);

            return (resp.Data ?? new List<UsbTargetVm>())
                .Where(v => !string.IsNullOrEmpty(v.Name) && v.Id != Guid.Empty)
                .ToList();
        }

        // 整个方法体放进 Task.Run：Process.Start("usbipd") 是同步调用，在 async 方法首个 await 之前会跑在
        // 调用线程上。本方法常被 UI 线程经 SyncDevicesLoop 的 Dispatcher.InvokeAsync(RefreshListInternal) 调到，
        // usbipd 进程创建一卡（驱动/服务状态、Defender 扫新进程）就冻住界面(未响应)。移到线程池后 UI 线程只 await、
        // 消息泵照转。与 WmiApi.QueryAsync 的 Task.Run 同一套路。
        public static Task<List<UsbDevice>> GetUsbIpDevicesAsync() => Task.Run(async () =>
        {
            var list = new List<UsbDevice>();
            try
            {
                var psi = new ProcessStartInfo("usbipd", "list")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.Default
                };
                using var proc = Process.Start(psi);
                string outStr = await proc!.StandardOutput.ReadToEndAsync();
                var lines = outStr.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                bool foundSection = false;
                foreach (var line in lines)
                {
                    if (line.Contains("BUSID")) { foundSection = true; continue; }
                    if (foundSection && line.Trim().Length > 0 && char.IsDigit(line.Trim()[0]))
                    {
                        var m = Regex.Match(line.Trim(), @"^([0-9\-.]+)\s+([0-9a-fA-F:]+)\s+(.*?)\s{2,}");
                        if (m.Success)
                            list.Add(new UsbDevice
                            {
                                BusId = m.Groups[1].Value.Trim(),
                                VidPid = m.Groups[2].Value.Trim(),
                                Description = m.Groups[3].Value.Trim()
                            });
                    }
                }
            }
            catch { }
            return list;
        });

        public static void EnsureServiceRegistered()
        {
            try
            {
                string regPath = $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization\GuestCommunicationServices\{ServiceId:B}";
                using var key = Registry.LocalMachine.CreateSubKey(regPath);
                key.SetValue("ElementName", "ExHyperV USB Proxy Infrastructure");
            }
            catch { }
        }

        // 宿主侧 USB 直通前置：usbipd-win 是否已安装。查服务注册表项（每个 Windows 服务在此都有键）+ 默认安装路径兜底；
        // 不起进程、不依赖 PATH。仅检测宿主——虚拟机侧（usbip-win2 / USBProxy）由用户自理，不在此列。
        public static bool IsUsbipdInstalled()
        {
            try
            {
                using var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\usbipd");
                if (svc != null) return true;
            }
            catch { }
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrEmpty(pf) && System.IO.File.Exists(System.IO.Path.Combine(pf, "usbipd-win", "usbipd.exe")))
                    return true;
            }
            catch { }
            return false;
        }
    }
}