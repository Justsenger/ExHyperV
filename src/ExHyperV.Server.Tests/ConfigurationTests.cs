using System.Security.AccessControl;
using System.Security.Principal;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using ExHyperV.Server.Hosting;

namespace ExHyperV.Server.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    private string directory = null!;
    [TestInitialize] public void Setup() => directory = TestStorage.CreatePath();
    [TestCleanup] public void Cleanup() => TestStorage.Delete(directory);

    [TestMethod]
    public async Task SavedConfigAppliesOnlyToUnspecifiedOptions()
    {
        string[] args = ["--state-dir", directory, "--read-only", "--url", "https://localhost:5500",
            "--toolsets", "host,vm", "--cors-origins", "https://one.test"];
        Assert.AreEqual(0, await Program.Main([..args, "--save-config"]));
        var loaded = LaunchOptions.Parse(["--state-dir", directory]);
        Assert.AreEqual("https://localhost:5500/", loaded.Url.ToString());
        Assert.IsTrue(loaded.ReadOnly);
        Assert.IsTrue(loaded.Toolsets.SetEquals(["host", "vm"]));
        Assert.IsTrue(loaded.Origins.SetEquals(["https://one.test"]));
        var cli = LaunchOptions.Parse(["--toolsets", "console", "--url", "http://localhost:6000", "--read-write",
            "--cors-origins", "https://two.test", "--state-dir", directory]);
        Assert.AreEqual(6000, cli.Url.Port);
        Assert.IsFalse(cli.ReadOnly);
        Assert.IsTrue(cli.Origins.SetEquals(["https://two.test"]));
        Assert.IsTrue(cli.Toolsets.SetEquals(["console"]));
        var data = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "server.json")));
        CollectionAssert.AreEquivalent(new[] { "url", "corsOrigins", "readOnly", "toolsets" }, data.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [TestMethod]
    public void InvalidOrCorruptConfigCannotBeOverwritten()
    {
        var options = LaunchOptions.Parse(["--state-dir", directory]);
        var store = new ServerConfigStore(directory);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "server.json");
        foreach (var text in new[] { "{", "null", "{\"url\":\"file:///C:/\"}", "{\"readOnly\":true,\"readOnly\":false}",
            "{\"toolsets\":[\"invalid\"]}", "{\"corsOrigins\":[\"*\"]}", "{\"readOnly\":\"false\"}", "{\"unexpected\":true}" })
        {
            File.WriteAllText(path, text);
            Assert.Throws<InvalidDataException>(() => LaunchOptions.Parse(["--save-config", "--state-dir", directory, "--read-only"]));
            Assert.Throws<InvalidDataException>(() => store.Save(options));
            Assert.AreEqual(text, File.ReadAllText(path));
        }
    }

    [TestMethod]
    public void DefaultDirectorySecurityRejectsUntrustedWriters()
    {
        var acl = new DirectorySecurity();
        using var identity = WindowsIdentity.GetCurrent();
        acl.SetOwner(identity.User!);
        acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        StateFiles.VerifyTrusted(FileAttributes.Directory, () => acl);
        Assert.Throws<UnauthorizedAccessException>(() => StateFiles.VerifyTrusted(FileAttributes.ReparsePoint, () => throw new AssertFailedException()));
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        acl.SetOwner(everyone);
        Assert.Throws<UnauthorizedAccessException>(() => StateFiles.VerifyTrusted(FileAttributes.Directory, () => acl));
        acl.SetOwner(identity.User!);
        acl.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Write, AccessControlType.Allow));
        Assert.Throws<UnauthorizedAccessException>(() => StateFiles.VerifyTrusted(FileAttributes.Directory, () => acl));
    }

    [TestMethod]
    public async Task ServiceArgumentsAreQuotedAndServiceModeHostsTheConfiguredEndpoint()
    {
        string[] args = ["--install-service", "--state-dir", directory, "--service-name", "ExHyperV.Test-1", "--read-only", "--toolsets", "vm"];
        var options = LaunchOptions.Parse(args);
        const string exe = @"C:\Program Files\ExHyperV\ExHyperV.Server.exe";
        var install = ServiceCommandBuilder.Build(options, exe, args);
        Assert.AreEqual(Path.Combine(Environment.SystemDirectory, "sc.exe"), install.FileName);
        Assert.IsFalse(install.UseShellExecute);
        CollectionAssert.AreEqual(new[] { "create", "ExHyperV.Test-1", "binPath=",
            $"\"{exe}\" \"--service\" \"--service-name\" \"ExHyperV.Test-1\" \"--state-dir\" \"{directory}\" \"--read-only\" \"--toolsets\" \"vm\"", "start=", "auto" }, install.ArgumentList.ToArray());
        var uninstall = LaunchOptions.Parse(["--uninstall-service", "--service-name", "ExHyperV.Test-1"]);
        CollectionAssert.AreEqual(new[] { "delete", "ExHyperV.Test-1" }, ServiceCommandBuilder.Build(uninstall, exe, []).ArgumentList.ToArray());
        foreach (var invalid in new[] { "/name", "-start", "two words", "a&b", "a\"b", "a\r\nb", new string('a', 81) })
            Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--install-service", "--service-name", invalid]));
        Assert.AreEqual("ExHyperV.Server", LaunchOptions.Parse(["--uninstall-service"]).ServiceName);
        await using var host = await SocketHost.Start(["--service", "--service-name", "ExHyperV.Test-1"]);
        Assert.AreEqual(System.Net.HttpStatusCode.OK, (await host.Http.GetAsync("health")).StatusCode);
    }

    [TestMethod]
    public async Task SimulatedServiceManagerLaunchesTheRegisteredImageWithPersistedConfiguration()
    {
        string state = Path.Combine(directory, "service state");
        var url = TransportTests.Loopback();
        Assert.AreEqual(0, await Program.Main(["--save-config", "--state-dir", state, "--url", url.ToString(),
            "--cors-origins", "https://service.test", "--toolsets", "vm"]));
        string[] args = ["--install-service", "--service-name", "ExHyperV.Test", "--state-dir", state, "--read-only"];
        string exe = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
        var install = ServiceCommandBuilder.Build(LaunchOptions.Parse(args), exe, args);
        string imagePath = install.ArgumentList[3];
        StringAssert.StartsWith(imagePath, $"\"{exe}\" ");
        // Play back SCM's image path directly. The system service registry is never opened.
        var start = new ProcessStartInfo(exe, imagePath[(exe.Length + 3)..]) { UseShellExecute = false, CreateNoWindow = true };
        string token = Guid.NewGuid().ToString("N");
        start.Environment["EXHYPERV_SERVER_TOKEN"] = token;
        using var service = Process.Start(start)!;
        try
        {
            using var http = TransportTests.Client(url);
            http.DefaultRequestHeaders.Add("Origin", "https://service.test");
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (true)
            {
                Assert.IsFalse(service.HasExited, "Service image exited before reaching readiness.");
                try
                {
                    var info = await http.GetFromJsonAsync<JsonElement>("server/info", timeout.Token);
                    Assert.AreEqual(service.Id, info.GetProperty("processId").GetInt32());
                    Assert.IsTrue(info.GetProperty("readOnly").GetBoolean());
                    Assert.AreEqual("vm", info.GetProperty("toolsets").EnumerateArray().Single().GetString());
                    break;
                }
                catch (HttpRequestException error) when (error.StatusCode == null) { await Task.Delay(50, timeout.Token); }
            }
        }
        finally
        {
            if (!service.HasExited) service.Kill();
            await service.WaitForExitAsync();
        }
    }
}
