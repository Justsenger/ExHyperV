using ExHyperV.Services;
using Microsoft.Win32.SafeHandles;
#if EXHYPERV_SERVER
using ExHyperV.Server.Hosting;
#else
using ExHyperV.App.UI.Shared.ConsoleHost;
#endif

namespace ExHyperV.ConsoleIpc;

internal sealed partial class ConsoleIpcSession : IConsoleConnection, IConsoleVideoSource, IConsoleGpuSource,
    IConsoleDisplayModes, IConsoleCursorStateSource, IConsoleInput, IConsoleClipboardProvider, IConsoleAudioInput, IConsoleAudioOutput
{
    private readonly object gate = new();
    private readonly ConsoleIpcDiscovery discovery;
    private readonly int role;
    private readonly ConsoleConnectionOptions options;
    private readonly IpcAccess access;
    private readonly Action<ConsoleControlState> controlChanged;
    // Server applies the opening viewer's revocation token at that viewer's final send gate.
    // The dedicated service connection supplies grants for the shared source itself.
    private CancellationToken SessionGrant => role == 1 ? access.Revoked : default;
    private readonly ConsoleCapabilities capabilities;
    private readonly CancellationTokenSource opening = new();
    private readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<ulong, ConsoleIpcGpuDelivery> deliveries = [];
    private ConsoleIpcChannel? pipe;
    private ConsoleFrame? latest;
    private ConsoleIpcCpuRing? ring;
    private ConsoleControlState control;
    private ulong session, deliveryId, lastTransfer, inputSequence;
    private ulong minimumCpuEpoch;
    private bool started, stopping, acceptingDeliveries;
    private int queued;
    private ConsoleDisconnectInfo? remoteEnd;
    private volatile ConsoleConnectionState state;

    internal ConsoleIpcSession(ConsoleIpcDiscovery discovery, int role, ConsoleConnectionOptions options, IpcAccess access,
        Action<ConsoleControlState> controlChanged)
    {
        this.discovery = discovery;
        this.role = role;
        this.options = options;
        this.access = access;
        this.controlChanged = controlChanged;
        control = access.Snapshot.Control;
        capabilities = access.Snapshot.Granted & access.Snapshot.Supported;
        ProviderInfo = new("External", options.VmId, access.Guest ?? "unknown", false, false, false);
    }

    public string BackendId => "External";
    public ConsoleConnectionState State => state;
    public IConsoleVideoSource? Video => Has(ConsoleCapabilities.Video) ? this : null;
    public IConsoleInput? Input => Has(ConsoleCapabilities.Input) ? this : null;
    public IConsoleClipboard? Clipboard => options.RedirectClipboard && Has(ConsoleCapabilities.Clipboard) ? this : null;
    public IConsoleAudioInput? AudioInput => options.RedirectAudioInput && Has(ConsoleCapabilities.AudioInput) ? this : null;
    public IConsoleAudioOutput? AudioOutput => options.RedirectAudioOutput && Has(ConsoleCapabilities.AudioOutput) ? this : null;
    private bool Has(ConsoleCapabilities flag) => capabilities.HasFlag(flag);
    public event Action? Connected;
    public event Action? LoginCompleted;
    public event Action<ConsoleDisconnectInfo>? Disconnected;
    public event Action<ConsoleFrame>? FrameReceived;
    public event Action<ConsoleCursor?>? CursorChanged;
    public event Action<ConsoleCursorState>? CursorStateChanged;
    public ConsoleCursorState? CursorState { get; private set; }
    public bool SupportsResize => false;
    public event Action<uint>? ResizeRejected { add { } remove { } }
    public bool Resize(int width, int height, uint desktopScale = 100) => false;

    public void Start()
    {
        lock (gate)
        {
            if (started || stopping) throw new InvalidOperationException("EXT-STATE");
            started = true;
            state = ConsoleConnectionState.Connecting;
        }
        _ = Task.Run(Run);
    }

    private async Task Run()
    {
        ConsoleDisconnectInfo end = new(0, null);
        ConsoleIpcChannel? channel = null;
        try
        {
            using var negotiation = CancellationTokenSource.CreateLinkedTokenSource(opening.Token, access.Revoked);
            negotiation.CancelAfter(options.ConnectTimeout);
            channel = await ConsoleIpcChannel.Open(discovery, role, true, Push, negotiation.Token).ConfigureAwait(false);
            if (channel.Instance != access.Instance) throw new IpcFailure(IpcFailure.Denied);
            if (access.Snapshot.TransportKind == ConsoleTransportKind.GpuShared && (channel.Features & 2) == 0)
                throw new IpcFailure(IpcFailure.Incompatible);
            using var opened = await channel.Call(IpcMessage.OpenSession, 0, w =>
            {
                w.Write(options.VmId.ToByteArray());
                w.Write(options.Width); w.Write(options.Height); w.Write(options.DesktopScale); w.Write(options.ConnectTimeout.Ticks);
                w.Write(options.HostCursor); w.Write(options.RedirectClipboard); w.Write(options.RedirectAudioOutput); w.Write(options.RedirectAudioInput);
                w.Text(options.ViewerContextId); w.Text(access.Subject); w.Write(access.Snapshot.Version); w.Text(access.Guest); w.Text(access.Domain);
            }, negotiation.Token).ConfigureAwait(false);
            ulong assigned = opened.ReadUInt64();
            if (assigned == 0) throw new IpcFailure(IpcFailure.Incompatible);
            lock (gate)
            {
                negotiation.Token.ThrowIfCancellationRequested();
                if (stopping) throw new OperationCanceledException();
                session = assigned;
                pipe = channel;
            }
            using var registration = SessionGrant.Register(channel.Dispose);
            using var ack = await channel.Call(IpcMessage.StartSession, assigned, null, negotiation.Token).ConfigureAwait(false);
            lock (gate)
            {
                acceptingDeliveries = true;
                foreach (ulong id in deliveries.Keys) Queue(IpcMessage.OpenDelivery, w => w.Write(id));
            }
            await channel.Completion.ConfigureAwait(false);
            end = new(IpcFailure.Unavailable, "EXT-001");
        }
        catch (IpcFailure error) { end = new(error.Code, "EXT-IPC"); }
        catch (UnauthorizedAccessException) { end = new(IpcFailure.Denied, "EXT-TRUST-02"); }
        catch (Exception) { end = new(IpcFailure.Unavailable, "EXT-001"); }
        finally
        {
            channel?.Dispose();
            // A cancelled call may finish before the reader exits its last callback.
            if (channel != null) try { await channel.Completion.ConfigureAwait(false); } catch (IpcFailure) { }
            lock (gate)
            {
                end = remoteEnd ?? (SessionGrant.IsCancellationRequested ? new(IpcFailure.Denied, "EXT-403") : stopping ? new(0, null) : end);
                stopping = true;
                state = ConsoleConnectionState.Disconnected;
                pipe = null;
                foreach (var delivery in deliveries.Values.ToArray()) delivery.Dispose();
                ring?.Dispose();
                latest?.Dispose();
                latest = null;
                ProviderInfo = ProviderInfo with { IsReady = false, CanRead = false, CanWrite = false };
                inputAvailable = outputAvailable = false;
            }
            done.TrySetResult();
            Raise(Disconnected, end);
        }
    }

    public Task DisconnectAsync()
    {
        lock (gate)
        {
            if (stopping) return done.Task;
            stopping = true;
            if (!started)
            {
                foreach (var delivery in deliveries.Values.ToArray()) delivery.Dispose();
                done.TrySetResult();
                return done.Task;
            }
            state = ConsoleConnectionState.Disconnecting;
            if (pipe == null) opening.Cancel();
            else _ = Close(pipe, session);
            return done.Task;
        }
    }

    private static async Task Close(ConsoleIpcChannel channel, ulong id)
    {
        try { using var ack = await channel.Call(IpcMessage.CloseSession, id, null).ConfigureAwait(false); }
        catch (Exception) { }
        finally { channel.Dispose(); }
    }

    public void Dispose() => _ = DisconnectAsync();
    public ValueTask DisposeAsync() => new(DisconnectAsync());

    private bool Queue(IpcMessage type, Action<BinaryWriter> payload)
    {
        // Called under gate, preserving the order of generation and sequence assignment.
        if (pipe == null || stopping || pipe.Closed || SessionGrant.IsCancellationRequested) return false;
        if (queued >= 64) { pipe.Dispose(); return false; }
        queued++;
        _ = Send(pipe, type, payload);
        return true;
    }

    private async Task Send(ConsoleIpcChannel channel, IpcMessage type, Action<BinaryWriter> payload)
    {
        try { await channel.Notify(type, session, payload).ConfigureAwait(false); }
        catch (Exception) { channel.Dispose(); }
        finally { lock (gate) queued--; }
    }

    private void Push(IpcPacket packet)
    {
        using var r = new IpcReader(packet.Body);
        lock (gate)
        {
            if (packet.Session == 0 || packet.Session != session) throw new IpcFailure(IpcFailure.Incompatible);
            if (packet.Type is IpcMessage.GpuSurfaceAttach or IpcMessage.CpuRingAttach) { Attach(packet.Type, r); return; }
            if (stopping || SessionGrant.IsCancellationRequested) return;
        }
        switch (packet.Type)
        {
            case IpcMessage.SessionConnected:
                lock (gate) { if (stopping) return; state = ConsoleConnectionState.Connected; }
                Raise(Connected);
                break;
            case IpcMessage.SessionLoginCompleted: Raise(LoginCompleted); break;
            case IpcMessage.SessionDisconnected:
                lock (gate) { remoteEnd = new(r.ReadUInt32(), r.SafeReason()); pipe?.Dispose(); }
                break;
            case IpcMessage.ControlChanged:
                var next = ConsoleIpcClient.ReadControl(r);
                lock (gate)
                {
                    if (next.Generation < control.Generation || next.Generation == control.Generation && next.HasControl != control.HasControl)
                        throw new IpcFailure(IpcFailure.Incompatible);
                    control = next;
                    if (!next.CanView) { remoteEnd = new(IpcFailure.Denied, "EXT-403"); pipe?.Dispose(); }
                }
                controlChanged(next);
                break;
            case IpcMessage.CpuFrameReady: CpuReady(r); break;
            case IpcMessage.GpuFrameReady:
                ulong id = r.ReadUInt64();
                ConsoleIpcGpuDelivery? delivery;
                lock (gate) deliveries.TryGetValue(id, out delivery);
                delivery?.Ready(r.ReadUInt64(), r.ReadInt32(), r.ReadInt64(), r.ReadUInt64(), r.ReadUInt64());
                break;
            default: ChannelPush(packet.Type, r); break;
        }
    }

    private void Attach(IpcMessage type, IpcReader r)
    {
        ulong transfer = r.ReadUInt64();
        long raw = r.ReadInt64();
        if (transfer == 0 || raw is <= 0 or > uint.MaxValue || (raw & 3) != 0) throw new IpcFailure(IpcFailure.Incompatible);
        // Duplicate notifications refer to a value which may already have been closed and recycled.
        if (transfer > lastTransfer)
        {
            lastTransfer = transfer;
            using var handle = new SafeFileHandle((nint)raw, true);
            if (!GetHandleInformation(handle, out _)) throw new IpcFailure(IpcFailure.Incompatible);
            if (!stopping && !SessionGrant.IsCancellationRequested && control.CanView && Video != null)
            {
                if (type == IpcMessage.CpuRingAttach)
                {
                    ulong epoch = r.ReadUInt64();
                    if (epoch >= minimumCpuEpoch && (ring == null || epoch > ring.Epoch))
                    {
                        var next = new ConsoleIpcCpuRing(handle, epoch, r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
                        ring?.Dispose(); ring = next;
                        latest?.Dispose(); latest = null;
                    }
                }
                else if (deliveries.TryGetValue(r.ReadUInt64(), out var delivery))
                    delivery.Attach(r.ReadUInt64(), r.ReadInt32(), r.ReadInt64(), r.ReadInt32(), r.ReadInt32(), handle);
            }
        }
        Queue(IpcMessage.TransferAck, w => w.Write(transfer));
    }

    private void CpuReady(IpcReader r)
    {
        ulong epoch = r.ReadUInt64();
        int slot = r.ReadInt32();
        long sequence = r.ReadInt64();
        ConsoleFrame? frame;
        lock (gate)
        {
            if (!control.CanView || stopping || SessionGrant.IsCancellationRequested) return;
            frame = ring?.Read(epoch, slot, sequence);
            Queue(IpcMessage.FrameConsumedAck, w => { w.Write(epoch); w.Write(slot); w.Write(sequence); });
            if (frame == null) return;
            if (latest == null && !frame.HasFullDamage || latest != null && sequence <= latest.Sequence) { frame.Dispose(); return; }
            latest?.Dispose();
            latest = frame.Retain();
        }
        using (frame) Raise(FrameReceived, frame);
    }

    public ConsoleFrame? CaptureFrame()
    {
        lock (gate) return stopping || !control.CanView || SessionGrant.IsCancellationRequested ? null : latest?.Retain();
    }

    public IConsoleGpuDelivery OpenDelivery()
    {
        lock (gate)
        {
            if (stopping || access.Snapshot.TransportKind != ConsoleTransportKind.GpuShared || deliveries.Count >= 16)
                throw new InvalidOperationException("EXT-GPU");
            ulong id = ++deliveryId;
            var delivery = new ConsoleIpcGpuDelivery(id, RequestCpu, CloseDelivery);
            deliveries.Add(id, delivery);
            if (acceptingDeliveries) Queue(IpcMessage.OpenDelivery, w => w.Write(id));
            return delivery;
        }
    }

    private ulong RequestCpu(ulong id)
    {
        using var r = (pipe ?? throw new IOException("EXT-001")).Call(IpcMessage.RequestCpuPath, session, w => w.Write(id)).GetAwaiter().GetResult();
        ulong next = r.ReadUInt64();
        lock (gate)
        {
            minimumCpuEpoch = Math.Max(minimumCpuEpoch, next);
            if (ring != null && ring.Epoch < minimumCpuEpoch)
            {
                ring.Dispose(); ring = null;
                latest?.Dispose(); latest = null;
            }
        }
        return next;
    }

    private void CloseDelivery(ulong id)
    {
        lock (gate)
        {
            deliveries.Remove(id);
            if (acceptingDeliveries) Queue(IpcMessage.CloseDelivery, w => w.Write(id));
        }
    }

    private static void Raise(Action? handlers)
    {
        foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            try { handler(); } catch (Exception) { }
    }

    private static void Raise<T>(Action<T>? handlers, T value)
    {
        foreach (var handler in Delegate.EnumerateInvocationList(handlers))
            try { handler(value); } catch (Exception) { }
    }

    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetHandleInformation(SafeFileHandle handle, out uint flags);
}
