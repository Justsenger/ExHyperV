using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExHyperV.Server.Api;
using ExHyperV.Server.Hosting;
using ExHyperV.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ExHyperV.Server.Tests;

[TestClass]
public sealed class EventTelemetryTests
{
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestMethod]
    public async Task WebSocketHandshakeConsumesShortTicketsAndRejectsCredentialUrls()
    {
        var clock = new Clock();
        string root = Guid.NewGuid().ToString("N");
        await using var host = await SocketHost.Start(["--cors-origins", "https://wow.test"], root: root, clock: clock);
        var auth = host.App.Services.GetRequiredService<Authentication>();
        host.Http.DefaultRequestHeaders.Authorization = new("Bearer", root);
        var login = await TransportTests.Json(await host.Http.PostAsJsonAsync("auth/login", new { token = root }));
        string session = login.GetProperty("token").GetString()!;
        host.Http.DefaultRequestHeaders.Authorization = new("Bearer", session);
        string path = "events?ticket=" + (await TransportTests.Json(await host.Http.PostAsync("auth/ws-ticket", null))).GetProperty("ticket").GetString();
        await host.Reject(path, HttpStatusCode.Forbidden, origin: "https://untrusted.test");
        using (var ws = await host.Connect(path, origin: "https://wow.test")) { await ws.Send(new { type = "ping" }); await ws.Json("pong"); await ws.Close(); }
        await host.Reject(path, HttpStatusCode.Unauthorized);
        var stale = auth.IssueTicket(null);
        clock.Now += TimeSpan.FromSeconds(30);
        foreach (var invalid in new[] { "events", "events?ticket=" + stale.Ticket, "events?ticket=" + root,
            "events?ticket=" + session, "telemetry?token=" + root, "events?access_token=" + session })
            await host.Reject(invalid, HttpStatusCode.Unauthorized);
        using var bearer = await host.Connect("telemetry", session);
        await bearer.Close();
        using var response = await host.Http.GetAsync("/ws/v1/events");
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        host.Http.DefaultRequestHeaders.Authorization = null;
        using var challenge = await host.Http.GetAsync("/ws/v1/events");
        Assert.AreEqual("Bearer", challenge.Headers.WwwAuthenticate.Single().Scheme);
    }

    [TestMethod]
    public async Task OperationWatchSendsProgressCallbackAndTerminalResult()
    {
        await using var host = await SocketHost.Start();
        using var ws = await host.Connect("events");
        var execution = host.App.Services.GetRequiredService<ApiExecution>();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var op = execution.Start(async current =>
        {
            await start.Task;
            current.Feed.Publish("progress", [50]);
            return await (Task<bool>)execution.Callback(current, "confirm", typeof(Task<bool>), ["continue?"])!;
        });
        try
        {
            await ws.Send(new { type = "subscribeOperation", operationId = op.Id });
            await ws.Json("operation");
            start.SetResult();
            bool sawProgress = false;
            JsonElement status;
            do
            {
                status = (await ws.Json("operation")).GetProperty("status");
                sawProgress |= status.GetProperty("events").GetProperty("events").EnumerateArray().Any(e => e.GetProperty("name").GetString() == "progress");
            } while (status.GetProperty("pendingCallbacks").GetArrayLength() == 0);
            Assert.IsTrue(sawProgress);
            var id = status.GetProperty("pendingCallbacks")[0].GetProperty("requestId").GetGuid();
            Assert.AreEqual(HttpStatusCode.NoContent, (await host.Http.PostAsJsonAsync($"operations/{op.Id}/reply", new { requestId = id, value = true })).StatusCode);
            do { status = (await ws.Json("operation")).GetProperty("status"); } while (status.GetProperty("result").ValueKind == JsonValueKind.Null);
            Assert.AreEqual("Completed", status.GetProperty("state").GetString());
            Assert.IsTrue(status.GetProperty("result").GetProperty("result").GetBoolean());
            await ws.Send(new { type = "unsubscribe", operationId = op.Id });
            await ws.Json("unsubscribed");
            await ws.Close();
        }
        finally { start.TrySetResult(); execution.CancelIfRunning(op.Id); }
    }

    [TestMethod]
    public async Task EventDisconnectDetachesSourceAndReturnsTransferredFrames()
    {
        await using var host = await SocketHost.Start();
        var objects = host.App.Services.GetRequiredService<ApiObjects>();
        var execution = host.App.Services.GetRequiredService<ApiExecution>();
        var source = new FakeConsole();
        Guid target = objects.Add(source);
        Guid sub = execution.Subscribe(typeof(IConsoleVideoSource).GetEvent("FrameReceived")!, target);
        for (int i = 1; i <= 4; i++) source.Emit(i);
        using var ws = await host.Connect("events");
        await ws.Send(new { type = "subscribeEvent", subscriptionId = sub });
        var batch = (await ws.Json("event")).GetProperty("batch");
        Assert.AreEqual(3, batch.GetProperty("dropped").GetInt32());
        var frame = batch.GetProperty("events")[0].GetProperty("arguments")[0].GetProperty("$handle").GetGuid();
        Assert.IsInstanceOfType<ConsoleFrame>(objects.Get(frame));
        source.Emit(5);
        await ws.Json("event");
        await ws.Close();
        Assert.AreEqual(1, objects.Count);
        Assert.AreEqual(0, source.LiveFrames);
        Assert.Throws<ArgumentException>(() => execution.Events(sub, 1));
        await objects.ReleaseAsync(target);
    }

    private sealed class FullBuffer : MemoryStream
    {
        public override long Length => 128L * 1024 * 1024;
    }

    [TestMethod]
    public async Task EventWatchRetriesBufferedFrameWhenAnObjectIsReleased()
    {
        await using var host = await SocketHost.Start();
        var objects = host.App.Services.GetRequiredService<ApiObjects>();
        var execution = host.App.Services.GetRequiredService<ApiExecution>();
        var source = new FakeConsole();
        Guid target = objects.Add(source);
        Guid full = objects.Add(new FullBuffer());
        Guid sub = execution.Subscribe(typeof(IConsoleVideoSource).GetEvent("FrameReceived")!, target);
        source.Emit(1);
        using var ws = await host.Connect("events");
        await ws.Send(new { type = "subscribeEvent", subscriptionId = sub });
        var batch = (await ws.Json("event")).GetProperty("batch");
        Assert.IsTrue(batch.GetProperty("resourceLimitReached").GetBoolean());
        Assert.AreEqual(1, batch.GetProperty("remaining").GetInt32());
        await objects.ReleaseAsync(full);
        batch = (await ws.Json("event")).GetProperty("batch");
        Assert.AreEqual(1, batch.GetProperty("events").GetArrayLength());
        await ws.Send(new { type = "unsubscribe", subscriptionId = sub });
        await ws.Json("unsubscribed");
        Assert.AreEqual(0, source.FrameListeners);
        Assert.AreEqual(0, source.LiveFrames);
        Assert.AreEqual(1, objects.Count);
        await ws.Close();
    }

    private sealed class DrainingSampler : ITelemetrySampler
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<object?> HostAsync(string metric, CancellationToken token)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        }
        public Task<IReadOnlyDictionary<Guid, object?>> VirtualMachinesAsync(string metric, Guid[] ids, CancellationToken token) =>
            throw new AssertFailedException();
        public async ValueTask DisposeAsync() { Disposing.SetResult(); await Finish.Task; }
    }

    [TestMethod]
    public async Task HostStopCancelsAnActiveSampleAndWaitsForItsAsyncCleanup()
    {
        var sampler = new DrainingSampler();
        await using var host = await SocketHost.Start(configure: services => services.AddSingleton<Func<ITelemetrySampler>>(() => sampler));
        using var ws = await host.Connect("telemetry");
        try
        {
            await ws.Send(new { type = "subscribe", host = true, vmIds = Array.Empty<Guid>(), metrics = new[] { "cpu" } });
            await sampler.Started.Task.WaitAsync(ws.Timeout);
            var stop = host.App.StopAsync();
            await sampler.Disposing.Task.WaitAsync(ws.Timeout);
            Assert.IsFalse(stop.IsCompleted);
            sampler.Finish.SetResult();
            await stop.WaitAsync(ws.Timeout);
        }
        finally { sampler.Finish.TrySetResult(); }
    }

    private sealed class Sampler : ITelemetrySampler
    {
        internal int Samples;
        internal bool Disposed;
        public Task<object?> HostAsync(string metric, CancellationToken token)
        {
            Interlocked.Increment(ref Samples);
            if (metric == "disk") throw new InvalidOperationException("磁盘暂不可用。");
            return Task.FromResult<object?>(37);
        }

        public Task<IReadOnlyDictionary<Guid, object?>> VirtualMachinesAsync(string metric, Guid[] ids, CancellationToken token) =>
            Task.FromResult<IReadOnlyDictionary<Guid, object?>>(ids.ToDictionary(id => id, _ => (object?)new { state = "WarmingUp" }));
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [TestMethod]
    public async Task TelemetrySwitchesSubscriptionsClampsIntervalsAndIsolatesFaults()
    {
        var created = new System.Collections.Concurrent.ConcurrentQueue<Sampler>();
        await using var host = await SocketHost.Start(configure: services => services.AddSingleton<Func<ITelemetrySampler>>(() =>
        { var sampler = new Sampler(); created.Enqueue(sampler); return sampler; }));
        using var ws = await host.Connect("telemetry");
        await ws.Send(new { type = "ping" }); await ws.Json("pong");
        Assert.IsEmpty(created);
        var id = Guid.NewGuid();
        await ws.Send(new { type = "subscribe", host = true, vmIds = new[] { id }, metrics = new[] { "disk", "memory" }, intervalMs = 5 });
        Assert.AreEqual(500, (await ws.Json("subscribed")).GetProperty("intervalMs").GetInt32());
        var sample = await ws.Json("telemetry");
        Assert.AreEqual(37, sample.GetProperty("host").GetProperty("memory").GetInt32());
        Assert.IsTrue(sample.GetProperty("host").GetProperty("disk").TryGetProperty("issue", out _));
        Assert.AreEqual("WarmingUp", sample.GetProperty("vms").GetProperty(id.ToString()).GetProperty("memory").GetProperty("state").GetString());
        await ws.Send(new { type = "subscribe", host = false, vmIds = new[] { id }, metrics = new[] { "cpu" }, intervalMs = 20000 });
        Assert.AreEqual(10000, (await ws.Json("subscribed")).GetProperty("intervalMs").GetInt32());
        Assert.IsTrue(created.First().Disposed);
        Assert.AreEqual(JsonValueKind.Null, (await ws.Json("telemetry")).GetProperty("host").ValueKind);
        await ws.Send(new { type = "unsubscribe" }); await ws.Json("unsubscribed");
        Assert.IsTrue(created.Last().Disposed);
        await ws.Send(new { type = "subscribe", host = true, vmIds = Array.Empty<Guid>(), metrics = new[] { "cpu" } });
        await ws.Json("telemetry");
        await ws.Close();
        Assert.IsTrue(created.All(s => s.Disposed));
    }

    [TestMethod]
    public async Task DisabledTelemetryToolsetsDoNotCreateSampler()
    {
        foreach (bool hostTarget in new[] { false, true })
        {
            int created = 0;
            await using var host = await SocketHost.Start(["--toolsets", hostTarget ? "vm" : "host"],
                services => services.AddSingleton<Func<ITelemetrySampler>>(() => { created++; return new Sampler(); }));
            using var ws = await host.Connect("telemetry");
            await ws.Send(new { type = "subscribe", host = hostTarget, vmIds = hostTarget ? Array.Empty<Guid>() : [Guid.NewGuid()], metrics = new[] { "cpu" } });
            StringAssert.Contains((await ws.Json("error")).GetProperty("error").GetString()!, hostTarget ? "host" : "vm");
            await ws.Close();
            Assert.AreEqual(0, created);
        }
    }
}
