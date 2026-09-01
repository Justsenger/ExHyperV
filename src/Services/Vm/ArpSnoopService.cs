using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Services
{
    /// <summary>
    /// 被动嗅探 Hyper-V 虚拟交换机上的 ARP，学习无集成服务 VM（如国产 Linux）的 IPv4。
    ///
    /// 使用系统 PktMon 驱动和 ETW：
    ///   CreateFile(\\.\PktMonDev) → PktmonAddFilter(ARP) → DeviceIoControl(0x220404, capture_type=1) 抓全包
    ///   → 驱动把帧打到 ETW provider {4D4F80D9-…} → 自建原生 ETW 消费者(advapi32 StartTrace/OpenTrace/ProcessTrace)
    ///   → 解析 ARP → MAC→IP（带 TTL）。
    /// start buffer 的 capture_type 必须 =1(=0 只计数无帧)。
    ///
    /// 缺少 PktMon 驱动或 DLL 时 <see cref="IsAvailable"/> 为 false，调用方回退到集成服务或邻居缓存。
    /// </summary>
    public sealed unsafe class ArpSnoopService : IDisposable
    {
        // ── PktMon 驱动 ──
        [DllImport("PktMonApi.dll")] private static extern int PktmonAddFilter(byte[] f);
        [DllImport("PktMonApi.dll")] private static extern int PktmonRemoveAllFilters();
        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sec, uint d, uint fl, IntPtr t);
        [DllImport("kernel32", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[]? inb, uint ins, byte[]? outb, uint outs, out uint ret, IntPtr ov);

        // ── 原生 ETW ──
        [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint StartTraceW(out long handle, string name, byte[] props);
        [DllImport("advapi32", SetLastError = true)]
        private static extern uint EnableTraceEx2(long handle, in Guid provider, uint ctrl, byte level, ulong any, ulong all, uint timeout, IntPtr p);
        [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern long OpenTraceW(ref EVENT_TRACE_LOGFILEW lf);
        [DllImport("advapi32", SetLastError = true)]
        private static extern uint ProcessTrace(long[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32", SetLastError = true)]
        private static extern uint CloseTrace(long handle);
        [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint ControlTraceW(long handle, string name, byte[] props, uint code);

        [StructLayout(LayoutKind.Sequential)]
        private struct EVENT_TRACE_LOGFILEW
        {
            public IntPtr LogFileName, LoggerName;
            public long CurrentTime;
            public uint BuffersRead, ProcessTraceMode;
            public fixed byte CurrentEvent[88];     // EVENT_TRACE
            public fixed byte LogfileHeader[280];   // TRACE_LOGFILE_HEADER (x64)
            public IntPtr BufferCallback;
            public uint BufferSize, Filled, EventsLost;
            public IntPtr EventRecordCallback;
            public uint IsKernelTrace;
            public IntPtr Context;
        }

        private delegate void EventRecordCallback(IntPtr rec);

        private static readonly Guid PktMonProvider = new("4D4F80D9-C8BD-4D73-BB5B-19C90402C5AC");
        private const string SessionName = "ExHyperV_ArpSnoop";
        private static readonly long TtlMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;

        // 归一化 MAC（去分隔符、大写）→ (IPv4, 收到时刻 TickCount64)
        private readonly ConcurrentDictionary<string, (string Ip, long Ticks)> _map = new();

        private long _ctrlHandle, _consumerHandle;
        private SafeFileHandle? _dev;
        private Thread? _pump;
        private EventRecordCallback? _cb; // 保活，防 GC
        private int _started;

        /// <summary>驱动+ETW 是否就绪。false 时调用方应回退其它 IP 路径。</summary>
        public bool IsAvailable { get; private set; }

        /// <summary>进程级单例——所有取 IP 的路径(仪表盘 / 网络页 / VmIpService)共用同一份嗅探表。</summary>
        public static ArpSnoopService Instance { get; } = new();

        /// <summary>启动后台嗅探(幂等、非阻塞)。任何失败都吞掉并保持 IsAvailable=false。</summary>
        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            try
            {
                // 先启用 ETW 会话，避免漏掉驱动启动后的早期事件。
                ControlTraceW(0, SessionName, BuildProps(SessionName), 1); // 清残留(STOP)
                if (StartTraceW(out _ctrlHandle, SessionName, BuildProps(SessionName)) != 0) { Cleanup(); return; }
                EnableTraceEx2(_ctrlHandle, in PktMonProvider, 1 /*ENABLE*/, 0xFF, ulong.MaxValue, 0, 0, IntPtr.Zero);

                // ETW 消费者必须先于驱动启动，否则会漏掉早期事件。
                _cb = OnRecord;
                var lf = new EVENT_TRACE_LOGFILEW
                {
                    LoggerName = Marshal.StringToHGlobalUni(SessionName),
                    ProcessTraceMode = 0x10000000 | 0x100, // EVENT_RECORD | REAL_TIME
                    EventRecordCallback = Marshal.GetFunctionPointerForDelegate(_cb)
                };
                _consumerHandle = OpenTraceW(ref lf);
                _pump = new Thread(() => { try { ProcessTrace(new[] { _consumerHandle }, 1, IntPtr.Zero, IntPtr.Zero); } catch { } })
                { IsBackground = true, Name = "ArpSnoop" };
                _pump.Start();
                Thread.Sleep(300);

                PktmonRemoveAllFilters();
                var filter = new byte[200];
                BitConverter.GetBytes((ushort)200).CopyTo(filter, 0);
                BitConverter.GetBytes((ushort)0x0806).CopyTo(filter, 0x90); // EtherType=ARP
                PktmonAddFilter(filter);

                _dev = CreateFileW(@"\\.\PktMonDev", 0xC0000000, 0, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
                if (_dev.IsInvalid) { Cleanup(); return; }

                var start = new byte[20];
                BitConverter.GetBytes((uint)1).CopyTo(start, 4);   // selection=全部
                BitConverter.GetBytes((uint)1).CopyTo(start, 12);  // capture_type=1 抓全包
                start[16] = 1;                                     // 有 TraceOptions(pkt-size=0=全包)
                if (!DeviceIoControl(_dev, 0x220404, start, 20, null, 0, out _, IntPtr.Zero)) { Cleanup(); return; }

                AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
                IsAvailable = true;
            }
            catch { Cleanup(); }
        }

        /// <summary>按 MAC 查嗅探到的 IPv4(带 TTL，过期视为未知)。</summary>
        public bool TryGetIp(string? mac, out string ip)
        {
            ip = string.Empty;
            if (!IsAvailable) return false;
            var key = NormMac(mac);
            if (key.Length == 0) return false;
            if (_map.TryGetValue(key, out var e))
            {
                if (Environment.TickCount64 - e.Ticks <= TtlMs) { ip = e.Ip; return true; }
                _map.TryRemove(key, out _); // VM 下线后不再使用过期地址。
            }
            return false;
        }

        private void OnRecord(IntPtr rec)
        {
            int len = (ushort)Marshal.ReadInt16(rec, 86);  // EVENT_RECORD.UserDataLength
            IntPtr ud = Marshal.ReadIntPtr(rec, 96);        // EVENT_RECORD.UserData
            if (len < 28 || ud == IntPtr.Zero) return;
            var b = new byte[len];
            try { Marshal.Copy(ud, b, 0, len); } catch { return; }
            for (int i = 0; i + 20 < b.Length; i++)
            {
                if (b[i] == 0x08 && b[i + 1] == 0x06 && b[i + 2] == 0x00 && b[i + 3] == 0x01 &&
                    b[i + 4] == 0x08 && b[i + 5] == 0x00 && b[i + 6] == 0x06 && b[i + 7] == 0x04)
                {
                    int sha = i + 10, spa = i + 16;
                    if (b[spa] == 0 && b[spa + 1] == 0 && b[spa + 2] == 0 && b[spa + 3] == 0) return;
                    string mac = $"{b[sha]:X2}{b[sha + 1]:X2}{b[sha + 2]:X2}{b[sha + 3]:X2}{b[sha + 4]:X2}{b[sha + 5]:X2}";
                    string ip = $"{b[spa]}.{b[spa + 1]}.{b[spa + 2]}.{b[spa + 3]}";
                    _map[mac] = (ip, Environment.TickCount64);
                    return;
                }
            }
        }

        private static string NormMac(string? m)
        {
            if (string.IsNullOrEmpty(m)) return string.Empty;
            Span<char> buf = stackalloc char[12];
            int n = 0;
            foreach (var c in m)
                if (Uri.IsHexDigit(c) && n < 12) buf[n++] = char.ToUpperInvariant(c);
            return n == 12 ? new string(buf) : string.Empty;
        }

        private static byte[] BuildProps(string name)
        {
            const int sz = 120; // EVENT_TRACE_PROPERTIES (x64)
            var buf = new byte[sz + (name.Length + 1) * 2];
            BitConverter.GetBytes(buf.Length).CopyTo(buf, 0);   // Wnode.BufferSize
            BitConverter.GetBytes(1u).CopyTo(buf, 40);          // Wnode.ClientContext=1(QPC)
            BitConverter.GetBytes(0x00020000u).CopyTo(buf, 44); // Wnode.Flags=WNODE_FLAG_TRACED_GUID
            BitConverter.GetBytes(0x100u).CopyTo(buf, 64);      // LogFileMode=REAL_TIME
            BitConverter.GetBytes(1u).CopyTo(buf, 68);          // FlushTimer=1s：实时会话每秒 flush，使无流量时 ProcessTrace 也周期醒来、能响应 CloseTrace 退出
            BitConverter.GetBytes(sz).CopyTo(buf, 116);         // LoggerNameOffset=120
            Encoding.Unicode.GetBytes(name).CopyTo(buf, sz);
            return buf;
        }

        private int _cleaned;

        private void Cleanup()
        {
            if (Interlocked.Exchange(ref _cleaned, 1) != 0) return; // 多入口(App.OnExit / ProcessExit / Dispose)防重复清理

            // 先停止驱动捕获，切断事件源。
            try
            {
                if (_dev != null && !_dev.IsInvalid)
                {
                    var so = new byte[20];
                    DeviceIoControl(_dev, 0x220408, null, 0, so, 20, out _, IntPtr.Zero); // STOP capture
                }
            }
            catch { }
            try { PktmonRemoveAllFilters(); } catch { }
            try { _dev?.Dispose(); } catch { }
            _dev = null;

            // 停止 ETW 会话以唤醒 ProcessTrace 线程。
            try { if (_ctrlHandle != 0) ControlTraceW(_ctrlHandle, SessionName, BuildProps(SessionName), 1); } catch { }
            _ctrlHandle = 0;

            // 最后关闭消费者句柄，使 ProcessTrace 返回。
            try { if (_consumerHandle != 0) CloseTrace(_consumerHandle); } catch { }
            _consumerHandle = 0;

            // 限时等待，避免 ETW 异常阻塞进程退出。
            try { _pump?.Join(3000); } catch { }
            _pump = null;

            IsAvailable = false;
        }

        public void Dispose() => Cleanup();
    }
}
