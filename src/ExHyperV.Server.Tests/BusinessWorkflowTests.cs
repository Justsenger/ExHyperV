using System.Drawing;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Server.Api;
using Microsoft.Extensions.DependencyInjection;

namespace ExHyperV.Server.Tests;

[TestClass]
public sealed partial class BusinessWorkflowTests
{
    // Only this in-process host discovers the simulated methods. All transport,
    // reflection, argument binding, policy and lifetime management run normally.
    private static void UseSimulatedMethods(IServiceCollection services)
    {
        var catalog = new ApiCatalog();
        var members = (IDictionary<string, MemberInfo>)catalog.Members;
        foreach (var method in typeof(Workflow).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            members.Add(ApiCatalog.Id(method), method);
        services.AddSingleton(catalog);
    }

    private static class Workflow
    {
        public static async Task<GpuDeploymentDecision> Review(Guid id, IProgress<int> progress,
            Func<Guid, Task<GpuDeploymentDecision>>? decide = null)
        {
            progress.Report(25);
            var decision = decide == null ? new GpuConfirmation(id, false) : await decide(id);
            progress.Report(100);
            return decision;
        }

        public static bool Compare(GpuDeploymentDecision decision, IEquatable<GpuConfirmation> other) =>
            decision is GpuConfirmation confirmation && other.Equals(confirmation);

        public static GpuDeploymentSnapshot Verify(ref int revision, out object credentials)
        {
            credentials = new { Password = "simulation-password", Passphrase = "simulation-phrase" };
            return new(Guid.NewGuid(), ++revision, GpuDeploymentState.CleanupRequired, [], null, [], true, true) { OutcomeUnknown = true };
        }

        public static byte[] Bytes(byte[] data) => data;
        public static MemoryStream Stream(byte[] data) => new(data) { Position = 7 };
        public static bool IsExactInteger(object value) => value is long and 9007199254740993L;
        public static int Total(Dictionary<byte, int[]> batches) => batches.Values.Sum(batch => batch.Sum());
    }

    private static string Method(string name) => ApiCatalog.Id(typeof(Workflow).GetMethod(name)!);
    private static Task<JsonElement> Call(HttpClient http, string apiId, object arguments, HttpStatusCode expected = HttpStatusCode.OK) =>
        Post(http, "core/call", new { apiId, arguments }, expected);

    private static async Task<JsonElement> Post(HttpClient http, string path, object body, HttpStatusCode expected = HttpStatusCode.OK) =>
        await TransportTests.Json(await http.PostAsJsonAsync(path, body), expected);

    [TestMethod]
    public void AllReviewedMembersSerializeTheirDetailedContract()
    {
        var catalog = new ApiCatalog();
        Assert.IsGreaterThan(0, catalog.Types.Count);
        Assert.AreEqual(catalog.Types.Count + catalog.Members.Values.Count(member => member is not Type), catalog.Members.Count);
        foreach (var member in catalog.Members.Values)
        {
            string id = ApiCatalog.Id(member);
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(catalog.Describe(member)));
            var entry = document.RootElement;
            Assert.AreEqual(id, entry.GetProperty("apiId").GetString());
            Assert.IsTrue(Hosting.LaunchOptions.Groups.Contains(entry.GetProperty("group").GetString()), id);
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.GetProperty("description").GetString()), id);
            var input = entry.GetProperty("inputSchema");
            Assert.AreEqual("object", input.GetProperty("type").GetString(), id);
            Assert.AreEqual(JsonValueKind.Object, input.GetProperty("properties").ValueKind, id);
            Assert.AreEqual(JsonValueKind.Object, entry.GetProperty("valueSchema").ValueKind, id);
            ParameterInfo[] parameters = member switch { MethodBase method => method.GetParameters(),
                PropertyInfo property => property.GetIndexParameters(), _ => [] };
            CollectionAssert.AreEqual(parameters.Select(p => p.Name).ToArray(),
                entry.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray(), id);
        }
        Assert.AreEqual(0, JsonSerializer.SerializeToElement(catalog.Coverage()).GetProperty("transportGaps").GetArrayLength());
    }

    private static Dictionary<string, object> Confirmation(Guid id) => new()
        { ["$type"] = typeof(GpuConfirmation).FullName!, ["requestId"] = id, ["accepted"] = true };

    [TestMethod]
    public async Task PolymorphicArgumentsAndOptionalDecisionsUseThePublishedWireShapes()
    {
        await using var host = await SocketHost.Start(configure: UseSimulatedMethods);
        var http = host.Http;
        var id = Guid.NewGuid();
        var model = Confirmation(id);
        var compare = Method(nameof(Workflow.Compare));
        var description = await http.GetFromJsonAsync<JsonElement>("core/describe?apiId=" + Uri.EscapeDataString(compare));
        foreach (string parameter in new[] { "decision", "other" })
        {
            var shapes = description.GetProperty("inputSchema").GetProperty("properties").GetProperty(parameter).GetProperty("anyOf");
            Assert.IsTrue(shapes.EnumerateArray().Any(shape => shape.GetProperty("required")[0].GetString() == "$type"
                && !shape.TryGetProperty("additionalProperties", out _)));
        }
        Assert.IsTrue((await Call(http, compare, new { decision = model, other = model })).GetProperty("result").GetBoolean());
        var review = Method(nameof(Workflow.Review));
        foreach (var arguments in new object[] { new { id }, new { id, decide = (object?)null } })
        {
            var completed = await Call(http, review, arguments);
            Assert.IsTrue(completed.GetProperty("success").GetBoolean());
            Assert.IsFalse(completed.GetProperty("result").GetProperty("Accepted").GetBoolean());
        }
        foreach (object invalid in new object[] { false, new { }, new Dictionary<string, object> { ["$callback"] = false } })
        {
            var rejected = await Call(http, review, new { id, decide = invalid }, HttpStatusCode.BadRequest);
            Assert.AreEqual("InvalidArgument", rejected.GetProperty("errorKind").GetString());
        }
        Assert.IsTrue((await Call(http, Method(nameof(Workflow.IsExactInteger)), new { value = 9007199254740993L })).GetProperty("result").GetBoolean());
    }

    [TestMethod]
    public async Task BodyLimitAndInvalidArgumentShapesReturnClientErrors()
    {
        await using var host = await SocketHost.Start(configure: UseSimulatedMethods);
        // Legal JSON padded to the HTTP boundary. Both requests use the same method.
        var body = JsonSerializer.SerializeToUtf8Bytes(new { apiId = Method(nameof(Workflow.Total)),
            arguments = new { batches = new Dictionary<string, int[]> { ["1"] = [4, 5] } } });
        var buffer = new byte[16 * 1024 * 1024 + 1];
        body.CopyTo(buffer, 0);
        buffer.AsSpan(body.Length).Fill((byte)' ');
        async Task<HttpResponseMessage> Send(int length)
        {
            using var content = new ByteArrayContent(buffer, 0, length);
            content.Headers.ContentType = new("application/json");
            return await host.Http.PostAsync("core/call", content);
        }
        Assert.AreEqual(9, (await TransportTests.Json(await Send(buffer.Length - 1))).GetProperty("result").GetInt32());
        using var oversized = await Send(buffer.Length);
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
        foreach (object batches in new object[] { 42, new Dictionary<string, object> { ["1"] = false }, new Dictionary<string, int[]> { ["256"] = [] } })
        {
            var error = await Call(host.Http, Method(nameof(Workflow.Total)), new { batches }, HttpStatusCode.BadRequest);
            Assert.AreEqual("InvalidArgument", error.GetProperty("errorKind").GetString());
        }
        foreach (string key in new[] { "$type", "$handle" })
        {
            var error = await Call(host.Http, Method(nameof(Workflow.Compare)), new
                { decision = new Dictionary<string, int> { [key] = 3 }, other = Confirmation(Guid.NewGuid()) }, HttpStatusCode.BadRequest);
            Assert.AreEqual("InvalidArgument", error.GetProperty("errorKind").GetString());
        }
    }

    [TestMethod]
    public async Task UnknownGpuOutcomeSurvivesRefAndOutPackingAndRedactsCredentials()
    {
        await using var host = await SocketHost.Start(configure: UseSimulatedMethods);
        var reply = await Call(host.Http, Method(nameof(Workflow.Verify)), new { revision = 7 });
        var status = await host.Http.GetFromJsonAsync<JsonElement>("operations/" + reply.GetProperty("operationId").GetGuid());
        Assert.AreEqual("RequiresVerification", status.GetProperty("state").GetString());
        foreach (var response in new[] { reply, status.GetProperty("result") })
        {
            Assert.IsFalse(response.GetProperty("success").GetBoolean());
            Assert.AreEqual("OutcomeUnknown", response.GetProperty("errorKind").GetString());
            var result = response.GetProperty("result");
            Assert.IsTrue(result.GetProperty("result").GetProperty("OutcomeUnknown").GetBoolean());
            var refs = result.GetProperty("refArguments");
            Assert.AreEqual(8, refs.GetProperty("revision").GetInt32());
            foreach (string name in new[] { "Password", "Passphrase" })
                Assert.AreEqual("[私密值]", refs.GetProperty("credentials").GetProperty(name).GetString());
            Assert.IsFalse(response.GetRawText().Contains("simulation-"));
        }
        // Exercise a Core member directly as well as the simulated ref/out signature.
        var objects = host.App.Services.GetRequiredService<ApiObjects>();
        int revision = 8;
        Guid target = objects.Add(Workflow.Verify(ref revision, out _));
        var clone = await Post(host.Http, "core/call", new { targetObjectId = target,
            apiId = ApiCatalog.Id(typeof(GpuDeploymentSnapshot).GetMethod("<Clone>$")!) });
        Assert.IsFalse(clone.GetProperty("success").GetBoolean());
        Assert.IsTrue(clone.GetProperty("result").GetProperty("OutcomeUnknown").GetBoolean());
        Assert.AreEqual("OutcomeUnknown", clone.GetProperty("errorKind").GetString());
        var clonedStatus = await host.Http.GetFromJsonAsync<JsonElement>("operations/" + clone.GetProperty("operationId").GetGuid());
        Assert.AreEqual("RequiresVerification", clonedStatus.GetProperty("state").GetString());
        await TransportTests.Json(await host.Http.DeleteAsync($"objects/{target}"));
    }

    [TestMethod]
    public async Task BinaryThresholdStreamPositionAndRgb565JpegRoundTripOverHttp()
    {
        await using var host = await SocketHost.Start(configure: UseSimulatedMethods);
        var http = host.Http;
        byte[] data = new byte[65537];
        new Random(8192).NextBytes(data);
        var small = await Call(http, Method(nameof(Workflow.Bytes)), new { data = data[..65536] });
        CollectionAssert.AreEqual(data[..65536], small.GetProperty("result").GetBytesFromBase64());
        foreach (string method in new[] { nameof(Workflow.Bytes), nameof(Workflow.Stream) })
        {
            var reply = await Call(http, Method(method), new { data });
            var result = reply.GetProperty("result");
            var handle = result.GetProperty("$handle").GetGuid();
            string url = $"/api/v1/binaries/{handle}";
            var objects = host.App.Services.GetRequiredService<ApiObjects>();
            var stream = objects.Get(handle) as MemoryStream;
            if (method == nameof(Workflow.Bytes)) Assert.AreEqual(url, result.GetProperty("href").GetString());
            else Assert.IsNotNull(stream);
            using var response = await http.GetAsync(url);
            Assert.AreEqual("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
            CollectionAssert.AreEqual(data, await response.Content.ReadAsByteArrayAsync());
            if (stream != null) Assert.AreEqual(7, stream.Position);
            await TransportTests.Json(await http.DeleteAsync($"objects/{handle}"));
            Assert.IsFalse(stream?.CanRead == true);
        }
        byte[] red = Enumerable.Repeat(new byte[] { 0, 248 }, 8).SelectMany(pixel => pixel).ToArray();
        string create = ApiCatalog.Id(typeof(VmScreenshotImage).GetMethod(nameof(VmScreenshotImage.FromRgb565))!);
        var screenshot = await Call(http, create, new { data = red, width = 4, height = 2 });
        var imageId = screenshot.GetProperty("result").GetProperty("$handle").GetGuid();
        using var image = await http.GetAsync($"binaries/{imageId}");
        Assert.AreEqual("image/jpeg", image.Content.Headers.ContentType!.MediaType);
        using var jpeg = await image.Content.ReadAsStreamAsync();
        using var bitmap = new Bitmap(jpeg);
        Assert.AreEqual(new Size(4, 2), bitmap.Size);
        Assert.AreEqual(System.Drawing.Imaging.ImageFormat.Jpeg.Guid, bitmap.RawFormat.Guid);
        var pixel = bitmap.GetPixel(3, 1);
        Assert.IsTrue(pixel.R > 245 && pixel.G < 10 && pixel.B < 10);
        await TransportTests.Json(await http.DeleteAsync($"objects/{imageId}"));
        Assert.AreEqual(0, (await http.GetFromJsonAsync<JsonElement>("objects")).GetProperty("objects").GetArrayLength());
    }
}
