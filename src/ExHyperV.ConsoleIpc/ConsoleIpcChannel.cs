using System.Collections.Concurrent;

namespace ExHyperV.ConsoleIpc;

internal sealed class ConsoleIpcChannel : IDisposable
{
    private sealed record Pending(IpcMessage Type, ulong Session, TaskCompletionSource<IpcPacket> Completion);
    private readonly Stream stream;
    private readonly SemaphoreSlim writer = new(1);
    private readonly ConcurrentDictionary<ulong, Pending> pending = new();
    private readonly CancellationTokenSource lifetime = new();
    private const ulong DefaultOfferedFeatures = 3ul;
    private readonly Action<IpcPacket> push;
    private readonly ulong offered;
    private long serial;
    private int closed;
    internal bool Closed => Volatile.Read(ref closed) != 0;
    internal Task Completion { get; }
    internal Guid Instance { get; private set; }
    internal ulong Features { get; private set; }

    private ConsoleIpcChannel(Stream stream, ulong offered, Action<IpcPacket> push)
    {
        this.stream = stream;
        this.offered = offered;
        this.push = push;
        Completion = Pump();
    }

    internal static async Task<ConsoleIpcChannel> Open(ConsoleIpcDiscovery discovery, int role, bool session,
        Action<IpcPacket> push, CancellationToken token, ulong offeredFeatures = DefaultOfferedFeatures)
    {
        var open = Task.Run(discovery.Open, token);
        Stream stream;
        try { stream = await open.WaitAsync(token).ConfigureAwait(false); }
        catch
        {
            _ = open.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
            throw;
        }
        var channel = new ConsoleIpcChannel(stream, offeredFeatures, push);
        try
        {
            using var hello = await channel.Call(IpcMessage.Hello, 0, w =>
                { w.Write(role); w.Write(session); w.Write(offeredFeatures); }, token).ConfigureAwait(false);
            return channel;
        }
        catch { channel.Dispose(); throw; }
    }

    internal async Task<IpcReader> Call(IpcMessage type, ulong session, Action<BinaryWriter>? payload, CancellationToken token = default)
    {
        ulong id = checked((ulong)Interlocked.Increment(ref serial));
        var request = new Pending(type, session, new(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!pending.TryAdd(id, request)) throw new InvalidOperationException();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await Write(ConsoleIpcWire.Encode(type, id, session, payload), deadline.Token).ConfigureAwait(false);
            var packet = await request.Completion.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            var reader = new IpcReader(packet.Body);
            if (packet.Type == IpcMessage.Error)
            {
                using (reader) throw new IpcFailure(reader.ReadUInt32());
            }
            return reader;
        }
        catch (OperationCanceledException) { Dispose(); throw; }
        finally { pending.TryRemove(id, out _); }
    }

    internal async Task Notify(IpcMessage type, ulong session, Action<BinaryWriter> payload)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        await Write(ConsoleIpcWire.Encode(type, 0, session, payload), deadline.Token).ConfigureAwait(false);
    }

    private async Task Write(byte[] bytes, CancellationToken token)
    {
        await writer.WaitAsync(token).ConfigureAwait(false);
        try { await stream.WriteAsync(bytes, token).ConfigureAwait(false); }
        catch { Dispose(); throw; }
        finally { writer.Release(); }
    }

    private async Task Pump()
    {
        Exception end = new IpcFailure(IpcFailure.Unavailable);
        try
        {
            while (true)
            {
                var packet = await ConsoleIpcWire.Read(stream, lifetime.Token).ConfigureAwait(false);
                if (packet.Request == 0)
                {
                    if (Instance == Guid.Empty) throw new IpcFailure(IpcFailure.Incompatible);
                    push(packet);
                    continue;
                }
                if (!pending.TryGetValue(packet.Request, out var request) || packet.Session != request.Session
                    || packet.Type != ConsoleIpcWire.Reply(request.Type) && packet.Type != IpcMessage.Error)
                    throw new IpcFailure(IpcFailure.Incompatible);
                if (packet.Type == ConsoleIpcWire.Reply(IpcMessage.Hello))
                {
                    using var hello = new IpcReader(packet.Body);
                    Instance = hello.Id();
                    Features = hello.ReadUInt64();
                    if (Instance == Guid.Empty || (Features & 1ul) == 0 || (Features & ~offered) != 0)
                        throw new IpcFailure(IpcFailure.Incompatible);
                }
                if (!request.Completion.TrySetResult(packet)) throw new IpcFailure(IpcFailure.Incompatible);
            }
        }
        catch (Exception error) { end = error; }
        finally
        {
            foreach (var request in pending.Values) request.Completion.TrySetException(end);
            Dispose();
        }
        if (end is IpcFailure failure) throw failure;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        lifetime.Cancel();
        stream.Dispose();
    }
}
