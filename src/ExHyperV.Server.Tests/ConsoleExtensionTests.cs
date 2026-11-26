using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExHyperV.Server.Hosting;
using ExHyperV.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32.SafeHandles;

namespace ExHyperV.Server.Tests;

public sealed partial class ConsoleFanoutTests
{
    private sealed class ExtensionConsole : FakeConsole, IConsoleCursorStateSource, IConsoleDisplayModes, IConsoleGpuSource
    {
        internal readonly List<GpuPath> Paths = [];
        internal readonly ConsoleDisplayMode Mode = new("desktop-1", 320, 240, 60000, 1001);
        internal string? Applied;
        public ConsoleCursorState? CursorState => null;
        public event Action<ConsoleCursorState>? CursorStateChanged;
        internal void Position() => CursorStateChanged?.Invoke(new(17, 23, true, false, ConsoleCoordinateSpace.DesktopPixels, 2));
        public IReadOnlyList<ConsoleDisplayMode> GetModes() => [Mode];
        public ConsoleDisplayModeResult SetMode(string id)
        {
            Applied = id;
            return new(id == Mode.Id, Mode);
        }
        public IConsoleGpuDelivery OpenDelivery() { var path = new GpuPath(); Paths.Add(path); return path; }
    }

    private sealed class GpuPath : IConsoleGpuDelivery
    {
        internal int CpuRequests;
        public event Action? Available;
        public void Publish() => Available?.Invoke();
        public ConsoleGpuFrame TakeLatest() => new(-1, 7, 0, 5, 320, 240, new SafeFileHandle(0, false), 0, 1);
        public ulong RequestCpu() { CpuRequests++; return 8; }
        public void Dispose() { }
    }

    private sealed class Provider(ExtensionConsole console) : IConsoleExtensionProvider
    {
        public string BackendId => "External";
        internal readonly CancellationTokenSource Permission = new();
        internal ulong Version = 1;
        internal string? ViewerContext;
        public ExtensionAccess Inspect(Guid vm, SocketAuthorization viewer)
        {
            var ready = new ConsoleExtensionStage((int)ConsoleExtensionStateCode.Ready);
            var caps = ConsoleCapabilities.Video | ConsoleCapabilities.Input | ConsoleCapabilities.DisplayResize;
            return new(new(Version, ready, ready, ready, ready, ConsoleTransportKind.GpuShared, 7, caps, caps,
                new(true, false, 0), [ConsoleExtensionActionId.MaintainHost], "private provider text", "EXT-700"), [console.Mode],
                "guest-session-1", "web-distribution", Permission.Token);
        }
        public IConsoleConnection Connect(ConsoleConnectionOptions options, ExtensionAccess access)
        {
            ViewerContext = options.ViewerContextId;
            return console.Create(options, BackendId);
        }
    }

    [TestMethod]
    public async Task ExtensionReportsModesCursorAndRevokesStaleVersions()
    {
        var fake = new ExtensionConsole();
        var provider = new Provider(fake);
        await using var host = await SocketHost.Start(configure: s => { s.AddSingleton<IConsoleExtensionProvider>(provider); s.AddSingleton<IConsoleSessionFactory>(fake); }, root: Root);
        var login = await Login(host);
        string stale = await Ticket(host, login, "External", "Watch");
        provider.Version++;
        await host.Reject($"v2/console/{vm}?backend=External&ticket={stale}", HttpStatusCode.Forbidden);
        var json = await host.Http.GetFromJsonAsync<JsonElement>($"vms/{vm}/console/backends");
        var ext = json.GetProperty("backends")[2];
        Assert.AreEqual(60000, ext.GetProperty("modes")[0].GetProperty("refreshNumerator").GetInt32());
        Assert.AreEqual(0, ext.GetProperty("extension").GetProperty("availableActions").GetArrayLength());
        Assert.IsFalse(json.ToString().Contains("private provider text"));
        using var ws = await Connect(host, login, "External", "bgra", "Watch", "Control", "DisplayMode");
        var caps = await ws.Json("capabilities");
        Assert.AreEqual("desktop-1", caps.GetProperty("modes")[0].GetProperty("id").GetString());
        var control = await ws.Json("control");
        long generation = control.GetProperty("generation").GetInt64();
        Assert.AreEqual("display", provider.ViewerContext);
        await ws.Send(new { type = "setMode", id = fake.Mode.Id, generation, inputSequence = 1 });
        Assert.IsTrue((await ws.Json("displayMode")).GetProperty("result").GetProperty("accepted").GetBoolean());
        fake.Position();
        var cursor = (await ws.Json("cursorState")).GetProperty("state");
        Assert.IsFalse(cursor.GetProperty("composited").GetBoolean());
        Assert.AreEqual(23, cursor.GetProperty("y").GetInt32());
        provider.Permission.Cancel();
        await WaitUntil(() => fake.Disposals == 1);
        Assert.AreEqual(0, fake.LiveFrames);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnopenableGpuSurfaceFallsBackOnlyForItsEncoder(bool service)
    {
        var fake = new ExtensionConsole();
        var provider = new Provider(fake);
        var independent = (GpuPath)fake.OpenDelivery();
        fake.Capture = () => fake.DesktopFrame(320, 240, 1280, new byte[320 * 240 * 4], 6, 5, new(8, 2, 4, 2), false);
        await using var host = await SocketHost.Start(service ? ["--service"] : [], s => { s.AddSingleton<IConsoleExtensionProvider>(provider); s.AddSingleton<IConsoleSessionFactory>(fake); }, Root);
        var login = await Login(host);
        using var ws = await Connect(host, login, "External", "h264", "Watch", "Control");
        var holder = await ws.Json("control");
        Assert.IsTrue(holder.GetProperty("hasControl").GetBoolean());
        Assert.AreEqual(2, fake.Paths.Count);
        fake.Paths[1].Publish();
        var path = await ws.Json("transport");
        Assert.AreEqual("CpuShared", path.GetProperty("transportKind").GetString());
        Assert.AreEqual(8, path.GetProperty("surfaceEpoch").GetInt32());
        byte[] video = await ws.Binary();
        Assert.AreEqual(1, video[3] & 1);
        Assert.AreEqual(1, fake.Paths[1].CpuRequests);
        Assert.AreEqual(0, independent.CpuRequests);
        Assert.AreEqual(1, fake.Created);
        await ws.Send(new { type = "key", generation = holder.GetProperty("generation").GetInt64(), inputSequence = 1, scanCode = 18, down = true });
        await ws.Send(new { type = "ping" }); await Message(ws, "pong");
        CollectionAssert.Contains(fake.Inputs, "key 18 False True");
        await ws.Close();
        Assert.AreEqual(0, fake.LiveFrames);
    }

    [TestMethod]
    public async Task LateH264ViewerReceivesParameterSetsAndIdrFromSameGroupBeforeDeltas()
    {
        var fake = new FakeConsole();
        byte[] pixels = new byte[128 * 128 * 4];
        long sequence = 1;
        fake.Capture = () => fake.DesktopFrame(128, 128, 512, pixels, sequence, sequence - 1, new(0, 0, 8, 8), false);
        await using var host = await SocketHost.Start(configure: s => s.AddSingleton<IConsoleSessionFactory>(fake), root: Root);
        var a = await Login(host); var b = await Login(host);
        using var first = await Connect(host, a, "RdpBasic", "h264", "Watch");
        await first.Json("control");
        using (var frame = fake.Capture()) fake.Publish(frame);
        Assert.AreEqual(3, (await first.Binary())[2]);
        sequence++;
        using var late = await Connect(host, b, "RdpBasic", "h264", "Watch");
        var idr = await late.Binary();
        Assert.AreEqual(3, idr[2]); Assert.AreEqual(1, idr[3] & 1);
        HashSet<int> nals = [];
        for (int i = 34; i + 3 < idr.Length; i++)
            if (idr[i] == 0 && idr[i + 1] == 0 && idr[i + 2] == 1) nals.Add(idr[i + 3] & 31);
        Assert.IsTrue(nals.IsSupersetOf([5, 7, 8]));
        Assert.AreEqual(1, fake.FrameListeners);
        byte[] same;
        do { same = await first.Binary(); } while (same[2] != 3);
        CollectionAssert.AreEqual(idr, same);
        var auth = host.App.Services.GetRequiredService<Authentication>();
        var subscription = auth.Subscribers(b.SessionId).Single();
        Assert.IsTrue(auth.Revoke(b.SessionId));
        long delivered = subscription.DeliveredFrames;
        var idle = await first.Binary();
        Assert.AreEqual(1, idle[2]); Assert.AreEqual(1, idle[3]);
        Assert.AreEqual(delivered, subscription.DeliveredFrames);
        await first.Close();
        Assert.AreEqual(0, fake.LiveFrames);
    }

    [TestMethod]
    public async Task SlowH264ViewerRecoversWithIdrAfterMissingDependencies()
    {
        var fake = new FakeConsole();
        var writer = new DelayedTransport();
        byte[] pixels = new byte[128 * 128 * 4];
        long seq = 1;
        fake.Capture = () => fake.DesktopFrame(128, 128, 512, pixels, seq, seq - 1, new(0, 0, 4, 4), false);
        await using var host = await SocketHost.Start(configure: s =>
        { s.AddSingleton<IConsoleSessionFactory>(fake); s.AddSingleton<ConsoleTransport>(writer); }, root: Root);
        var a = await Login(host); var b = await Login(host);
        using var fast = await Connect(host, a, "RdpBasic", "h264", "Watch");
        await fast.Json("control");
        writer.Delay = b.SessionId;
        using var slow = await Connect(host, b, "RdpBasic", "h264", "Watch");
        await writer.Blocked.Task.WaitAsync(fast.Timeout);
        await fast.Binary();
        for (seq = 2; seq <= 4; seq++)
        {
            using (var frame = fake.Capture()) fake.Publish(frame);
            byte[] received;
            do { received = await fast.Binary(); } while (received[2] != 3);
            Assert.AreEqual(seq, BinaryPrimitives.ReadInt64LittleEndian(received.AsSpan(6)));
        }
        writer.Delay = null;
        writer.Resume.SetResult();
        byte[] first = await slow.Binary(), recovered = await slow.Binary();
        Assert.AreEqual(1, first[3] & 1);
        Assert.AreEqual(4L, BinaryPrimitives.ReadInt64LittleEndian(recovered.AsSpan(6)));
        Assert.AreEqual(1, recovered[3] & 1);
        Assert.AreEqual(1, fake.FrameListeners);
        await fast.Close(); await slow.Close();
        Assert.AreEqual(0, fake.LiveFrames);
    }

    [TestMethod]
    public void WarmLosslessEncodersKeepPerPacketManagedAllocationBelowLohThreshold()
    {
        const int width = 1920, height = 1080;
        using var frame = new ConsoleFrame(width, height, width * 4, new byte[width * height * 4], 1);
        foreach (string encoding in new[] { "bgra", "deflate", "png", "regions" })
        {
            ConsolePacket Build() => encoding == "regions" ? ConsoleWire.Regions(frame, true)
                : ConsoleWire.Video(frame, encoding, 0, 0, 0);
            using (Build()) { }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 4; i++)
            {
                using var packet = Build();
                Assert.AreEqual(packet.Length, (long)packet.Data.Length);
            }
            long perPacket = (GC.GetAllocatedBytesForCurrentThread() - before) / 4;
            Assert.IsLessThan(85_000L, perPacket, encoding + " " + perPacket);
        }
    }

    [TestMethod]
    public async Task LoginFrameHandlesAreIsolatedAndRevocationReleasesRetainedPixels()
    {
        await using var host = await SocketHost.Start(root: Root);
        var auth = host.App.Services.GetRequiredService<Authentication>();
        var login = await Login(host); var other = await Login(host);
        var resources = host.App.Services.GetRequiredService<LoginResources>().For(auth.Principal(login.SessionId));
        var fake = new FakeConsole();
        Guid frame = resources.Objects.Add(fake.CaptureFrame());
        host.Http.DefaultRequestHeaders.Authorization = new("Bearer", login.Token);
        Assert.AreEqual(HttpStatusCode.OK, (await host.Http.GetAsync("binaries/" + frame)).StatusCode);
        host.Http.DefaultRequestHeaders.Authorization = new("Bearer", other.Token);
        Assert.AreEqual(HttpStatusCode.BadRequest, (await host.Http.GetAsync("binaries/" + frame)).StatusCode);
        var list = await host.Http.GetFromJsonAsync<JsonElement>("objects");
        Assert.AreEqual(0, list.GetProperty("objects").GetArrayLength());
        Assert.IsTrue(auth.Revoke(login.SessionId));
        Assert.AreEqual(0, fake.LiveFrames);
    }

    [TestMethod]
    public void RegionPacketRestoresOverlappingMovesBeforeTwoDirtyUpdates()
    {
        ConsoleFrameRegion[] dirty = [new(0, 0, 1, 1), new(4, 0, 1, 1)];
        ConsoleFrameMove[] moves = [new(2, 0, new(1, 0, 2, 1)), new(1, 0, new(3, 0, 1, 1))];
        byte[] original = [1, 2, 3, 4, 5], expected = [9, 3, 4, 2, 8];
        using var frame = new ConsoleFrame(5, 1, 20, expected.SelectMany(b => new byte[] { b, b, b, 255 }).ToArray(),
            11, 10, ConsoleFrameUpdateKind.Delta, dirty, moves);
        using var packet = ConsoleWire.Regions(frame, false);
        var p = packet.Data.ToArray();
        Assert.AreEqual(p.Length, BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(6)));
        int meta = 40, data = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(34));
        int U16(int i) => BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(i));
        byte[] reconstructed = original.ToArray();
        for (int i = 0; i < U16(32); i++)
        {
            int offset = meta + U16(30) * 8 + i * 12;
            original.AsSpan(U16(offset), U16(offset + 8)).CopyTo(reconstructed.AsSpan(U16(offset + 4)));
        }
        for (int i = 0; i < U16(30); i++, meta += 8)
            for (int x = 0; x < U16(meta + 4); x++, data += 4) reconstructed[U16(meta) + x] = p[data];
        Assert.AreEqual(data, p.Length);
        CollectionAssert.AreEqual(expected, reconstructed);
    }
}
