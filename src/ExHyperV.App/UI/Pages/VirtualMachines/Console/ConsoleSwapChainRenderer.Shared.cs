using ExHyperV.App.UI.Shared.ConsoleHost;
using ExHyperV.Services;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

internal sealed unsafe partial class ConsoleSwapChainRenderer
{
    private long adapterLuid;
    private ulong sharedEpoch;
    private long sharedSequence;
    private readonly Dictionary<int, (nint Texture, nint Mutex)> sharedSlots = [];

    private struct AdapterDesc
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public long Luid;
    }

    private void ClearSharedSlots()
    {
        foreach (var (texture, mutex) in sharedSlots.Values)
        {
            nint t = texture, m = mutex;
            Release(ref m);
            Release(ref t);
        }
        sharedSlots.Clear();
        sharedSequence = 0;
    }

    internal bool TryPresent(ConsoleGpuFrame frame, double dipWidth, double dipHeight)
    {
        if (IsDisabled) return false;
        int hr = PresentShared(frame, dipWidth, dipHeight);
        if (hr == 0 || hr == DxgiStatusOccluded) return true;
        ClearSharedSlots();
        if (hr is DxgiErrorDeviceRemoved or DxgiErrorDeviceReset) TearDownDevice();
        return false;
    }

    private int PresentShared(ConsoleGpuFrame frame, double dipWidth, double dipHeight)
    {
        const int invalid = unchecked((int)0x80070057);
        if (frame.Width is < 1 or > 8192 || frame.Height is < 1 or > 8192 || frame.Slot < 0 ||
            frame.Sequence <= 0 || frame.Texture.IsInvalid) return invalid;
        int hr = EnsureDevice();
        if (hr < 0) return hr;
        if (adapterLuid != frame.AdapterLuid) return invalid;
        if (swapChain == 0 || frame.Width != width || frame.Height != height)
        {
            hr = EnsureSurfaceResources(frame.Width, frame.Height);
            if (hr < 0) return hr;
        }
        if (sharedEpoch != frame.Epoch)
        {
            ClearSharedSlots();
            sharedEpoch = frame.Epoch;
        }
        if (frame.Sequence <= sharedSequence) return invalid;
        if (!sharedSlots.TryGetValue(frame.Slot, out var slot))
        {
            if (sharedSlots.Count >= 64) return invalid;
            hr = OpenSlot(frame, out slot);
            if (hr < 0) return hr;
            sharedSlots.Add(frame.Slot, slot);
        }

        var scale = ComputeScale(dipWidth, dipHeight, width, height);
        if (Math.Abs(scale.ScaleX - lastScaleX) > 1e-5f || Math.Abs(scale.ScaleY - lastScaleY) > 1e-5f)
        {
            hr = ApplyMatrixTransform(scale.ScaleX, scale.ScaleY);
            if (hr < 0) return hr;
        }
        hr = EnsureBackingTexture();
        if (hr < 0) return hr;
        hr = ((delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)Slot(slot.Mutex, 8))(slot.Mutex, frame.ReadKey, 16);
        if (hr != 0) return hr;
        try { CopyResource(backingTexture, slot.Texture); }
        finally { hr = ((delegate* unmanaged[Stdcall]<nint, ulong, int>)Slot(slot.Mutex, 9))(slot.Mutex, frame.ReturnKey); }
        if (hr != 0) return hr;
        hr = PresentFullFromBacking();
        hasFrame = hr >= 0;
        if (hasFrame) sharedSequence = frame.Sequence;
        return hr;
    }

    private int PresentFullFromBacking()
    {
        var iid = IidID3D11Texture2D;
        nint buffer = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(swapChain, 9))(swapChain, 0, &iid, &buffer);
        if (hr < 0) return hr;
        try
        {
            CopyResource(buffer, backingTexture);
            DxgiPresentParameters parameters = default;
            hr = Present1(&parameters);
            lastPresentOccluded = hr == DxgiStatusOccluded;
            fullFramesRemaining = BufferCount;
            if (lastPresentOccluded) Array.Clear(dirtyHistory);
            else
            {
                Array.Copy(dirtyHistory, 1, dirtyHistory, 0, dirtyHistory.Length - 1);
                dirtyHistory[^1] = null;
            }
            return hr;
        }
        finally { Release(ref buffer); }
    }

    private int OpenSlot(ConsoleGpuFrame frame, out (nint Texture, nint Mutex) slot)
    {
        slot = default;
        Guid deviceIid = new("a04bfb29-08ef-43d6-a49c-a9bdbdcbe686");
        Guid mutexIid = new("9d8e1289-d7b3-465f-8126-250e349af85d");
        Guid textureIid = IidID3D11Texture2D;
        nint device1 = 0, texture = 0, mutex = 0;
        bool retained = false;
        try
        {
            int hr = QueryInterface(device, &deviceIid, &device1);
            if (hr < 0) return hr;
            frame.Texture.DangerousAddRef(ref retained);
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(device1, 48))(
                device1, frame.Texture.DangerousGetHandle(), &textureIid, &texture);
            if (hr < 0) return hr;
            D3D11Texture2DDesc desc;
            ((delegate* unmanaged[Stdcall]<nint, D3D11Texture2DDesc*, void>)Slot(texture, 10))(texture, &desc);
            if (desc.Width != width || desc.Height != height || desc.Format != FormatBgra8Unorm ||
                desc.MipLevels != 1 || desc.ArraySize != 1 || desc.SampleDescCount != 1 || desc.SampleDescQuality != 0)
                return unchecked((int)0x80070057);
            hr = QueryInterface(texture, &mutexIid, &mutex);
            if (hr < 0) return hr;
            slot = (texture, mutex);
            texture = mutex = 0;
            return 0;
        }
        finally
        {
            if (retained) frame.Texture.DangerousRelease();
            Release(ref mutex);
            Release(ref texture);
            Release(ref device1);
        }
    }

    // A screenshot owns its staging copy while UI rendering continues on another dispatcher.
    internal Readback? BeginReadback()
    {
        if (!hasFrame || backingTexture == 0) return null;
        var desc = new D3D11Texture2DDesc { Width = (uint)width, Height = (uint)height, MipLevels = 1,
            ArraySize = 1, Format = FormatBgra8Unorm, SampleDescCount = 1, Usage = UsageStaging, CPUAccessFlags = 0x20000 };
        nint staging = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, D3D11Texture2DDesc*, nint, nint*, int>)Slot(device, 5))(device, &desc, 0, &staging);
        if (hr < 0 || staging == 0) return null;
        CopyResource(staging, backingTexture);
        ((delegate* unmanaged[Stdcall]<nint, void>)Slot(context, 111))(context); // Flush
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(context, 1))(context);
        return new(context, staging, width, height);
    }

    internal sealed class Readback(nint sourceContext, nint staging, int width, int height) : IDisposable
    {
        private nint ctx = sourceContext, texture = staging;

        internal ConsoleFrame? Read()
        {
            D3D11MappedSubresource mapped;
            int hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, D3D11MappedSubresource*, int>)Slot(ctx, 14))(
                ctx, texture, 0, 1, 0x100000, &mapped); // READ, DO_NOT_WAIT
            if (hr == unchecked((int)0x887A000A)) return null;
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(hr);
            try
            {
                int stride = checked(width * 4);
                var pixels = new byte[checked(stride * height)];
                for (int y = 0; y < height; y++)
                    new ReadOnlySpan<byte>((byte*)mapped.Data + y * (long)mapped.RowPitch, stride).CopyTo(pixels.AsSpan(y * stride));
                return new(width, height, stride, pixels, 1);
            }
            finally { ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(ctx, 15))(ctx, texture, 0); }
        }

        public void Dispose() { Release(ref texture); Release(ref ctx); }
    }
}
