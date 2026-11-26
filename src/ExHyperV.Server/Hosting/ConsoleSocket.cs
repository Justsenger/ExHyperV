using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using ExHyperV.Services;

namespace ExHyperV.Server.Hosting;

internal sealed class ConsoleSocket(SocketPeer peer, ConsoleSource source, ConsoleRequest request, SocketAuthorization grant)
{
    internal const int IdleLosslessCompensationMs = 300;
    private const int MaxVideoBytes = 65 * 1024 * 1024;
    private readonly object gate = new();
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(1);
    private readonly Queue<(byte[] Json, bool Terminal)> controls = [];
    private readonly Queue<ConsolePacket> sound = [];
    private const int MaxControlBytes = 48 * 1024 * 1024;
    private const int MaxAudioBytes = 8 * (ConsolePayload.MaxAudioPacketBytes + ConsoleWire.AudioHeader);
    private int controlBytes, audioBytes;
    private CancellationTokenRegistration cancelled;
    private ConsoleMediaGroup? group;
    private ConsolePacket? pending;
    private ConsoleCursor? pendingCursor;
    private ConsoleCursorState? pendingPosition;
    private bool cursorPending, stopped, needsKey = true;
    private long baseline = -1, displayed = -1, lastMode = -1000;
    private string activeEncoding = Choose(request.Encoding);
    private bool hostCursor => request.Options.HostCursor;
    private IConsoleConnection connection => source.Connection;
    internal IConsoleConnection Connection => connection;
    internal bool V2 => grant.Scoped;
    private readonly HashSet<(ushort Scan, bool Extended)> keysDown = [];
    private readonly HashSet<ConsolePointerButton> buttonsDown = [];
    private ushort lastX, lastY;

    // The source controller serializes both submission and release against handoff.
    internal bool ReleaseHeld()
    {
        if (connection.Input is not { } input) return true;
        bool released = true;
        foreach (var key in keysDown) released &= input.SendKey(key.Scan, key.Extended, false);
        foreach (var button in buttonsDown) released &= input.SendPointer(lastX, lastY, button, false, 0);
        released &= input.ReleaseAllInputs();
        if (released) { keysDown.Clear(); buttonsDown.Clear(); }
        return released;
    }

    private void Track(ConsoleKeyEvent key)
    {
        if (key.Down) keysDown.Add((key.ScanCode, key.Extended));
        else keysDown.Remove((key.ScanCode, key.Extended));
    }

    internal bool Has(ConsoleRights right) => !peer.Token.IsCancellationRequested && grant.Rights.HasFlag(right);
    internal bool NeedsKey { get { lock (gate) return needsKey; } }
    private static string Choose(string encoding) => encoding == "h264" && !ConsoleH264Encoder.IsAvailable ? "deflate" : encoding;
    private sealed record State(string StateName, uint ErrorCode = 0, string? Error = null);

    internal static async Task Run(SocketPeer peer, ConsoleRequest request, ConsoleSources sources, SocketAuthorization grant, ExtensionAccess? access)
    {
        try { await sources.Serve(peer, request, grant, access); }
        catch (Exception error) when (!peer.Token.IsCancellationRequested && error is not (InvalidDataException or WebSocketException))
        { await peer.Error(access == null ? error.Message : "扩展连接失败（EXT-002）"); }
    }

    internal void Attach()
    {
        cancelled = peer.Token.Register(ClearQueue);
        Control(new State("Connecting"));
        Capabilities(false);
        source.Controller.Join(this);
        Bind(true);
        group = source.Subscribe(this, activeEncoding);
        if (connection.State == ConsoleConnectionState.Connected) Connected();
        if (V2 && connection.Video is IConsoleCursorStateSource { CursorState: { } cursor }) CursorState(cursor);
    }

    internal async Task Detach()
    {
        cancelled.Dispose();
        ClearQueue();
        Bind(false);
        try { source.Controller.Leave(this); }
        finally { if (group != null) await source.Unsubscribe(this, group); }
    }

    private void ClearQueue()
    {
        lock (gate)
        {
            stopped = true; pending?.Dispose(); pending = null; pendingCursor = null; pendingPosition = null;
            while (sound.TryDequeue(out var packet)) packet.Dispose();
            while (controls.TryDequeue(out var item)) Array.Clear(item.Json);
            controlBytes = audioBytes = 0;
        }
    }

    internal async Task Run()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
        var send = Pump(stop.Token);
        var receive = peer.Receive(ReceiveCommand, ReceiveMicrophone);
        try { await await Task.WhenAny(send, receive); }
        finally
        {
            await stop.CancelAsync();
            if (!receive.IsCompleted && !peer.Token.IsCancellationRequested)
            {
                await peer.Close(WebSocketCloseStatus.NormalClosure, "控制台已断开。");
                try { await receive.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { peer.Stop(); }
            }
            if (!send.IsCompleted && receive.IsCompleted)
            {
                try { await send.WaitAsync(TimeSpan.FromSeconds(1)); }
                catch (TimeoutException) { peer.Stop(); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            try { await Task.WhenAll(receive, send); } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    internal void ControlState(bool holder, long generation)
    {
        if (V2) Control(new { type = "control", hasControl = holder, canTake = Has(ConsoleRights.Control), generation });
    }
    internal void SetGroup(ConsoleMediaGroup next) { lock (gate) group = next; }
    internal bool CanApply(ConsoleMediaGroup sender, long sequence, bool idle)
    {
        lock (gate) return group == sender && pending == null && baseline == sequence && (!idle || !V2 || displayed == sequence);
    }
    internal void Offer(ConsoleMediaGroup sender, ConsolePacket packet, long sequence, bool standalone, bool h264, bool idle = false)
    {
        lock (gate)
        {
            if (stopped || peer.Token.IsCancellationRequested || group != sender) return;
            if (idle && pending != null) return;
            if (pending != null || packet.Length > MaxVideoBytes || h264 && needsKey)
            {
                pending?.Dispose(); pending = null;
                needsKey = true; baseline = -1;
                if (!standalone || packet.Length > MaxVideoBytes) return;
            }
            pending = packet.Retain();
            baseline = sequence;
            if (h264) needsKey = false;
            wake.Writer.TryWrite(true);
        }
    }
    internal void Transport(ConsoleMediaGroup sender, string transportKind, ulong surfaceEpoch)
    {
        if (V2 && group == sender) Control(new { type = "transport", transportKind, surfaceEpoch });
    }
    internal void MediaFailed() => peer.Stop();
    internal void EncodingChanged(ConsoleMediaGroup sender, string encoding)
    {
        lock (gate)
        {
            if (group != sender) return;
            activeEncoding = encoding;
            Control(new { type = "encodingChanged", encoding, reason = "encoder_fallback" });
        }
    }
    private void CursorState(ConsoleCursorState state)
    {
        lock (gate) { if (stopped) return; pendingPosition = state; wake.Writer.TryWrite(true); }
    }
    private void Cursor(ConsoleCursor? cursor)
    {
        lock (gate) { if (stopped) return; pendingCursor = cursor; cursorPending = true; wake.Writer.TryWrite(true); }
    }
    private void Bind(bool add)
    {
        if (add)
        {
            connection.Connected += Connected;
            connection.LoginCompleted += Login;
            connection.Disconnected += Disconnected;
        }
        else
        {
            connection.Connected -= Connected;
            connection.LoginCompleted -= Login;
            connection.Disconnected -= Disconnected;
        }
        if (connection.Video is { } video)
        {
            if (hostCursor) source.ObserveCursor(Cursor, add);
            if (add) video.ResizeRejected += ResizeRejected; else video.ResizeRejected -= ResizeRejected;
        }
        if (V2 && connection.Video is IConsoleCursorStateSource positions)
        {
            if (add) positions.CursorStateChanged += CursorState;
            else positions.CursorStateChanged -= CursorState;
        }
        if (grant.Rights.HasFlag(ConsoleRights.Clipboard) && connection.Clipboard is { } clipboard)
        {
            if (add) { clipboard.ClipboardReceived += Clipboard; clipboard.ClipboardAvailabilityChanged += Capabilities; }
            else { clipboard.ClipboardReceived -= Clipboard; clipboard.ClipboardAvailabilityChanged -= Capabilities; }
        }
        if (grant.Rights.HasFlag(ConsoleRights.AudioOutput) && connection.AudioOutput is { } output)
        {
            if (add) { output.AudioReceived += Audio; output.AvailabilityChanged += Capabilities; }
            else { output.AudioReceived -= Audio; output.AvailabilityChanged -= Capabilities; }
        }
        if (connection.AudioInput is { } input)
        {
            if (add) input.AvailabilityChanged += Capabilities;
            else input.AvailabilityChanged -= Capabilities;
        }
    }

    private void Control(object value)
    {
        lock (gate)
        {
            if (stopped) return;
            bool terminal = value is State { StateName: "Disconnected" };
            if (value is State state) value = new { type = "state", state = state.StateName, state.ErrorCode, state.Error };
            var json = JsonSerializer.SerializeToUtf8Bytes(value, SocketPeer.Json);
            if (controls.Count >= 32 || json.Length > MaxControlBytes - controlBytes)
            {
                Array.Clear(json);
                peer.Stop();
                return;
            }
            controlBytes += json.Length;
            controls.Enqueue((json, terminal));
            wake.Writer.TryWrite(true);
        }
    }

    private void Capabilities(bool _)
    {
        string currentEncoding;
        lock (gate) currentEncoding = activeEncoding;
        Control(new
        {
            type = "capabilities",
            connection.BackendId,
            supportsResize = Has(ConsoleRights.DisplayMode) && connection.Video?.SupportsResize == true,
            clipboardAvailable = Has(ConsoleRights.Clipboard) && connection.Clipboard?.IsAvailable == true,
            audioOutputAvailable = Has(ConsoleRights.AudioOutput) && connection.AudioOutput?.IsAvailable == true,
            audioInputAvailable = Has(ConsoleRights.AudioInput) && connection.AudioInput?.IsAvailable == true,
            audioInputFormat = Has(ConsoleRights.AudioInput) ? connection.AudioInput?.Format : null,
            modes = V2 && connection.Video is IConsoleDisplayModes modes ? ConsoleDirectory.SafeModes(modes.GetModes()) : [],
            supportedEncodings = ConsoleH264Encoder.SupportedEncodings,
            h264HardwareAvailable = ConsoleH264Encoder.HardwareAvailable,
            activeEncoding = currentEncoding
        });
    }

    private void Audio(ConsoleAudioPacket audio)
    {
        lock (gate)
        {
            if (stopped || audio.Samples.Length > ConsolePayload.MaxAudioPacketBytes) return;
            var packet = ConsoleWire.Audio(audio);
            while (sound.Count >= 8 || audioBytes + packet.Length > MaxAudioBytes)
            {
                using var oldest = sound.Dequeue();
                audioBytes -= (int)oldest.Length;
            }
            sound.Enqueue(packet);
            audioBytes += (int)packet.Length;
            wake.Writer.TryWrite(true);
        }
    }
    private void Connected() { Control(new State("Connected")); Capabilities(false); }
    private void Login() => Control(new State("LoginCompleted"));
    private void Disconnected(ConsoleDisconnectInfo info) => Control(new State("Disconnected", info.ErrorCode,
        info.Error == null ? null : ConsoleDirectory.Builtin(request.Backend) ? Api.DisplayText.Clean(info.Error) : "扩展连接已断开（EXT-002）"));
    private void ResizeRejected(uint errorCode) => Control(new { type = "resizeRejected", errorCode });
    private void Clipboard(ConsoleClipboardData clipboard) => Control(new { type = "clipboard", clipboard.Formats });


    private async Task Pump(CancellationToken token)
    {
        while (await wake.Reader.WaitToReadAsync(token))
        {
            while (wake.Reader.TryRead(out _)) { }
            while (true)
            {
                (byte[] Json, bool Terminal) item;
                lock (gate)
                {
                    if (!controls.TryDequeue(out item)) break;
                    controlBytes -= item.Json.Length;
                }
                try { await peer.Write(item.Json, WebSocketMessageType.Text); }
                finally { Array.Clear(item.Json); }
                if (item.Terminal) return;
            }
            ConsolePacket? frame;
            ConsoleCursor? cursor;
            ConsoleCursorState? position;
            bool sendCursor;
            lock (gate)
            {
                frame = pending; pending = null;
                cursor = pendingCursor; pendingCursor = null;
                position = pendingPosition; pendingPosition = null;
                sendCursor = cursorPending; cursorPending = false;
            }
            using (frame)
            {
                if (sendCursor) await peer.Send(ConsoleWire.Cursor(cursor));
                if (position != null) await peer.Send(new { type = "cursorState", state = position });
                if (frame != null) await peer.Write(frame.Data, WebSocketMessageType.Binary);
            }
            for (int i = 0; i < 8; i++)
            {
                ConsolePacket? packet;
                lock (gate)
                {
                    if (!sound.TryDequeue(out packet)) break;
                    audioBytes -= (int)packet.Length;
                }
                using (packet) await peer.Write(packet.Data, WebSocketMessageType.Binary);
            }
        }
    }

    private async Task ReceiveCommand(JsonElement message)
    {
        try { await Command(message); }
        catch (Exception) when (!ConsoleDirectory.Builtin(request.Backend) && !peer.Token.IsCancellationRequested)
        { throw new ArgumentException("扩展请求未获接受（EXT-003）"); }
    }
    private void ReceiveMicrophone(ReadOnlyMemory<byte> data)
    {
        try { Microphone(data); }
        catch (Exception) when (!ConsoleDirectory.Builtin(request.Backend) && !peer.Token.IsCancellationRequested)
        { throw new ArgumentException("扩展音频输入未获接受（EXT-004）"); }
    }
    private void Microphone(ReadOnlyMemory<byte> data)
    {
        long generation = 0, sequence = 0;
        if (V2)
        {
            if (data.Length < 34) throw new ArgumentException("麦克风消息长度无效。");
            generation = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span);
            sequence = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data.Span[8..]);
            data = data[16..];
        }
        source.Controller.Input(this, ConsoleRights.AudioInput, generation, sequence, () => ConsoleWire.AudioInput(connection.AudioInput, data.Span));
    }
    private async Task Command(JsonElement msg)
    {
        var type = msg.GetProperty("type").GetString();
        if (type == "captureFrame")
        {
            if (group?.Refresh() != true) throw new ArgumentException("画面不可用。");
            return;
        }
        if (type == "displayed" && V2)
        {
            lock (gate)
            {
                long sequence = msg.GetProperty("sequence").GetInt64();
                if (sequence == baseline) displayed = sequence;
            }
            return;
        }
        if (type == "setEncoding")
        {
            var encoding = msg.GetProperty("encoding").GetString();
            if (encoding is not ("h264" or "bgra" or "deflate" or "png")) throw new ArgumentException("不支持的视频编码格式。");
            encoding = Choose(encoding);
            if (activeEncoding != encoding)
            {
                var old = group!;
                bool refresh;
                lock (gate)
                {
                    group = null; pending?.Dispose(); pending = null; needsKey = true;
                    refresh = baseline >= 0; displayed = baseline = -1;
                }
                await source.Unsubscribe(this, old);
                activeEncoding = encoding;
                Control(new { type = "encodingChanged", encoding });
                group = source.Subscribe(this, encoding);
                if (refresh) group.Refresh();
            }
            return;
        }
        long generation = V2 ? msg.GetProperty("generation").GetInt64() : 0;
        if (V2 && type == "takeControl") { source.Controller.Take(this, generation); return; }
        long order = V2 ? msg.GetProperty("inputSequence").GetInt64() : 0;
        source.Controller.Input(this, type switch { "resize" or "setMode" => ConsoleRights.DisplayMode,
            "clipboard" => ConsoleRights.Clipboard, _ => ConsoleRights.Control }, generation, order, () => Submit(msg));
    }

    private Task Submit(JsonElement message)
    {
        JsonElement Get(string key) => message.GetProperty(key);
        T Optional<T>(string key, T fallback) where T : struct =>
            message.TryGetProperty(key, out var value) ? value.Deserialize<T>(SocketPeer.Json) : fallback;
        string? type = Get("type").GetString();
        var input = connection.Input;
        bool? accepted;
        switch (type)
        {
            case "key":
                var key = new ConsoleKeyEvent(Get("scanCode").GetUInt16(), Optional("extended", false), Get("down").GetBoolean());
                accepted = input?.SendKey(key.ScanCode, key.Extended, key.Down);
                if (accepted == true) Track(key);
                break;
            case "keys":
                var keys = Get("events");
                if (keys.GetArrayLength() is < 1 or > 256) throw new ArgumentException("组合键需要 1 至 256 个按键事件。");
                var batch = keys.EnumerateArray().Select(key => new ConsoleKeyEvent(key.GetProperty("scanCode").GetUInt16(),
                    key.TryGetProperty("extended", out var ext) && ext.GetBoolean(), key.GetProperty("down").GetBoolean())).ToArray();
                accepted = input?.SendKeys(batch);
                if (accepted == true) foreach (var k in batch) Track(k);
                break;
            case "pause": accepted = input?.SendPause(); break;
            case "releaseAllInputs": accepted = input?.ReleaseAllInputs();
                if (accepted == true) { keysDown.Clear(); buttonsDown.Clear(); }
                break;
            case "syncKeyboard":
                var flags = Get("flags").GetUInt32();
                accepted = flags <= 15 && input?.SynchronizeKeyboard((ConsoleKeyboardToggleState)flags) == true;
                break;
            case "pointer": case "click":
                var button = Optional("button", type == "click" ? ConsolePointerButton.Left : ConsolePointerButton.None);
                if (!Enum.IsDefined(button)) throw new ArgumentException("无效的鼠标按键。");
                ushort x = Get("x").GetUInt16(), y = Get("y").GetUInt16();
                accepted = type == "click" ? input?.SendClick(x, y, button)
                    : input?.SendPointer(x, y, button, Optional("down", false), Optional<short>("wheel", 0));
                if (accepted == true && type == "pointer")
                {
                    (lastX, lastY) = (x, y);
                    if (button != ConsolePointerButton.None)
                    {
                        if (Optional("down", false)) buttonsDown.Add(button); else buttonsDown.Remove(button);
                    }
                }
                break;
            case "resize":
                int width = Get("width").GetInt32(), height = Get("height").GetInt32();
                uint scale = Optional("desktopScale", 100u);
                if (!ConsolePayload.IsDesktopSize(width, height) || scale is < 100 or > 500
                    || connection.Video?.Resize(width, height, scale) != true) ResizeRejected(0);
                return Task.CompletedTask;
            case "clipboard":
                var formats = Get("formats");
                if (formats.GetArrayLength() > 3) throw new ArgumentException("剪贴板最多接受三种格式。");
                var clipboard = new ConsoleClipboardData(formats.Deserialize<ConsoleClipboardFormat[]>(SocketPeer.Json)!);
                accepted = ConsolePayload.TryWriteClipboard(clipboard, out _) && connection.Clipboard?.SetClipboard(clipboard) == true;
                break;
            case "setMode":
                var modes = connection.Video as IConsoleDisplayModes;
                string? id = Get("id").GetString();
                if (modes == null || !ConsoleDirectory.SafeModes(modes.GetModes()).Any(mode => mode.Id == id) || Environment.TickCount64 - lastMode < 500)
                    throw new ArgumentException("显示模式无效或切换过于频繁。");
                lastMode = Environment.TickCount64;
                Control(new { type = "displayMode", result = modes.SetMode(id!) });
                return Task.CompletedTask;
            default: throw new ArgumentException("未知的控制台指令。");
        }
        if (accepted != true) throw new ArgumentException("当前连接拒绝此输入或数据格式无效。");
        return Task.CompletedTask;
    }
}
