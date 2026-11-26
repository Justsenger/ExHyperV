using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using ExHyperV.ConsoleIpc;
using ExHyperV.ConsoleIpc.Tests;
using ExHyperV.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ExHyperV.Server.Tests;

public sealed partial class ConsoleFanoutTests
{
    private Task<SocketHost> IpcHost(ConsoleIpcTestPeer peer) => SocketHost.Start(configure: services =>
    {
        services.AddSingleton<ConsoleIpcDiscovery>(peer);
        services.AddSingleton<IConsoleSessionFactory>(new FakeConsole());
    }, root: Root);

    [TestMethod, DataRow(true), DataRow(false)]
    public async Task DefaultProviderPassesPipeTextureToWebSocketH264(bool namedPipe)
    {
        if (!ConsoleH264Encoder.Probe.VideoProcessorAvailable) Assert.Inconclusive("Hardware video processing is unavailable.");
        using var peer = new ConsoleIpcTestPeer(namedPipe) { Gpu = true };
        await using var host = await IpcHost(peer);
        Assert.IsInstanceOfType<ConsoleIpcServerProvider>(host.App.Services.GetRequiredService<IConsoleExtensionProvider>());
        var login = await Login(host);
        using var ws = await Connect(host, login, "External", "h264", "Watch");
        ulong id = await peer.Delivery.Task.WaitAsync(ws.Timeout);
        using var texture = new ConsoleIpcTestTexture(320, 240);
        await peer.AttachGpu(texture.Shared, id, texture.Adapter, 320, 240);
        await peer.ReadyGpu(id, 1);
        Assert.AreEqual("GpuShared", (await ws.Json("transport")).GetProperty("transportKind").GetString());
        var packet = await ws.Binary();
        Assert.AreEqual(3, packet[2]);
        Assert.AreEqual(1, packet[3] & 1);
        await ws.Close();
    }

    [TestMethod, DataRow(true), DataRow(false)]
    public async Task DefaultProviderFallsBackPerDeliveryAndRevokedViewerReceivesZeroNewFrames(bool namedPipe)
    {
        using var peer = new ConsoleIpcTestPeer(namedPipe) { Gpu = true };
        await using var host = await IpcHost(peer);
        var login = await Login(host);
        var other = await Login(host);
        using var ws = await Connect(host, login, "External", "h264", "Watch");
        using var survivor = await Connect(host, other, "External", "h264", "Watch");
        await ws.Json("control"); await survivor.Json("control");
        ulong id = await peer.Delivery.Task.WaitAsync(ws.Timeout);
        using var texture = new ConsoleIpcTestTexture(320, 240);
        await peer.AttachGpu(texture.Shared, id, ~texture.Adapter, 320, 240);
        await peer.ReadyGpu(id, 1);
        await peer.CpuRequested.Task.WaitAsync(ws.Timeout);
        var path = await ws.Json("transport");
        Assert.AreEqual("CpuShared", path.GetProperty("transportKind").GetString());
        Assert.AreEqual(8, path.GetProperty("surfaceEpoch").GetInt32());

        const int bytes = 320 * 240 * 4 + 88;
        using var mapping = MemoryMappedFile.CreateNew(null, bytes);
        using var view = mapping.CreateViewAccessor();
        await peer.AttachRing(mapping, bytes, 8);
        async Task Emit(int sequence)
        {
            ConsoleIpcTestPeer.FullFrame(view, 8, 320, 240, sequence, (byte)sequence);
            await peer.ReadyCpu(8, sequence);
            byte[] packet;
            do { packet = await survivor.Binary(); } while (packet[2] != 3);
            Assert.AreEqual((long)sequence, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(6)));
        }
        await Emit(2);
        Assert.AreEqual(3, (await ws.Binary())[2]);
        var auth = host.App.Services.GetRequiredService<Authentication>();
        var revoked = auth.Subscribers(login.SessionId).Single();
        await peer.Changed(vm, login.SessionId.ToString("N"));
        while (auth.Subscribers(login.SessionId).Length > 0) await Task.Delay(10, ws.Timeout);
        long final = revoked.DeliveredFrames;
        for (int i = 3; i <= 6; i++) await Emit(i);
        Assert.AreEqual(0L, revoked.DeliveredFrames - final);
        Assert.AreEqual(2, peer.Verified);
        await survivor.Close();
    }
}
