using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;
using ExHyperV.Services;

namespace ExHyperV.ConsoleIpc.Tests;

// Each native transfer targets the client process pinned on the authenticated session pipe.
internal sealed class ConsoleIpcTestPeer : ConsoleIpcDiscovery, IDisposable
{
    private readonly string name;
    private readonly int expectedClient;
    private SafeProcessHandle? target;
    private readonly Guid instance = Guid.NewGuid();
    private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(45));
    private readonly SemaphoreSlim writes = new(1);
    private readonly Stream control, session;
    private readonly Queue<Stream> endpoints = [];
    private readonly Task controlLoop, sessionLoop;
    private readonly Channel<IpcPacket> received = Channel.CreateUnbounded<IpcPacket>();
    internal readonly TaskCompletionSource<ulong> Delivery = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource CpuRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal IpcPacket? OpenRequest { get; private set; }
    internal Task SessionClosed => sessionLoop;
    internal int Verified, CpuSwitches;
    internal bool Gpu;
    private long transfer;

    internal ConsoleIpcTestPeer(bool namedPipe, Guid? pipeId = null, int? expectedClient = null)
    {
        name = "ExHyperV.Test." + (pipeId ?? Guid.NewGuid()).ToString("N");
        this.expectedClient = expectedClient ?? Environment.ProcessId;
        control = Endpoint();
        try { session = Endpoint(); } catch { control.Dispose(); throw; }
        controlLoop = Serve(control, false);
        sessionLoop = Serve(session, true);

        Stream Endpoint()
        {
            if (namedPipe) return Listen();
            var requests = Channel.CreateUnbounded<byte[]>();
            var replies = Channel.CreateUnbounded<byte[]>();
            endpoints.Enqueue(new Duplex(replies.Reader, requests.Writer));
            return new Duplex(requests.Reader, replies.Writer);
        }
    }

    private sealed class Duplex(ChannelReader<byte[]> incoming, ChannelWriter<byte[]> outgoing) : Stream
    {
        private ReadOnlyMemory<byte> remaining;
        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token = default)
        {
            if (remaining.IsEmpty)
            {
                if (!await incoming.WaitToReadAsync(token)) return 0;
                remaining = await incoming.ReadAsync(token);
            }
            int count = Math.Min(remaining.Length, destination.Length);
            remaining[..count].CopyTo(destination);
            remaining = remaining[count..];
            return count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            outgoing.WriteAsync(buffer.ToArray(), token);
        protected override void Dispose(bool disposing) { outgoing.TryComplete(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private NamedPipeServerStream Listen() => new(name, PipeDirection.InOut, 2, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    internal override Stream Open()
    {
        if (control is NamedPipeServerStream) return base.Open();
        Interlocked.Increment(ref Verified);
        lock (endpoints) return endpoints.Dequeue();
    }
    internal override ConsoleIpcEndpoint Find() => Validate(name, null, "S-1-5-18", new string('0', 64), true);
    internal override void Verify(SafePipeHandle pipe, ConsoleIpcEndpoint endpoint, long before)
    {
        if (!GetNamedPipeServerProcessId(pipe, out uint pid) || pid != Environment.ProcessId) throw new UnauthorizedAccessException();
        Interlocked.Increment(ref Verified);
    }

    private async Task Serve(Stream stream, bool dedicated)
    {
        try
        {
            if (stream is NamedPipeServerStream pipe)
            {
                await pipe.WaitForConnectionAsync(stop.Token);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || pid != expectedClient)
                    throw new UnauthorizedAccessException("Unexpected test client");
                if (dedicated)
                {
                    target = OpenProcess(0x1040, false, pid);
                    if (target.IsInvalid || !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint again) || again != pid)
                        throw new UnauthorizedAccessException("Test client process unavailable");
                }
            }
            while (!stop.IsCancellationRequested)
            {
                var p = await ConsoleIpcWire.Read(stream, stop.Token);
                Action<BinaryWriter>? reply = null;
                switch (p.Type)
                {
                    case IpcMessage.Hello:
                        using (var hello = new IpcReader(p.Body))
                        {
                            if (hello.ReadInt32() is not (1 or 2) || hello.Flag() != dedicated || hello.ReadUInt64() != 3ul)
                                throw new InvalidDataException("Invalid hello");
                        }
                        reply = w => { w.Write(instance.ToByteArray()); w.Write(3ul); };
                        break;
                    case IpcMessage.InspectSnapshot: reply = Snapshot; break;
                    case IpcMessage.GetModes: reply = w => { w.Write(1); Mode(w); }; break;
                    case IpcMessage.AllowsFallback: reply = w => w.Write(true); break;
                    case IpcMessage.OpenSession: OpenRequest = p; reply = w => w.Write(7UL); break;
                    case IpcMessage.StartSession:
                        await Reply(stream, p, null);
                        await Push(IpcMessage.SessionConnected);
                        await Push(IpcMessage.SessionLoginCompleted);
                        Started.TrySetResult();
                        continue;
                    case IpcMessage.OpenDelivery: Delivery.TrySetResult(BitConverter.ToUInt64(p.Body)); continue;
                    case IpcMessage.RequestCpuPath:
                        Interlocked.Increment(ref CpuSwitches);
                        await Reply(stream, p, w => w.Write(8UL));
                        CpuRequested.TrySetResult();
                        continue;
                    case IpcMessage.SetMode: reply = w => { w.Write(true); w.Write(true); Mode(w); }; break;
                    case IpcMessage.CloseSession: await Reply(stream, p, null); return;
                    default: await received.Writer.WriteAsync(p, stop.Token); continue;
                }
                await Reply(stream, p, reply);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private static void Mode(BinaryWriter w)
    { w.Text("desktop-1"); w.Write(320); w.Write(240); w.Write(60u); w.Write(1u); }

    internal string? ReasonText { get; set; }

    private void Snapshot(BinaryWriter w)
    {
        w.Write(2UL);
        for (int i = 0; i < 4; i++) w.Write(6);
        w.Write(Gpu ? 2 : 1); w.Write(7UL); w.Write(63ul); w.Write(63ul);
        w.Write(true); w.Write(true); w.Write(5UL);
        w.Write(1); Mode(w);
        w.Text("guest"); w.Text("domain"); w.Text(ReasonText); w.Text("EXT-200");
    }

    internal async Task<IpcReader> Receive(IpcMessage type)
    {
        while (true)
        {
            var packet = await received.Reader.ReadAsync(stop.Token);
            if (packet.Type == type) return new(packet.Body);
        }
    }

    private Task Reply(Stream stream, IpcPacket request, Action<BinaryWriter>? payload) =>
        Write(stream, ConsoleIpcWire.Encode(ConsoleIpcWire.Reply(request.Type), request.Request, request.Session, payload));
    internal void DropControl() => control.Dispose();
    internal Task Push(IpcMessage type, Action<BinaryWriter>? body = null) =>
        Write(session, ConsoleIpcWire.Encode(type, 0, 7, body));
    internal Task Changed(Guid vm, string? subject = null) => Write(control, ConsoleIpcWire.Encode(IpcMessage.SnapshotChanged, 0, 0,
        w => { w.Write(vm.ToByteArray()); w.Text(subject); }));

    private async Task Write(Stream stream, byte[] message)
    {
        await writes.WaitAsync(stop.Token);
        try { await stream.WriteAsync(message, stop.Token); }
        finally { writes.Release(); }
    }

    private long Transfer(SafeHandle source, uint access)
    {
        using var self = new SafeProcessHandle(-1, false);
        if (!DuplicateHandle(-1, source, target ?? self, out nint copy, access, false, access == 0 ? 2u : 0u))
            throw new IOException("Test handle transfer failed");
        return copy.ToInt64();
    }

    internal Task AttachGpu(SafeFileHandle texture, ulong delivery, long luid, int width, int height)
    {
        long raw = Transfer(texture, 0), id = Interlocked.Increment(ref transfer);
        return Push(IpcMessage.GpuSurfaceAttach, w =>
        { w.Write(id); w.Write(raw); w.Write(delivery); w.Write(7UL); w.Write(0); w.Write(luid); w.Write(width); w.Write(height); });
    }

    internal Task ReadyGpu(ulong id, long sequence) => Push(IpcMessage.GpuFrameReady, w =>
    { w.Write(id); w.Write(7UL); w.Write(0); w.Write(sequence); w.Write(1UL); w.Write(0UL); });

    internal Task AttachRing(MemoryMappedFile ring, int bytes, ulong epoch)
    {
        long raw = Transfer(ring.SafeMemoryMappedFileHandle, 4), id = Interlocked.Increment(ref transfer);
        return Push(IpcMessage.CpuRingAttach, w =>
        { w.Write(id); w.Write(raw); w.Write(epoch); w.Write(bytes); w.Write(1); w.Write(bytes); });
    }

    internal Task ReadyCpu(ulong epoch, long sequence) => Push(IpcMessage.CpuFrameReady, w =>
    { w.Write(epoch); w.Write(0); w.Write(sequence); });

    internal static void FullFrame(MemoryMappedViewAccessor view, ulong epoch, int width, int height, long sequence, byte value)
    {
        view.Write(0, sequence * 2 - 1);
        Thread.MemoryBarrier();
        view.Write(8, epoch); view.Write(16, sequence); view.Write(24, 0L); view.Write(32, 2);
        view.Write(36, width); view.Write(40, height); view.Write(44, width * 4); view.Write(48, 88); view.Write(52, width * height * 4);
        view.Write(56, 1); view.Write(60, 0); view.Write(64, 72); view.Write(68, 0);
        view.Write(72, 0); view.Write(76, 0); view.Write(80, width); view.Write(84, height);
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, value);
        view.WriteArray(88, pixels, 0, pixels.Length);
        Thread.MemoryBarrier();
        view.Write(0, sequence * 2);
    }

    public void Dispose()
    {
        stop.Cancel(); control.Dispose(); session.Dispose();
        Task.WhenAll(controlLoop, sessionLoop).GetAwaiter().GetResult();
        target?.Dispose(); writes.Dispose(); stop.Dispose();
    }

    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool DuplicateHandle(nint from, SafeHandle source, SafeProcessHandle to, out nint copy, uint access, bool inherit, uint options);
}
