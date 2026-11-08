using System.ComponentModel;
using System.Runtime.InteropServices;
using ExHyperV.Tools;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Transport;

// Byte transport only: no USB, video, TCP, or application lifetime policy.
internal sealed class VmbusPipeTransport : IDisposable
{
    private readonly OverlappedDevice device;
    private static readonly Lazy<Api> Native = new(() => new Api());
    internal Guid InstanceId { get; }

    private VmbusPipeTransport(OverlappedDevice device, Guid instance) { this.device = device; InstanceId = instance; }

    internal static VmbusPipeTransport Offer(Guid vmId, Guid interfaceType, Guid instance, string name)
    {
        if (vmId == Guid.Empty || interfaceType == Guid.Empty || instance == Guid.Empty) throw new ArgumentException("通道标识不能为空。");
        var offer = new OfferData { Version = 1, Size = (uint)Marshal.SizeOf<OfferData>(), VmId = vmId,
            InterfaceType = interfaceType, InstanceId = instance, Revision = 1, Flags = 0x11, UserData = new byte[112] };
        offer.Name = Marshal.StringToHGlobalUni(name);
        try
        {
            uint error = Native.Value.Offer(ref offer, 0xc0000000 | 0x40000000, 0, out nint handle);
            if (error != 0) { if (handle != 0 && handle != -1) new SafeFileHandle(handle, true).Dispose(); throw new Win32Exception((int)error); }
            if (handle == 0 || handle == -1) throw new IOException("VMBusPipe 未返回有效句柄。");
            return new(new OverlappedDevice(new SafeFileHandle(handle, true)), instance);
        }
        finally { Marshal.FreeHGlobal(offer.Name); }
    }

    internal unsafe Task ConnectAsync(CancellationToken cancellationToken) => device.ConnectAsync(Native.Value.Connect.Invoke, cancellationToken);
    internal Task<int> ReadAsync(byte[] buffer, int offset, int length, CancellationToken token) => device.ReadAsync(buffer, offset, length, token);

    internal async Task ReadExactlyAsync(byte[] buffer, int offset, int length, CancellationToken token)
    {
        while (length != 0)
        {
            int read = await device.ReadAsync(buffer, offset, length, token).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("VMBusPipe 已断开。");
            offset += read; length -= read;
        }
    }

    internal async Task WriteAsync(byte[] buffer, int offset, int length, CancellationToken token)
    {
        while (length != 0)
        {
            int written = await device.WriteAsync(buffer, offset, Math.Min(length, 16384), token).ConfigureAwait(false);
            if (written == 0) throw new EndOfStreamException("VMBusPipe 未接受数据。");
            offset += written; length -= written;
        }
    }

    public void Dispose() => device.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct OfferData
    {
        internal uint Version, Size;
        internal Guid VmId;
        internal uint InterruptLatency;
        internal Guid InterfaceType, InstanceId;
        internal uint Revision;
        internal ushort Mmio, Flags;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 112)] internal byte[] UserData;
        internal nint Name;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate uint OfferDelegate(ref OfferData data, uint openMode, uint pipeMode, out nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private unsafe delegate bool ConnectDelegate(nint handle, NativeOverlapped* overlapped);

    private sealed class Api
    {
        // Process lifetime module: delegates cannot outlive it.
        private readonly nint module;
        internal readonly OfferDelegate Offer;
        internal readonly ConnectDelegate Connect;
        internal Api()
        {
            module = NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "vmbuspipe.dll"));
            try
            {
                Offer = Marshal.GetDelegateForFunctionPointer<OfferDelegate>(NativeLibrary.GetExport(module, "VmbusPipeServerOfferChannelEx"));
                Connect = Marshal.GetDelegateForFunctionPointer<ConnectDelegate>(NativeLibrary.GetExport(module, "VmbusPipeServerConnectPipe"));
            }
            catch { NativeLibrary.Free(module); throw; }
        }
    }
}
