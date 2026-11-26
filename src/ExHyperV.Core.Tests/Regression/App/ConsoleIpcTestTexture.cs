using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.ConsoleIpc.Tests;

internal sealed unsafe class ConsoleIpcTestTexture : IDisposable
{
    private nint device, context, texture, mutex;
    internal SafeFileHandle Shared { get; }
    internal long Adapter { get; }
    internal byte[] Pixels { get; }
    internal ConsoleIpcTestTexture(int width, int height)
    {
        nint dev = 0, ctx = 0, tex = 0, dxgi = 0, adapter = 0, resource = 0;
        try
        {
            Check(D3D11CreateDevice(0, 1, 0, 0x820, 0, 0, 7, &dev, 0, &ctx));
            device = dev; context = ctx;
            dxgi = Query(dev, "54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dxgi, 7))(dxgi, &adapter));
            byte* adapterDesc = stackalloc byte[304];
            Check(((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(adapter, 8))(adapter, adapterDesc));
            Adapter = *(long*)(adapterDesc + 272 + 3 * sizeof(nint));
            uint* desc = stackalloc uint[] { (uint)width, (uint)height, 1, 1, 87, 1, 0, 0, 0x20, 0, 0x900 };
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, nint, nint*, int>)Slot(dev, 5))(dev, desc, 0, &tex));
            texture = tex;
            resource = Query(tex, "30961379-4609-4a41-998e-54fe567ee0c1");
            mutex = Query(tex, "9d8e1289-d7b3-465f-8126-250e349af85d");
            nint handle = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, nint*, int>)Slot(resource, 13))(resource, 0, 0x80000000, 0, &handle));
            Shared = new SafeFileHandle(handle, true);
            Pixels = new byte[width * height * 4];
            new Random(35).NextBytes(Pixels);
            Check(((delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)Slot(mutex, 8))(mutex, 0, 1000));
            fixed (byte* src = Pixels)
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, byte*, uint, uint, void>)Slot(ctx, 48))(
                    ctx, tex, 0, 0, src, (uint)width * 4, 0);
            Check(((delegate* unmanaged[Stdcall]<nint, ulong, int>)Slot(mutex, 9))(mutex, 1));
        }
        catch { Dispose(); throw; }
        finally { Release(ref resource); Release(ref adapter); Release(ref dxgi); }
    }

    private static void Check(int hr)
    {
        if (hr != 0) throw new InvalidOperationException($"D3D11 HRESULT {hr:X8}");
    }
    private static nint Slot(nint obj, int slot) => (*(nint**)obj)[slot];
    private static nint Query(nint obj, string id)
    {
        Guid iid = new(id);
        Check(Marshal.QueryInterface(obj, in iid, out var result));
        return result;
    }
    private static void Release(ref nint obj)
    {
        if (obj != 0) Marshal.Release(obj);
        obj = 0;
    }
    public void Dispose()
    {
        Shared?.Dispose();
        Release(ref mutex); Release(ref texture); Release(ref context); Release(ref device);
    }
    [DllImport("d3d11.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int D3D11CreateDevice(nint adapter, uint type, nint software, uint flags, nint levels, uint count,
        uint sdk, nint* device, nint level, nint* context);
}
