using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using ExHyperV.Server.Api;

namespace ExHyperV.Server.Tests;

[TestClass]
public sealed class TransportTests
{
    private string directory = null!;
    private static string Exe => Path.ChangeExtension(typeof(ApiCatalog).Assembly.Location, ".exe");
    [TestInitialize] public void Setup() => directory = TestStorage.CreatePath();
    [TestCleanup] public void Cleanup() => TestStorage.Delete(directory);

    internal static Uri Loopback()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return new Uri($"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}/");
    }

    internal static HttpClient Client(Uri url) => new(new HttpClientHandler { UseProxy = false })
        { BaseAddress = new Uri(url, "api/v1/"), Timeout = TimeSpan.FromSeconds(30) };

    private Process Launch(string? root, string? input, bool interactive, params string[] args)
    {
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        start.Environment.Remove("EXHYPERV_SERVER_TOKEN");
        if (root != null) start.Environment["EXHYPERV_SERVER_TOKEN"] = root;
        start.Environment["EXHYPERV_SERVER_STATE_DIRECTORY"] = directory;
        start.Environment["EXHYPERV_TEST_COMMAND"] = JsonSerializer.Serialize(new { executable = Exe, arguments = args, input, interactive });
        foreach (string arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-File",
            Path.Combine(AppContext.BaseDirectory, "Invoke-TestChild.ps1"), directory }) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    private string ReadLog(string name) => File.Exists(Path.Combine(directory, name))
        ? File.ReadAllText(Path.Combine(directory, name)) : "";

    private async Task<(int Code, string Output, string Errors)> Command(string? input, params string[] args)
    {
        using var child = Launch(null, input, false, args);
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            return (child.ExitCode, ReadLog("stdout.txt"), ReadLog("stderr.txt"));
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
    }

    [TestMethod]
    public async Task CliPersistsPasswordVerifiesFingerprintAndRejectsUnprotectedRemoteBinding()
    {
        Assert.AreEqual("2.0.0-Alpha", (await Command(null, "--version")).Output.Trim());
        StringAssert.Contains((await Command(null, "-h")).Output, "--cors-origins");
        var verified = await Command(null, "--verify-contracts");
        Assert.AreEqual(0, verified.Code, verified.Errors);
        Assert.AreEqual(new ApiCatalog().ContractFingerprint, verified.Output.Trim());
        Assert.AreNotEqual(0, (await Command(null, "--url", "http://0.0.0.0:5191")).Code);
        Assert.AreEqual(0, (await Command("secret from stdin", "--set-password")).Code);
        Assert.IsTrue(new Hosting.PasswordStore(directory).Load()!.Verify("secret from stdin"));
        Assert.AreEqual(0, (await Command(null, "--password", "second secret")).Code);
        Assert.IsTrue(new Hosting.PasswordStore(directory).Load()!.Verify("second secret"));
        Assert.AreEqual(0, (await Command(null, "--clear-password")).Code);
        Assert.IsNull(new Hosting.PasswordStore(directory).Load());
    }

    [TestMethod]
    public async Task RealServerSupportsBrowserLoginCoreCallsAndGracefulExit()
    {
        const string password = "server password from stdin";
        string root = Guid.NewGuid().ToString("N");
        var saved = await Command(password, "--set-password");
        Assert.AreEqual(0, saved.Code, saved.Errors);
        var url = Loopback();
        using var http = Client(url);
        using var child = Launch(root, null, true, "--url", url.ToString(), "--read-only", "--cors-origins", "https://trusted.test", "--toolsets", "console");
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            JsonElement health;
            while (true)
            {
                if (child.HasExited) Assert.Fail(ReadLog("stderr.txt") + ReadLog("stdout.txt"));
                try { health = await http.GetFromJsonAsync<JsonElement>("health", deadline.Token); break; }
                catch (HttpRequestException error) when (error.StatusCode == null) { await Task.Delay(100, deadline.Token); }
            }
            await VerifyManagementFlow(http, url, password, root, health);
            File.WriteAllText(Path.Combine(directory, "shutdown"), "");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.AreEqual(0, child.ExitCode, ReadLog("stderr.txt"));
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
        }
    }

    [TestMethod]
    public async Task ManagementFlowUsesTheSameHttpContractInAHostedInstance()
    {
        var url = Loopback();
        string root = Guid.NewGuid().ToString("N");
        const string password = "test password";
        new Hosting.PasswordStore(directory).Save(Hosting.PasswordHash.Create(password));
        var options = Hosting.LaunchOptions.Parse(["--url", url.ToString(), "--read-only", "--state-dir", directory,
            "--cors-origins", "https://trusted.test", "--toolsets", "console"]);
        await using var app = Program.Build(options, root);
        await app.StartAsync();
        try
        {
            using var http = Client(url);
            var health = await http.GetFromJsonAsync<JsonElement>("health");
            await VerifyManagementFlow(http, url, password, root, health);
        }
        finally { await app.StopAsync(); }
    }

    private static async Task VerifyManagementFlow(HttpClient http, Uri url, string password, string root, JsonElement health)
    {
        Assert.AreEqual(3, health.EnumerateObject().Count());
        Assert.AreEqual("ok", health.GetProperty("status").GetString());
        Assert.IsTrue(health.GetProperty("authRequired").GetBoolean());
        using var denied = await http.GetAsync("server/info");
        Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.IsTrue(denied.Headers.CacheControl!.NoStore);
        using var rebound = new HttpRequestMessage(HttpMethod.Get, "health");
        rebound.Headers.Host = "rebind.test";
        Assert.AreEqual(HttpStatusCode.Forbidden, (await http.SendAsync(rebound)).StatusCode);
        foreach (string origin in new[] { url.GetLeftPart(UriPartial.Authority), "https://trusted.test", "https://other.test" })
        {
            using var preflight = new HttpRequestMessage(HttpMethod.Options, "server/info");
            preflight.Headers.Add("Origin", origin);
            preflight.Headers.Add("Access-Control-Request-Method", "GET");
            preflight.Headers.Add("Access-Control-Request-Headers", "Authorization, Content-Type");
            preflight.Headers.Add("Access-Control-Request-Private-Network", "true");
            using var response = await http.SendAsync(preflight);
            if (origin == "https://other.test") { Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode); continue; }
            Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            Assert.AreEqual(origin, response.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.AreEqual("true", response.Headers.GetValues("Access-Control-Allow-Private-Network").Single());
            StringAssert.Contains(string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")), "Authorization");
            StringAssert.Contains(string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")), "Content-Type");
        }

        using var login = await http.PostAsJsonAsync("auth/login", new { password, clientName = "browser" });
        var session = await Json(login);
        Guid id = session.GetProperty("sessionId").GetGuid();
        string token = session.GetProperty("token").GetString()!;
        http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        http.DefaultRequestHeaders.Add("Origin", "https://trusted.test");
        using var info = await http.GetAsync("server/info");
        Assert.AreEqual("https://trusted.test", info.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var settings = await Json(info);
        Assert.IsTrue(settings.GetProperty("readOnly").GetBoolean());
        Assert.IsTrue(settings.GetProperty("contractReviewed").GetBoolean());
        Assert.AreEqual("token+password", settings.GetProperty("authenticationMode").GetString());
        var spec = await http.GetFromJsonAsync<JsonElement>("openapi.json");
        Assert.AreEqual("3.0.3", spec.GetProperty("openapi").GetString());
        var catalog = await http.GetFromJsonAsync<JsonElement>("core/catalog?group=console&kind=Method&search=GetBackends&readOnlyOnly=true&limit=1");
        Assert.AreEqual(1, catalog.GetProperty("total").GetInt32());
        var apiId = catalog.GetProperty("entries")[0].GetProperty("apiId").GetString();
        var describe = await http.GetFromJsonAsync<JsonElement>("core/describe?apiId=" + Uri.EscapeDataString(apiId!));
        Assert.AreEqual("object", describe.GetProperty("inputSchema").GetProperty("type").GetString());
        Assert.IsTrue(describe.GetProperty("description").GetString()!.Contains("后端"));
        Assert.AreEqual(0, (await http.GetFromJsonAsync<JsonElement>("core/catalog?group=vm")).GetProperty("total").GetInt32());
        var call = await Json(await http.PostAsJsonAsync("core/call", new { apiId, background = false }));
        Assert.IsTrue(call.GetProperty("success").GetBoolean());
        StringAssert.Contains(call.GetProperty("result").GetRawText(), "RdpBasic");
        var op = await Json(await http.PostAsJsonAsync("core/call", new { apiId, background = true }), HttpStatusCode.Accepted);
        Assert.AreNotEqual(Guid.Empty, op.GetProperty("operationId").GetGuid());

        Assert.AreEqual(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("auth/password", new { password })).StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("core/set", new { apiId, value = 4 })).StatusCode);
        var ticket = await Json(await http.PostAsync("auth/ws-ticket", null));
        Assert.IsTrue(ticket.GetProperty("ticket").GetString()!.Length >= 32);
        var sessions = (await http.GetFromJsonAsync<JsonElement>("auth/sessions")).GetProperty("sessions");
        Assert.AreEqual(1, sessions.GetArrayLength());
        Assert.AreEqual(3, sessions[0].EnumerateObject().Count());
        Assert.AreEqual(id, sessions[0].GetProperty("sessionId").GetGuid());
        Assert.AreEqual(HttpStatusCode.NoContent, (await http.DeleteAsync($"auth/sessions/{id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync("server/info")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new("Bearer", root);
        Assert.AreEqual(HttpStatusCode.NoContent, (await http.PostAsync("auth/logout", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await http.GetAsync("server/info")).StatusCode);
        var next = await Json(await http.PostAsJsonAsync("auth/login", new { token = root }));
        http.DefaultRequestHeaders.Authorization = new("Bearer", next.GetProperty("token").GetString());
        Assert.AreEqual(HttpStatusCode.NoContent, (await http.PostAsync("auth/logout", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await http.GetAsync("auth/sessions")).StatusCode);
        for (int i = 0; i < 5; i++)
        {
            using var bad = await http.PostAsJsonAsync("auth/login", new { password = "wrong" });
            Assert.AreEqual(i == 4 ? HttpStatusCode.TooManyRequests : HttpStatusCode.Unauthorized, bad.StatusCode);
        }
    }

    internal static async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using (response)
        {
            string content = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(expected, response.StatusCode, content);
            return JsonSerializer.Deserialize<JsonElement>(content);
        }
    }
}
