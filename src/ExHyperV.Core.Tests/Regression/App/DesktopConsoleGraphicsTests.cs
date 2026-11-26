using System.Runtime.InteropServices;
using ExHyperV.App.UI.Pages.VirtualMachines.Console;
using ExHyperV.App.UI.Shared.ConsoleHost;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("WindowsIntegration")]
public sealed class DesktopConsoleGraphicsTests
{
    [TestInitialize]
    public void RequireWindowsIntegration()
    {
        if (Environment.GetEnvironmentVariable("EXHYPERV_TEST_WINDOWS") != "1")
            Assert.Inconclusive("Windows graphics integration is disabled.");
    }

    private static ConsoleSwapChainRenderer Renderer() => new(_ => 0, () => (1, 1), (name, detail) =>
        System.Diagnostics.Trace.WriteLine($"{name} {detail}"));

    private static byte[] Pixels(params byte[] row) => row.SelectMany(b => new byte[] { b, b, b, 255 }).ToArray();

    private static byte[] Readback(ConsoleSwapChainRenderer renderer)
    {
        using var copy = renderer.BeginReadback();
        Assert.IsNotNull(copy);
        for (int i = 0; i < 200; i++)
        {
            using var ready = copy.Read();
            if (ready != null) return ready.CopyPixels();
            Thread.Sleep(5);
        }
        throw new TimeoutException("GPU readback did not complete.");
    }

    [TestMethod]
    public void CpuMovesUseOneBaseAndScreenshotPreservesEveryDirtyRectangle()
    {
        using var renderer = Renderer();
        byte[] initial = Pixels(1, 2, 3, 4, 5), expected = Pixels(9, 3, 4, 2, 8);
        using var first = new ConsoleFrame(5, 1, 20, initial, 1);
        Assert.IsTrue(renderer.TryPresent(first, ConsoleFrameDamage.FullFrame, 5, 1));
        ConsoleFrameRegion[] dirty = [new(0, 0, 1, 1), new(4, 0, 1, 1)];
        ConsoleFrameMove[] moves = [new(2, 0, new(1, 0, 2, 1)), new(1, 0, new(3, 0, 1, 1))];
        using var delta = new ConsoleFrame(5, 1, 20, expected, 2, 1, ConsoleFrameUpdateKind.Delta, dirty, moves);
        Assert.IsTrue(renderer.TryPresent(delta, new(1, false, dirty, moves), 5, 1));
        CollectionAssert.AreEqual(expected, Readback(renderer));
    }

    [DllImport("d3d11.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern unsafe int D3D11CreateDevice(nint adapter, uint type, nint software, uint flags, nint levels,
        uint count, uint sdkVersion, nint* device, nint level, nint* context);

    private static unsafe nint Slot(nint obj, int index) => (*(nint**)obj)[index];

    private static nint Interface(nint parent, string id)
    {
        Guid iid = new(id);
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(parent, in iid, out var result));
        return result;
    }

    [TestMethod]
    public unsafe void SharedTextureReturnsKeyAndScreenshotsPrivatePixelsAcrossEpochs()
    {
        using var renderer = Renderer();
        nint device = 0, context = 0, texture = 0, dxgi = 0, adapter = 0, resource = 0, mutex = 0;
        try
        {
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, &device, 0, &context));
            dxgi = Interface(device, "54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dxgi, 7))(dxgi, &adapter));
            byte* desc = stackalloc byte[304];
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(adapter, 8))(adapter, desc));
            long luid = *(long*)(desc + 272 + 3 * sizeof(nint));
            uint* description = stackalloc uint[] { 4, 1, 1, 1, 87, 1, 0, 0, 8, 0, 0x900 };
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Slot(device, 5))(
                device, description, 0, &texture));
            resource = Interface(texture, "30961379-4609-4a41-998e-54fe567ee0c1");
            mutex = Interface(texture, "9d8e1289-d7b3-465f-8126-250e349af85d");
            nint handle = 0;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint*, int>)Slot(resource, 13))(
                resource, 0, 0x80000000, 0, &handle));
            using var shared = new SafeFileHandle(handle, true);
            for (int i = 1; i <= 4; i++)
            {
                Assert.AreEqual(0, ((delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)Slot(mutex, 8))(mutex, 0, 1000));
                byte[] pixels = Pixels(4, 5, (byte)i, 7);
                fixed (byte* src = pixels)
                    ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, byte*, uint, uint, void>)Slot(context, 48))(
                        context, texture, 0, 0, src, 16, 0);
                Assert.AreEqual(0, ((delegate* unmanaged[Stdcall]<nint, ulong, int>)Slot(mutex, 9))(mutex, 1));
                using var frame = new ConsoleGpuFrame(luid, (ulong)((i + 1) / 2), 0, i, 4, 1, new(handle, false), 1, 0);
                Assert.IsTrue(renderer.TryPresent(frame, 4, 1));
                CollectionAssert.AreEqual(pixels, Readback(renderer));
            }
            using var mismatch = new ConsoleGpuFrame(~luid, 3, 0, 5, 4, 1, new(handle, false), 1, 0);
            Assert.IsFalse(renderer.TryPresent(mismatch, 4, 1));
            using var wrongKey = new ConsoleGpuFrame(luid, 3, 0, 6, 4, 1, new(handle, false), 7, 0);
            Assert.IsFalse(renderer.TryPresent(wrongKey, 4, 1));
            using var cpu = new ConsoleFrame(4, 1, 16, Pixels(1, 2, 3, 4), 1);
            Assert.IsTrue(renderer.TryPresent(cpu, ConsoleFrameDamage.FullFrame, 4, 1));
            CollectionAssert.AreEqual(cpu.CopyPixels(), Readback(renderer));
        }
        finally
        {
            foreach (var obj in new[] { mutex, resource, adapter, dxgi, texture, context, device })
                if (obj != 0) Marshal.Release(obj);
        }
    }
}
