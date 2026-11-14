using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>捕获本机 ARP 流量并维护 MAC 到 IPv4 的近期映射，作为来宾上报地址之外的辅助来源。</summary>
/// <remarks>
/// <para>取得 <see cref="ArpSnoopService.Instance"/> 后调用 <see cref="ArpSnoopService.Start"/> 开始捕获，再通过 <see cref="ArpSnoopService.TryGetIp"/> 按 MAC 读取近期 IPv4 地址。需要持续更新地址的宿主应用应在整个使用期间保持捕获，结束时调用 <see cref="ArpSnoopService.Dispose"/>。</para>
/// <para>Start 可重复调用；启动失败或 Dispose 后，同一实例不能重新启动。状态见 <see cref="ArpSnoopService.IsAvailable"/>，错误见 <see cref="ArpSnoopService.LastFailure"/>。每条缓存记录有效期为 5 分钟。</para>
/// <para>使用微软公开 Packet Monitor API 的独立会话和实时流，需要管理员权限及支持该 API 的 Windows。不同进程可独立捕获，关闭只释放本实例的资源。公开 API 不可用时不回退到私有接口；其他 IP 来源不受影响。</para>
/// <para>缓存按 MAC 保存最近观察到的一个 IPv4 地址，跨交换机或 VLAN 使用相同 MAC 时会相互覆盖。</para>
/// </remarks>
public sealed unsafe class ArpSnoopService : IDisposable
{
    private const int Created = 0, Starting = 1, Running = 2, Faulted = 3, Stopping = 4, Stopped = 5;
    private static readonly long TtlMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
    private readonly ConcurrentDictionary<string, (string Ip, long Ticks, DateTimeOffset ObservedAtUtc)> _map = new();
    private readonly object _lifecycleLock = new();
    private int _state;
    private ApiResponse? _lastFailure;
    private PacketMonitorApi? _api;
    private nint _monitor, _session, _stream;
    private bool _active;
    private PacketMonitorApi.DataCallback? _dataCallback;
    private PacketMonitorApi.EventCallback? _eventCallback;

    /// <summary>本实例的捕获流是否正在运行；初始化失败、流停止或最终关闭后为 false。</summary>
    public bool IsAvailable => Volatile.Read(ref _state) == Running;

    /// <summary>首次捕获或清理失败的诊断；尚未失败时为 null。Code 为 HRESULT，ErrorKind 区分权限不足、不支持和执行失败。</summary>
    /// <remarks>Start 初始化失败通过此属性报告，正常 Dispose 不产生错误。</remarks>
    public ApiResponse? LastFailure => Volatile.Read(ref _lastFailure);

    /// <summary>本进程共享的 ARP 嗅探实例；读取不会启动捕获，任一使用者释放它都会终止该共享实例。</summary>
    public static ArpSnoopService Instance { get; } = new();

    /// <summary>启动本实例的独立 ARP 捕获；重复调用不会创建第二套资源。</summary>
    /// <remarks>同步准备捕获资源，不等待报文到达。初始化失败保留 IsAvailable=false 并设置 LastFailure；初始化失败或 Dispose 后不支持在同一实例重新启动。</remarks>
    public void Start()
    {
        lock (_lifecycleLock)
        {
            if (_state != Created) return;
            Volatile.Write(ref _state, Starting);
            string operation = "加载 Packet Monitor 公开接口";
            try
            {
                _api = new PacketMonitorApi();
                operation = "PacketMonitorInitialize";
                Check(_api.Initialize(PacketMonitorApi.ApiVersion, 0, out nint monitor));
                RequireHandle(monitor);
                _monitor = monitor;
                operation = "PacketMonitorCreateLiveSession";
                Check(_api.CreateSession(_monitor, $"ExHyperV.ARP.{Environment.ProcessId}.{Guid.NewGuid():N}", out nint session));
                RequireHandle(session);
                _session = session;

                // 不添加组件过滤；只限制协议，不冻结启动时的网卡/交换机清单。
                // 只对本会话添加 ARP 过滤，不修改系统全局过滤器。
                ProtocolConstraint constraint = default;
                "ARP".AsSpan().CopyTo(new Span<char>(constraint.Name, ProtocolConstraint.NameCapacity));
                constraint.IsPresent = 1 << 3;
                constraint.EtherType = 0x0806;
                operation = "PacketMonitorAddCaptureConstraint";
                Check(_api.AddConstraint(_session, in constraint));

                _dataCallback = OnPacket;
                _eventCallback = OnStreamEvent;
                StreamConfiguration configuration = new()
                {
                    EventCallback = Marshal.GetFunctionPointerForDelegate(_eventCallback),
                    DataCallback = Marshal.GetFunctionPointerForDelegate(_dataCallback),
                    BufferSizeMultiplier = 4,
                    TruncationSize = 128
                };
                operation = "PacketMonitorCreateRealtimeStream";
                Check(_api.CreateStream(_monitor, in configuration, out nint stream));
                RequireHandle(stream);
                _stream = stream;
                operation = "PacketMonitorAttachOutputToSession";
                Check(_api.Attach(_session, _stream));
                AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
                operation = "PacketMonitorSetSessionActive";
                Check(_api.SetActive(_session, 1));
                _active = true;

                // 激活时回调可能已经报告失败，不能再把该实例标记为可用。
                if (Interlocked.CompareExchange(ref _state, Running, Starting) != Starting)
                    CleanupCore();
            }
            catch (Exception error)
            {
                RememberFailure(operation, error);
                CleanupCore();
            }
        }
    }

    /// <summary>读取指定 MAC 的有效 IPv4 缓存。</summary>
    /// <returns>找到有效缓存时为 true；否则为 false 且 ip 为空。</returns>
    /// <param name="mac">待查询的 MAC；允许空值，格式会归一化。</param>
    /// <param name="ip">成功时接收 IPv4 地址；失败时为空字符串。</param>
    public bool TryGetIp(string? mac, out string ip) => TryGetObservation(mac, out ip, out _);

    internal bool TryGetObservation(string? mac, out string ip, out DateTimeOffset? observedAtUtc)
    {
        observedAtUtc = null;
        ip = string.Empty;
        if (!IsAvailable) return false;
        string key = NormMac(mac);
        if (key.Length == 0) return false;
        if (_map.TryGetValue(key, out var entry))
        {
            long age = Environment.TickCount64 - entry.Ticks;
            if (age >= 0 && age <= TtlMs) { ip = entry.Ip; observedAtUtc = entry.ObservedAtUtc; return true; }
            // 只移除读到的过期项，不能误删另一回调刚刚写入的新地址。
            _map.TryRemove(new KeyValuePair<string, (string Ip, long Ticks, DateTimeOffset ObservedAtUtc)>(key, entry));
        }
        return false;
    }

    private void OnPacket(nint context, nint descriptorPointer)
    {
        // 不获取生命周期锁：CloseRealtimeStream 可能等待此回调结束。
        try
        {
            if (Volatile.Read(ref _state) is not (Starting or Running)) return;
            if (descriptorPointer == 0) throw new InvalidDataException("缺少实时流数据描述符。");
            StreamDescriptor descriptor = Marshal.PtrToStructure<StreamDescriptor>(descriptorPointer);
            if (descriptor.Data == 0 || descriptor.DataSize > 1024 * 1024
                || descriptor.MetadataOffset > descriptor.DataSize || descriptor.DataSize - descriptor.MetadataOffset < sizeof(StreamMetadata)
                || descriptor.PacketOffset > descriptor.DataSize || descriptor.PacketLength > descriptor.DataSize - descriptor.PacketOffset
                || descriptor.PacketLength > 9000)
                throw new InvalidDataException("实时流报文范围无效。");

            StreamMetadata metadata = Marshal.PtrToStructure<StreamMetadata>(descriptor.Data + checked((int)descriptor.MetadataOffset));
            var packet = new ReadOnlySpan<byte>((void*)(descriptor.Data + checked((int)descriptor.PacketOffset)), checked((int)descriptor.PacketLength));
            if (ArpPacketParser.TryParse(packet, metadata.PacketType, out string mac, out string address))
                _map[mac] = (address, Environment.TickCount64, DateTimeOffset.UtcNow);
        }
        catch (Exception error) { FailFromCallback("PacketMonitor 实时报文", error); }
    }

    private void OnStreamEvent(nint context, nint info, uint kind)
    {
        // Started / ProcessInfo 并不代表流失败；正常主动停止也不应记为故障。
        if (kind is not (1 or 2)) return;
        try
        {
            int reason = info == 0 ? 0 : Marshal.ReadInt32(info, 4);
            int hr = reason == 0 ? unchecked((int)0x80004005) : unchecked((int)(0x80070000u | ((uint)reason & 0xffff)));
            FailFromCallback(kind == 2 ? "PacketMonitor 流故障" : "PacketMonitor 流意外停止", new COMException("ARP 捕获流已经停止。", hr));
        }
        catch (Exception error) { FailFromCallback("PacketMonitor 流事件", error); }
    }

    private void FailFromCallback(string operation, Exception error)
    {
        // 不允许托管异常越过原生回调边界，也不能在流回调线程同步关闭自己。
        try
        {
            int state;
            do
            {
                state = Volatile.Read(ref _state);
                if (state is not (Starting or Running)) return;
            } while (Interlocked.CompareExchange(ref _state, Faulted, state) != state);
            RememberFailure(operation, error);
            ThreadPool.QueueUserWorkItem(_ => Dispose());
        }
        catch { }
    }

    private void RememberFailure(string operation, Exception error)
    {
        int code = error.HResult;
        ApiErrorKind kind = error is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or BadImageFormatException
            || code is unchecked((int)0x80070032) or unchecked((int)0x80070519) or unchecked((int)0x8007051A) or unchecked((int)0x80004001)
            ? ApiErrorKind.Unsupported
            : error is UnauthorizedAccessException || code == unchecked((int)0x80070005)
                ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed;
        var failure = ApiResponse.Fail($"{operation}失败：{error.Message}", code, ApiErrorSource.Win32, error, kind);
        Interlocked.CompareExchange(ref _lastFailure, failure, null);
    }

    private static void Check(int result) => Marshal.ThrowExceptionForHR(result);
    private static void RequireHandle(nint handle)
    {
        if (handle == 0) throw new InvalidOperationException("接口成功但未返回捕获句柄。");
    }

    private static string NormMac(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        Span<char> buffer = stackalloc char[12];
        int length = 0;
        foreach (char c in value)
            if (Uri.IsHexDigit(c) && length < 12) buffer[length++] = char.ToUpperInvariant(c);
        return length == 12 ? new string(buffer) : string.Empty;
    }

    private void OnProcessExit(object? sender, EventArgs args) => Dispose();

    private void CleanupCore()
    {
        if (_state == Stopped) return;
        Volatile.Write(ref _state, Stopping);
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        try
        {
            if (_active && _session != 0)
            {
                try { Check(_api!.SetActive(_session, 0)); }
                catch (Exception error) { RememberFailure("PacketMonitorSetSessionActive(false)", error); }
            }
            _active = false;
            // CloseStream 返回后回调已结束；到此为止必须保留委托和 DLL。
            if (_stream != 0) { _api!.CloseStream(_stream); _stream = 0; }
            if (_session != 0) { _api!.CloseSession(_session); _session = 0; }
            if (_monitor != 0) { _api!.Uninitialize(_monitor); _monitor = 0; }
            _api?.Dispose();
            _api = null;
            _map.Clear();
            Volatile.Write(ref _state, Stopped);
        }
        finally
        {
            GC.KeepAlive(_dataCallback);
            GC.KeepAlive(_eventCallback);
        }
    }

    /// <summary>停止捕获并清理本实例的会话和实时流。释放后不可重新启动。</summary>
    public void Dispose()
    {
        lock (_lifecycleLock) CleanupCore();
    }
}
