using System.Reflection;
using ExHyperV.Server.Hosting;
using ExHyperV.Services;

namespace ExHyperV.Server.Tests;

internal class FakeConsole : IConsoleConnection, IConsoleVideoSource, IConsoleInput, IConsoleClipboard,
    IConsoleAudioInput, IConsoleAudioOutput, IConsoleSessionFactory
{
    private static readonly ConstructorInfo FrameConstructor = typeof(ConsoleFrame).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(int), typeof(int), typeof(int), typeof(ReadOnlyMemory<byte>),
        typeof(long), typeof(long), typeof(ConsoleFrameRegion), typeof(bool), typeof(IDisposable)], null)!;
    private sealed class Owner(FakeConsole source) : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref source.LiveFrames);
    }

    internal int LiveFrames, Disconnections, Disposals, Created;
    internal Action? OnStart;
    internal Action<int, int>? OnResize;
    internal Func<ConsoleFrame>? Capture;
    internal ConsoleKeyEvent[]? KeyBatch;
    internal readonly List<string> Inputs = [];
    internal ConsoleClipboardData? ClipboardData;
    internal byte[]? Microphone;
    internal ConsoleConnectionOptions? Options;
    public Task<ConsoleVmPolicy> Inspect(Guid vm, CancellationToken token) => Task.FromResult(new ConsoleVmPolicy(ConsoleRights.All, ConsoleRights.All, true));
    public IConsoleConnection Create(ConsoleConnectionOptions options, string backend)
    { Created++; Options = options; BackendId = backend; return this; }
    public string BackendId { get; private set; } = ConsoleService.RdpBasicBackendId;
    public ConsoleConnectionState State { get; private set; }
    public IConsoleVideoSource Video => this;
    public IConsoleInput Input => this;
    public IConsoleClipboard Clipboard => this;
    public IConsoleAudioInput AudioInput => this;
    public IConsoleAudioOutput AudioOutput => this;
    public bool IsAvailable => true;
    public bool SupportsResize => true;
    private bool microphoneAvailable = true;
    bool IConsoleAudioInput.IsAvailable => microphoneAvailable;
    public ConsoleAudioFormat Format { get; set; } = new(48000, 2, ConsoleAudioSampleFormat.Signed16LittleEndian);
    ConsoleAudioFormat? IConsoleAudioInput.Format => microphoneAvailable ? Format : null;
    public event Action? Connected, LoginCompleted;
    public event Action<ConsoleDisconnectInfo>? Disconnected;
    public event Action<ConsoleFrame>? FrameReceived;
    public event Action<ConsoleCursor?>? CursorChanged;
    public event Action<uint>? ResizeRejected;
    public event Action<ConsoleClipboardData>? ClipboardReceived;
    public event Action<ConsoleAudioPacket>? AudioReceived;
    public event Action<bool>? AvailabilityChanged;
    private event Action<bool>? MicrophoneChanged;
    event Action<bool>? IConsoleAudioInput.AvailabilityChanged
    {
        add => MicrophoneChanged += value;
        remove => MicrophoneChanged -= value;
    }
    public event Action<bool>? ClipboardAvailabilityChanged;
    internal int FrameListeners => FrameReceived?.GetInvocationList().Length ?? 0;
    internal int CursorListeners => CursorChanged?.GetInvocationList().Length ?? 0;

    public void Start()
    {
        State = ConsoleConnectionState.Connected;
        Connected?.Invoke(); LoginCompleted?.Invoke();
        AvailabilityChanged?.Invoke(true); ClipboardAvailabilityChanged?.Invoke(true);
        OnStart?.Invoke();
    }
    internal void End() => Disconnected?.Invoke(new(77, "远端已关闭。"));
    public Task DisconnectAsync() { Disconnections++; State = ConsoleConnectionState.Disconnected; return Task.CompletedTask; }
    public void Dispose() => Disposals++;
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

    internal static byte[] Pixels => [3, 4, 5, 255, 20, 30, 40, 255, 99, 99, 99, 99,
                                      6, 7, 8, 255, 50, 60, 70, 255, 99, 99, 99, 99];
    private ConsoleFrame NewFrame(long sequence, bool full, long baseSequence) =>
        DesktopFrame(2, 2, 12, Pixels, sequence, baseSequence, new(1, 1, 1, 1), full);

    internal ConsoleFrame DesktopFrame(int width, int height, int stride, byte[] pixels, long sequence,
        long baseSequence, ConsoleFrameRegion damage, bool full)
    {
        var frame = (ConsoleFrame)FrameConstructor.Invoke([width, height, stride, new ReadOnlyMemory<byte>(pixels), sequence,
            baseSequence, damage, full, new Owner(this)]);
        Interlocked.Increment(ref LiveFrames);
        return frame;
    }
    internal void Publish(ConsoleFrame frame) => FrameReceived?.Invoke(frame);
    internal void PublishCursor(ConsoleCursor? cursor) => CursorChanged?.Invoke(cursor);
    internal void Emit(long sequence, bool full = true, long? baseSequence = null)
    {
        using var frame = NewFrame(sequence, full, baseSequence ?? sequence - 1);
        FrameReceived?.Invoke(frame);
    }
    public ConsoleFrame CaptureFrame() => Capture?.Invoke() ?? NewFrame(100, true, 99);
    private bool Log(string value) { Inputs.Add(value); return true; }
    public bool SendKey(ushort scanCode, bool extended, bool down) => Log($"key {scanCode} {extended} {down}");
    public bool SendKeys(IReadOnlyList<ConsoleKeyEvent> events)
    { KeyBatch = events.ToArray(); return Log($"keys {events.Count} {events[0].ScanCode}"); }
    public bool SendPause() => Log("pause");
    public bool ReleaseAllInputs() => Log("release");
    public bool SynchronizeKeyboard(ConsoleKeyboardToggleState flags) => Log($"sync {(uint)flags}");
    public bool SendPointer(ushort x, ushort y, ConsolePointerButton button, bool down, short wheel) => Log($"pointer {x} {y} {button} {down} {wheel}");
    public bool SendClick(ushort x, ushort y, ConsolePointerButton button) => Log($"click {x} {y} {button}");
    public bool Resize(int width, int height, uint scale)
    { if (OnResize != null) OnResize(width, height); else ResizeRejected?.Invoke(123); return Log($"resize {width} {height} {scale}"); }
    public bool SetClipboard(ConsoleClipboardData clipboard)
    { ClipboardData = clipboard; ClipboardReceived?.Invoke(clipboard); return true; }
    public bool SendAudio(ConsoleAudioFormat format, ReadOnlySpan<byte> samples) { Microphone = samples.ToArray(); return true; }
    internal void Sound() => AudioReceived?.Invoke(new(Format, [1, 2, 3, 4], 1, TimeSpan.FromTicks(123456)));
    internal void Publish(ConsoleAudioPacket packet) => AudioReceived?.Invoke(packet);
    internal void Publish(ConsoleClipboardData clipboard) => ClipboardReceived?.Invoke(clipboard);
    internal void SetMicrophoneAvailable(bool available)
    {
        microphoneAvailable = available;
        MicrophoneChanged?.Invoke(available);
    }
}
