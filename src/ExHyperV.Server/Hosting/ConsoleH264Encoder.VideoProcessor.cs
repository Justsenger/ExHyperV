using System.Runtime.InteropServices;

namespace ExHyperV.Server.Hosting;

internal sealed unsafe partial class ConsoleH264Encoder
{
    private nint videoDevice, videoContext, enumerator, processor, allocator, uploadTexture, device1;
    private readonly Dictionary<int, nint> sharedInputs = [];
    private ulong? surfaceEpoch;
    private long? adapterLuid, requestedAdapter;

    private static nint FindAdapter(long luid)
    {
        Guid iid = new("770aae78-f26f-4dba-a829-253c83d1b387");
        nint factory = 0;
        if (CreateDXGIFactory1(&iid, &factory) < 0) return 0;
        try
        {
            byte* desc = stackalloc byte[304];
            for (uint i = 0; ; i++)
            {
                nint adapter = 0;
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(factory, 12))(factory, i, &adapter) < 0) return 0;
                if (((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(adapter, 8))(adapter, desc) >= 0
                    && *(long*)(desc + 272 + 3 * sizeof(nint)) == luid) return adapter;
                ReleaseCom(ref adapter);
            }
        }
        finally { ReleaseCom(ref factory); }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(Guid* iid, nint* factory);
    private ConsoleGpuFrame? sharedInput;
    private bool skippedGpuFrame;
    internal bool UsedVideoProcessor { get; private set; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorStream
    {
        public int Enabled;
        public uint OutputIndex, InputIndex, PastFrames, FutureFrames;
        public nint Past, Input, Future, PastRight, InputRight, FutureRight;
    }

    private bool Query(nint obj, string id, out nint result)
    {
        Guid iid = new(id);
        return QueryCom(obj, &iid, out result) >= 0;
    }

    private bool InitializeProcessor()
    {
        processorStatus = "VideoInterfacesUnavailable";
        if (!Query(d3dDevice, "10ec4d5b-975a-4689-b9e4-d0aac30fe333", out videoDevice)
            || !Query(d3dContext, "61f21c45-3c0e-4a74-9cea-67100d9ad5e4", out videoContext)) return false;
        uint w = (uint)Width, h = (uint)Height;
        uint* content = stackalloc uint[] { 0, 60, 1, w, h, 60, 1, w, h, 0 };
        nint en = 0, proc = 0;
        if (((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Slot(videoDevice, 10))(videoDevice, content, &en) < 0) return false;
        enumerator = en;
        processorStatus = "VideoFormatsUnavailable";
        uint bgra = 0, nv12 = 0;
        var formats = (delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)Slot(en, 8);
        if (formats(en, 87, &bgra) < 0 || (bgra & 1) == 0 || formats(en, DxgiFormatNv12, &nv12) < 0 || (nv12 & 2) == 0
            || ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint*, int>)Slot(videoDevice, 4))(videoDevice, en, 0, &proc) < 0) return false;
        processor = proc;
        processorStatus = "VideoAllocatorUnavailable";
        // Full-range RGB input and studio-range BT.709 output.
        uint rgb = 2u << 4, yuv = (1u << 2) | (1u << 4);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint*, void>)Slot(videoContext, 28))(videoContext, proc, 0, &rgb);
        ((delegate* unmanaged[Stdcall]<nint, nint, uint*, void>)Slot(videoContext, 15))(videoContext, proc, &yuv);

        Guid iid = new("545b3a48-3283-4f62-866f-a62d8f598f9f");
        nint pool = 0, attrs = 0, mediaType = 0;
        try
        {
            if (MFCreateVideoSampleAllocatorEx(&iid, &pool) < 0 || MFCreateAttributes(&attrs, 1) < 0
                || SetUInt32Attr(attrs, new("eacf97ad-065c-4408-bee3-fdcbfd128be2"), 0x20 | D3d11BindVideoEncoder) < 0
                || ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(transform, 17))(transform, inputStreamId, &mediaType) < 0
                || ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(pool, 3))(pool, dxgiManager) < 0
                || ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint, nint, int>)Slot(pool, 7))(pool, 2, 6, attrs, mediaType) < 0) return false;
            processorStatus = "VideoSampleUnavailable";
            allocator = pool; pool = 0;
            return true;
        }
        finally { ReleaseCom(ref attrs); ReleaseCom(ref mediaType); ReleaseCom(ref pool); }
    }

    private bool PrepareSurface(ReadOnlySpan<byte> pixels, int stride, out nint sample)
    {
        sample = 0;
        nint allocated = 0, buffer = 0, dxgi = 0, target = 0, inputView = 0, outputView = 0, mutex = 0;
        bool acquired = false;
        try
        {
            // MF recycles this texture only after the MFT releases the tracked sample.
            if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(allocator, 6))(allocator, &allocated) < 0
                || ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(allocated, 40))(allocated, 0, &buffer) < 0
                || !Query(buffer, "e7174cfa-1c9e-48b1-8866-626226bfc258", out dxgi)) return false;
            var textureId = IidID3d11Texture2D;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(dxgi, 3))(dxgi, &textureId, &target) < 0) return false;
            processorStatus = "InputTextureUnavailable";
            nint input;
            if (sharedInput is { } shared)
            {
                input = OpenSurface(shared);
                if (input == 0 || !Query(input, "9d8e1289-d7b3-465f-8126-250e349af85d", out mutex)) return false;
                int hr = ((delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)Slot(mutex, 8))(mutex, shared.ReadKey, 10);
                skippedGpuFrame = hr == 258;
                if (hr != 0) return false;
                acquired = true;
            }
            else
            {
                if (uploadTexture == 0)
                {
                    var desc = new D3d11Texture2dDesc { Width = (uint)Width, Height = (uint)Height, ArraySize = 1,
                        MipLevels = 1, Format = 87, SampleCount = 1, BindFlags = 0 };
                    nint texture = 0;
                    if (((delegate* unmanaged[Stdcall]<nint, D3d11Texture2dDesc*, nint, nint*, int>)Slot(d3dDevice, 5))(d3dDevice, &desc, 0, &texture) < 0) return false;
                    uploadTexture = texture;
                }
                input = uploadTexture;
                fixed (byte* src = pixels)
                    ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, byte*, uint, uint, void>)Slot(d3dContext, 48))(
                        d3dContext, input, 0, 0, src, (uint)stride, 0);
            }
            uint* iv = stackalloc uint[] { 0, 1, 0, 0 };
            D3d11Texture2dDesc outputDesc = default;
            ((delegate* unmanaged[Stdcall]<nint, D3d11Texture2dDesc*, void>)Slot(target, 10))(target, &outputDesc);
            uint index = 0;
            if (((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(dxgi, 4))(dxgi, &index) < 0
                || outputDesc.MipLevels != 1 || index >= outputDesc.ArraySize) return false;
            uint* ov = stackalloc uint[] { outputDesc.ArraySize == 1 ? 1u : 2u, 0, index, 1 };
            var createInput = (delegate* unmanaged[Stdcall]<nint, nint, nint, uint*, nint*, int>)Slot(videoDevice, 8);
            var createOutput = (delegate* unmanaged[Stdcall]<nint, nint, nint, uint*, nint*, int>)Slot(videoDevice, 9);
            int hrInput = createInput(videoDevice, input, enumerator, iv, &inputView);
            if (hrInput < 0) { processorStatus = $"InputView-{hrInput:X8}"; return false; }
            int hrOutput = createOutput(videoDevice, target, enumerator, ov, &outputView);
            if (hrOutput < 0) { processorStatus = $"OutputView-{hrOutput:X8}"; return false; }
            processorStatus = "VideoProcessorBltFailed";
            var stream = new ProcessorStream { Enabled = 1, Input = inputView };
            if (((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, uint, ProcessorStream*, int>)Slot(videoContext, 53))(
                videoContext, processor, outputView, 0, 1, &stream) < 0) return false;
            if (acquired)
            {
                acquired = false;
                if (((delegate* unmanaged[Stdcall]<nint, ulong, int>)Slot(mutex, 9))(mutex, sharedInput!.ReturnKey) != 0) return false;
            }
            if (!WrapSurface(target, index, allocated, out sample)) return false;
            processorStatus = "VideoProcessorBlt";
            return true;
        }
        finally
        {
            if (acquired) ((delegate* unmanaged[Stdcall]<nint, ulong, int>)Slot(mutex, 9))(mutex, sharedInput!.ReturnKey);
            ReleaseCom(ref mutex); ReleaseCom(ref inputView); ReleaseCom(ref outputView);
            ReleaseCom(ref target); ReleaseCom(ref dxgi); ReleaseCom(ref buffer); ReleaseCom(ref allocated);
        }
    }

    private bool WrapSurface(nint texture, uint index, nint lease, out nint sample)
    {
        sample = 0;
        nint buffer = 0, dxgi = 0, wrapper = 0;
        Guid textureId = IidID3d11Texture2D;
        Guid allocatorLease = new("a7455afb-e585-458b-bc02-8d07850d5ab1");
        try
        {
            // The DXGI buffer retains the allocator sample even if the MFT retains only the buffer.
            if (MFCreateDXGISurfaceBuffer(&textureId, texture, index, 0, &buffer) < 0
                || !Query(buffer, "e7174cfa-1c9e-48b1-8866-626226bfc258", out dxgi)
                || ((delegate* unmanaged[Stdcall]<nint, Guid*, nint, int>)Slot(dxgi, 6))(dxgi, &allocatorLease, lease) < 0
                || MFCreateSample(&wrapper) < 0
                || ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(wrapper, 42))(wrapper, buffer) < 0) return false;
            sample = wrapper;
            wrapper = 0;
            return true;
        }
        finally { ReleaseCom(ref wrapper); ReleaseCom(ref dxgi); ReleaseCom(ref buffer); }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateDXGISurfaceBuffer(Guid* iid, nint surface, uint index, int bottomUp, nint* buffer);

    private nint OpenSurface(ConsoleGpuFrame frame)
    {
        if (device1 == 0 && !Query(d3dDevice, "a04bfb29-08ef-43d6-a49c-a9bdbdcbe686", out device1)) return 0;
        if (adapterLuid == null)
        {
            nint adapter = 0, dxgi = 0;
            try
            {
                if (!Query(d3dDevice, "54ec77fa-1377-44e6-8c32-88fd5f44c84c", out dxgi)
                    || ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dxgi, 7))(dxgi, &adapter) < 0) return 0;
                // DXGI_ADAPTER_DESC ends in three SIZE_T values and the adapter LUID.
                byte* desc = stackalloc byte[304];
                if (((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(adapter, 8))(adapter, desc) < 0) return 0;
                adapterLuid = *(long*)(desc + 272 + 3 * sizeof(nint));
            }
            finally { ReleaseCom(ref adapter); ReleaseCom(ref dxgi); }
        }
        if (adapterLuid != frame.AdapterLuid || frame.Slot is < 0 or >= 16 || frame.Texture.IsInvalid) return 0;
        if (surfaceEpoch is ulong epoch && frame.Epoch < epoch) return 0;
        if (surfaceEpoch != frame.Epoch) { ClearSurfaces(); surfaceEpoch = frame.Epoch; }
        if (sharedInputs.TryGetValue(frame.Slot, out var existing)) return existing;
        nint opened = 0;
        var iid = IidID3d11Texture2D;
        if (((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(device1, 48))(
            device1, frame.Texture.DangerousGetHandle(), &iid, &opened) < 0) return 0;
        D3d11Texture2dDesc description = default;
        ((delegate* unmanaged[Stdcall]<nint, D3d11Texture2dDesc*, void>)Slot(opened, 10))(opened, &description);
        if (description.Format != 87 || description.Width != Width || description.Height != Height || description.ArraySize != 1
            || description.MipLevels != 1 || description.SampleCount != 1 || (description.MiscFlags & 0x900) != 0x900)
        { ReleaseCom(ref opened); return 0; }
        sharedInputs.Add(frame.Slot, opened);
        return opened;
    }

    private void ClearSurfaces()
    {
        foreach (nint surface in sharedInputs.Values) { nint value = surface; ReleaseCom(ref value); }
        sharedInputs.Clear();
    }

    private void ReleaseProcessor()
    {
        ClearSurfaces();
        ReleaseCom(ref allocator); ReleaseCom(ref uploadTexture); ReleaseCom(ref processor); ReleaseCom(ref enumerator);
        ReleaseCom(ref device1); ReleaseCom(ref videoContext); ReleaseCom(ref videoDevice);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateVideoSampleAllocatorEx(Guid* iid, nint* allocator);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateAttributes(nint* attributes, uint capacity);
}
