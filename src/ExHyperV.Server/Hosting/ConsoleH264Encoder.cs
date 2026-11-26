using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.InteropServices;

namespace ExHyperV.Server.Hosting;

/// <summary>
/// Native Windows Media Foundation H.264 encoder for console video frames.
/// Prefers hardware MFTs (with D3D11 DXGI manager) and falls back cleanly to software MFTs.
/// </summary>
internal sealed unsafe partial class ConsoleH264Encoder : IDisposable
{
    private const uint MfVersion = 0x00020070;
    private const uint MfStartupNoSocket = 1;
    private const uint MftEnumFlagSyncMft = 0x00000001;
    private const uint MftEnumFlagAsyncMft = 0x00000002;
    private const uint MftEnumFlagHardware = 0x00000004;
    private const uint MftEnumFlagSortAndFilter = 0x00000040;
    private const uint MftOutputStreamProvidesSamples = 0x100;
    private const uint MftOutputStreamCanProvideSamples = 0x200;
    private const uint MftMessageSetD3dManager = 0x2;
    private const uint MftMessageCommandFlush = 0x0;
    private const uint MftMessageNotifyBeginStreaming = 0x10000000;
    private const uint MftMessageNotifyEndStreaming = 0x10000001;
    private const uint MftMessageNotifyStartOfStream = 0x10000003;
    private const uint MeTransformNeedInput = 601;
    private const uint MeTransformHaveOutput = 602;
    private const uint MfEventFlagNoWait = 1;
    private const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
    private const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
    private const int MfENotAccepting = unchecked((int)0xC00D36B5);
    private const int MfENoMoreTypes = unchecked((int)0xC00D36B9);
    private const int ENotImpl = unchecked((int)0x80004001);
    private const int RpcEChangedMode = unchecked((int)0x80010106);

    private const uint D3dDriverTypeHardware = 1;
    private const uint D3d11SdkVersion = 7;
    private const uint D3d11CreateDeviceBgraSupport = 0x20;
    private const uint D3d11CreateDeviceVideoSupport = 0x800;
    private const uint DxgiFormatNv12 = 103;
    private const uint D3d11BindVideoEncoder = 0x400;

    private const uint MfVideoInterlaceProgressive = 2;
    private const uint MfVideoTransferMatrixBt709 = 1;
    private const uint MfNominalRange16To235 = 2;
    private const uint AvEncH264ProfileBase = 66;
    private const uint AvEncH264ProfileMain = 77;
    private const uint AvEncRateControlLowDelayVbr = 4;
    private const uint AvEncRateControlPeakConstrainedVbr = 1;
    private const uint AvEncRateControlCbr = 0;
    private const uint AvScenarioDisplayRemoting = 1;
    private const ushort VtBool = 11;
    private const ushort VtUi4 = 19;
    private const short VariantTrue = -1;

    private static readonly Guid MftCategoryVideoEncoder = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid MfMediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfVideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfVideoFormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid MfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MfMtFrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MfMtPixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MfMtInterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MfMtAvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MfMtDefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    private static readonly Guid MfMtYuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid MfMtVideoNominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid MfMtMpeg2Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MfMtMpegSequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    private static readonly Guid MfLowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid MfTransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    private static readonly Guid MfTransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid MfSaD3d11Aware = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");
    private static readonly Guid MfSampleExtensionCleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");

    private static readonly Guid CodecApiRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CodecApiLowLatency = new("9d3ecd55-89e8-490a-970a-0c9548d5a56e");
    private static readonly Guid CodecApiRealTime = new("143a0ff6-a131-43da-b81e-98fbb8ec378e");
    private static readonly Guid CodecApiMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid CodecApiMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    private static readonly Guid CodecApiGopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid CodecApiDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid CodecApiForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private static readonly Guid CodecApiScenarioInfo = new("b28a6e64-3ff9-446a-8a4b-0d7a53413236");

    private static readonly Guid IidImfTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    private static readonly Guid IidImfMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");
    private static readonly Guid IidICodecApi = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private static readonly Guid IidID3d10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    private static readonly Guid IidID3d11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    internal sealed record ProbeResult(bool Available, bool HardwareAvailable, string[] SupportedEncodings, bool VideoProcessorAvailable = false, string VideoProcessorStatus = "Unavailable");

    private static readonly Lazy<ProbeResult> CachedProbe = new(ProbeHost, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static ProbeResult Probe => CachedProbe.Value;
    internal static bool IsAvailable => CachedProbe.Value.Available;
    internal static bool HardwareAvailable => CachedProbe.Value.HardwareAvailable;
    internal static string[] SupportedEncodings => CachedProbe.Value.SupportedEncodings;

    internal int Width { get; }
    internal int Height { get; }
    internal bool IsHardware { get; }

    private nint activate;
    private nint transform;
    private nint eventGen;
    private nint codecApi;
    private nint d3dDevice;
    private nint d3dContext;
    private nint dxgiManager;

    private nint inputSample;
    private nint inputBuffer;
    private nint outputSample;
    private bool mfStarted;
    private bool isAsync;
    private bool useD3dInput;
    private string processorStatus = "MftNotD3D11Aware";
    private bool outputProvidesSamples;
    private uint inputStreamId;
    private uint outputStreamId;
    private uint outputBufferSize;
    private int pendingNeedInput;
    private byte[] nv12Scratch;
    private byte[] sps = [], pps = [];
    private bool disposed;
    private bool activated;
    private readonly Lock workerLock = new();
    private readonly AutoResetEvent workSignal = new(false);
    private readonly ManualResetEventSlim workDone = new(false);
    private Thread? workerThread;
    private byte* pendingBgra;
    private int pendingBgraLength;
    private int pendingStride;
    private long pendingTimestampUs;
    private bool pendingForceKeyFrame;
    private ConsolePacket? pendingOutput;
    private bool pendingIsKeyFrame;
    private bool pendingEncodeResult;
    private int pendingCommand;

    private ConsoleH264Encoder(int width, int height, bool isHardware)
    {
        Width = width;
        Height = height;
        IsHardware = isHardware;
        nv12Scratch = new byte[checked(width * height * 3 / 2)];
    }

    private static ProbeResult ProbeHost()
    {
        try
        {
            byte[] pixels = new byte[640 * 480 * 4];
            foreach (bool hardware in new[] { true, false })
            {
                using var probe = TryCreate(640, 480, hardware);
                if (probe == null) continue;
                if (probe.EncodePacket(pixels, 640 * 4, 0, true, null, out var packet, out bool idr))
                {
                    using (packet)
                        if (idr && packet?.Length > 0)
                            return new(true, probe.IsHardware, ["bgra", "deflate", "png", "h264"], probe.UsedVideoProcessor, probe.processorStatus);
                }
            }
        }
        catch
        {
            // Media Foundation or video encoder MFT unavailable on this host.
        }
        return new ProbeResult(false, false, ["bgra", "deflate", "png"]);
    }

    internal static ConsoleH264Encoder? TryCreate(int width, int height, bool preferHardware = true)
        => TryCreate(width, height, preferHardware, out _);

    internal static ConsoleH264Encoder? TryCreate(int width, int height, bool preferHardware, out string? diagnostic, long? adapterLuid = null)
    {
        diagnostic = null;
        if (width < 64 || height < 64 || (width & 1) != 0 || (height & 1) != 0)
        {
            diagnostic = "H.264 requires even dimensions >= 64x64.";
            return null;
        }

        ConsoleH264Encoder? created = null;
        string? localDiag = null;
        using var initDone = new ManualResetEventSlim(false);

        // Pin each encoder instance to a dedicated MTA thread because hardware MFTs/D3D11 contexts
        // (including Qualcomm HW H.264 MFT) deadlock on teardown if called across different ThreadPool threads.
        var thread = new Thread(() =>
        {
            using var com = new ComScope();
            ConsoleH264Encoder? enc = null;
            if (preferHardware)
            {
                enc = TryCreateWithFlags(width, height, MftEnumFlagHardware | MftEnumFlagSortAndFilter, isHardware: true, out localDiag, adapterLuid);
            }
            if (enc == null)
            {
                enc = TryCreateWithFlags(width, height, MftEnumFlagSyncMft | MftEnumFlagAsyncMft | MftEnumFlagSortAndFilter, isHardware: false, out var swDiag);
                if (enc == null) localDiag ??= swDiag;
            }

            if (enc != null) enc.workerThread = Thread.CurrentThread;
            created = enc;
            initDone.Set();
            enc?.RunWorkerLoop();
        })
        {
            IsBackground = true,
            Name = $"ExHyperV.H264.{width}x{height}"
        };
        thread.TrySetApartmentState(ApartmentState.MTA);
        thread.Start();
        initDone.Wait();

        diagnostic = localDiag;
        return created;
    }

    private void RunWorkerLoop()
    {
        while (true)
        {
            workSignal.WaitOne();
            int cmd = pendingCommand;
            if (cmd == 1)
            {
                try
                {
                    var span = new ReadOnlySpan<byte>(pendingBgra, pendingBgraLength);
                    pendingEncodeResult = EncodeOnWorker(span, pendingStride, pendingTimestampUs, pendingForceKeyFrame, out pendingOutput, out pendingIsKeyFrame);
                }
                catch
                {
                    pendingOutput = null;
                    pendingIsKeyFrame = false;
                    pendingEncodeResult = false;
                }
                finally
                {
                    workDone.Set();
                }
            }
            else if (cmd == 2)
            {
                try { DisposeCore(); }
                finally { workDone.Set(); }
                break;
            }
        }
    }

    private static ConsoleH264Encoder? TryCreateWithFlags(int width, int height, uint enumFlags, bool isHardware, out string? diagnostic, long? requestedLuid = null)
    {
        diagnostic = null;
        bool started = false;
        nint* activates = null;
        uint count = 0;

        try
        {
            int hr = MFStartup(MfVersion, MfStartupNoSocket);
            if (hr < 0) { diagnostic = $"MFStartup failed: 0x{hr:X8}"; return null; }
            started = true;

            var outputInfo = new MftRegisterTypeInfo { MajorType = MfMediaTypeVideo, Subtype = MfVideoFormatH264 };
            hr = MFTEnumEx(MftCategoryVideoEncoder, enumFlags, null, &outputInfo, out activates, out count);
            if (hr < 0 || activates == null || count == 0)
            {
                diagnostic = $"MFTEnumEx(flags=0x{enumFlags:X}) found 0 H.264 encoders (hr=0x{hr:X8}).";
                return null;
            }

            for (uint i = 0; i < count; i++)
            {
                nint candidateActivate = activates[i];
                if (candidateActivate == 0) continue;
                activates[i] = 0; // Ownership transferred to encoder immediately.

                var encoder = new ConsoleH264Encoder(width, height, isHardware) { mfStarted = true, requestedAdapter = requestedLuid };
                if (encoder.InitializeFromActivate(candidateActivate, out diagnostic))
                {
                    started = false;  // MFShutdown ownership transferred to encoder.
                    return encoder;
                }
                encoder.mfStarted = false;
                encoder.Dispose();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            diagnostic = ex.Message;
        }
        finally
        {
            if (activates != null)
            {
                for (uint i = 0; i < count; i++) ReleaseCom(ref activates[i]);
                CoTaskMemFree((nint)activates);
            }
            if (started) MFShutdown();
        }
        return null;
    }

    private bool InitializeFromActivate(nint candidateActivate, out string? diagnostic)
    {
        diagnostic = null;
        activate = candidateActivate;

        var guidTransform = IidImfTransform;
        nint mftPtr = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(activate, 33))(activate, &guidTransform, &mftPtr);
        if (hr < 0 || mftPtr == 0)
        {
            diagnostic = $"IMFActivate.ActivateObject failed: 0x{hr:X8}";
            return false;
        }
        transform = mftPtr;
        activated = true;

        nint attrPtr = 0;
        hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(transform, 8))(transform, &attrPtr);
        if (hr >= 0 && attrPtr != 0)
        {
            try
            {
                if (GetUInt32Attr(attrPtr, MfTransformAsync, out uint asyncFlag) && asyncFlag != 0)
                {
                    isAsync = true;
                    SetUInt32Attr(attrPtr, MfTransformAsyncUnlock, 1);
                }
                SetUInt32Attr(attrPtr, MfLowLatency, 1);

                if (IsHardware && GetUInt32Attr(attrPtr, MfSaD3d11Aware, out uint d3dAware) && d3dAware != 0)
                {
                    TryAttachD3d11Manager();
                }
            }
            finally
            {
                ReleaseCom(ref attrPtr);
            }
        }

        if (isAsync)
        {
            var guidEg = IidImfMediaEventGenerator;
            hr = QueryCom(transform, &guidEg, out eventGen);
            if (hr < 0 || eventGen == 0)
            {
                diagnostic = $"Async MFT missing IMFMediaEventGenerator: 0x{hr:X8}";
                return false;
            }
        }

        uint inCount = 0, outCount = 0;
        if (((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(transform, 4))(transform, &inCount, &outCount) >= 0 && inCount > 0 && outCount > 0)
        {
            uint* inIds = stackalloc uint[(int)Math.Min(inCount, 16)];
            uint* outIds = stackalloc uint[(int)Math.Min(outCount, 16)];
            hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint*, uint, uint*, int>)Slot(transform, 5))(
                transform, Math.Min(inCount, 16), inIds, Math.Min(outCount, 16), outIds);
            if (hr >= 0) { inputStreamId = inIds[0]; outputStreamId = outIds[0]; }
            else { inputStreamId = 0; outputStreamId = 0; }
        }

        var guidCodec = IidICodecApi;
        QueryCom(transform, &guidCodec, out codecApi);

        int bitrate = (int)Math.Clamp((long)Width * Height * 6L, 4_000_000L, 30_000_000L);
        ConfigureCodecApiPreType(bitrate);

        if (!ConfigureOutputAndInputTypes(bitrate, out diagnostic))
            return false;

        useD3dInput = dxgiManager != 0 && InitializeProcessor();
        ConfigureCodecApiPostType(bitrate);
        ExtractSequenceHeaderFromOutputType();

        MftOutputStreamInfo outInfo = default;
        hr = ((delegate* unmanaged[Stdcall]<nint, uint, MftOutputStreamInfo*, int>)Slot(transform, 7))(transform, outputStreamId, &outInfo);
        if (hr < 0)
        {
            diagnostic = $"GetOutputStreamInfo failed: 0x{hr:X8}";
            return false;
        }

        outputProvidesSamples = (outInfo.Flags & (MftOutputStreamProvidesSamples | MftOutputStreamCanProvideSamples)) != 0;
        outputBufferSize = Math.Max(outInfo.Size, (uint)Math.Max(262_144, Width * Height));

        hr = ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(transform, 23))(transform, MftMessageNotifyBeginStreaming, 0);
        if (hr < 0) { diagnostic = $"BeginStreaming failed: 0x{hr:X8}"; return false; }

        hr = ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(transform, 23))(transform, MftMessageNotifyStartOfStream, 0);
        if (hr < 0) { diagnostic = $"StartOfStream failed: 0x{hr:X8}"; return false; }

        return true;
    }

    private void TryAttachD3d11Manager()
    {
        nint dev = 0, ctx = 0, mgr = 0, mt = 0, adapter = 0;
        try
        {
            uint flags = D3d11CreateDeviceVideoSupport | D3d11CreateDeviceBgraSupport;
            if (requestedAdapter is long luid && (adapter = FindAdapter(luid)) == 0) { processorStatus = "AdapterUnavailable"; return; }
            int hr = D3D11CreateDevice(adapter, adapter == 0 ? D3dDriverTypeHardware : 0, 0, flags, 0, 0, D3d11SdkVersion, &dev, 0, &ctx);
            if (hr < 0 || dev == 0) { processorStatus = $"CreateDevice-{hr:X8}"; return; }

            var guidMt = IidID3d10Multithread;
            if (QueryCom(dev, &guidMt, out mt) >= 0 && mt != 0)
            {
                ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(mt, 5))(mt, 1);
                ReleaseCom(ref mt);
            }

            hr = MFCreateDXGIDeviceManager(out uint resetToken, &mgr);
            if (hr < 0 || mgr == 0) { processorStatus = $"CreateManager-{hr:X8}"; return; }

            hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, int>)Slot(mgr, 7))(mgr, dev, resetToken);
            if (hr < 0) { processorStatus = $"ResetManager-{hr:X8}"; return; }

            hr = ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(transform, 23))(transform, MftMessageSetD3dManager, (nuint)mgr);
            if (hr < 0) { processorStatus = $"AttachManager-{hr:X8}"; return; }

            d3dDevice = dev; dev = 0;
            d3dContext = ctx; ctx = 0;
            dxgiManager = mgr; mgr = 0;
        }
        finally
        {
            ReleaseCom(ref mt);
            ReleaseCom(ref mgr);
            ReleaseCom(ref ctx);
            ReleaseCom(ref dev);
            ReleaseCom(ref adapter);
        }
    }

    private void ConfigureCodecApiPreType(int bitrate)
    {
        if (codecApi == 0) return;
        SetCodecBool(codecApi, CodecApiLowLatency, true);
        SetCodecBool(codecApi, CodecApiRealTime, true);
        SetCodecUInt32(codecApi, CodecApiScenarioInfo, AvScenarioDisplayRemoting);
        SetCodecUInt32(codecApi, CodecApiDefaultBPictureCount, 0);
        if (SetCodecUInt32(codecApi, CodecApiRateControlMode, AvEncRateControlPeakConstrainedVbr) < 0 &&
            SetCodecUInt32(codecApi, CodecApiRateControlMode, AvEncRateControlCbr) < 0)
        {
            SetCodecUInt32(codecApi, CodecApiRateControlMode, AvEncRateControlLowDelayVbr);
        }
        SetCodecUInt32(codecApi, CodecApiMeanBitRate, (uint)bitrate);
        SetCodecUInt32(codecApi, CodecApiMaxBitRate, (uint)(bitrate * 3L / 2L));
        SetCodecUInt32(codecApi, CodecApiGopSize, 120);
    }

    private void ConfigureCodecApiPostType(int bitrate)
    {
        if (codecApi == 0) return;
        SetCodecBool(codecApi, CodecApiLowLatency, true);
        SetCodecUInt32(codecApi, CodecApiDefaultBPictureCount, 0);
        SetCodecUInt32(codecApi, CodecApiMeanBitRate, (uint)bitrate);
        SetCodecUInt32(codecApi, CodecApiGopSize, 120);
    }

    private bool ConfigureOutputAndInputTypes(int bitrate, out string? diagnostic)
    {
        diagnostic = null;
        nint outType = 0;
        nint inType = 0;
        try
        {
            int hr = MFCreateMediaType(&outType);
            if (hr < 0 || outType == 0) { diagnostic = $"MFCreateMediaType(out) failed: 0x{hr:X8}"; return false; }

            SetGuidAttr(outType, MfMtMajorType, MfMediaTypeVideo);
            SetGuidAttr(outType, MfMtSubtype, MfVideoFormatH264);
            SetUInt64Attr(outType, MfMtFrameSize, ((ulong)(uint)Width << 32) | (uint)Height);
            SetUInt64Attr(outType, MfMtFrameRate, (60UL << 32) | 1UL);
            SetUInt64Attr(outType, MfMtPixelAspectRatio, (1UL << 32) | 1UL);
            SetUInt32Attr(outType, MfMtInterlaceMode, MfVideoInterlaceProgressive);
            SetUInt32Attr(outType, MfMtAvgBitrate, (uint)bitrate);
            SetUInt32Attr(outType, MfMtMpeg2Profile, AvEncH264ProfileMain);

            hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 16))(transform, outputStreamId, outType, 0);
            if (hr < 0)
            {
                SetUInt32Attr(outType, MfMtMpeg2Profile, AvEncH264ProfileBase);
                hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 16))(transform, outputStreamId, outType, 0);
                if (hr < 0)
                {
                    diagnostic = $"SetOutputType failed: 0x{hr:X8}";
                    return false;
                }
            }

            bool inputSet = false;
            for (uint typeIndex = 0; ; typeIndex++)
            {
                nint avail = 0;
                hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(transform, 13))(transform, inputStreamId, typeIndex, &avail);
                if (hr == MfENoMoreTypes || hr < 0 || avail == 0) break;
                try
                {
                    if (GetGuidAttr(avail, MfMtSubtype, out var sub) && sub == MfVideoFormatNv12)
                    {
                        SetUInt64Attr(avail, MfMtFrameSize, ((ulong)(uint)Width << 32) | (uint)Height);
                        SetUInt64Attr(avail, MfMtFrameRate, (60UL << 32) | 1UL);
                        SetUInt64Attr(avail, MfMtPixelAspectRatio, (1UL << 32) | 1UL);
                        SetUInt32Attr(avail, MfMtInterlaceMode, MfVideoInterlaceProgressive);
                        SetUInt32Attr(avail, MfMtDefaultStride, (uint)Width);
                        SetUInt32Attr(avail, MfMtYuvMatrix, MfVideoTransferMatrixBt709);
                        SetUInt32Attr(avail, MfMtVideoNominalRange, MfNominalRange16To235);
                        hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 15))(transform, inputStreamId, avail, 0);
                        if (hr >= 0) { inputSet = true; break; }
                    }
                }
                finally { ReleaseCom(ref avail); }
            }

            if (!inputSet)
            {
                hr = MFCreateMediaType(&inType);
                if (hr < 0 || inType == 0) { diagnostic = $"MFCreateMediaType(in) failed: 0x{hr:X8}"; return false; }

                SetGuidAttr(inType, MfMtMajorType, MfMediaTypeVideo);
                SetGuidAttr(inType, MfMtSubtype, MfVideoFormatNv12);
                SetUInt64Attr(inType, MfMtFrameSize, ((ulong)(uint)Width << 32) | (uint)Height);
                SetUInt64Attr(inType, MfMtFrameRate, (60UL << 32) | 1UL);
                SetUInt64Attr(inType, MfMtPixelAspectRatio, (1UL << 32) | 1UL);
                SetUInt32Attr(inType, MfMtInterlaceMode, MfVideoInterlaceProgressive);
                SetUInt32Attr(inType, MfMtDefaultStride, (uint)Width);
                SetUInt32Attr(inType, MfMtYuvMatrix, MfVideoTransferMatrixBt709);
                SetUInt32Attr(inType, MfMtVideoNominalRange, MfNominalRange16To235);
                hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 15))(transform, inputStreamId, inType, 0);
                if (hr < 0)
                {
                    diagnostic = $"SetInputType(NV12) failed: 0x{hr:X8}";
                    return false;
                }
            }

            return true;
        }
        finally
        {
            ReleaseCom(ref inType);
            ReleaseCom(ref outType);
        }
    }

    private void ExtractSequenceHeaderFromOutputType()
    {
        nint currType = 0;
        try
        {
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(transform, 18))(transform, outputStreamId, &currType);
            if (hr < 0 || currType == 0) return;
            var seqKey = MfMtMpegSequenceHeader;
            uint blobSize = 0;
            if (((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(currType, 14))(currType, &seqKey, &blobSize) >= 0 && blobSize > 0 && blobSize <= 4096)
            {
                var buf = new byte[blobSize];
                fixed (byte* pBuf = buf)
                {
                    if (((delegate* unmanaged[Stdcall]<nint, Guid*, byte*, uint, uint*, int>)Slot(currType, 15))(currType, &seqKey, pBuf, blobSize, &blobSize) >= 0)
                        ReadParameterSets(buf.AsSpan(0, (int)blobSize), out _);
                }
            }
        }
        finally
        {
            ReleaseCom(ref currType);
        }
    }

    internal bool Encode(ReadOnlySpan<byte> bgra, int stride, long timestampUs, bool forceKeyFrame, out byte[] annexB, out bool key)
    {
        bool ok = EncodePacket(bgra, stride, timestampUs, forceKeyFrame, null, out var output, out key);
        using (output) annexB = output?.Data.ToArray() ?? [];
        return ok;
    }

    internal bool EncodePacket(ReadOnlySpan<byte> bgra, int srcStride, long timestampUs, bool forceKeyFrame, ConsoleGpuFrame? surface, out ConsolePacket? annexB, out bool isKeyFrame)
    {
        annexB = null;
        isKeyFrame = false;
        if (surface == null && (srcStride < Width * 4 || bgra.Length < (Height - 1L) * srcStride + Width * 4)) return false;

        lock (workerLock)
        {
            if (disposed || workerThread == null || transform == 0) return false;
            sharedInput = surface;
            if (Thread.CurrentThread == workerThread)
                return EncodeOnWorker(bgra, srcStride, timestampUs, forceKeyFrame, out annexB, out isKeyFrame);

            fixed (byte* pBgra = bgra)
            {
                pendingBgra = pBgra;
                pendingBgraLength = bgra.Length;
                pendingStride = srcStride;
                pendingTimestampUs = timestampUs;
                pendingForceKeyFrame = forceKeyFrame;
                pendingCommand = 1;
                workDone.Reset();
                workSignal.Set();
                workDone.Wait();
                sharedInput = null;
                pendingBgra = null;
                pendingBgraLength = 0;
                annexB = pendingOutput;
                isKeyFrame = pendingIsKeyFrame;
                pendingOutput = null;
                return pendingEncodeResult;
            }
        }
    }

    private bool EncodeOnWorker(ReadOnlySpan<byte> bgra, int srcStride, long timestampUs, bool forceKeyFrame, out ConsolePacket? annexB, out bool isKeyFrame)
    {
        annexB = null;
        isKeyFrame = false;
        if (disposed || transform == 0) return false;

        try
        {
            nint sample;
            skippedGpuFrame = false;
            UsedVideoProcessor = false;
            ReleaseCom(ref inputBuffer);
            ReleaseCom(ref inputSample);
            if (useD3dInput && PrepareSurface(bgra, srcStride, out sample))
            {
                inputSample = sample;
                UsedVideoProcessor = true;
            }
            else
            {
                if (sharedInput != null) return skippedGpuFrame;
                useD3dInput = false;
                ConvertBgra32ToNv12(bgra, srcStride, nv12Scratch, Width, Height);
                if (!EnsureInputSample(out sample) || !CopyNv12ToMediaBuffer(nv12Scratch)) return false;
            }

            ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 36))(sample, timestampUs * 10L);
            ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 38))(sample, 166_667L);

            if (forceKeyFrame && codecApi != 0)
            {
                SetCodecUInt32(codecApi, CodecApiForceKeyFrame, 1);
            }

            return isAsync
                ? EncodeAsync(sample, out annexB, out isKeyFrame)
                : EncodeSync(sample, out annexB, out isKeyFrame);
        }
        catch
        {
            return false;
        }
        finally { ReleaseCom(ref inputSample); ReleaseCom(ref inputBuffer); }
    }

    private bool EnsureInputSample(out nint sample)
    {
        if (inputSample != 0) { sample = inputSample; return true; }

        nint samp = 0, buf = 0;
        int hr = MFCreateSample(&samp);
        if (hr < 0 || samp == 0) { sample = 0; return false; }

        hr = MFCreateMemoryBuffer((uint)nv12Scratch.Length, &buf);
        if (hr < 0 || buf == 0)
        {
            ReleaseCom(ref samp);
            sample = 0;
            return false;
        }

        hr = ((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(samp, 42))(samp, buf);
        if (hr < 0)
        {
            ReleaseCom(ref buf);
            ReleaseCom(ref samp);
            sample = 0;
            return false;
        }

        inputBuffer = buf;
        inputSample = samp;
        sample = samp;
        return true;
    }

    private bool CopyNv12ToMediaBuffer(byte[] nv12)
    {
        byte* dst = null;
        int hr = ((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(inputBuffer, 3))(inputBuffer, &dst, null, null);
        if (hr < 0 || dst == null) return false;
        try
        {
            fixed (byte* src = nv12)
            {
                Buffer.MemoryCopy(src, dst, nv12.Length, nv12.Length);
            }
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, int>)Slot(inputBuffer, 4))(inputBuffer);
        }
        hr = ((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(inputBuffer, 6))(inputBuffer, (uint)nv12.Length);
        return hr >= 0;
    }

    private bool EncodeSync(nint sample, out ConsolePacket? annexB, out bool isKeyFrame)
    {
        annexB = null;
        isKeyFrame = false;

        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 24))(transform, inputStreamId, sample, 0);
        if (hr == MfENotAccepting)
        {
            DrainSyncOutputs(out var drained, out _);
            drained?.Dispose();
            hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 24))(transform, inputStreamId, sample, 0);
        }
        if (hr < 0) return false;

        return DrainSyncOutputs(out annexB, out isKeyFrame);
    }

    private bool DrainSyncOutputs(out ConsolePacket? annexB, out bool isKeyFrame)
    {
        annexB = null;
        isKeyFrame = false;
        using var combined = new ConsolePacket(4096);

        while (true)
        {
            if (!TryProcessOutputOnce(combined, ref isKeyFrame, out int hr))
            {
                if (hr == MfETransformNeedMoreInput) break;
                if (hr == MfETransformStreamChange)
                {
                    if (!HandleOutputStreamChange()) return false;
                    continue;
                }
                return false;
            }
        }

        if (combined.Length == 0) return true;
        annexB = FinalizeAnnexB(combined, ref isKeyFrame);
        return true;
    }

    private bool EncodeAsync(nint sample, out ConsolePacket? annexB, out bool isKeyFrame)
    {
        annexB = null;
        isKeyFrame = false;
        bool inputSubmitted = false;
        using var combined = new ConsolePacket(4096);

        long deadline = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < deadline)
        {
            if (!inputSubmitted && pendingNeedInput > 0)
            {
                pendingNeedInput--;
                int phr = ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 24))(transform, inputStreamId, sample, 0);
                if (phr < 0) return false;
                inputSubmitted = true;
            }

            nint evt = 0;
            uint flags = MfEventFlagNoWait;
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(eventGen, 3))(eventGen, flags, &evt);
            if (hr < 0 || evt == 0)
            {
                if (combined.Length > 0) break;
                if (hr != unchecked((int)0xc00d3e80)) return false; // MF_E_NO_EVENTS_AVAILABLE
                Thread.Sleep(1);
                continue;
            }

            try
            {
                uint met = 0;
                hr = ((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(evt, 33))(evt, &met);
                if (hr < 0) return false;

                if (met == MeTransformNeedInput)
                {
                    pendingNeedInput++;
                }
                else if (met == MeTransformHaveOutput)
                {
                    if (!TryProcessOutputOnce(combined, ref isKeyFrame, out int ohr))
                    {
                        if (ohr == MfETransformStreamChange)
                        {
                            if (!HandleOutputStreamChange()) return false;
                        }
                        else if (ohr != MfETransformNeedMoreInput)
                        {
                            return false;
                        }
                    }
                    if (inputSubmitted && combined.Length > 0)
                    {
                        // Drain any immediately ready queued events/outputs then return.
                        DrainPendingAsyncEvents(combined, ref isKeyFrame);
                        break;
                    }
                }
            }
            finally
            {
                ReleaseCom(ref evt);
            }
        }

        if (combined.Length == 0) return false;
        annexB = FinalizeAnnexB(combined, ref isKeyFrame);
        return true;
    }

    private void DrainPendingAsyncEvents(ConsolePacket combined, ref bool isKeyFrame)
    {
        for (int i = 0; i < 8; i++)
        {
            nint evt = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(eventGen, 3))(eventGen, MfEventFlagNoWait, &evt);
            if (hr < 0 || evt == 0) break;
            try
            {
                uint met = 0;
                if (((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(evt, 33))(evt, &met) < 0) break;
                if (met == MeTransformNeedInput) pendingNeedInput++;
                else if (met == MeTransformHaveOutput)
                {
                    if (!TryProcessOutputOnce(combined, ref isKeyFrame, out int ohr) && ohr == MfETransformStreamChange)
                        HandleOutputStreamChange();
                }
            }
            finally { ReleaseCom(ref evt); }
        }
    }

    private bool TryProcessOutputOnce(ConsolePacket combined, ref bool isKeyFrame, out int hr)
    {
        nint providedSample = 0;
        if (!outputProvidesSamples)
        {
            if (outputSample == 0 && !AllocateReusableOutputSample())
            {
                hr = unchecked((int)0x8007000E);
                return false;
            }
            providedSample = outputSample;
        }

        var outBuffer = new MftOutputDataBuffer
        {
            StreamId = outputStreamId,
            Sample = providedSample,
            Status = 0,
            Events = 0
        };
        uint status = 0;
        hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, MftOutputDataBuffer*, uint*, int>)Slot(transform, 25))(
            transform, 0, 1, &outBuffer, &status);

        if (outBuffer.Events != 0) ReleaseCom(ref outBuffer.Events);

        nint produced = outBuffer.Sample;
        if (hr < 0)
        {
            if (outputProvidesSamples && produced != 0) ReleaseCom(ref produced);
            return false;
        }

        if (produced != 0)
        {
            try
            {
                if (GetUInt32Attr(produced, MfSampleExtensionCleanPoint, out uint cleanPoint) && cleanPoint != 0)
                    isKeyFrame = true;

                nint mediaBuf = 0;
                if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(produced, 41))(produced, &mediaBuf) >= 0 && mediaBuf != 0)
                {
                    try
                    {
                        byte* ptr = null;
                        uint curLen = 0;
                        if (((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(mediaBuf, 3))(mediaBuf, &ptr, null, &curLen) >= 0
                            && ptr != null && curLen > 0)
                        {
                            try { combined.Write(new ReadOnlySpan<byte>(ptr, (int)curLen)); }
                            finally { ((delegate* unmanaged[Stdcall]<nint, int>)Slot(mediaBuf, 4))(mediaBuf); }
                        }
                    }
                    finally { ReleaseCom(ref mediaBuf); }
                }
            }
            finally
            {
                if (outputProvidesSamples) ReleaseCom(ref produced);
            }
        }

        return true;
    }

    private bool AllocateReusableOutputSample()
    {
        nint samp = 0, buf = 0;
        if (MFCreateSample(&samp) < 0 || samp == 0) return false;
        if (MFCreateMemoryBuffer(outputBufferSize, &buf) < 0 || buf == 0) { ReleaseCom(ref samp); return false; }
        if (((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(samp, 42))(samp, buf) < 0)
        {
            ReleaseCom(ref buf);
            ReleaseCom(ref samp);
            return false;
        }
        ReleaseCom(ref buf); // Sample holds its own ref.
        outputSample = samp;
        return true;
    }

    private bool HandleOutputStreamChange()
    {
        nint newType = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(transform, 14))(transform, outputStreamId, 0, &newType);
        if (hr < 0 || newType == 0) return false;
        try
        {
            hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 16))(transform, outputStreamId, newType, 0);
            if (hr < 0) return false;
            ExtractSequenceHeaderFromOutputType();
            MftOutputStreamInfo outInfo = default;
            if (((delegate* unmanaged[Stdcall]<nint, uint, MftOutputStreamInfo*, int>)Slot(transform, 7))(transform, outputStreamId, &outInfo) >= 0)
            {
                outputProvidesSamples = (outInfo.Flags & (MftOutputStreamProvidesSamples | MftOutputStreamCanProvideSamples)) != 0;
                outputBufferSize = Math.Max(outInfo.Size, outputBufferSize);
                ReleaseCom(ref outputSample);
            }
            return true;
        }
        finally { ReleaseCom(ref newType); }
    }

    private ConsolePacket FinalizeAnnexB(ConsolePacket raw, ref bool isKeyFrame)
    {
        bool preceding = ReadParameterSets(raw.Bytes, out bool hasIdr);
        isKeyFrame = hasIdr && sps.Length > 0 && pps.Length > 0;
        if (!isKeyFrame || preceding) return raw.Retain();
        var combined = new ConsolePacket(checked(sps.Length + pps.Length + (int)raw.Length));
        combined.Write(sps);
        combined.Write(pps);
        combined.Write(raw.Bytes);
        return combined;
    }

    private bool ReadParameterSets(ReadOnlySpan<byte> data, out bool hasIdr)
    {
        hasIdr = false;
        bool hasSps = false, hasPps = false;
        for (int i = 0; i < data.Length;)
        {
            int prefix = MatchStartCode(data, i);
            if (prefix == 0) { i++; continue; }
            int start = i + prefix;
            if (start == data.Length) break;
            int next = FindNextStartCode(data, start + 1);
            var nal = data[i..next];
            switch (data[start] & 31)
            {
                case 5: hasIdr = true; break;
                case 7 when nal.Length <= 65536:
                    hasSps |= !hasIdr;
                    if (!nal.SequenceEqual(sps)) sps = nal.ToArray();
                    break;
                case 8 when nal.Length <= 65536:
                    hasPps |= !hasIdr;
                    if (!nal.SequenceEqual(pps)) pps = nal.ToArray();
                    break;
            }
            i = next;
        }
        return hasSps && hasPps;
    }

    private static int MatchStartCode(ReadOnlySpan<byte> data, int index)
    {
        if (index + 3 <= data.Length && data[index] == 0 && data[index + 1] == 0)
        {
            if (data[index + 2] == 1) return 3;
            if (index + 4 <= data.Length && data[index + 2] == 0 && data[index + 3] == 1) return 4;
        }
        return 0;
    }

    private static int FindNextStartCode(ReadOnlySpan<byte> data, int start)
    {
        for (int i = start; i + 2 < data.Length; i++)
        {
            if (data[i] == 0 && data[i + 1] == 0 && (data[i + 2] == 1 || (i + 3 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)))
                return i;
        }
        return data.Length;
    }

    private static uint Luma(Vector128<uint> r, Vector128<uint> g, Vector128<uint> b)
    {
        var values = ((r * 47u + g * 157u + b * 16u + Vector128.Create(128u)) >> 8) + Vector128.Create(16u);
        var shorts = Vector128.Narrow(values, Vector128<uint>.Zero);
        return Vector128.Narrow(shorts, Vector128<ushort>.Zero).AsUInt32()[0];
    }

    private static Vector128<int> ChromaAverage(Vector128<uint> sum) =>
        ((sum + Vector128.Shuffle(sum, Vector128.Create(1u, 0u, 3u, 2u)) + Vector128.Create(2u)) >> 2).AsInt32();

    internal static void ConvertBgra32ToNv12(ReadOnlySpan<byte> bgra, int srcStride, Span<byte> nv12, int width, int height)
    {
        int yPlaneSize = width * height;
        fixed (byte* srcBase = bgra, dstBase = nv12)
        {
            byte* yDst = dstBase;
            byte* uvDst = dstBase + yPlaneSize;

            for (int y = 0; y < height; y += 2)
            {
                byte* row0 = srcBase + y * srcStride;
                byte* row1 = row0 + srcStride;
                byte* yRow0 = yDst + y * width;
                byte* yRow1 = yRow0 + width;
                byte* uvRow = uvDst + (y >> 1) * width;

                int x = 0;
                if (Vector128.IsHardwareAccelerated)
                {
                    for (; x <= width - 4; x += 4)
                    {
                        var top = Unsafe.ReadUnaligned<Vector128<uint>>(row0 + x * 4);
                        var bottom = Unsafe.ReadUnaligned<Vector128<uint>>(row1 + x * 4);
                        var mask = Vector128.Create(255u);
                        var rt = (top >> 16) & mask; var gt = (top >> 8) & mask; var bt = top & mask;
                        var rb = (bottom >> 16) & mask; var gb = (bottom >> 8) & mask; var bb = bottom & mask;
                        Unsafe.WriteUnaligned(yRow0 + x, Luma(rt, gt, bt));
                        Unsafe.WriteUnaligned(yRow1 + x, Luma(rb, gb, bb));
                        var r = ChromaAverage(rt + rb); var g = ChromaAverage(gt + gb); var b = ChromaAverage(bt + bb);
                        var u = ((b * 112 - r * 26 - g * 86 + Vector128.Create(128)) >> 8) + Vector128.Create(128);
                        var v = ((r * 112 - g * 102 - b * 10 + Vector128.Create(128)) >> 8) + Vector128.Create(128);
                        uvRow[x] = (byte)u[0]; uvRow[x + 1] = (byte)v[0];
                        uvRow[x + 2] = (byte)u[2]; uvRow[x + 3] = (byte)v[2];
                    }
                }
                for (; x < width; x += 2)
                {
                    int off0 = x * 4;

                    int b00 = row0[off0], g00 = row0[off0 + 1], r00 = row0[off0 + 2];
                    int b01 = row0[off0 + 4], g01 = row0[off0 + 5], r01 = row0[off0 + 6];
                    int b10 = row1[off0], g10 = row1[off0 + 1], r10 = row1[off0 + 2];
                    int b11 = row1[off0 + 4], g11 = row1[off0 + 5], r11 = row1[off0 + 6];

                    // BT.709 studio range (Y: 16..235, UV: 16..240)
                    yRow0[x] = (byte)(((47 * r00 + 157 * g00 + 16 * b00 + 128) >> 8) + 16);
                    yRow0[x + 1] = (byte)(((47 * r01 + 157 * g01 + 16 * b01 + 128) >> 8) + 16);
                    yRow1[x] = (byte)(((47 * r10 + 157 * g10 + 16 * b10 + 128) >> 8) + 16);
                    yRow1[x + 1] = (byte)(((47 * r11 + 157 * g11 + 16 * b11 + 128) >> 8) + 16);

                    int rAvg = (r00 + r01 + r10 + r11 + 2) >> 2;
                    int gAvg = (g00 + g01 + g10 + g11 + 2) >> 2;
                    int bAvg = (b00 + b01 + b10 + b11 + 2) >> 2;

                    int u = ((-26 * rAvg - 86 * gAvg + 112 * bAvg + 128) >> 8) + 128;
                    int v = ((112 * rAvg - 102 * gAvg - 10 * bAvg + 128) >> 8) + 128;
                    uvRow[x] = (byte)Math.Clamp(u, 16, 240);
                    uvRow[x + 1] = (byte)Math.Clamp(v, 16, 240);
                }
            }
        }
    }

    public void Dispose()
    {
        Thread? threadToJoin = null;
        lock (workerLock)
        {
            if (disposed) return;
            disposed = true;
            if (workerThread != null && Thread.CurrentThread != workerThread)
            {
                pendingCommand = 2;
                workDone.Reset();
                workSignal.Set();
                workDone.Wait();
                threadToJoin = workerThread;
                workerThread = null;
            }
            else
            {
                DisposeCore();
            }
        }

        threadToJoin?.Join();
        workSignal.Dispose();
        workDone.Dispose();
    }

    private void DisposeCore()
    {
        if (transform != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(transform, 23))(transform, MftMessageCommandFlush, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(transform, 23))(transform, MftMessageNotifyEndStreaming, 0);
        }

        ReleaseCom(ref outputSample);
        ReleaseCom(ref inputBuffer);
        ReleaseCom(ref inputSample);
        ReleaseProcessor();
        ReleaseCom(ref codecApi);
        ReleaseCom(ref eventGen);
        ReleaseCom(ref transform);

        if (activate != 0)
        {
            if (activated)
            {
                activated = false;
                ((delegate* unmanaged[Stdcall]<nint, int>)Slot(activate, 34))(activate); // ShutdownObject
            }
            ReleaseCom(ref activate);
        }

        ReleaseCom(ref dxgiManager);
        ReleaseCom(ref d3dContext);
        ReleaseCom(ref d3dDevice);

        if (mfStarted)
        {
            mfStarted = false;
            MFShutdown();
        }
    }

    private static nint Slot(nint comPtr, int index) => (*(nint**)comPtr)[index];

    private static int QueryCom(nint comPtr, Guid* iid, out nint result)
    {
        nint obj = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(comPtr, 0))(comPtr, iid, &obj);
        result = hr >= 0 ? obj : 0;
        return hr;
    }

    private static void ReleaseCom(ref nint comPtr)
    {
        if (comPtr == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(comPtr, 2))(comPtr);
        comPtr = 0;
    }

    private static bool GetUInt32Attr(nint attr, Guid key, out uint value)
    {
        uint v = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(attr, 7))(attr, &key, &v);
        value = v;
        return hr >= 0;
    }

    private static bool GetGuidAttr(nint attr, Guid key, out Guid value)
    {
        Guid v = default;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attr, 10))(attr, &key, &v);
        value = v;
        return hr >= 0;
    }

    private static int SetUInt32Attr(nint attr, Guid key, uint value) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(attr, 21))(attr, &key, value);

    private static int SetUInt64Attr(nint attr, Guid key, ulong value) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(attr, 22))(attr, &key, value);

    private static int SetGuidAttr(nint attr, Guid key, Guid value) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(attr, 24))(attr, &key, &value);

    private static int SetCodecUInt32(nint api, Guid key, uint value)
    {
        var variant = new ComVariant { Vt = VtUi4, UIntVal = value };
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, ComVariant*, int>)Slot(api, 9))(api, &key, &variant);
    }

    private static int SetCodecBool(nint api, Guid key, bool value)
    {
        var variant = new ComVariant { Vt = VtBool, BoolVal = value ? VariantTrue : (short)0 };
        return ((delegate* unmanaged[Stdcall]<nint, Guid*, ComVariant*, int>)Slot(api, 9))(api, &key, &variant);
    }

    private readonly ref struct ComScope
    {
        private readonly bool initialized;
        public ComScope()
        {
            int hr = CoInitializeEx(0, 0);
            initialized = hr >= 0;
            if (hr < 0 && hr != RpcEChangedMode)
                Marshal.ThrowExceptionForHR(hr);
        }
        public void Dispose()
        {
            if (initialized) CoUninitialize();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftRegisterTypeInfo
    {
        public Guid MajorType;
        public Guid Subtype;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputStreamInfo
    {
        public uint Flags;
        public uint Size;
        public uint Alignment;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MftOutputDataBuffer
    {
        public uint StreamId;
        public nint Sample;
        public uint Status;
        public nint Events;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3d11Texture2dDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public uint SampleCount;
        public uint SampleQuality;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct ComVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public uint UIntVal;
        [FieldOffset(8)] public short BoolVal;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint dwFlags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(Guid guidCategory, uint flags, MftRegisterTypeInfo* pInputType, MftRegisterTypeInfo* pOutputType, out nint* pppMFTActivate, out uint pcMFTActivate);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMediaType(nint* ppMFType);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateSample(nint* ppIMFSample);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateMemoryBuffer(uint cbMaxLength, nint* ppBuffer);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFCreateDXGIDeviceManager(out uint resetToken, nint* ppDeviceManager);


    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(nint pAdapter, uint driverType, nint software, uint flags, nint pFeatureLevels, uint featureLevels, uint sdkVersion, nint* ppDevice, nint pFeatureLevel, nint* ppImmediateContext);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(nint reserved, uint dwCoInit);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoTaskMemFree(nint pv);
}
