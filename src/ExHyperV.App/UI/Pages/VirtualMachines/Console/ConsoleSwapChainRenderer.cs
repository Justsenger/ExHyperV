using System.Runtime.InteropServices;
using ExHyperV.Services;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

// Presents BGRA32 console frames through a composition swap chain bound to a SwapChainPanel.
// Maintains a GPU backing texture and tracks per-frame dirty history so partial updates upload
// and copy only changed rectangles while keeping all flip-sequential buffers coherent.
internal sealed unsafe partial class ConsoleSwapChainRenderer(Func<nint, int> bindSwapChain,
    Func<(float X, float Y)> compositionScale, Action<string, string> log) : IDisposable
{
    private const int BufferCount = 2;
    private const int DriverHardware = 1;
    private const int DriverWarp = 5;
    private const uint CreateDeviceBgraSupport = 0x20;
    private const uint D3D11SdkVersion = 7;
    private const uint FormatBgra8Unorm = 87;
    private const uint UsageRenderTargetOutput = 0x20;
    private const int ScalingStretch = 0;
    private const int SwapEffectFlipSequential = 3;
    private const int AlphaModeIgnore = 3;
    private const uint UsageDefault = 0;
    private const uint UsageStaging = 3;
    private const uint BindShaderResource = 0x8;
    private const int DxgiStatusOccluded = 0x087A0001;
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);

    private static readonly Guid IidID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IidIDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidIDXGIFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    private static readonly Guid IidIDXGISwapChain2 = new("a8be2ac4-199f-4946-b331-79599fb98de7");

    private readonly ConsoleFrameRegion[]?[] dirtyHistory = new ConsoleFrameRegion[BufferCount - 1][];
    private readonly List<ConsoleFrameRegion> scratchRegions = new(ConsoleDamageAccumulator.RegionLimit + 2);
    private nint device;
    private nint context;
    private nint factory;
    private nint swapChain;
    private nint backingTexture;
    private nint moveTexture;
    private int width;
    private int height;
    private int fullFramesRemaining;
    private float lastScaleX;
    private float lastScaleY;
    private bool usingWarp;
    private bool hasFrame;
    private bool lastPresentOccluded;
    private bool disabled;
    private bool disposed;

    internal bool IsDisabled => disabled || disposed;
    internal bool HasSurface(int sourceWidth, int sourceHeight) =>
        swapChain != 0 && width == sourceWidth && height == sourceHeight;

    internal bool TryPresent(ConsoleFrame frame, ConsoleFrameDamage damage, double surfaceDipWidth, double surfaceDipHeight)
    {
        if (disabled || disposed || frame.Width <= 0 || frame.Height <= 0) return false;
        if (hasFrame && width == frame.Width && height == frame.Height && !damage.Full &&
            damage.Dirty.Length == 0 && damage.Moves.Length == 0) return true;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            int hr = TryPresentCore(frame, damage, surfaceDipWidth, surfaceDipHeight, out bool recreated);
            if (hr >= 0) return true;

            log("SwapChainPresentFailed", $"hr=0x{hr:X8} warp={usingWarp} attempt={attempt}");
            bool canRecover = attempt == 0 && (hr == DxgiErrorDeviceRemoved || hr == DxgiErrorDeviceReset || !recreated);
            TearDownDevice();
            if (!canRecover) break;
            damage = ConsoleFrameDamage.FullFrame;
        }

        disabled = true;
        TearDownDevice();
        return false;
    }

    internal bool UpdateDisplaySize(double surfaceDipWidth, double surfaceDipHeight)
    {
        if (disabled || disposed || swapChain == 0 || width <= 0 || height <= 0) return true;
        var (scaleX, scaleY) = ComputeScale(surfaceDipWidth, surfaceDipHeight, width, height);
        if (Math.Abs(scaleX - lastScaleX) <= 1e-5f && Math.Abs(scaleY - lastScaleY) <= 1e-5f) return true;

        int hr = ApplyMatrixTransform(scaleX, scaleY);
        if (hr < 0)
        {
            log("SwapChainTransformFailed", $"hr=0x{hr:X8}");
            if (hr is DxgiErrorDeviceRemoved or DxgiErrorDeviceReset) TearDownDevice();
            return false;
        }

        if (!hasFrame || PresentFromBacking([]) >= 0) return true;
        TearDownDevice();
        return false;
    }

    internal void Clear()
    {
        disabled = false;
        hasFrame = false;
        lastPresentOccluded = false;
        fullFramesRemaining = 0;
        width = height = 0;
        lastScaleX = lastScaleY = 0;
        Array.Clear(dirtyHistory);
        ReleaseSwapChainResources();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        TearDownDevice();
    }

    private int TryPresentCore(
        ConsoleFrame frame,
        ConsoleFrameDamage damage,
        double surfaceDipWidth,
        double surfaceDipHeight,
        out bool recreated)
    {
        recreated = false;
        int hr = EnsureDevice();
        if (hr < 0) return hr;

        bool sizeChanged = swapChain == 0 || width != frame.Width || height != frame.Height;
        if (sizeChanged)
        {
            hr = EnsureSurfaceResources(frame.Width, frame.Height);
            if (hr < 0 && !usingWarp)
            {
                TearDownDevice();
                hr = EnsureDevice(forceWarp: true);
                if (hr >= 0) hr = EnsureSurfaceResources(frame.Width, frame.Height);
            }
            if (hr < 0) return hr;
            recreated = true;
            damage = ConsoleFrameDamage.FullFrame;
        }

        var (scaleX, scaleY) = ComputeScale(surfaceDipWidth, surfaceDipHeight, width, height);
        if (Math.Abs(scaleX - lastScaleX) > 1e-5f || Math.Abs(scaleY - lastScaleY) > 1e-5f)
        {
            hr = ApplyMatrixTransform(scaleX, scaleY);
            if (hr < 0) return hr;
        }

        ConsoleFrameRegion[] dirty = damage.Full ? [new(0, 0, width, height)] : damage.Dirty;
        hr = EnsureBackingTexture();
        if (hr < 0) return hr;
        if (!damage.Full && damage.Moves.Length != 0)
        {
            hr = ApplyMoves(damage.Moves);
            if (hr < 0) return hr;
        }
        fixed (byte* source = frame.Pixels)
        {
            foreach (var rect in dirty)
            {
                var box = ToBox(rect);
                byte* pixels = source + (long)rect.Y * frame.Stride + (long)rect.X * 4;
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, D3D11Box*, byte*, uint, uint, void>)Slot(context, 48))(
                    context, backingTexture, 0, &box, pixels, (uint)frame.Stride, 0);
            }
        }
        ConsoleFrameRegion[] historyEntry;
        if (damage.Moves.Length == 0) historyEntry = dirty;
        else
        {
            historyEntry = new ConsoleFrameRegion[dirty.Length + damage.Moves.Length];
            Array.Copy(dirty, historyEntry, dirty.Length);
            for (int i = 0; i < damage.Moves.Length; i++) historyEntry[dirty.Length + i] = damage.Moves[i].Destination;
        }
        hr = PresentFromBacking(historyEntry);
        if (hr < 0) return hr;

        hasFrame = true;
        return hr;
    }

    private int EnsureDevice(bool forceWarp = false)
    {
        if (device != 0 && context != 0 && factory != 0) return 0;
        TearDownDevice();

        int hr;
        if (!forceWarp)
        {
            hr = CreateD3DDevice(DriverHardware, out device, out context);
            if (hr >= 0) usingWarp = false;
            else
            {
                hr = CreateD3DDevice(DriverWarp, out device, out context);
                usingWarp = hr >= 0;
            }
        }
        else
        {
            hr = CreateD3DDevice(DriverWarp, out device, out context);
            usingWarp = hr >= 0;
        }
        if (hr < 0) return hr;

        nint dxgiDevice = 0, adapter = 0;
        try
        {
            var iidDxgiDevice = IidIDXGIDevice;
            hr = QueryInterface(device, &iidDxgiDevice, &dxgiDevice);
            if (hr < 0 || dxgiDevice == 0) return hr < 0 ? hr : unchecked((int)0x80004002);

            hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dxgiDevice, 7))(dxgiDevice, &adapter);
            if (hr < 0 || adapter == 0) return hr < 0 ? hr : unchecked((int)0x80004002);

            AdapterDesc desc;
            hr = ((delegate* unmanaged[Stdcall]<nint, AdapterDesc*, int>)Slot(adapter, 8))(adapter, &desc);
            if (hr < 0) return hr;
            adapterLuid = desc.Luid;

            var iidFactory2 = IidIDXGIFactory2;
            nint factoryPtr = 0;
            hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(adapter, 6))(adapter, &iidFactory2, &factoryPtr);
            if (hr < 0 || factoryPtr == 0) return hr < 0 ? hr : unchecked((int)0x80004002);
            factory = factoryPtr;

            return 0;
        }
        finally
        {
            Release(ref adapter);
            Release(ref dxgiDevice);
        }
    }

    private int EnsureSurfaceResources(int targetWidth, int targetHeight)
    {
        Release(ref backingTexture);
        Release(ref moveTexture);
        ClearSharedSlots();
        hasFrame = false;
        lastPresentOccluded = false;
        Array.Clear(dirtyHistory);

        int hr;
        if (swapChain == 0)
        {
            var desc = new DxgiSwapChainDesc1
            {
                Width = (uint)targetWidth,
                Height = (uint)targetHeight,
                Format = FormatBgra8Unorm,
                SampleDescCount = 1,
                BufferUsage = UsageRenderTargetOutput,
                BufferCount = BufferCount,
                Scaling = ScalingStretch,
                SwapEffect = SwapEffectFlipSequential,
                AlphaMode = AlphaModeIgnore
            };
            nint swapChain1 = 0;
            hr = ((delegate* unmanaged[Stdcall]<nint, nint, DxgiSwapChainDesc1*, nint, nint*, int>)Slot(factory, 24))(
                factory, device, &desc, 0, &swapChain1);
            if (hr < 0 || swapChain1 == 0) return hr < 0 ? hr : unchecked((int)0x80004005);

            try
            {
                var iidSwapChain2 = IidIDXGISwapChain2;
                nint sc2 = 0;
                hr = QueryInterface(swapChain1, &iidSwapChain2, &sc2);
                if (hr < 0 || sc2 == 0) return hr < 0 ? hr : unchecked((int)0x80004002);
                swapChain = sc2;
            }
            finally
            {
                Release(ref swapChain1);
            }

            hr = bindSwapChain(swapChain);
            if (hr < 0) return hr;
            lastScaleX = lastScaleY = 0;
        }
        else
        {
            hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, uint, uint, int>)Slot(swapChain, 13))(
                swapChain, BufferCount, (uint)targetWidth, (uint)targetHeight, FormatBgra8Unorm, 0);
            if (hr < 0) return hr;
        }

        width = targetWidth;
        height = targetHeight;
        fullFramesRemaining = BufferCount;
        return 0;
    }

    private int EnsureBackingTexture()
    {
        if (backingTexture != 0) return 0;
        var backingDesc = new D3D11Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = FormatBgra8Unorm,
            SampleDescCount = 1,
            Usage = UsageDefault,
            BindFlags = BindShaderResource
        };
        nint backingPtr = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, D3D11Texture2DDesc*, nint, nint*, int>)Slot(device, 5))(
            device, &backingDesc, 0, &backingPtr);
        if (hr < 0 || backingPtr == 0) return hr < 0 ? hr : unchecked((int)0x80004005);
        backingTexture = backingPtr;
        return 0;
    }

    private int ApplyMoves(ConsoleFrameMove[] moves)
    {
        if (moveTexture == 0)
        {
            var desc = new D3D11Texture2DDesc { Width = (uint)width, Height = (uint)height, MipLevels = 1,
                ArraySize = 1, Format = FormatBgra8Unorm, SampleDescCount = 1 };
            nint texture = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint, D3D11Texture2DDesc*, nint, nint*, int>)Slot(device, 5))(
                device, &desc, 0, &texture);
            if (hr < 0) return hr;
            moveTexture = texture;
        }
        // All move sources refer to the same base frame, including overlapping moves.
        CopyResource(moveTexture, backingTexture);
        foreach (var move in moves)
        {
            var box = ToBox(move.Destination with { X = move.SourceX, Y = move.SourceY });
            CopySubresourceRegion(backingTexture, (uint)move.Destination.X, (uint)move.Destination.Y, moveTexture, &box);
        }
        return 0;
    }

    private int PresentFromBacking(ConsoleFrameRegion[] changes)
    {
        var iid = IidID3D11Texture2D;
        nint buffer = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(swapChain, 9))(swapChain, 0, &iid, &buffer);
        if (hr < 0) return hr;
        try
        {
            var full = new ConsoleFrameRegion(0, 0, width, height);
            bool copyAll = fullFramesRemaining > 0 || ContainsRegion(changes, full);
            scratchRegions.Clear();
            if (!copyAll)
            {
                for (int h = 0; h < dirtyHistory.Length; h++)
                {
                    if (dirtyHistory[h] is { } prior && !AddUniqueRegions(prior, full)) { copyAll = true; break; }
                }
                if (!copyAll && !AddUniqueRegions(changes, full)) copyAll = true;
            }
            NativeRect* rects = stackalloc NativeRect[ConsoleDamageAccumulator.RegionLimit];
            DxgiPresentParameters parameters = default;
            if (copyAll || scratchRegions.Count == 0) CopyResource(buffer, backingTexture);
            else
            {
                for (int i = 0; i < scratchRegions.Count; i++)
                {
                    var r = scratchRegions[i];
                    var box = ToBox(r);
                    CopySubresourceRegion(buffer, (uint)r.X, (uint)r.Y, backingTexture, &box);
                    rects[i] = new() { Left = r.X, Top = r.Y, Right = r.X + r.Width, Bottom = r.Y + r.Height };
                }
                parameters.DirtyRectsCount = (uint)scratchRegions.Count;
                parameters.DirtyRects = rects;
            }
            hr = Present1(&parameters);
            lastPresentOccluded = hr == DxgiStatusOccluded;
            if (lastPresentOccluded)
            {
                fullFramesRemaining = BufferCount;
                Array.Clear(dirtyHistory);
            }
            else if (hr >= 0)
            {
                if (fullFramesRemaining > 0) fullFramesRemaining--;
                Array.Copy(dirtyHistory, 1, dirtyHistory, 0, dirtyHistory.Length - 1);
                dirtyHistory[^1] = changes;
            }
            return hr;
        }
        finally { Release(ref buffer); }
    }

    private static bool ContainsRegion(ConsoleFrameRegion[] items, ConsoleFrameRegion target)
    {
        for (int i = 0; i < items.Length; i++)
            if (items[i] == target) return true;
        return false;
    }

    private bool AddUniqueRegions(ConsoleFrameRegion[] items, ConsoleFrameRegion full)
    {
        for (int i = 0; i < items.Length; i++)
        {
            var region = items[i];
            if (region == full) return false;
            bool seen = false;
            for (int j = 0; j < scratchRegions.Count; j++)
            {
                if (scratchRegions[j] == region) { seen = true; break; }
            }
            if (!seen)
            {
                if (scratchRegions.Count >= ConsoleDamageAccumulator.RegionLimit) return false;
                scratchRegions.Add(region);
            }
        }
        return true;
    }

    private int ApplyMatrixTransform(float scaleX, float scaleY)
    {
        var matrix = new DxgiMatrix3x2F { M11 = scaleX, M22 = scaleY };
        int hr = ((delegate* unmanaged[Stdcall]<nint, DxgiMatrix3x2F*, int>)Slot(swapChain, 34))(swapChain, &matrix);
        if (hr >= 0)
        {
            lastScaleX = scaleX;
            lastScaleY = scaleY;
        }
        return hr;
    }

    private (float ScaleX, float ScaleY) ComputeScale(double surfaceDipWidth, double surfaceDipHeight, int frameWidth, int frameHeight)
    {
        if (surfaceDipWidth > 0 && surfaceDipHeight > 0 && double.IsFinite(surfaceDipWidth) && double.IsFinite(surfaceDipHeight))
            return ((float)(surfaceDipWidth / frameWidth), (float)(surfaceDipHeight / frameHeight));

        var (x, y) = compositionScale();
        return (x > 0 ? 1f / x : 1f, y > 0 ? 1f / y : 1f);
    }

    private int Present1(DxgiPresentParameters* presentParams) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, DxgiPresentParameters*, int>)Slot(swapChain, 22))(
            swapChain, 0, 0, presentParams);

    private void CopyResource(nint dst, nint src) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(context, 47))(context, dst, src);

    private void CopySubresourceRegion(nint dst, uint dstX, uint dstY, nint src, D3D11Box* srcBox) =>
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, uint, nint, uint, D3D11Box*, void>)Slot(context, 46))(
            context, dst, 0, dstX, dstY, 0, src, 0, srcBox);

    private void ReleaseSwapChainResources()
    {
        Release(ref backingTexture);
        Release(ref moveTexture);
        ClearSharedSlots();
        if (swapChain != 0) bindSwapChain(0);
        Release(ref swapChain);
    }

    private void TearDownDevice()
    {
        hasFrame = false;
        lastPresentOccluded = false;
        fullFramesRemaining = 0;
        width = height = 0;
        lastScaleX = lastScaleY = 0;
        Array.Clear(dirtyHistory);
        ReleaseSwapChainResources();
        Release(ref factory);
        if (context != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, void>)Slot(context, 110))(context);
            ((delegate* unmanaged[Stdcall]<nint, void>)Slot(context, 111))(context);
        }
        Release(ref context);
        Release(ref device);
    }

    private static int CreateD3DDevice(int driverType, out nint outDevice, out nint outContext) =>
        D3D11CreateDevice(0, driverType, 0, CreateDeviceBgraSupport, null, 0, D3D11SdkVersion, out outDevice, out _, out outContext);

    private static D3D11Box ToBox(ConsoleFrameRegion region) => new()
    {
        Left = (uint)region.X,
        Top = (uint)region.Y,
        Front = 0,
        Right = (uint)(region.X + region.Width),
        Bottom = (uint)(region.Y + region.Height),
        Back = 1
    };

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static int QueryInterface(nint instance, Guid* iid, nint* target) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(instance, 0))(instance, iid, target);

    private static void Release(ref nint instance)
    {
        nint ptr = instance;
        if (ptr == 0) return;
        instance = 0;
        _ = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(ptr, 2))(ptr);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiSwapChainDesc1
    {
        public uint Width, Height, Format;
        public int Stereo;
        public uint SampleDescCount, SampleDescQuality, BufferUsage, BufferCount;
        public int Scaling, SwapEffect, AlphaMode;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiPresentParameters
    {
        public uint DirtyRectsCount;
        public NativeRect* DirtyRects;
        public NativeRect* ScrollRect;
        public nint ScrollOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiMatrix3x2F
    {
        public float M11, M12, M21, M22, M31, M32;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Texture2DDesc
    {
        public uint Width, Height, MipLevels, ArraySize, Format;
        public uint SampleDescCount, SampleDescQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Box
    {
        public uint Left, Top, Front, Right, Bottom, Back;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11MappedSubresource
    {
        public void* Data;
        public uint RowPitch, DepthPitch;
    }

    [LibraryImport("d3d11.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int D3D11CreateDevice(
        nint pAdapter,
        int driverType,
        nint software,
        uint flags,
        uint* pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out nint ppDevice,
        out uint pFeatureLevel,
        out nint ppImmediateContext);
}
