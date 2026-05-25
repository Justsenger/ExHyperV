using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ExHyperV.Api;

// ══════════════════════════════════════════════════════════════════
//  VmbusApi — 公开封装层
//  VMBus socket 的底层连接建立
//  业务逻辑（隧道管理、看门狗、自动恢复）留在 UsbVmbusService
// ══════════════════════════════════════════════════════════════════
public static class VmbusApi
{
    // VMBus 地址族和协议常量
    public const int AF_HYPERV = 34;
    public const int SOCK_STREAM = 1;
    public const int HV_PROTOCOL_RAW = 1;

    // TCP 优化常量（供 UsbVmbusService 使用）
    public const uint SIO_TCP_SET_ACK_FREQUENCY = 0x98000017;
    public const int IPPROTO_TCP = 6;
    public const int TCP_NODELAY = 0x0001;
    public const int SOL_SOCKET = 0xFFFF;
    public const int SO_SNDBUF = 0x1001;
    public const int SO_RCVBUF = 0x1002;

    // ── VMBus Socket 操作 ─────────────────────────────────────────

    /// <summary>
    /// 创建一个 AF_HYPERV 原生 socket 句柄。
    /// 返回的句柄需要包装成 Socket 对象后使用。
    /// 失败时返回 INVALID_HANDLE_VALUE。
    /// </summary>
    public static ApiResponse<nint> CreateVmbusSocket()
    {
        nint handle = VmbusNative.socket(AF_HYPERV, SOCK_STREAM, HV_PROTOCOL_RAW);
        if (handle == VmbusNative.INVALID_SOCKET)
        {
            int err = Marshal.GetLastWin32Error();
            return ApiResponse<nint>.Fail(
                $"Failed to create VMBus socket",
                err, ApiErrorSource.Win32);
        }
        return ApiResponse<nint>.Ok(handle);
    }

    /// <summary>
    /// 将原生 socket 句柄包装成托管 Socket 对象。
    /// 包装后句柄的生命周期由 Socket 对象管理。
    /// </summary>
    public static Socket WrapHandle(nint handle)
        => new Socket(new SafeSocketHandle(handle, ownsHandle: true));

    /// <summary>
    /// 关闭 socket 句柄。
    /// </summary>
    public static int CloseSocket(nint handle)
        => VmbusNative.closesocket(handle);

    // ── TCP socket 优化 ───────────────────────────────────────────
    // 供 UsbVmbusService 对 TCP 侧的 socket 进行性能调优

    /// <summary>
    /// 设置 TCP ACK 频率（减少延迟，提升小包吞吐）。
    /// </summary>
    public static ApiResponse SetAckFrequency(nint handle, int frequency)
    {
        uint bytesReturned;
        int ret = VmbusNative.WSAIoctl(
            handle,
            SIO_TCP_SET_ACK_FREQUENCY,
            ref frequency, 4,
            nint.Zero, 0,
            out bytesReturned,
            nint.Zero, nint.Zero);

        return ret == 0
            ? ApiResponse.Ok()
            : ApiResponse.Fail(
                "WSAIoctl SIO_TCP_SET_ACK_FREQUENCY failed",
                Marshal.GetLastWin32Error(), ApiErrorSource.Win32);
    }

    /// <summary>
    /// 禁用 Nagle 算法（降低小包延迟）。
    /// </summary>
    public static ApiResponse SetNoDelay(nint handle)
    {
        int opt = 1;
        int ret = VmbusNative.setsockopt(handle, IPPROTO_TCP, TCP_NODELAY, ref opt, 4);
        return ret == 0
            ? ApiResponse.Ok()
            : ApiResponse.Fail(
                "setsockopt TCP_NODELAY failed",
                Marshal.GetLastWin32Error(), ApiErrorSource.Win32);
    }

    /// <summary>
    /// 设置 socket 发送缓冲区大小。
    /// </summary>
    public static ApiResponse SetSendBuffer(nint handle, int size)
    {
        int ret = VmbusNative.setsockopt(handle, SOL_SOCKET, SO_SNDBUF, ref size, 4);
        return ret == 0
            ? ApiResponse.Ok()
            : ApiResponse.Fail(
                "setsockopt SO_SNDBUF failed",
                Marshal.GetLastWin32Error(), ApiErrorSource.Win32);
    }

    /// <summary>
    /// 设置 socket 接收缓冲区大小。
    /// </summary>
    public static ApiResponse SetReceiveBuffer(nint handle, int size)
    {
        int ret = VmbusNative.setsockopt(handle, SOL_SOCKET, SO_RCVBUF, ref size, 4);
        return ret == 0
            ? ApiResponse.Ok()
            : ApiResponse.Fail(
                "setsockopt SO_RCVBUF failed",
                Marshal.GetLastWin32Error(), ApiErrorSource.Win32);
    }

    // ── 原生读写（供 UsbVmbusService 的 NativePump 使用）─────────

    /// <summary>
    /// 从 socket 读取数据到非托管缓冲区。
    /// 返回读取的字节数，0 或负数表示连接断开或错误。
    /// </summary>
    public static unsafe int Recv(nint socket, void* buffer, int length)
        => VmbusNative.recv(socket, buffer, length, 0);

    /// <summary>
    /// 向 socket 发送非托管缓冲区中的数据。
    /// 返回发送的字节数，0 或负数表示连接断开或错误。
    /// </summary>
    public static unsafe int Send(nint socket, void* buffer, int length)
        => VmbusNative.send(socket, buffer, length, 0);
}

// ══════════════════════════════════════════════════════════════════
//  VmbusNative — ws2_32.dll P/Invoke 声明
//  VmbusApi 以外的代码不应直接引用
// ══════════════════════════════════════════════════════════════════
internal static class VmbusNative
{
    public static readonly nint INVALID_SOCKET = new(-1);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern nint socket(int af, int type, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern unsafe int recv(nint s, void* buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern unsafe int send(nint s, void* buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int closesocket(nint s);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int setsockopt(
        nint s, int level, int optname, ref int optval, int optlen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int WSAIoctl(
        nint s,
        uint dwIoControlCode,
        ref int lpvInBuffer,
        uint cbInBuffer,
        nint lpvOutBuffer,
        uint cbOutBuffer,
        out uint lpcbBytesReturned,
        nint lpOverlapped,
        nint lpCompletionRoutine);
}