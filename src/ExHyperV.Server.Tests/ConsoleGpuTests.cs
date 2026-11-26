using System.Runtime.InteropServices;
using ExHyperV.Server.Hosting;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Server.Tests;

[TestClass]
public sealed unsafe class ConsoleGpuTests
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags, nint levels,
        uint count, uint sdkVersion, nint* device, nint level, nint* context);
    private static nint Slot(nint obj, int slot) => (*(nint**)obj)[slot];
    private static void Check(int hr) => Assert.AreEqual(0, hr, $"HRESULT 0x{hr:X8}");
    private static nint Query(nint obj, string id)
    {
        Guid iid = new(id); nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(obj, 0))(obj, &iid, &result));
        return result;
    }

    [TestMethod]
    public void SameAdapterSharedTextureFeedsHardwareMftAndReturnsKeyAfterGpuRead()
    {
        if (!ConsoleH264Encoder.Probe.VideoProcessorAvailable) Assert.Inconclusive("本次宿主未提供可用的硬件视频处理器。");
        nint device = 0, context = 0, texture = 0, dxgi = 0, adapter = 0, resource = 0, mutex = 0;
        try
        {
            Check(D3D11CreateDevice(0, 1, 0, 0x820, 0, 0, 7, &device, 0, &context));
            dxgi = Query(device, "54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dxgi, 7))(dxgi, &adapter));
            byte* description = stackalloc byte[304];
            Check(((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(adapter, 8))(adapter, description));
            long luid = *(long*)(description + 272 + 3 * sizeof(nint));
            uint* desc = stackalloc uint[] { 320, 240, 1, 1, 87, 1, 0, 0, 0x20, 0, 0x900 };
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Slot(device, 5))(device, desc, 0, &texture));
            resource = Query(texture, "30961379-4609-4a41-998e-54fe567ee0c1");
            mutex = Query(texture, "9d8e1289-d7b3-465f-8126-250e349af85d");
            nint handle = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint*, int>)Slot(resource, 13))(
                resource, 0, 0x80000000, 0, &handle));
            using var shared = new ConsoleGpuFrame(luid, 1, 0, 1, 320, 240, new SafeFileHandle(handle, true), 1, 0);
            using var encoder = ConsoleH264Encoder.TryCreate(320, 240);
            Assert.IsNotNull(encoder);
            byte[] pixels = new byte[320 * 240 * 4];
            for (int n = 0; n < 10; n++)
            {
                Check(((delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)Slot(mutex, 8))(mutex, 0, 2000));
                Array.Fill(pixels, (byte)(n * 23));
                fixed (byte* src = pixels)
                    ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, byte*, uint, uint, void>)Slot(context, 48))(
                        context, texture, 0, 0, src, 1280, 0);
                Check(((delegate* unmanaged[Stdcall]<nint, ulong, int>)Slot(mutex, 9))(mutex, 1));
                Assert.IsTrue(encoder.EncodePacket([], 0, 16667L * n, n == 0, shared, out var packet, out bool idr));
                using (packet)
                {
                    Assert.IsTrue(encoder.UsedVideoProcessor);
                    Assert.IsGreaterThan(0L, packet!.Length);
                    if (n == 0) Assert.IsTrue(idr);
                }
            }
        }
        finally
        {
            foreach (nint obj in new[] { mutex, resource, texture, adapter, dxgi, context, device })
                if (obj != 0) Marshal.Release(obj);
        }
    }

    [TestMethod]
    public void ConsoleCoreGpuSource_WrapsBinderDecodesSlotDimsFeedsEncoderAndEnforcesMonotonicCpuEpoch()
    {
        using var texture = new ExHyperV.ConsoleIpc.Tests.ConsoleIpcTestTexture(320, 240);
        Assert.IsNull(ExHyperV.ConsoleIpc.ConsoleCoreGpuSource.Wrap(new object()));

        int closes = 0;
        ulong cpuRequests = 0;
        Action? fireAvailable = null;
        bool returnedFrame = false;
        Func<Action, (Func<(long, ulong, long, ulong, SafeFileHandle, ulong, ulong)?>, Func<ulong>, Action)> binder = onAvailable =>
        {
            fireAvailable = onAvailable;
            return (
                () => returnedFrame
                    ? (texture.Adapter, 2UL, 11L, 3UL | (320UL << 16) | (240UL << 32), new SafeFileHandle(texture.Shared.DangerousGetHandle(), ownsHandle: false), 1UL, 2UL)
                    : null,
                () => ++cpuRequests,
                () => Interlocked.Increment(ref closes));
        };

        var source = ExHyperV.ConsoleIpc.ConsoleCoreGpuSource.Wrap(new BinderServiceProvider(binder));
        Assert.IsNotNull(source);

        int notifications = 0;
        using var delivery = source.OpenDelivery();
        delivery.Available += () => Interlocked.Increment(ref notifications);
        Assert.IsNull(delivery.TakeLatest());

        returnedFrame = true;
        fireAvailable?.Invoke();
        Assert.AreEqual(1, notifications);

        using var frame = delivery.TakeLatest();
        Assert.IsNotNull(frame);
        Assert.AreEqual(texture.Adapter, frame.AdapterLuid);
        Assert.AreEqual(2UL, frame.Epoch);
        Assert.AreEqual(3, frame.Slot);
        Assert.AreEqual(11L, frame.Sequence);
        Assert.AreEqual(320, frame.Width);
        Assert.AreEqual(240, frame.Height);
        Assert.AreEqual(1UL, frame.ReadKey);
        Assert.AreEqual(2UL, frame.ReturnKey);

        using var encoder = ConsoleH264Encoder.TryCreate(320, 240, true, out _, texture.Adapter);
        if (encoder is not null)
        {
            bool ok = encoder.EncodePacket([], 0, 0, true, frame, out var packet, out bool key);
            using (packet)
            {
                Assert.IsTrue(ok && key && packet is { Data.Length: > 0 },
                    "Hardware H.264 encoder should encode shared GPU frame from ConsoleCoreGpuSource.");
            }
        }

        ulong firstEpoch = delivery.RequestCpu();
        ulong secondEpoch = delivery.RequestCpu();
        Assert.IsGreaterThan(0UL, firstEpoch);
        Assert.IsGreaterThanOrEqualTo(firstEpoch, secondEpoch);

        delivery.Dispose();
        delivery.Dispose();
        Assert.AreEqual(1, closes);
    }

    private sealed class BinderServiceProvider(
        Func<Action, (Func<(long, ulong, long, ulong, SafeFileHandle, ulong, ulong)?>, Func<ulong>, Action)> binder) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == binder.GetType() ? binder : null;
    }
}
