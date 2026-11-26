using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ExHyperV.Server.Api;
using ExHyperV.Server.Hosting;
using ExHyperV.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ExHyperV.Server.Tests;

public sealed partial class BusinessWorkflowTests
{
    private const string Origin = "https://browser.test";

    [TestMethod]
    public async Task AuthenticatedBrowserCompletesManagementTelemetryAndConsoleSession()
    {
        var console = new FakeConsole();
        var eventsSource = new FakeConsole();
        var sampler = new SessionSampler();
        await using var host = await SocketHost.Start(["--cors-origins", Origin], services =>
        {
            UseSimulatedMethods(services);
            services.AddSingleton<IConsoleSessionFactory>(console);
            services.AddSingleton<Func<ITelemetrySampler>>(() => sampler);
        });
        var http = host.Http;
        Assert.AreEqual("ok", (await http.GetFromJsonAsync<JsonElement>("health")).GetProperty("status").GetString());
        using (var set = await http.PostAsJsonAsync("auth/password", new { password = "browser test password" }))
            Assert.AreEqual(HttpStatusCode.NoContent, set.StatusCode);
        http.DefaultRequestHeaders.Add("Origin", Origin);
        Assert.IsTrue((await http.GetFromJsonAsync<JsonElement>("health")).GetProperty("authRequired").GetBoolean());
        var session = await Post(http, "auth/login", new { password = "browser test password", clientName = "browser" });
        http.DefaultRequestHeaders.Authorization = new("Bearer", session.GetProperty("token").GetString());
        using (var info = await http.GetAsync("server/info"))
        {
            Assert.AreEqual(Origin, info.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.AreEqual("password", (await TransportTests.Json(info)).GetProperty("authenticationMode").GetString());
        }
        var catalog = await http.GetFromJsonAsync<JsonElement>("core/catalog?kind=Method&search=" + Uri.EscapeDataString(Method(nameof(Workflow.Review))));
        string apiId = catalog.GetProperty("entries").EnumerateArray().Single().GetProperty("apiId").GetString()!;
        var description = await http.GetFromJsonAsync<JsonElement>("core/describe?apiId=" + Uri.EscapeDataString(apiId));
        Assert.IsTrue(description.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("decide", out _));
        Guid[] ids = [Guid.NewGuid(), Guid.NewGuid()];
        var op = await Post(http, "core/call", new { apiId, background = true, arguments = new
            { id = ids[0], decide = new Dictionary<string, bool> { ["$callback"] = true } } }, HttpStatusCode.Accepted);
        Guid operationId = op.GetProperty("operationId").GetGuid();
        async Task<string> Ticket() =>
            (await TransportTests.Json(await http.PostAsync("auth/ws-ticket", null))).GetProperty("ticket").GetString()!;
        var authentication = host.App.Services.GetRequiredService<Authentication>();
        var principal = authentication.Principal(session.GetProperty("sessionId").GetGuid());
        var resources = host.App.Services.GetRequiredService<LoginResources>().For(principal);
        var objects = resources.Objects;
        var sourceId = objects.Add(eventsSource);
        var subscription = await Post(http, "events/subscribe", new { targetObjectId = sourceId,
            apiId = ApiCatalog.Id(typeof(IConsoleVideoSource).GetEvent(nameof(IConsoleVideoSource.FrameReceived))!) });
        Guid subscriptionId = subscription.GetProperty("subscriptionId").GetGuid();
        using (var events = await host.Connect("events?ticket=" + await Ticket(), origin: Origin))
        {
            // Subscribe sequentially so each message is consumed by its own handler.
            await events.Send(new { type = "subscribeEvent", subscriptionId });
            await events.Json("event");
            eventsSource.Emit(1);
            var batch = (await events.Json("event")).GetProperty("batch");
            Guid frame = batch.GetProperty("events")[0].GetProperty("arguments")[0].GetProperty("$handle").GetGuid();
            using var image = await http.GetAsync($"binaries/{frame}");
            Assert.AreEqual("image/png", image.Content.Headers.ContentType!.MediaType);
            await events.Send(new { type = "unsubscribe", subscriptionId });
            await events.Json("unsubscribed");
            Assert.AreEqual(0, eventsSource.FrameListeners);
            Assert.AreEqual(0, eventsSource.LiveFrames);
            await events.Send(new { type = "subscribeOperation", operationId });
            List<int> progress = [];
            JsonElement status;
            async Task<JsonElement> Next()
            {
                var snapshot = (await events.Json("operation")).GetProperty("status");
                foreach (var entry in snapshot.GetProperty("events").GetProperty("events").EnumerateArray())
                    if (entry.GetProperty("name").GetString() == "progress") progress.Add(entry.GetProperty("arguments")[0].GetInt32());
                return snapshot;
            }
            do { status = await Next(); } while (status.GetProperty("pendingCallbacks").GetArrayLength() == 0);
            Assert.AreEqual("AwaitingInput", status.GetProperty("state").GetString());
            var pending = status.GetProperty("pendingCallbacks")[0];
            Assert.AreEqual(ids[0], pending.GetProperty("arguments")[0].GetGuid());
            var requestId = pending.GetProperty("requestId").GetGuid();
            var unrelated = objects.Add(new MemoryStream());
            using var invalid = await http.PostAsJsonAsync($"operations/{operationId}/reply", new
                { requestId, value = new Dictionary<string, Guid> { ["$handle"] = unrelated } });
            Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            // Rejecting the reply must return its lease while the decision is still pending.
            await TransportTests.Json(await http.DeleteAsync($"objects/{unrelated}"));
            using var reply = await http.PostAsJsonAsync($"operations/{operationId}/reply", new
                { requestId, value = Confirmation(ids[0]) });
            Assert.AreEqual(HttpStatusCode.NoContent, reply.StatusCode);
            do { status = await Next(); } while (status.GetProperty("result").ValueKind == JsonValueKind.Null);
            Assert.AreEqual("Completed", status.GetProperty("state").GetString());
            var result = status.GetProperty("result").GetProperty("result");
            Assert.AreEqual(ids[0], result.GetProperty("RequestId").GetGuid());
            Assert.IsTrue(result.GetProperty("Accepted").GetBoolean());
            CollectionAssert.AreEqual(new[] { 25, 100 }, progress);
            await events.Close();
        }
        using (var telemetry = await host.Connect("telemetry?ticket=" + await Ticket(), origin: Origin))
        {
            await VerifyTelemetry(telemetry, ids);
            await telemetry.Close();
        }
        Assert.IsTrue(sampler.Disposed);
        using (var ws = await host.Connect($"console/{ids[0]}?ticket={await Ticket()}&backend=RdpEnhanced&frameEncoding=deflate", origin: Origin))
        {
            await ConsoleMediaWorkflowTests.VerifyDesktop(ws, console, "deflate");
            Assert.AreEqual(ids[0], console.Options!.VmId);
            Assert.AreEqual("RdpEnhanced", console.BackendId);
            console.Sound();
            var sound = await ws.Binary();
            Assert.AreEqual(2, sound[2]);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, sound[18..]);
            await ConsoleMediaWorkflowTests.VerifyMicrophone(ws, console);
            await ConsoleMediaWorkflowTests.VerifyInputAndClipboard(ws, console);
            await ws.Close();
        }
        Assert.AreEqual(0, console.LiveFrames);
        Assert.AreEqual(0, console.FrameListeners);
        Assert.AreEqual(1, console.Disconnections);
        Assert.AreEqual(1, console.Disposals);
        Assert.AreEqual("release", console.Inputs.Last());
        // The route returns its slot after writing the close handshake.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (host.App.Services.GetRequiredService<ConsoleSlots>().Available.CurrentCount != 16)
            await Task.Delay(10, deadline.Token);
        await TransportTests.Json(await http.DeleteAsync($"objects/{sourceId}"));
        using (var forget = await http.DeleteAsync($"operations/{operationId}"))
            Assert.AreEqual(HttpStatusCode.NoContent, forget.StatusCode);
        Assert.AreEqual(0, (await http.GetFromJsonAsync<JsonElement>("operations")).GetProperty("operations").GetArrayLength());
        Assert.AreEqual(0, (await http.GetFromJsonAsync<JsonElement>("objects")).GetProperty("objects").GetArrayLength());
        Assert.Throws<ArgumentException>(() => resources.Execution.Events(subscriptionId, 1));
        string unused = await Ticket();
        using (var logout = await http.PostAsync("auth/logout", null)) Assert.AreEqual(HttpStatusCode.NoContent, logout.StatusCode);
        using (var expired = await http.GetAsync("server/info")) Assert.AreEqual(HttpStatusCode.Unauthorized, expired.StatusCode);
        await host.Reject("events?ticket=" + unused, HttpStatusCode.Unauthorized, origin: Origin);
    }

    private static async Task VerifyTelemetry(WireClient ws, Guid[] ids)
    {
        string[] metrics = ["cpu", "memory", "disk", "network", "gpu", "hostThreads"];
        await ws.Send(new { type = "subscribe", host = true, vmIds = ids, metrics });
        await ws.Json("subscribed");
        var sample = await ws.Json("telemetry");
        _ = sample.GetProperty("timestamp").GetDateTimeOffset();
        var host = sample.GetProperty("host");
        CollectionAssert.AreEquivalent(metrics, host.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.AreEqual(25, host.GetProperty("cpu")[0].GetProperty("usage").GetInt32());
        Assert.AreEqual(16384, host.GetProperty("memory").GetProperty("totalMiB").GetInt32());
        Assert.AreEqual("Unavailable", host.GetProperty("disk").GetProperty("state").GetString());
        Assert.AreEqual(512, host.GetProperty("network").GetProperty("eth0").GetProperty("bytesPerSecond").GetInt32());
        Assert.AreEqual(33, host.GetProperty("gpu").GetProperty("gpu0").GetInt32());
        Assert.AreEqual(4, host.GetProperty("hostThreads").GetProperty("threads")[0].GetProperty("threadId").GetInt32());
        var vms = sample.GetProperty("vms");
        Assert.AreEqual(2, vms.GetPropertyCount());
        for (int i = 0; i < ids.Length; i++)
        {
            var row = vms.GetProperty(ids[i].ToString());
            CollectionAssert.AreEquivalent(metrics[..5], row.EnumerateObject().Select(p => p.Name).ToArray());
            foreach (var (metric, field, value) in new[] { ("cpu", "Processor.UsagePercent", 27),
                ("memory", "Memory.AssignedMiB", 4096), ("disk", "Storage.ReadBytesPerSecond", 1024) })
                Assert.AreEqual(value + i, row.GetProperty(metric).GetProperty("values").GetProperty(field).GetProperty("value").GetInt32());
            if (i == 0) Assert.AreEqual(40, row.GetProperty("gpu").GetProperty("values").GetProperty("GpuPerformance.Gpu3d").GetProperty("value").GetInt32());
            else Assert.AreEqual("Unavailable", row.GetProperty("gpu").GetProperty("state").GetString());
            var adapter = row.GetProperty("network").GetProperty("collections").GetProperty("NetworkAdapters").GetProperty("rows")[0];
            Assert.AreEqual(i == 0 ? "192.0.2.20" : null, adapter.GetProperty("observedIPv4").GetString());
            Assert.AreEqual(i == 0 ? null : "采样中断", adapter.GetProperty("arpIssue").GetString());
            Assert.AreEqual("Available", adapter.GetProperty("values").GetProperty(VmNetworkFields.MacAddress.Id).GetProperty("state").GetString());
        }
        await ws.Send(new { type = "unsubscribe" });
        await ws.Json("unsubscribed");
    }

    private sealed class SessionSampler : ITelemetrySampler
    {
        internal bool Disposed;
        public Task<object?> HostAsync(string metric, CancellationToken token) => Task.FromResult<object?>(metric switch
        {
            "cpu" => new[] { new { coreId = 0, usage = 25 } }, "memory" => new { totalMiB = 16384, availableMiB = 8192 },
            "disk" => throw new IOException("磁盘采样失败"), "network" => new { eth0 = new { bytesPerSecond = 512 } },
            "gpu" => new { gpu0 = 33 }, "hostThreads" => new { threads = new[] { new { threadId = 4 } } },
            _ => throw new AssertFailedException(metric)
        });

        // Core query results have internal constructors. Use the real result types
        // and the production recursive projection without opening a Hyper-V provider.
        private static T Construct<T>(params object?[] args) => (T)typeof(T)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single().Invoke(args);
        private static VmQueryValue Reading<T>(T value) =>
            Construct<VmQueryValue<T>>(VmQueryValueState.Available, value, DateTimeOffset.UnixEpoch, null);
        public Task<IReadOnlyDictionary<Guid, object?>> VirtualMachinesAsync(string metric, Guid[] ids, CancellationToken token)
        {
            var result = new Dictionary<Guid, object?>();
            for (int i = 0; i < ids.Length; i++)
            {
                var values = new Dictionary<string, VmQueryValue>();
                var collections = new Dictionary<VmQueryCollection, VmQueryCollectionResult>();
                switch (metric)
                {
                    case "cpu": values.Add(VmProcessorFields.UsagePercent.Id, Reading(27d + i)); break;
                    case "memory": values.Add(VmMemoryFields.AssignedMiB.Id, Reading(4096L + i)); break;
                    case "disk": values.Add(VmStorageFields.ReadBytesPerSecond.Id, Reading(1024L + i)); break;
                    case "gpu": values.Add(VmGpuPerformanceFields.Gpu3d.Id, Reading(i == 0 ? 40d : double.NaN)); break;
                    case "network":
                        var port = Construct<VmQueryRow>("eth0", new Dictionary<string, VmQueryValue>
                            { [VmNetworkFields.MacAddress.Id] = Reading($"00:11:22:33:44:0{i}") }, null);
                        collections.Add(VmQueryCollection.NetworkAdapters, new(VmQueryValueState.Available, [port], null, DateTimeOffset.UnixEpoch));
                        break;
                    default: throw new AssertFailedException(metric);
                }
                var row = Construct<VmQueryRow>(ids[i].ToString(), values, collections);
                result.Add(ids[i], CoreTelemetrySampler.Project(row, mac =>
                    mac.EndsWith("00", StringComparison.Ordinal) ? ("192.0.2.20", null) : (null, "采样中断")));
            }
            return Task.FromResult<IReadOnlyDictionary<Guid, object?>>(result);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
