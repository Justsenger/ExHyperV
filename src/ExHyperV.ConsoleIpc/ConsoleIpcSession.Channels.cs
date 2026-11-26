using ExHyperV.Services;

namespace ExHyperV.ConsoleIpc;

internal sealed partial class ConsoleIpcSession
{
    public IReadOnlyList<ConsoleDisplayMode> GetModes() => access.Modes;

    public ConsoleDisplayModeResult SetMode(string id)
    {
        ConsoleIpcChannel channel;
        ulong generation;
        lock (gate)
        {
            if (!CanWrite(ConsoleCapabilities.DisplayResize) || !access.Modes.Any(m => m.Id == id)) return new(false, null);
            channel = pipe!;
            generation = control.Generation;
        }
        using var r = channel.Call(IpcMessage.SetMode, session, w => { w.Write(generation); w.Text(id); }).GetAwaiter().GetResult();
        return new(r.Flag(), r.Flag() ? ConsoleIpcClient.ReadMode(r) : null);
    }

    private bool CanWrite(ConsoleCapabilities cap) => state == ConsoleConnectionState.Connected && !stopping &&
        !SessionGrant.IsCancellationRequested && control.HasControl && Has(cap);

    private bool InputMessage(IpcMessage type, Action<BinaryWriter>? payload = null)
    {
        lock (gate)
        {
            if (!CanWrite(ConsoleCapabilities.Input)) return false;
            ulong generation = control.Generation, sequence = checked(++inputSequence);
            return Queue(type, w => { w.Write(generation); w.Write(sequence); payload?.Invoke(w); });
        }
    }

    public bool SendKey(ushort scanCode, bool extended, bool down) => InputMessage(IpcMessage.InputKey,
        w => { w.Write(scanCode); w.Write(extended); w.Write(down); });
    public bool SendKeys(IReadOnlyList<ConsoleKeyEvent> events) => events.Count is > 0 and <= 1024 &&
        InputMessage(IpcMessage.InputKeys, w =>
        {
            w.Write(events.Count);
            foreach (var key in events) { w.Write(key.ScanCode); w.Write(key.Extended); w.Write(key.Down); }
        });
    public bool SendPause() => InputMessage(IpcMessage.InputPause);
    public bool ReleaseAllInputs() => InputMessage(IpcMessage.ReleaseAllInputs);
    public bool SynchronizeKeyboard(ConsoleKeyboardToggleState flags) => InputMessage(IpcMessage.InputSyncLocks, w => w.Write((uint)flags));
    public bool SendPointer(ushort x, ushort y, ConsolePointerButton button = ConsolePointerButton.None, bool down = false, short wheel = 0) =>
        InputMessage(IpcMessage.InputPointer, w => { w.Write(x); w.Write(y); w.Write((int)button); w.Write(down); w.Write(wheel); });
    public bool SendClick(ushort x, ushort y, ConsolePointerButton button = ConsolePointerButton.Left)
    {
        lock (gate) return SendPointer(x, y, button, true) && SendPointer(x, y, button, false);
    }

    public ConsoleClipboardProviderInfo ProviderInfo { get; private set; }
    bool IConsoleClipboard.IsAvailable => ProviderInfo.IsReady;
    public event Action<bool>? ClipboardAvailabilityChanged;
    public event Action<ConsoleClipboardData>? ClipboardReceived;
    public bool SetClipboard(ConsoleClipboardData clipboard)
    {
        lock (gate)
        {
            if (!CanWrite(ConsoleCapabilities.Clipboard) || !ProviderInfo.IsReady || !ProviderInfo.CanWrite ||
                clipboard.Formats.Count > 256 || clipboard.Formats.Sum(f => (long)f.Data.Length + 12 + ConsoleIpcWire.Utf8.GetByteCount(f.Name ?? "")) > ConsoleIpcWire.Limit - 64) return false;
            ulong generation = control.Generation;
            return Queue(IpcMessage.SetClipboard, w =>
            {
                w.Write(generation); w.Write(clipboard.Formats.Count);
                foreach (var f in clipboard.Formats) { w.Write(f.Id); w.Text(f.Name); w.Write(f.Data.Length); w.Write(f.Data.Span); }
            });
        }
    }

    private volatile bool inputAvailable, outputAvailable;
    public ConsoleAudioFormat? Format { get; private set; }
    bool IConsoleAudioInput.IsAvailable => inputAvailable;
    bool IConsoleAudioOutput.IsAvailable => outputAvailable;
    private event Action<bool>? OutputAvailability, InputAvailability;
    event Action<bool>? IConsoleAudioOutput.AvailabilityChanged { add => OutputAvailability += value; remove => OutputAvailability -= value; }
    event Action<bool>? IConsoleAudioInput.AvailabilityChanged { add => InputAvailability += value; remove => InputAvailability -= value; }
    public event Action<ConsoleAudioPacket>? AudioReceived;
    public bool SendAudio(ConsoleAudioFormat format, ReadOnlySpan<byte> samples)
    {
        lock (gate)
        {
            if (!CanWrite(ConsoleCapabilities.AudioInput) || !inputAvailable || format != Format || samples.Length > 1 << 20 ||
                samples.Length % (format.Channels * (format.SampleFormat == ConsoleAudioSampleFormat.Signed16LittleEndian ? 2 : 4)) != 0) return false;
            var data = samples.ToArray();
            ulong generation = control.Generation;
            return Queue(IpcMessage.AudioInput, w =>
            {
                w.Write(generation); w.Write(format.SampleRate); w.Write(format.Channels); w.Write((int)format.SampleFormat);
                w.Write(data.Length); w.Write(data);
            });
        }
    }

    private static ConsoleAudioFormat ReadFormat(IpcReader r)
    {
        var format = new ConsoleAudioFormat(r.ReadInt32(), r.ReadInt32(), (ConsoleAudioSampleFormat)r.ReadInt32());
        return format.SampleRate is > 0 and <= 384000 && format.Channels is > 0 and <= 32 && Enum.IsDefined(format.SampleFormat)
            ? format : throw new IpcFailure(IpcFailure.Incompatible);
    }

    private void ChannelPush(IpcMessage type, IpcReader r)
    {
        if (!control.CanView) return;
        switch (type)
        {
            case IpcMessage.CursorShape when Video != null:
                ConsoleCursor? cursor = null;
                if (r.Flag())
                {
                    int width = r.Count(512), height = r.Count(512), x = r.ReadInt32(), y = r.ReadInt32();
                    if (x < 0 || x >= width || y < 0 || y >= height) throw new IpcFailure(IpcFailure.Incompatible);
                    cursor = new(width, height, x, y, r.Bytes(width * height * 4));
                }
                Raise(CursorChanged, cursor);
                break;
            case IpcMessage.CursorState when Video != null:
                CursorState = new(r.ReadInt32(), r.ReadInt32(), r.Flag(), r.Flag(), (ConsoleCoordinateSpace)r.ReadInt32(), r.ReadUInt64());
                Raise(CursorStateChanged, CursorState);
                break;
            case IpcMessage.ClipboardAvailability when Clipboard != null:
                var info = new ConsoleClipboardProviderInfo(r.String(), r.Id(), r.String(), r.Flag(), r.Flag(), r.Flag());
                if (info.VmId != options.VmId || access.Guest != null && info.GuestSessionId != access.Guest)
                    throw new IpcFailure(IpcFailure.Denied);
                lock (gate) ProviderInfo = info;
                Raise(ClipboardAvailabilityChanged, info.IsReady);
                break;
            case IpcMessage.ClipboardData when Clipboard != null && ProviderInfo is { IsReady: true, CanRead: true }:
                var formats = new ConsoleClipboardFormat[r.Count(256)];
                for (int i = 0; i < formats.Length; i++) formats[i] = new(r.ReadUInt32(), r.Text(), r.Bytes(r.Count(ConsoleIpcWire.Limit)));
                Raise(ClipboardReceived, new(formats));
                break;
            case IpcMessage.AudioAvailability:
                bool input = r.Flag(), available = r.Flag();
                if (input)
                {
                    Format = available ? ReadFormat(r) : null;
                    inputAvailable = available && AudioInput != null;
                    Raise(InputAvailability, inputAvailable);
                }
                else
                {
                    outputAvailable = available && AudioOutput != null;
                    Raise(OutputAvailability, outputAvailable);
                }
                break;
            case IpcMessage.AudioOutput when outputAvailable:
                var format = ReadFormat(r);
                long sequence = r.ReadInt64(), ticks = r.ReadInt64();
                Raise(AudioReceived, new(format, r.Bytes(r.Count(1 << 20)), sequence, TimeSpan.FromTicks(ticks)));
                break;
            default: throw new IpcFailure(IpcFailure.Incompatible);
        }
    }
}
