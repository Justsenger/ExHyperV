using ExHyperV.Tools;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ExHyperV.Services;

internal sealed class FreeRdpNative(bool enhanced) : IConsoleNativeBackend
{
    // Successful code modules stay loaded for the process; every connection owns and frees its contexts.
    private static readonly object ApiGate = new();
    private static NativeApi? loadedApi;
    private static NativeApi GetApi(string? cacheRoot = null)
    {
        // Publish only a complete load. LoadApi releases failed modules and file locks;
        // a later connection may retry after a temporary extraction/loading failure.
        lock (ApiGate) return loadedApi ??= LoadApi(cacheRoot);
    }
    public IConsoleNativeConnection Create(ConsoleConnectionOptions options, Action<uint, byte[], uint, uint> callback, Action<ConsoleFrame> frameCallback)
        => new Connection(GetApi(), options, enhanced, callback, frameCallback);

    internal static uint RunSelfTest() => GetApi(AppContext.BaseDirectory).SelfTest();
    internal static uint RunGfxSelfTest() => GetApi(AppContext.BaseDirectory).SelfTestGfxD3D11?.Invoke()
        ?? throw new MissingMethodException("exhv_selftest_gfx_d3d11");

    private sealed class Connection : IConsoleNativeConnection
    {
        private readonly NativeApi api;
        private readonly Callback callback;
        private readonly object gpuGate = new();
        private GpuCallback? gpuCallback;
        private Action? gpuAvailable;
        private nint handle;
        internal Connection(NativeApi api, ConsoleConnectionOptions options, bool enhanced, Action<uint, byte[], uint, uint> receiver, Action<ConsoleFrame> frameReceiver)
        {
            this.api = api;
            callback = (kind, data, length, a, b, _) =>
            {
                try
                {
                    if (kind == 7)
                    {
                        DeliverFrame(api, data, length, a, b, frameReceiver);
                        return;
                    }
                    uint limit = kind == 9 ? (uint)ConsolePayload.MaxAudioPacketBytes
                        : kind == 11 ? 1024u * 1024u
                        : ConsolePayload.MaxClipboardBytes;
                    if (kind == 3 || length > limit || (length != 0 && data == 0)) return;
                    byte[] copy = length == 0 ? [] : new byte[checked((int)length)];
                    if (length != 0) Marshal.Copy(data, copy, 0, copy.Length);
                    receiver(kind, copy, a, b);
                }
                catch (Exception diagnosticError) { CoreDiagnostics.Report("NativeCallbackFailed", error: diagnosticError); }
            };
            uint modeFlags = 0u;
            if (enhanced)
            {
                modeFlags |= 1u;
                if (!options.RedirectClipboard) modeFlags |= 2u;
                if (!options.RedirectAudioOutput) modeFlags |= 4u;
                if (!options.RedirectAudioInput) modeFlags |= 8u;
            }
            handle = api.Create(options.VmId.ToString("D"), modeFlags,
                (uint)options.Width, (uint)options.Height, options.DesktopScale,
                (uint)Math.Ceiling(options.ConnectTimeout.TotalMilliseconds), callback, 0);
            if (handle == 0) throw new InvalidOperationException("FreeRDP could not initialize the console connection.");
        }
        public uint Run() => api.Run(handle);
        public void Stop() => api.Stop(handle);
        public ConsoleFrame? CaptureFrame()
        {
            nint descriptor = api.CaptureFrame(handle);
            if (descriptor == 0) return null;
            try
            {
                var info = Marshal.PtrToStructure<FrameDescriptor>(descriptor);
                ConsoleFrame? result = null;
                DeliverFrame(api, descriptor, info.Size, info.Width, info.Height, frame => result = frame.Retain());
                return result;
            }
            finally { api.FrameRelease(descriptor); }
        }
        public unsafe bool Send(uint kind, ReadOnlySpan<byte> payload)
        { fixed (byte* data = payload) return api.Send(handle, kind, (nint)data, (uint)payload.Length) != 0; }
        public bool SupportsGpu
        {
            get
            {
                nint h = Volatile.Read(ref handle);
                return h != 0 && api.IsGfxD3D11 is { } probe && probe(h) != 0;
            }
        }
        public bool OpenGpu(Action available)
        {
            lock (gpuGate)
            {
                nint h = Volatile.Read(ref handle);
                if (h == 0 || api.GpuOpen is null || api.IsGfxD3D11 is null || api.IsGfxD3D11(h) == 0) return false;
                Volatile.Write(ref gpuAvailable, available);
                gpuCallback ??= _ =>
                {
                    var listener = Volatile.Read(ref gpuAvailable);
                    if (listener is null) return;
                    try { listener(); }
                    catch (Exception diagnosticError) { CoreDiagnostics.Report("NativeGpuCallbackFailed", error: diagnosticError); }
                };
                return api.GpuOpen(h, gpuCallback, 0) != 0;
            }
        }
        public void CloseGpu()
        {
            lock (gpuGate)
            {
                Volatile.Write(ref gpuAvailable, null);
                nint h = Volatile.Read(ref handle);
                if (h != 0) api.GpuClose?.Invoke(h);
            }
        }
        public (long AdapterLuid, ulong Epoch, long Sequence, ulong SlotDims, SafeFileHandle Texture, ulong ReadKey, ulong ReturnKey)? TakeGpuLatest()
        {
            lock (gpuGate)
            {
                nint h = Volatile.Read(ref handle);
                if (h == 0 || api.GpuTakeLatest is null) return null;
                if (api.GpuTakeLatest(h, 1u, (uint)Marshal.SizeOf<NativeGpuFrameDescriptor>(), out var gf) == 0 || gf.TextureHandle == 0)
                    return null;
                var texHandle = new SafeFileHandle(gf.TextureHandle, ownsHandle: true);
                ulong slotDims = ((ulong)(ushort)gf.Slot) | ((ulong)gf.Width << 16) | ((ulong)gf.Height << 32);
                return (gf.AdapterLuid, gf.Epoch, checked((long)gf.Sequence), slotDims, texHandle, gf.ReadKey, gf.ReturnKey);
            }
        }
        public ulong RequestGpuCpu()
        {
            lock (gpuGate)
            {
                nint h = Volatile.Read(ref handle);
                return (h != 0 && api.GpuRequestCpu is not null) ? api.GpuRequestCpu(h) : 0;
            }
        }
        public void Dispose()
        {
            nint owned;
            lock (gpuGate)
            {
                Volatile.Write(ref gpuAvailable, null);
                owned = Interlocked.Exchange(ref handle, 0);
                if (owned != 0) api.GpuClose?.Invoke(owned);
            }
            if (owned != 0) api.Destroy(owned);
            GC.KeepAlive(callback);
            GC.KeepAlive(gpuCallback);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameDescriptor
    {
        internal uint Size, Width, Height, Stride, ByteLength, Flags;
        internal nint Pixels;
        internal ulong Sequence, BaseSequence;
        internal int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGpuFrameDescriptor
    {
        internal uint Size, Slot;
        internal long AdapterLuid;
        internal ulong Epoch, Sequence;
        internal uint Width, Height, Flags, Reserved;
        internal ulong ReadKey, ReturnKey;
        internal nint TextureHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RegionDescriptor
    {
        internal ConsoleFrameUpdateKind Kind;
        internal uint DirtyCount, MoveCount, Reserved;
        internal nint Dirty, Moves;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly ConsoleFrameRegion Region => new(Left, Top, checked(Right - Left), checked(Bottom - Top));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMove
    {
        internal int SourceX, SourceY;
        internal NativeRect Destination;
    }

    // SafeHandle pins each individual read even if a saved list outlives its frame.
    private sealed class RegionList<TNative, T>(NativeFrameHandle owner, nint address, int count, Func<TNative, T> convert)
        : IReadOnlyList<T> where TNative : unmanaged
    {
        public int Count => count;
        public unsafe T this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)count);
                bool held = false;
                try
                {
                    owner.DangerousAddRef(ref held);
                    return convert(((TNative*)address)[index]);
                }
                finally { if (held) owner.DangerousRelease(); }
            }
        }
        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < Count; i++) yield return this[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void DeliverFrame(NativeApi api, nint descriptor, uint length, uint width, uint height, Action<ConsoleFrame> receiver)
    {
        if (descriptor == 0 || length != 64) return;
        var frame = Marshal.PtrToStructure<FrameDescriptor>(descriptor);
        if (frame.Size != 64 || frame.Width != width || frame.Height != height || frame.Pixels == 0
            || width == 0 || height == 0 || width > 8192 || height > 8192 || frame.Stride != (ulong)width * 4
            || frame.ByteLength != (ulong)frame.Stride * height || frame.ByteLength > ConsolePayload.MaxFrameBytes
            || (frame.Flags & ~1u) != 0 || frame.Sequence == 0 || frame.Sequence > long.MaxValue || frame.BaseSequence >= frame.Sequence
            || frame.Left < 0 || frame.Top < 0 || frame.Right < frame.Left || frame.Bottom < frame.Top
            || frame.Right > width || frame.Bottom > height) return;
        if (api.FrameRetain(descriptor) == 0) return;
        NativeFrameHandle? owner = null;
        bool transferred = false;
        try
        {
            owner = new NativeFrameHandle(descriptor, api.FrameRelease);
            var envelope = new ConsoleFrameRegion(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
            using var lease = ReadFrame(api, owner, frame, envelope);
            if (lease == null) return;
            transferred = true;
            // Disjoint dirty rectangles can give the legacy reader a full-screen envelope.
            if (lease.Damage != envelope) return;
            receiver(lease);
        }
        finally
        {
            if (!transferred)
            {
                if (owner is not null) owner.Dispose();
                else api.FrameRelease(descriptor);
            }
        }
    }

    private static ConsoleFrame? ReadFrame(NativeApi api, NativeFrameHandle owner, FrameDescriptor frame, ConsoleFrameRegion envelope)
    {
        if (api.QueryRegions == null)
            return new((int)frame.Width, (int)frame.Height, (int)frame.Stride, frame.Pixels, (int)frame.ByteLength,
                owner, (long)frame.Sequence, (long)frame.BaseSequence, envelope, (frame.Flags & 1) != 0);

        if (api.QueryRegions(owner.DangerousGetHandle(), 1, (uint)Marshal.SizeOf<RegionDescriptor>(), out var regions) == 0
            || regions.Reserved != 0 || regions.DirtyCount > int.MaxValue / 16 || regions.MoveCount > int.MaxValue / 24
            || (regions.DirtyCount != 0 && regions.Dirty == 0) || (regions.MoveCount != 0 && regions.Moves == 0)) return null;

        IReadOnlyList<ConsoleFrameRegion> dirty = regions.DirtyCount == 0 ? [] :
            new RegionList<NativeRect, ConsoleFrameRegion>(owner, regions.Dirty, (int)regions.DirtyCount, static r => r.Region);
        IReadOnlyList<ConsoleFrameMove> moves = regions.MoveCount == 0 ? [] :
            new RegionList<NativeMove, ConsoleFrameMove>(owner, regions.Moves, (int)regions.MoveCount,
                static m => new(m.SourceX, m.SourceY, m.Destination.Region));
        return new((int)frame.Width, (int)frame.Height, (int)frame.Stride, frame.Pixels, (int)frame.ByteLength,
            owner, (long)frame.Sequence, (long)frame.BaseSequence, regions.Kind, dirty, moves);
    }

    internal static void RunFrameFixture(Action<ConsoleFrame> receiver)
    {
        var api = GetApi(AppContext.BaseDirectory);
        Exception? failure = null;
        var fixture = api.FrameFixture ?? throw new EntryPointNotFoundException("The native frame fixture is missing.");
        uint result = fixture((_, data, length, width, height, _) =>
        {
            try { DeliverFrame(api, data, length, width, height, receiver); }
            catch (Exception error) { failure ??= error; }
        }, 0);
        if (result != 0 || failure != null) throw new InvalidDataException($"Frame fixture failed ({result}).", failure);
    }

    private sealed class NativeFrameHandle : SafeHandle
    {
        private readonly StopDelegate release;
        internal NativeFrameHandle(nint handle, StopDelegate release) : base(0, true)
        { this.release = release; SetHandle(handle); }
        public override bool IsInvalid => handle == 0;
        protected override bool ReleaseHandle() { release(handle); return true; }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Callback(uint kind, nint data, uint length, uint a, uint b, nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CreateDelegate([MarshalAs(UnmanagedType.LPUTF8Str)] string vmId, uint enhanced, uint width, uint height, uint scale, uint timeout, Callback callback, nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint RunDelegate(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StopDelegate(nint handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SendDelegate(nint handle, uint kind, nint data, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate uint SelfTestDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FrameRetainDelegate(nint frame);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint CaptureFrameDelegate(nint connection);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int QueryRegionsDelegate(nint frame, uint version, uint size, out RegionDescriptor regions);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint FrameFixtureDelegate(Callback receiver, nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GpuCallback(nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GpuOpenDelegate(nint connection, GpuCallback? callback, nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void GpuCloseDelegate(nint connection);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GpuTakeLatestDelegate(nint connection, uint version, uint size, out NativeGpuFrameDescriptor descriptor);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ulong GpuRequestCpuDelegate(nint connection);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IsGfxD3D11Delegate(nint connection);

    private sealed class NativeApi(nint module, List<FileStream> locks)
    {
        // Keep the verified files open for the same lifetime as the loaded module, including its dependencies.
        private readonly List<FileStream> artifactLocks = locks;
        internal readonly CreateDelegate Create = Export<CreateDelegate>(module, "exhv_create");
        internal readonly RunDelegate Run = Export<RunDelegate>(module, "exhv_run");
        internal readonly StopDelegate Stop = Export<StopDelegate>(module, "exhv_stop");
        internal readonly StopDelegate Destroy = Export<StopDelegate>(module, "exhv_destroy");
        internal readonly SendDelegate Send = Export<SendDelegate>(module, "exhv_send");
        internal readonly SelfTestDelegate SelfTest = Export<SelfTestDelegate>(module, "exhv_selftest");
        internal readonly FrameRetainDelegate FrameRetain = Export<FrameRetainDelegate>(module, "exhv_frame_retain");
        internal readonly StopDelegate FrameRelease = Export<StopDelegate>(module, "exhv_frame_release");
        internal readonly CaptureFrameDelegate CaptureFrame = Export<CaptureFrameDelegate>(module, "exhv_capture_frame");
        internal readonly QueryRegionsDelegate? QueryRegions = Optional<QueryRegionsDelegate>(module, "exhv_frame_query_regions");
        internal readonly FrameFixtureDelegate? FrameFixture = Optional<FrameFixtureDelegate>(module, "exhv_selftest_frames");
        internal readonly GpuOpenDelegate? GpuOpen = Optional<GpuOpenDelegate>(module, "exhv_gpu_open");
        internal readonly GpuCloseDelegate? GpuClose = Optional<GpuCloseDelegate>(module, "exhv_gpu_close");
        internal readonly GpuTakeLatestDelegate? GpuTakeLatest = Optional<GpuTakeLatestDelegate>(module, "exhv_gpu_take_latest");
        internal readonly GpuRequestCpuDelegate? GpuRequestCpu = Optional<GpuRequestCpuDelegate>(module, "exhv_gpu_request_cpu");
        internal readonly IsGfxD3D11Delegate? IsGfxD3D11 = Optional<IsGfxD3D11Delegate>(module, "exhv_is_gfx_d3d11");
        internal readonly SelfTestDelegate? SelfTestGfxD3D11 = Optional<SelfTestDelegate>(module, "exhv_selftest_gfx_d3d11");
        private static T? Optional<T>(nint module, string name) where T : Delegate
            => NativeLibrary.TryGetExport(module, name, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;
        private static T Export<T>(nint module, string name) where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, name));
    }

    private static NativeApi LoadApi(string? cacheRoot)
    {
        string rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            Architecture.X64 => "win-x64",
            _ => throw new PlatformNotSupportedException("The FreeRDP console supports Windows ARM64 and x64.")
        };
        using var resource = OpenConsolePackage(rid);
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        string identity = Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
        string folder = Path.Combine(cacheRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExHyperV", "console-native", rid, identity);
        using var mutex = new Mutex(false, @"Local\ExHyperV.ConsoleNative." + identity);
        bool acquired = false;
        var locks = new List<FileStream>();
        nint module = 0;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("Timed out preparing the native console package.");
            Directory.CreateDirectory(folder);
            bytes.Position = 0;
            using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (entry.Name != entry.FullName || !names.Add(entry.Name) || entry.Length > 32 * 1024 * 1024
                    || !entry.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
                    throw new InvalidDataException("Invalid native console package entry.");
                string path = Path.Combine(folder, entry.Name);
                using var source = entry.Open();
                using var content = new MemoryStream();
                source.CopyTo(content);
                byte[] expected = SHA256.HashData(content.GetBuffer().AsSpan(0, checked((int)content.Length)));
                bool matches = false;
                if (File.Exists(path))
                {
                    using var existing = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    matches = CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(existing));
                }
                if (!matches)
                {
                    string temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
                    try
                    {
                        using (var output = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        { content.Position = 0; content.CopyTo(output); output.Flush(true); }
                        File.Move(temporary, path, overwrite: true);
                    }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                if (Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    locks.Add(locked);
                    if (!CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(locked)))
                        throw new InvalidDataException("Native console library verification failed.");
                }
            }
            if (!names.Contains("ExHyperV.FreeRdp.dll")) throw new InvalidDataException("The native console library is missing.");
            VerifyLoadedDependencies(locks);
            // Absolute directory search avoids changing the application's PATH or DLL search policy.
            module = LoadLibraryExW(Path.Combine(folder, "ExHyperV.FreeRdp.dll"), 0, 0x100 | 0x1000);
            if (module == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to load the FreeRDP console libraries.");
            VerifyLoadedDependencies(locks);
            return new NativeApi(module, locks);
        }
        catch
        {
            if (module != 0) NativeLibrary.Free(module);
            foreach (var file in locks) file.Dispose();
            throw;
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private static Stream OpenConsolePackage(string rid)
    {
        string name = $"ExHyperV.ConsoleNative.{rid}.zip";
        var core = typeof(FreeRdpNative).Assembly;
        if (core.GetManifestResourceStream(name + ".br") is { } packed)
            return new BrotliStream(packed, CompressionMode.Decompress);
        if (core.GetManifestResourceStream(name) is { } portable) return portable;
        throw new FileNotFoundException($"The {rid} native console package was not included in this build.");
    }

    private static void VerifyLoadedDependencies(IEnumerable<FileStream> files)
    {
        foreach (var file in files)
        {
            nint loaded = GetModuleHandleW(Path.GetFileName(file.Name));
            if (loaded == 0) continue;
            var path = new StringBuilder(32768);
            uint length = GetModuleFileNameW(loaded, path, (uint)path.Capacity);
            if (length == 0 || length >= path.Capacity || !Path.GetFullPath(path.ToString()).Equals(Path.GetFullPath(file.Name), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Another version of {Path.GetFileName(file.Name)} is already loaded in this process. The console requires its bundled native libraries.");
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadLibraryExW(string path, nint file, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(nint module, StringBuilder path, uint capacity);
}
