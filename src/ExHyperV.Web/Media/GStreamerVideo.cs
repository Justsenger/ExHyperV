using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Web;

internal sealed record VideoSettings(int MaxWidth = 1920, int MaxHeight = 1080, int Fps = 60,
    uint Bitrate = 8_000_000, int Encoder = 2);

internal readonly record struct MediaSignal(int Kind, int Value, string Text);

[StructLayout(LayoutKind.Sequential)]
internal struct MediaStatistics
{
    public ulong Pushed, Dropped, Encoded, EncodedBytes, ReceiverBitrate;
    public double EncodeMilliseconds;
    public uint Bitrate;
    public int Hardware, OutputWidth, OutputHeight;
}

internal sealed class GStreamerVideo : IDisposable, IAsyncDisposable
{
    private readonly MediaHandle handle;
    private Task? cleanup;
    private readonly byte[] signalBuffer = new byte[65537];
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    public GStreamerVideo(int width, int height, VideoSettings settings)
    {
        GStreamerRuntime.ThrowIfUnavailable();
        SourceWidth = width; SourceHeight = height;
        var error = new StringBuilder(2048);
        handle = MediaNative.Create(width, height, settings.MaxWidth, settings.MaxHeight,
            settings.Fps, settings.Bitrate, settings.Encoder, error, error.Capacity);
        if (handle.IsInvalid) { handle.Dispose(); throw new InvalidOperationException(error.ToString()); }
    }

    public unsafe bool Push(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        if (width != SourceWidth || height != SourceHeight || stride < width * 4 || (long)stride * height > pixels.Length)
            throw new InvalidDataException("Source dimensions changed or pixel buffer is invalid.");
        // A ConsoleFrame may use managed memory. Pin only for this bounded native copy;
        // never retain an unpinned pointer after the call returns.
        fixed (byte* pointer = pixels)
        {
            int result = MediaNative.Push(handle, pointer, width, height, stride);
            if (result < 0) throw new InvalidOperationException("The media pipeline rejected a frame.");
            return result == 1;
        }
    }

    public unsafe MediaSignal? Poll()
    {
        fixed (byte* pointer = signalBuffer)
        {
            if (MediaNative.Poll(handle, out int kind, out int value, pointer, signalBuffer.Length) == 0) return null;
            int length = Array.IndexOf(signalBuffer, (byte)0);
            return new(kind, value, Encoding.UTF8.GetString(signalBuffer, 0, length < 0 ? signalBuffer.Length : length));
        }
    }
    public void Answer(string sdp)
    {
        if (string.IsNullOrWhiteSpace(sdp) || Encoding.UTF8.GetByteCount(sdp) > 65536 || MediaNative.Answer(handle, sdp) == 0)
            throw new InvalidDataException("Invalid WebRTC answer.");
    }
    public void AddIce(int line, string candidate)
    {
        if (line is < 0 or > 8 || Encoding.UTF8.GetByteCount(candidate) > 4096 || MediaNative.AddIce(handle, line, candidate) == 0)
            throw new InvalidDataException("Invalid WebRTC ICE candidate.");
    }
    public MediaStatistics Statistics { get { MediaNative.Stats(handle, out var result); return result; } }
    public bool SetBitrate(uint bitrate) => MediaNative.SetBitrate(handle, bitrate) != 0;
    public bool RequestKeyFrame() => MediaNative.KeyFrame(handle) != 0;
    public void Dispose() => handle.Dispose();
    public async ValueTask DisposeAsync()
    {
        // A driver may block inside gst_element_set_state(NULL). Keep ownership of
        // the handle until native cleanup returns; never free its memory on timeout.
        cleanup ??= Task.Run(handle.Dispose);
        try { await cleanup.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            GStreamerRuntime.MarkUnavailable();
            throw new InvalidOperationException("视频编码组件未能完成释放，请重启 Web 服务后重连。虚拟机无需重启。");
        }
    }
}

internal static class GStreamerRuntime
{
    private static readonly object Gate = new();
    private static nint library, directoryCookie;
    private static bool initialized;
    private static Exception? failure;
    private static int unavailable;
    public static void MarkUnavailable() => Interlocked.Exchange(ref unavailable, 1);
    public static void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref unavailable) != 0)
            throw new InvalidOperationException("视频编码组件曾停止响应，请重启 Web 服务后重连。虚拟机无需重启。");
    }
    public static void Initialize(string runtimeRoot)
    {
        lock (Gate)
        {
            if (initialized) return;
            if (failure is not null) throw new InvalidOperationException("The media runtime failed to initialize; restart after repairing it.", failure);
            try
            {
            runtimeRoot = Path.GetFullPath(runtimeRoot);
            string bin = Path.Combine(runtimeRoot, "bin"), plugins = Path.Combine(runtimeRoot, "lib", "gstreamer-1.0");
            string bridge = Path.Combine(bin, "exhyperv-web-media.dll");
            if (!File.Exists(bridge) || !Directory.Exists(plugins))
                throw new FileNotFoundException("The Web media runtime is missing. Build or publish its GStreamer runtime first.", bridge);
            string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExHyperV", "Web");
            Directory.CreateDirectory(cache);
            Environment.SetEnvironmentVariable("GST_PLUGIN_SYSTEM_PATH_1_0", plugins);
            Environment.SetEnvironmentVariable("GST_PLUGIN_PATH_1_0", "");
            Environment.SetEnvironmentVariable("GST_REGISTRY_1_0", Path.Combine(cache, $"gstreamer-{RuntimeInformation.ProcessArchitecture}.bin"));
            Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
            directoryCookie = AddDllDirectory(bin);
            library = NativeLibrary.Load(bridge, typeof(GStreamerRuntime).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.UserDirectories | DllImportSearchPath.System32);
            NativeLibrary.SetDllImportResolver(typeof(GStreamerRuntime).Assembly,
                (name, _, _) => name == MediaNative.Library ? library : 0);
            var error = new StringBuilder(2048);
            if (MediaNative.Initialize(error, error.Capacity) == 0) throw new InvalidOperationException(error.ToString());
            initialized = true;
            }
            catch (Exception exception) { failure = exception; throw; }
        }
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint AddDllDirectory(string path);
}

internal sealed class MediaHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public MediaHandle() : base(true) { }
    protected override bool ReleaseHandle() { MediaNative.Destroy(handle); return true; }
}

internal static partial class MediaNative
{
    internal const string Library = "exhyperv-web-media";
    [DllImport(Library, EntryPoint = "exhv_media_init", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Initialize(StringBuilder error, int capacity);
    [DllImport(Library, EntryPoint = "exhv_media_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern MediaHandle Create(int width, int height, int maxWidth, int maxHeight, int fps,
        uint bitrate, int encoder, StringBuilder error, int capacity);
    [DllImport(Library, EntryPoint = "exhv_media_push", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int Push(MediaHandle handle, byte* data, int width, int height, int stride);
    [DllImport(Library, EntryPoint = "exhv_media_poll", CallingConvention = CallingConvention.Cdecl)]
    internal static extern unsafe int Poll(MediaHandle handle, out int kind, out int value, byte* text, int capacity);
    [DllImport(Library, EntryPoint = "exhv_media_answer", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Answer(MediaHandle handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string sdp);
    [DllImport(Library, EntryPoint = "exhv_media_add_ice", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int AddIce(MediaHandle handle, int line, [MarshalAs(UnmanagedType.LPUTF8Str)] string candidate);
    [DllImport(Library, EntryPoint = "exhv_media_stats", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Stats(MediaHandle handle, out MediaStatistics statistics);
    [DllImport(Library, EntryPoint = "exhv_media_set_bitrate", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SetBitrate(MediaHandle handle, uint bitrate);
    [DllImport(Library, EntryPoint = "exhv_media_keyframe", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int KeyFrame(MediaHandle handle);
    [DllImport(Library, EntryPoint = "exhv_media_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(nint handle);
}
