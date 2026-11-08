using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ExHyperV.Mcp.Api;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ExHyperV.Mcp.Tests;

[TestClass]
public sealed class TransportTests
{
    private string directory = null!;
    private static string Exe => Path.ChangeExtension(typeof(ApiCatalog).Assembly.Location, ".exe");
    [TestInitialize] public void Initialize() => directory = TestStorage.CreatePath();
    [TestCleanup] public void Cleanup() => TestStorage.Delete(directory);

    [TestMethod]
    public async Task StdioListsContractsReturnsStructuredDataAndFiltersTools()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Command = Exe, Arguments = ["--read-only", "--toolsets", "vm"],
            EnvironmentVariables = new Dictionary<string, string?> { ["EXHYPERV_MCP_STATE_DIRECTORY"] = directory }
        }), cancellationToken: timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.IsTrue(tools.All(tool => tool.ProtocolTool.OutputSchema.HasValue));
        Assert.IsFalse(tools.Any(tool => tool.Name == "vm_power"));
        var result = await client.CallToolAsync("core_api_list", new Dictionary<string, object?> { ["search"] = "GetVmMemorySettingsAsync" }, cancellationToken: timeout.Token);
        Assert.IsFalse(result.IsError == true);
        Assert.IsTrue(result.StructuredContent!.Value.GetProperty("success").GetBoolean());
        Assert.AreEqual(1, result.StructuredContent.Value.GetProperty("data").GetProperty("total").GetInt32());
        var blocked = await client.CallToolAsync("core_api_query", new Dictionary<string, object?>
            { ["apiId"] = "M:ExHyperV.Services.ArpSnoopService.Start" }, cancellationToken: timeout.Token);
        Assert.IsTrue(blocked.IsError);
        var resources = await client.ListResourcesAsync(cancellationToken: timeout.Token);
        Assert.IsTrue(resources.Any(resource => resource.Uri == "exhyperv://core/api"));
    }

    [TestMethod]
    public async Task HttpRetainsTasksAcrossRequestsAndRequiresConfiguredToken()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        string token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--transport", "http", "--url", $"http://127.0.0.1:{port}", "--read-only" }) start.ArgumentList.Add(argument);
        start.Environment["EXHYPERV_MCP_TOKEN"] = token;
        start.Environment["EXHYPERV_MCP_STATE_DIRECTORY"] = directory;
        using var process = Process.Start(start)!;
        Task<string> errors = process.StandardError.ReadToEndAsync();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            for (int i = 0; ; i++)
            {
                if (process.HasExited) Assert.Fail(await errors);
                try { using var response = await http.GetAsync(endpoint, timeout.Token); Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode); break; }
                catch (HttpRequestException) when (i < 100) { await Task.Delay(100, timeout.Token); }
            }
            await using var client = await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = endpoint, AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token }
            }, http, ownsHttpClient: false), cancellationToken: timeout.Token);
            var result = await client.CallToolWithPollingAsync(new CallToolRequestParams
            {
                Name = "core_api_list", Arguments = new Dictionary<string, JsonElement> { ["limit"] = JsonSerializer.SerializeToElement(1) }
            }, cancellationToken: timeout.Token);
            Assert.IsFalse(result.IsError == true);
            Assert.IsTrue(result.StructuredContent!.Value.GetProperty("success").GetBoolean());
            Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(directory, "tasks"), "*.json").Any());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(errors, output);
        }
    }
}
