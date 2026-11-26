using System.Net;
using System.Text.Json;
using ExHyperV.Server.Hosting;
using Microsoft.AspNetCore.Http;

namespace ExHyperV.Server.Tests;

[TestClass]
public sealed class SecurityTests
{
    private static readonly string Root = new('X', 32);
    private string directory = null!;
    [TestInitialize] public void Setup() => directory = TestStorage.CreatePath();
    [TestCleanup] public void Cleanup() => TestStorage.Delete(directory);

    private Authentication Create(Clock clock, params string[] args) =>
        new(LaunchOptions.Parse(["--state-dir", directory, ..args]), null, clock);

    [TestMethod]
    public void LaunchValidationRejectsAmbiguousUrlsAndOrigins()
    {
        Assert.AreEqual(5191, LaunchOptions.Parse([]).Url.Port);
        foreach (var url in new[] { "ftp://127.0.0.1", "http://dns.test:51", "http://127.0.0.1:0", "http://localhost/path" })
            Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--url", url]), url);
        foreach (var origin in new[] { "null", "*", "https://good.test/", "https://good.test/a/..", "https://@good.test",
            "https://good.test?", "https://good.test#", "https://good.test, ", "https://good.test:bad", "https://good.test\\a" })
            Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--cors-origins", origin]), origin);
        Assert.AreEqual(2, LaunchOptions.Parse(["--cors-origins", "https://one.test, http://[::1]:51"]).Origins.Count);
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--url"]));
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--toolsets", "unknown"]));
        Assert.AreEqual(Command.ClearPassword, LaunchOptions.Parse(["--set-password", "--clear-password"]).Command);
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--clear-password", "--password", "pwd"]));
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--unknown"]));
    }

    [TestMethod]
    public void RemoteListenersRequireAValidRootOrPersistedPassword()
    {
        foreach (string url in new[] { "http://0.0.0.0:5191", "http://[::]:5191", "http://192.0.2.1:5191" })
            Assert.Throws<ArgumentException>(() => Create(new(), "--url", url));
        var options = LaunchOptions.Parse(["--state-dir", directory, "--url", "http://[::]:5191"]);
        foreach (string token in new[] { "short", new string('a', 513), Root + " " })
            Assert.Throws<ArgumentException>(() => new Authentication(options, token, TimeProvider.System));
        var auth = new Authentication(options, Root, TimeProvider.System);
        Assert.IsTrue(auth.Authenticate(Root, out var id));
        Assert.IsNull(id);
        Assert.IsFalse(auth.Authenticate(new string('x', 32), out _));
    }

    [TestMethod]
    public void PasswordUsesRandomSaltAndFailsClosedOnCorruptState()
    {
        var store = new PasswordStore(directory);
        const string password = "persistent secret";
        store.Save(PasswordHash.Create(password));
        byte[] salt = store.Load()!.Salt;
        store.Save(PasswordHash.Create(password));
        Assert.IsFalse(salt.SequenceEqual(store.Load()!.Salt));
        string path = Path.Combine(directory, "auth.json");
        Assert.IsFalse(File.ReadAllText(path).Contains(password));
        var restarted = Create(new Clock(), "--url", "http://0.0.0.0:5191");
        var login = restarted.Login(new(password, null, "client"), "local");
        Assert.IsTrue(restarted.Authenticate(login.Token, out _));
        File.WriteAllText(path, "{}");
        Assert.Throws<InvalidDataException>(() => Create(new()));
        store.Clear();
        Assert.IsFalse(Create(new()).Required);
    }

    [TestMethod]
    public void TicketsExpireAtThirtySecondsAndRevokeWithTheSession()
    {
        var clock = new Clock();
        var auth = new Authentication(LaunchOptions.Parse(["--state-dir", directory]), Root, clock);
        var login = auth.Login(new(null, Root, "client"), "local");
        var ticket = auth.IssueTicket(login.SessionId);
        Assert.AreEqual(clock.GetUtcNow().AddSeconds(30), ticket.ExpiresAt);
        Assert.IsNotNull(auth.ConsumeTicket(ticket.Ticket));
        Assert.IsNull(auth.ConsumeTicket(ticket.Ticket));
        ticket = auth.IssueTicket(login.SessionId);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.IsNull(auth.ConsumeTicket(ticket.Ticket));
        ticket = auth.IssueTicket(login.SessionId);
        Assert.IsTrue(auth.Revoke(login.SessionId));
        Assert.IsNull(auth.ConsumeTicket(ticket.Ticket));
        Assert.IsFalse(auth.Authenticate(login.Token, out _));
        login = auth.Login(new(null, Root, "client"), "local");
        clock.Advance(TimeSpan.FromHours(24));
        Assert.IsFalse(auth.Authenticate(login.Token, out _));
        Assert.IsEmpty(auth.List());
    }

    [TestMethod]
    public async Task RemoteIpAndHostValidationPrecedeAnonymousAccess()
    {
        async Task<int> Check(string url, string host, string remote, string? token = null)
        {
            var options = LaunchOptions.Parse(["--url", url, "--state-dir", directory]);
            var auth = new Authentication(options, token, TimeProvider.System);
            var context = new DefaultHttpContext();
            context.Request.Host = new(host);
            context.Connection.RemoteIpAddress = IPAddress.Parse(remote);
            await HttpAccess.Invoke(context, _ => Task.CompletedTask, options, auth);
            Assert.AreEqual("no-store", context.Response.Headers.CacheControl.ToString());
            return context.Response.StatusCode;
        }
        Assert.AreEqual(200, await Check("http://localhost:5191", "localhost", "::ffff:127.0.0.1"));
        Assert.AreEqual(403, await Check("http://localhost:5191", "localhost", "192.0.2.2"));
        Assert.AreEqual(403, await Check("http://localhost:5191", "dns.test", "::1"));
        Assert.AreEqual(403, await Check("http://192.0.2.1:5191", "192.0.2.2", "192.0.2.3", Root));
        Assert.AreEqual(401, await Check("http://[::]:5191", "[::1]", "::1", Root));
        Assert.AreEqual(403, await Check("http://[::]:5191", "dns.test", "::1", Root));
    }

    [TestMethod]
    public void LockoutExpiresWithoutSleeping()
    {
        var clock = new Clock();
        var auth = new Authentication(LaunchOptions.Parse(["--state-dir", directory]), Root, clock);
        for (int i = 0; i < 4; i++) Assert.Throws<InvalidCredentialException>(() => auth.Login(new("wrong", null, null), "local"));
        Assert.Throws<LoginLimitedException>(() => auth.Login(new("wrong", null, null), "local"));
        Assert.Throws<LoginLimitedException>(() => auth.Login(new(null, Root, null), "local"));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.IsNotNull(auth.Login(new(null, Root, null), "local"));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan step) => now += step;
    }
}
