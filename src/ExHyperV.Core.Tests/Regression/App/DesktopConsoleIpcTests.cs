using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using ExHyperV.App.UI.Pages.VirtualMachines.Console;
using ExHyperV.ConsoleIpc;
using ExHyperV.ConsoleIpc.Tests;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class DesktopConsoleIpcTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    [TestMethod]
    public void InvalidRegistrationsDenyBeforeOpeningAndPathsUseDirectoryBoundaries()
    {
        string pin = new('a', 64);
        foreach (var (name, sid, hash) in new (string?, string?, string?)[]
        {
            ("local", null, pin), ("local", "S-1-5-18", null), ("local", "S-1-5-18", pin[..63]),
            (@"\\host\pipe\x", "S-1-5-18", pin), (@"\\.\pipe\..\x", "S-1-5-18", pin),
            (@"foo\bar", "S-1-5-18", pin), ("x", "bogus", pin), (new string('x', 241), "S-1-5-18", pin)
        })
        {
            var error = Assert.ThrowsExactly<UnauthorizedAccessException>(() => ConsoleIpcDiscovery.Validate(name, null, sid, hash, true));
            Assert.AreEqual("EXT-TRUST-01", error.Message);
        }
        Assert.AreEqual(@"\\.\pipe\test", ConsoleIpcDiscovery.Validate(@"\\.\pipe\test", null, "S-1-5-18", pin, true).Pipe);
        Assert.IsFalse(ConsoleIpcTrust.IsProtected(Path.Combine(Path.GetTempPath(), "service.exe")));
        Assert.IsFalse(ConsoleIpcTrust.IsProtected(Environment.SystemDirectory + "-extra\\service.exe"));
        Assert.IsTrue(ConsoleIpcTrust.IsProtected(Path.Combine(Environment.SystemDirectory, "service.exe")));
        string image = typeof(DesktopConsoleIpcTests).Assembly.Location;
        var endpoint = ConsoleIpcDiscovery.Validate("test", null, "S-1-5-18", pin, true);
        Assert.AreEqual("EXT-TRUST-02", Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            ConsoleIpcTrust.VerifyImage(image, endpoint)).Message);
        Assert.AreEqual("EXT-TRUST-02", Assert.ThrowsExactly<UnauthorizedAccessException>(() =>
            ConsoleIpcTrust.VerifyImage(image, endpoint with { ProtectedPath = false })).Message);
    }

    private sealed class Registration(bool missing) : ConsoleIpcDiscovery
    {
        internal int Attempts;
        internal override ConsoleIpcEndpoint? Find() => missing ? null : Validate("test", null, "S-1-5-18", null, true);
        internal override void Verify(SafePipeHandle pipe, ConsoleIpcEndpoint endpoint, long openedAfter) => Attempts++;
    }

    [TestMethod]
    public void AuthenticodeRequiresTheLeafFromTheVerifiedSignature()
    {
        string image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        using var certificate = X509Certificate.CreateFromSignedFile(image);
        string pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var endpoint = ConsoleIpcDiscovery.Validate("test", null, "S-1-5-18", pin.ToLowerInvariant(), true);
        ConsoleIpcTrust.VerifyImage(image, endpoint);
        string other = (pin[0] == 'A' ? "B" : "A") + pin[1..];
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => ConsoleIpcTrust.VerifyImage(image, endpoint with { Thumbprint = other }));
    }

    [TestMethod, DataRow(true), DataRow(false)]
    public void DiscoveryFailuresPreservePublicStates(bool missing)
    {
        var registry = new Registration(missing);
        using var client = new ConsoleIpcClient(registry, 2);
        var status = client.Inspect(Guid.NewGuid(), "viewer", true).Snapshot;
        Assert.AreEqual(missing ? ConsoleExtensionStateCode.Unreachable : ConsoleExtensionStateCode.AccessDenied, status.Host.Code);
        Assert.AreEqual(ConsoleExtensionStateCode.Unverified, status.Authorization.Code);
        Assert.AreEqual(missing ? "EXT-001" : "EXT-TRUST-01", status.DiagnosticId);
        Assert.AreEqual(0, registry.Attempts);
    }

    [TestMethod]
    public async Task EnvelopeOffsetsAndMandatoryFeaturesAreInteroperable()
    {
        const ulong request = 0x0807060504030201, session = 0x100f0e0d0c0b0a09;
        byte[] golden = Convert.FromHexString("455848430100000020001000290000000102030405060708090A0B0C0D0E0F10000000000000000055");
        CollectionAssert.AreEqual(golden, ConsoleIpcWire.Encode(IpcMessage.OpenSession, request, session, w => w.Write((byte)0x55)));
        foreach (int offset in new[] { 4, 8, 32, 12 })
        {
            var bad = (byte[])golden.Clone();
            bad[offset] = offset == 12 ? (byte)39 : (byte)2;
            var failure = await Assert.ThrowsExactlyAsync<IpcFailure>(() => ConsoleIpcWire.Read(new MemoryStream(bad), default));
            Assert.AreEqual(0xec000002u, failure.Code);
        }
        golden[36] = 1;
        var packet = await ConsoleIpcWire.Read(new MemoryStream(golden), default);
        Assert.AreEqual(session, packet.Session);
        CollectionAssert.AreEqual(new byte[] { 0x55 }, packet.Body);
    }

    private sealed class Restartable(ConsoleIpcTestPeer initial) : ConsoleIpcDiscovery
    {
        internal ConsoleIpcTestPeer Service = initial;
        internal override Stream Open() => Volatile.Read(ref Service).Open();
    }

    [TestMethod]
    public async Task RestartInvalidatesOldGrantsAndAuthenticatesTheNewInstance()
    {
        using var first = new ConsoleIpcTestPeer(false);
        using var second = new ConsoleIpcTestPeer(false);
        var endpoint = new Restartable(first);
        using var client = new ConsoleIpcClient(endpoint, 2);
        Guid vm = Guid.NewGuid();
        var initial = Inspect(client, vm);
        var offline = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SnapshotChanged += id => { if (id == Guid.Empty) offline.TrySetResult(); };
        Volatile.Write(ref endpoint.Service, second);
        first.DropControl();
        await offline.Task.WaitAsync(Timeout);
        Assert.IsTrue(initial.Revoked.IsCancellationRequested);
        var fresh = Inspect(client, vm);
        Assert.AreNotEqual(initial.Instance, fresh.Instance);
        Assert.IsFalse(fresh.Revoked.IsCancellationRequested);
        Assert.AreEqual(1, second.Verified);
    }

    [TestMethod]
    public async Task RingRejectsTornWritesAndRetainedPixelsOutliveConcurrentSlotReuse()
    {
        const int size = 4096;
        using var mapping = MemoryMappedFile.CreateNew(null, size);
        using var view = mapping.CreateViewAccessor();
        using var borrowed = new SafeFileHandle(mapping.SafeMemoryMappedFileHandle.DangerousGetHandle(), false);
        using var ring = new ConsoleIpcCpuRing(borrowed, 4, size, 1, size);
        ConsoleIpcTestPeer.FullFrame(view, 4, 8, 4, 1, 17);
        using var frame = ring.Read(4, 0, 1);
        Assert.IsNotNull(frame);
        using var retained = frame.Retain();
        frame.Dispose();
        byte[] original = Enumerable.Repeat((byte)17, 128).ToArray();
        view.Write(0, 3L);
        Assert.IsNull(ring.Read(4, 0, 1));
        view.Write(0, 4L); view.Write(48, int.MaxValue);
        Assert.IsNull(ring.Read(4, 0, 1));
        ConsoleIpcTestPeer.FullFrame(view, 4, 8, 4, 2, 2);
        view.Write(0, 5L); view.Write(32, 1); view.Write(24, 1L);
        view.Write(56, 2); view.Write(60, 2); view.Write(64, 256); view.Write(68, 288);
        int[] rectangles = [0,0,1,1, 3,1,2,2, 1,1,0,1,1,1, 5,2,4,0,2,1];
        view.WriteArray(256, rectangles, 0, rectangles.Length);
        Thread.MemoryBarrier(); view.Write(0, 6L);
        using var delta = ring.Read(4, 0, 2);
        Assert.IsNotNull(delta);
        CollectionAssert.AreEqual(new ConsoleFrameRegion[] { new(0,0,1,1), new(3,1,2,2) }, delta.DirtyRegions.ToArray());
        CollectionAssert.AreEqual(new ConsoleFrameMove[] { new(1,1,new(0,1,1,1)), new(5,2,new(4,0,2,1)) }, delta.MoveRegions.ToArray());
        view.Write(288, int.MaxValue);
        Assert.IsNull(ring.Read(4, 0, 2));
        var writer = Task.Run(() =>
        {
            for (int i = 4; i < 4096; i++) ConsoleIpcTestPeer.FullFrame(view, 4, 8, 4, i, (byte)i);
        });
        do
        {
            long seq = view.ReadInt64(16);
            using var copy = ring.Read(4, 0, seq);
            if (copy != null) Assert.IsTrue(copy.CopyPixels().All(b => b == (byte)seq));
        } while (!writer.IsCompleted);
        await writer;
        ring.Dispose(); mapping.Dispose(); view.Dispose();
        CollectionAssert.AreEqual(original, retained.CopyPixels());
    }

    private static IpcAccess Inspect(ConsoleIpcClient client, Guid vm)
    {
        var access = client.Inspect(vm, "viewer", true);
        Assert.AreEqual(ConsoleExtensionStateCode.Ready, access.Snapshot.Host.Code, access.Snapshot.DiagnosticId);
        return access;
    }

    [TestMethod, DataRow(true), DataRow(false)]
    public async Task LocalPipesShareControlAndIsolateSessionInputModesAndCursor(bool namedPipe)
    {
        using var peer = new ConsoleIpcTestPeer(namedPipe) { ReasonText = "尚未激活；请前往扩展程序完成授权·重试" };
        using var client = new ConsoleIpcClient(peer, 1);
        Guid vm = Guid.NewGuid();
        var access = Inspect(client, vm);
        Assert.AreEqual("尚未激活，请前往扩展程序完成授权/重试", access.Snapshot.Reason);
        Assert.AreEqual(63ul, access.SupportedMask);
        Assert.AreEqual(63ul, access.GrantedMask);
        Assert.IsTrue(client.AllowsFallback(vm, "RdpEnhanced", "viewer"));
        await using var session = client.Session(new() { VmId = vm, ViewerContextId = "context" }, access);
        var loggedIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource<ConsoleDisconnectInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cursor = Channel.CreateUnbounded<ConsoleCursorState>();
        var shape = new TaskCompletionSource<ConsoleCursor?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.LoginCompleted += () => loggedIn.TrySetResult();
        session.CursorStateChanged += value => cursor.Writer.TryWrite(value);
        session.CursorChanged += value => shape.TrySetResult(value);
        session.Disconnected += value => disconnected.TrySetResult(value);
        session.Start();
        await loggedIn.Task.WaitAsync(Timeout);
        Assert.AreEqual(2, peer.Verified);
        await CheckClipboardAndAudio(peer, session, vm);
        Assert.IsTrue(session.SetMode("desktop-1").Accepted);
        Assert.IsTrue(session.SendKey(42, false, true));
        using (var input = await peer.Receive(IpcMessage.InputKey))
        { Assert.AreEqual(5UL, input.ReadUInt64()); Assert.AreEqual(1UL, input.ReadUInt64()); }
        async Task Cursor(bool baked)
        {
            await peer.Push(IpcMessage.CursorState, w =>
            { w.Write(4); w.Write(5); w.Write(true); w.Write(baked); w.Write(1); w.Write(3UL); });
            var state = await cursor.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
            Assert.AreEqual(baked, state.Composited);
        }
        await peer.Push(IpcMessage.ControlChanged, w => { w.Write(true); w.Write(false); w.Write(6UL); });
        await Cursor(true);
        Assert.IsFalse(session.SendPause());
        await peer.Push(IpcMessage.ControlChanged, w => { w.Write(true); w.Write(true); w.Write(7UL); });
        await peer.Push(IpcMessage.CursorShape, w => { w.Write(true); w.Write(1); w.Write(1); w.Write(0); w.Write(0); w.Write(0xffffffffu); });
        Assert.AreEqual(4, (await shape.Task.WaitAsync(Timeout))!.Pixels.Length);
        await Cursor(false);
        Assert.IsTrue(session.SendPause());
        using (var input = await peer.Receive(IpcMessage.InputPause))
        { Assert.AreEqual(7UL, input.ReadUInt64()); Assert.AreEqual(2UL, input.ReadUInt64()); }
        await peer.Push(IpcMessage.SessionDisconnected, w => { w.Write(IpcFailure.Incompatible); w.Text("EXT-IPC-02"); });
        var end = await disconnected.Task.WaitAsync(Timeout);
        Assert.AreEqual(ConsoleBackendFailure.ProtocolIncompatible, ConsoleSessionState.Classify("External", end.ErrorCode));
        var changed = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SnapshotChanged += id => changed.TrySetResult(id);
        await peer.Changed(vm);
        Assert.AreEqual(vm, await changed.Task.WaitAsync(Timeout));
        Assert.IsTrue(access.Revoked.IsCancellationRequested);
    }

    private static async Task CheckClipboardAndAudio(ConsoleIpcTestPeer peer, ConsoleIpcSession session, Guid vm)
    {
        byte[] content = [7, 9, 1, 4];
        var clipboard = new TaskCompletionSource<ConsoleClipboardData>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ClipboardReceived += value => clipboard.TrySetResult(value);
        await peer.Push(IpcMessage.ClipboardAvailability, w =>
        {
            w.Text("test-provider"); w.Write(vm.ToByteArray()); w.Text("guest"); w.Write(true); w.Write(true); w.Write(true);
        });
        await peer.Push(IpcMessage.ClipboardData, w =>
        { w.Write(1); w.Write(13u); w.Text(null); w.Write(content.Length); w.Write(content); });
        var data = await clipboard.Task.WaitAsync(Timeout);
        Assert.AreEqual(new ConsoleClipboardProviderInfo("test-provider", vm, "guest", true, true, true), session.ProviderInfo);
        Assert.IsTrue(session.SetClipboard(data));
        using (var r = await peer.Receive(IpcMessage.SetClipboard))
        {
            Assert.AreEqual(5UL, r.ReadUInt64()); Assert.AreEqual(1, r.ReadInt32()); Assert.AreEqual(13u, r.ReadUInt32());
            Assert.IsNull(r.Text()); CollectionAssert.AreEqual(content, r.Bytes(r.ReadInt32()));
        }
        var audio = new TaskCompletionSource<ConsoleAudioPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.AudioReceived += value => audio.TrySetResult(value);
        static void Pcm(BinaryWriter w) { w.Write(48000); w.Write(2); w.Write(0); }
        await peer.Push(IpcMessage.AudioAvailability, w => { w.Write(true); w.Write(true); Pcm(w); });
        await peer.Push(IpcMessage.AudioAvailability, w => { w.Write(false); w.Write(true); });
        await peer.Push(IpcMessage.AudioOutput, w =>
        { Pcm(w); w.Write(1L); w.Write(150L); w.Write(content.Length); w.Write(content); });
        var packet = await audio.Task.WaitAsync(Timeout);
        Assert.AreEqual(150L, packet.Timestamp.Ticks);
        CollectionAssert.AreEqual(content, packet.CopySamples());
        Assert.IsTrue(session.SendAudio(packet.Format, content));
        using var input = await peer.Receive(IpcMessage.AudioInput);
        Assert.AreEqual(5UL, input.ReadUInt64()); Assert.AreEqual(48000, input.ReadInt32()); Assert.AreEqual(2, input.ReadInt32());
        Assert.AreEqual(0, input.ReadInt32()); CollectionAssert.AreEqual(content, input.Bytes(input.ReadInt32()));
    }

    [TestMethod, DataRow(true), DataRow(false)]
    public async Task PipeTexturePresentsAndSwitchesToNewCpuEpoch(bool namedPipe)
    {
        using var peer = new ConsoleIpcTestPeer(namedPipe) { Gpu = true };
        using var client = new ConsoleIpcClient(peer, 1);
        Guid vm = Guid.NewGuid();
        await using var session = client.Session(new() { VmId = vm }, Inspect(client, vm));
        using var delivery = session.OpenDelivery();
        var available = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        delivery.Available += () => available.TrySetResult();
        session.Start();
        ulong id = await peer.Delivery.Task.WaitAsync(Timeout);
        using var texture = new ConsoleIpcTestTexture(64, 64);
        await peer.AttachGpu(texture.Shared, id, texture.Adapter, 64, 64);
        await peer.ReadyGpu(id, 1);
        await available.Task.WaitAsync(Timeout);
        using var frame = delivery.TakeLatest();
        Assert.IsNotNull(frame);
        using var renderer = new ConsoleSwapChainRenderer(_ => 0, () => (1, 1), (_, _) => { });
        Assert.IsTrue(renderer.TryPresent(frame, 64, 64));
        CollectionAssert.AreEqual(texture.Pixels, DesktopConsoleGraphicsTests.Readback(renderer));
        ulong[] epochs = await Task.WhenAll(Task.Run(delivery.RequestCpu), Task.Run(delivery.RequestCpu)).WaitAsync(Timeout);
        CollectionAssert.AreEqual(new ulong[] { 8, 8 }, epochs);
        Assert.AreEqual(1, peer.CpuSwitches);
        using var mapping = MemoryMappedFile.CreateNew(null, 4096);
        using var view = mapping.CreateViewAccessor();
        ConsoleIpcTestPeer.FullFrame(view, 8, 4, 4, 2, 17);
        var pixels = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.FrameReceived += f => pixels.TrySetResult(f.CopyPixels());
        await peer.AttachRing(mapping, 4096, 8);
        await peer.ReadyCpu(8, 2);
        CollectionAssert.AreEqual(Enumerable.Repeat((byte)17, 64).ToArray(), await pixels.Task.WaitAsync(Timeout));
        await session.DisconnectAsync().WaitAsync(Timeout);
    }
}
