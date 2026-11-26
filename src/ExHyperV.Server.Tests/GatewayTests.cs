using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ExHyperV.Server.Api;
using ExHyperV.Server.Hosting;
using ExHyperV.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ExHyperV.Server.Tests;

[TestClass]
public sealed class GatewayTests
{
    private string directory = null!;
    [TestInitialize] public void Setup() => directory = TestStorage.CreatePath();
    [TestCleanup] public void Cleanup() => TestStorage.Delete(directory);

    [TestMethod]
    public async Task NativeLoopbackCanBootstrapPasswordAndThenRequiresBearer()
    {
        var url = TransportTests.Loopback();
        await using var app = Program.Build(LaunchOptions.Parse(["--url", url.ToString(), "--state-dir", directory]), null);
        await app.StartAsync();
        try
        {
            using var http = TransportTests.Client(url);
            Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("server/info")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NoContent, (await http.PostAsJsonAsync("auth/password", new { password = "local password" })).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync("server/info")).StatusCode);
            var session = await TransportTests.Json(await http.PostAsJsonAsync("auth/login", new { password = "local password" }));
            http.DefaultRequestHeaders.Authorization = new("Bearer", session.GetProperty("token").GetString());
            Assert.AreEqual(HttpStatusCode.NoContent, (await http.PostAsJsonAsync("auth/password", new { password = "new password" })).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync("server/info")).StatusCode);
            Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.PostAsJsonAsync("auth/login", new { password = "local password" })).StatusCode);
            Assert.AreEqual(HttpStatusCode.OK, (await http.PostAsJsonAsync("auth/login", new { password = "new password" })).StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    public async Task RoutesMatchOpenApiAndCallbacksResumeOrCancelOverHttp()
    {
        var url = TransportTests.Loopback();
        await using var app = Program.Build(LaunchOptions.Parse(["--url", url.ToString(), "--state-dir", directory]), null);
        await app.StartAsync();
        try
        {
            using var client = TransportTests.Client(url);
            var spec = await client.GetFromJsonAsync<JsonElement>("openapi.json");
            var documented = spec.GetProperty("paths").EnumerateObject()
                .SelectMany(path => path.Value.EnumerateObject().Select(verb => verb.Name.ToUpperInvariant() + " " + path.Name)).ToArray();
            var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints.OfType<RouteEndpoint>())
                .SelectMany(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Select(verb => verb + " " +
                    endpoint.RoutePattern.RawText!.Replace(":guid", ""))).ToArray();
            CollectionAssert.AreEquivalent(routes, documented);
            foreach (var (path, resource) in new[] { ("meta/guide", "Core.API.md"), ("meta/field-help", "Core.FieldHelp.json") })
            {
                using var stream = typeof(ApiCatalog).Assembly.GetManifestResourceStream(resource)!;
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                CollectionAssert.AreEqual(memory.ToArray(), await client.GetByteArrayAsync(path));
            }

            var execution = app.Services.GetRequiredService<ApiExecution>();
            foreach (bool cancel in new[] { false, true })
            {
                var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var operation = execution.Start(async current =>
                {
                    var answer = (Task<bool>)execution.Callback(current, "confirm", typeof(Task<bool>), ["continue?"])!;
                    waiting.SetResult();
                    return await answer;
                });
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                string path = $"operations/{operation.Id}";
                var status = await client.GetFromJsonAsync<JsonElement>(path);
                Assert.AreEqual("AwaitingInput", status.GetProperty("state").GetString());
                var requestId = status.GetProperty("pendingCallbacks")[0].GetProperty("requestId").GetGuid();
                if (cancel) Assert.AreEqual(HttpStatusCode.OK, (await client.PostAsync(path + "/cancel", null)).StatusCode);
                else Assert.AreEqual(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(path + "/reply", new { requestId, value = true })).StatusCode);
                var result = await operation.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreEqual(!cancel, result.Success);
                if (!cancel) Assert.AreEqual(true, result.Result);
                status = await client.GetFromJsonAsync<JsonElement>(path);
                Assert.AreEqual(cancel ? "Cancelled" : "Completed", status.GetProperty("state").GetString());
                Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode);
            }
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    public async Task RetainedModelsEventsAndBinaryFramesRoundTrip()
    {
        var url = TransportTests.Loopback();
        await using var app = Program.Build(LaunchOptions.Parse(["--url", url.ToString(), "--state-dir", directory]), null);
        await app.StartAsync();
        try
        {
            using var client = TransportTests.Client(url);
            var created = await TransportTests.Json(await client.PostAsJsonAsync("core/call", new { apiId = "M:ExHyperV.Models.VmMemorySettings.#ctor" }));
            var targetObjectId = created.GetProperty("result").GetProperty("$handle").GetGuid();
            const string apiId = "P:ExHyperV.Models.VmMemorySettings.Startup";
            await TransportTests.Json(await client.PostAsJsonAsync("core/set", new { apiId, targetObjectId, value = 2048 }));
            var got = await TransportTests.Json(await client.PostAsJsonAsync("core/get", new { apiId, targetObjectId }));
            Assert.AreEqual(2048, got.GetProperty("value").GetInt32());
            await TransportTests.Json(await client.DeleteAsync($"objects/{targetObjectId}"));

            var objects = app.Services.GetRequiredService<ApiObjects>();
            var video = new VideoSource();
            var videoId = objects.Add(video);
            var subscribed = await TransportTests.Json(await client.PostAsJsonAsync("events/subscribe", new
                { apiId = ApiCatalog.Id(typeof(IConsoleVideoSource).GetEvent("FrameReceived")!), targetObjectId = videoId }));
            var sub = subscribed.GetProperty("subscriptionId").GetGuid();
            for (int i = 1; i <= 3; i++)
            {
                using var frame = new ConsoleFrame(1, 1, 4, new byte[] { 1, 2, (byte)i, 255 }, i);
                video.Raise(frame);
            }
            var batch = await client.GetFromJsonAsync<JsonElement>($"events/{sub}?limit=1");
            Assert.AreEqual(2, batch.GetProperty("dropped").GetInt32());
            var frameId = batch.GetProperty("events")[0].GetProperty("arguments")[0].GetProperty("$handle").GetGuid();
            using var image = await client.GetAsync($"binaries/{frameId}");
            Assert.AreEqual("image/png", image.Content.Headers.ContentType!.MediaType);
            using var stream = await image.Content.ReadAsStreamAsync();
            using var bitmap = new System.Drawing.Bitmap(stream);
            Assert.AreEqual(System.Drawing.Color.FromArgb(255, 3, 2, 1).ToArgb(), bitmap.GetPixel(0, 0).ToArgb());
            Assert.AreEqual(HttpStatusCode.Conflict, (await client.DeleteAsync($"objects/{videoId}")).StatusCode);
            Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync($"events/{sub}")).StatusCode);
            await TransportTests.Json(await client.DeleteAsync($"objects/{frameId}"));
            await TransportTests.Json(await client.DeleteAsync($"objects/{videoId}"));
            Assert.AreEqual(0, objects.Count);
        }
        finally { await app.StopAsync(); }
    }

    [TestMethod]
    public async Task AudioPacketSpanConstructorUsesRealCoreContract()
    {
        var catalog = new ApiCatalog();
        var objects = new ApiObjects();
        var codec = new ApiCodec(catalog, objects);
        var execution = new ApiExecution(catalog, objects, codec, Microsoft.Extensions.Logging.Abstractions.NullLogger<ApiExecution>.Instance);
        var constructor = typeof(ConsoleAudioPacket).GetConstructors().Single();
        var arguments = JsonSerializer.SerializeToElement(new
        {
            format = new { sampleRate = 48000, channels = 1, sampleFormat = "Signed16LittleEndian" },
            samples = "AQI=", sequence = 1, timestamp = "00:00:00"
        });
        var result = await execution.Start(op => execution.InvokeAsync(ApiCatalog.Id(constructor), arguments, null, null, null, op), true).Task;
        Assert.IsTrue(result.Success, result.Error);
        var handle = JsonSerializer.SerializeToElement(result.Result).GetProperty("$handle").GetGuid();
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, ((ConsoleAudioPacket)objects.Get(handle)).CopySamples());
        await execution.StopAsync(CancellationToken.None);
    }

    private sealed class VideoSource : IConsoleVideoSource
    {
        public ConsoleFrame? CaptureFrame() => null;
        public bool SupportsResize => false;
        public event Action<ConsoleFrame>? FrameReceived;
        public event Action<ConsoleCursor?>? CursorChanged { add { } remove { } }
        public event Action<uint>? ResizeRejected { add { } remove { } }
        public bool Resize(int width, int height, uint desktopScale = 100) => false;
        public void Raise(ConsoleFrame frame) => FrameReceived?.Invoke(frame);
    }
}
