using System.Runtime.InteropServices;

namespace ExHyperV.Services;

/// <summary>Microsoft Packet Monitor 公开 API 的进程内绑定。</summary>
/// <remarks>调用方先关闭流、会话及驱动句柄，最后释放此绑定；回调存活期间不得卸载 DLL。</remarks>
internal sealed class PacketMonitorApi : IDisposable
{
    internal const uint ApiVersion = 0x00010000;

    private nint _library;

    internal readonly InitializeDelegate Initialize;
    internal readonly UninitializeDelegate Uninitialize;
    internal readonly CreateSessionDelegate CreateSession;
    internal readonly CloseHandleDelegate CloseSession;
    internal readonly SetActiveDelegate SetActive;
    internal readonly AddConstraintDelegate AddConstraint;
    internal readonly CreateStreamDelegate CreateStream;
    internal readonly CloseHandleDelegate CloseStream;
    internal readonly AttachDelegate Attach;

    internal PacketMonitorApi()
    {
        // 微软未提供 SDK 头文件或导入库，公开约定要求动态查找这些导出。
        // https://learn.microsoft.com/windows/win32/pktmon/pktmon
        // 使用系统目录绝对路径，避免从工作目录或应用目录加载同名 DLL。
        _library = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "PktMonApi.dll"));
        try
        {
            Initialize = Load<InitializeDelegate>("PacketMonitorInitialize");
            Uninitialize = Load<UninitializeDelegate>("PacketMonitorUninitialize");
            CreateSession = Load<CreateSessionDelegate>("PacketMonitorCreateLiveSession");
            CloseSession = Load<CloseHandleDelegate>("PacketMonitorCloseSessionHandle");
            SetActive = Load<SetActiveDelegate>("PacketMonitorSetSessionActive");
            AddConstraint = Load<AddConstraintDelegate>("PacketMonitorAddCaptureConstraint");
            CreateStream = Load<CreateStreamDelegate>("PacketMonitorCreateRealtimeStream");
            CloseStream = Load<CloseHandleDelegate>("PacketMonitorCloseRealtimeStream");
            Attach = Load<AttachDelegate>("PacketMonitorAttachOutputToSession");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private T Load<T>(string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int InitializeDelegate(uint version, nint reserved, out nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void UninitializeDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    internal delegate int CreateSessionDelegate(nint handle, [MarshalAs(UnmanagedType.LPWStr)] string name, out nint session);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void CloseHandleDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int SetActiveDelegate(nint session, byte active);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int AddConstraintDelegate(nint session, in ProtocolConstraint constraint);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int CreateStreamDelegate(nint handle, in StreamConfiguration configuration, out nint stream);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate int AttachDelegate(nint session, nint stream);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void DataCallback(nint context, nint descriptor);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void EventCallback(nint context, nint info, uint kind);

    public void Dispose()
    {
        nint library = Interlocked.Exchange(ref _library, 0);
        if (library != 0) NativeLibrary.Free(library);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct StreamConfiguration
{
    internal nint Context, EventCallback, DataCallback;
    internal ushort BufferSizeMultiplier, TruncationSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StreamDescriptor
{
    internal nint Data;
    internal uint DataSize, MetadataOffset, PacketOffset, PacketLength, MissedPacketWriteCount, MissedPacketReadCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct StreamMetadata
{
    internal ulong PacketGroupId;
    internal ushort PacketCount, AppearanceCount, DirectionName, PacketType, ComponentId, EdgeId, Reserved;
    internal uint DropReason, DropLocation;
    internal ushort Processor;
    internal long Timestamp;
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal unsafe struct IpAddress
{
    [FieldOffset(0)] internal uint IPv4;
    [FieldOffset(0)] internal ulong IPv6Part;
    [FieldOffset(0)] internal fixed byte Bytes[16];
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct ProtocolConstraint
{
    // API 1.0 文档给出字段，但缺少 PACKETMONITOR_MAX_NAME_LENGTH 的数值。
    // https://learn.microsoft.com/windows/win32/pktmon/packetmonitor/nf-packetmonitor-packetmonitoraddcaptureconstraint
    // Name=64、结构大小=216 已在 PktMonApi 10.0.29671.1000 核对 ABI 并实测；
    // 这是已验证的布局，不代表微软文档保证了所有 Windows 版本均提供此 API。
    internal const int NameCapacity = 64;
    internal const int NativeSize = 216;

    internal fixed char Name[NameCapacity];
    internal uint IsPresent;
    internal fixed byte Mac1[6], Mac2[6];
    internal ushort VlanId, EtherType, Dscp;
    internal byte TransportProtocol;
    internal IpAddress Ip1, Ip2;
    internal byte PrefixLength1, PrefixLength2;
    internal ushort Port1, Port2;
    internal byte TcpFlags;
    internal uint EncapType;
    internal ushort VxLanPort;
    internal ulong Packets, Bytes;
}
