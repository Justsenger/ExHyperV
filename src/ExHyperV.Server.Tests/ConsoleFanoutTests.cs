using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using ExHyperV.Server.Hosting;
using ExHyperV.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ExHyperV.Server.Tests;

[TestClass]
public sealed partial class ConsoleFanoutTests
{
    private const string Root = "test-console-broker-key-0123456789abcdef";
    private readonly Guid vm = Guid.NewGuid();
    private sealed class DelayedTransport : ConsoleTransport
    {
        internal readonly TaskCompletionSource Blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Guid? Delay;
        internal override async ValueTask Send(SocketAuthorization grant, WebSocket socket, ReadOnlyMemory<byte> data,
            WebSocketMessageType type, CancellationToken token)
        {
            if (type == WebSocketMessageType.Binary && Delay == grant.SessionId)
            {
                Blocked.TrySetResult();
                await Resume.Task.WaitAsync(token);
            }
            await base.Send(grant, socket, data, type, token);
        }
    }

    private static async Task<LoginReply> Login(SocketHost host)
    {
        using var reply = await host.Http.PostAsJsonAsync("auth/login", new { token = Root });
        reply.EnsureSuccessStatusCode();
        return (await reply.Content.ReadFromJsonAsync<LoginReply>())!;
    }

    private async Task<string> Ticket(SocketHost host, LoginReply login, string backend, params string[] rights)
    {
        host.Http.DefaultRequestHeaders.Authorization = new("Bearer", login.Token);
        using var result = await host.Http.PostAsJsonAsync("auth/ws-ticket", new { vmId = vm, channel = "console",
            backendId = backend, requestedRights = rights, viewerContextId = "display" });
        result.EnsureSuccessStatusCode();
        return (await result.Content.ReadFromJsonAsync<TicketReply>())!.Ticket;
    }

    private static async Task<JsonElement> Message(WireClient socket, string type)
    {
        while (true)
        {
            var message = await socket.Read();
            if (message.Type != WebSocketMessageType.Text) continue;
            var json = JsonSerializer.Deserialize<JsonElement>(message.Bytes);
            if (json.GetProperty("type").GetString() == type) return json;
            Assert.AreNotEqual("error", json.GetProperty("type").GetString(), json.ToString());
        }
    }

    private async Task<WireClient> Connect(SocketHost host, LoginReply login, string backend, string encoding, params string[] rights)
        => await host.Connect($"v2/console/{vm}?backend={backend}&frameEncoding={encoding}&ticket={await Ticket(host, login, backend, rights)}");

    [TestMethod]
    public async Task ScopedTicketsBindTargetChannelBackendAndAreSingleUse()
    {
        await using var host = await SocketHost.Start(configure: s => s.AddSingleton<IConsoleSessionFactory>(new FakeConsole()), root: Root);
        var login = await Login(host);
        var auth = host.App.Services.GetRequiredService<Authentication>();
        var grant = auth.ConsumeTicket(await Ticket(host, login, "RdpBasic", "Watch"));
        Assert.IsNotNull(grant);
        Assert.AreEqual(login.SessionId, grant.SessionId);
        Assert.AreEqual(login.SessionId.ToString("N"), grant.Subject);
        Assert.AreEqual(vm, grant.VmId);
        Assert.AreEqual(ConsoleRights.Watch, grant.Rights);
        Assert.AreEqual("display", grant.ViewerContextId);
        Assert.IsGreaterThan(0L, grant.Version);
        foreach (string target in new[] { "events", $"console/{vm}", $"v2/console/{Guid.NewGuid()}", $"v2/console/{vm}?backend=RdpEnhanced" })
        {
            string ticket = await Ticket(host, login, "RdpBasic", "Watch");
            await host.Reject(target + (target.Contains('?') ? "&" : "?") + "ticket=" + ticket, HttpStatusCode.Forbidden);
            await host.Reject($"v2/console/{vm}?ticket={ticket}", HttpStatusCode.Unauthorized);
        }
        string legacy = (await (await host.Http.PostAsJsonAsync("auth/ws-ticket", new { })).Content.ReadFromJsonAsync<TicketReply>())!.Ticket;
        await host.Reject($"v2/console/{vm}?ticket={legacy}", HttpStatusCode.Forbidden);
        await host.Reject($"v2/console/{vm}?requiredFeatures=1&ticket={await Ticket(host, login, "RdpBasic", "Watch")}", HttpStatusCode.BadRequest);
        var summary = await host.Http.GetFromJsonAsync<JsonElement>($"vms/{vm}/console/backends");
        var extension = summary.GetProperty("backends")[2].GetProperty("extension");
        Assert.AreEqual((int)ConsoleExtensionStateCode.Unreachable, extension.GetProperty("host").GetProperty("rawCode").GetInt32());
        Assert.AreEqual((int)ConsoleExtensionStateCode.Unverified, extension.GetProperty("authorization").GetProperty("rawCode").GetInt32());
    }

    [TestMethod]
    public async Task ReadOnlyRequiresExplicitWatchAndRejectsGuestWrites()
    {
        var fake = new FakeConsole();
        await using var host = await SocketHost.Start(["--read-only"], s => s.AddSingleton<IConsoleSessionFactory>(fake), Root);
        var login = await Login(host);
        using var watch = await Connect(host, login, "RdpBasic", "bgra", "Watch");
        var state = await watch.Json("control");
        Assert.IsFalse(state.GetProperty("canTake").GetBoolean());
        await watch.Send(new { type = "takeControl", generation = 0 });
        await watch.Json("error");
        foreach (string type in new[] { "key", "resize", "clipboard", "setMode", "releaseAllInputs" })
        {
            await watch.Send(new { type, generation = 0, inputSequence = 1 });
            await watch.Json("error");
        }
        await watch.Audio(new byte[34]); await watch.Json("error");
        Assert.IsEmpty(fake.Inputs);
        Assert.IsNull(fake.Microphone);
        Assert.IsNull(fake.ClipboardData);
        fake.Emit(1);
        Assert.AreEqual(4, (await watch.Binary())[2]);
        await host.Reject($"console/{vm}", HttpStatusCode.Forbidden, login.Token);
        await watch.Close();
        Assert.IsEmpty(fake.Inputs);
    }

    [TestMethod]
    public async Task RevokeInterruptsBlockedSendDropsBacklogAndKeepsOtherViewerLive()
    {
        var fake = new FakeConsole(); var writer = new DelayedTransport();
        await using var host = await SocketHost.Start(configure: s =>
        { s.AddSingleton<IConsoleSessionFactory>(fake); s.AddSingleton<ConsoleTransport>(writer); }, root: Root);
        var a = await Login(host); var b = await Login(host);
        writer.Delay = a.SessionId;
        using var slow = await Connect(host, a, "RdpBasic", "bgra", "Watch");
        using var fast = await Connect(host, b, "RdpBasic", "bgra", "Watch");
        await slow.Json("control"); await fast.Json("control");
        fake.Emit(1);
        await writer.Blocked.Task.WaitAsync(fast.Timeout);
        await fast.Binary();
        for (int i = 2; i <= 10; i++)
        {
            fake.Emit(i);
            byte[] packet;
            do { packet = await fast.Binary(); } while (BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(10)) != i);
        }
        Assert.AreEqual(1, fake.Created);
        Assert.AreEqual(1, fake.FrameListeners);
        Assert.AreEqual(0, fake.LiveFrames);
        var auth = host.App.Services.GetRequiredService<Authentication>();
        var revoked = auth.Subscribers(a.SessionId).Single();
        var survivor = auth.Subscribers(b.SessionId).Single();
        Assert.IsTrue(auth.Revoke(a.SessionId));
        long final = revoked.DeliveredFrames, other = survivor.DeliveredFrames;
        for (int i = 11; i < 19; i++) { fake.Emit(i); await fast.Binary(); }
        await Task.Delay(ConsoleSocket.IdleLosslessCompensationMs + 50);
        Assert.AreEqual(0L, revoked.DeliveredFrames - final);
        Assert.IsGreaterThan(other, survivor.DeliveredFrames);
        Assert.IsEmpty(auth.Subscribers(a.SessionId));
        await fast.Close();
        Assert.AreEqual(1, fake.Disconnections);
        Assert.AreEqual(0, fake.LiveFrames);
    }

    [TestMethod]
    public async Task HandoffSealsGenerationAndOnlyHolderReleasesPressedInputs()
    {
        var fake = new FakeConsole();
        await using var host = await SocketHost.Start(configure: s => s.AddSingleton<IConsoleSessionFactory>(fake), root: Root);
        var a = await Login(host); var b = await Login(host);
        using var owner = await Connect(host, a, "RdpBasic", "bgra", "Watch", "Control");
        long generation = (await owner.Json("control")).GetProperty("generation").GetInt64();
        await owner.Send(new { type = "key", scanCode = 29, down = true, generation, inputSequence = 1 });
        await owner.Send(new { type = "pointer", x = 3, y = 4, button = "Left", down = true, generation, inputSequence = 2 });
        await owner.Send(new { type = "ping" }); await owner.Json("pong");
        using (var watch = await Connect(host, b, "RdpBasic", "bgra", "Watch"))
        { await watch.Json("control"); await watch.Close(); }
        Assert.IsFalse(fake.Inputs.Contains("release"));
        using var next = await Connect(host, b, "RdpBasic", "bgra", "Watch", "Control");
        Assert.IsFalse((await Message(next, "control")).GetProperty("hasControl").GetBoolean());
        await next.Send(new { type = "takeControl", generation });
        long current = (await Message(next, "control")).GetProperty("generation").GetInt64();
        Assert.IsGreaterThan(generation, current);
        CollectionAssert.Contains(fake.Inputs, "key 29 False False");
        CollectionAssert.Contains(fake.Inputs, "pointer 3 4 Left False 0");
        await owner.Send(new { type = "releaseAllInputs", generation, inputSequence = 3 }); await Message(owner, "error");
        await next.Send(new { type = "key", scanCode = 17, down = true, generation = current, inputSequence = 5 });
        await next.Send(new { type = "releaseAllInputs", generation = current, inputSequence = 4 }); await Message(next, "error");
        Assert.AreEqual("key 17 False True", fake.Inputs.Last());
        await owner.Close(); await next.Close();
        Assert.AreEqual(0, fake.LiveFrames);
    }

    [TestMethod]
    public async Task PasswordChangeRevokesUpgradedV1AndScopedV2()
    {
        var fake = new FakeConsole();
        await using var host = await SocketHost.Start(configure: s => s.AddSingleton<IConsoleSessionFactory>(fake), root: Root);
        var a = await Login(host); var b = await Login(host);
        using var v1 = await host.Connect($"console/{vm}?frameEncoding=bgra", a.Token);
        using var v2 = await Connect(host, b, "RdpBasic", "bgra", "Watch");
        await v1.Json("capabilities"); await v2.Json("control");
        fake.Emit(2);
        Assert.AreEqual(1, (await v1.Binary())[2]);
        Assert.AreEqual(4, (await v2.Binary())[2]);
        var auth = host.App.Services.GetRequiredService<Authentication>();
        auth.SetPassword("new password value");
        Assert.IsEmpty(auth.Subscribers(a.SessionId));
        Assert.IsEmpty(auth.Subscribers(b.SessionId));
        Assert.AreEqual(0, fake.LiveFrames);
        Assert.AreEqual(0, fake.FrameListeners);
    }
}
