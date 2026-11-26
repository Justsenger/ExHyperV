using System.Security.Cryptography;
using System.Text;

namespace ExHyperV.Server.Hosting;

internal sealed record LoginRequest(string? Password, string? Token, string? ClientName);
internal sealed record SessionInfo(Guid SessionId, DateTimeOffset ExpiresAt, string ClientName);
internal sealed record LoginReply(Guid SessionId, string Token, DateTimeOffset ExpiresAt, string ClientName);
internal sealed record TicketReply(string Ticket, DateTimeOffset ExpiresAt);
internal sealed class LoginLimitedException : Exception;
internal sealed class InvalidCredentialException : Exception;

internal sealed class Authentication
{
    private readonly object gate = new();
    private readonly PasswordStore store;
    private readonly TimeProvider clock;
    private readonly byte[]? root;
    private PasswordHash? password;
    private sealed record Session(byte[] Hash, SessionInfo Info);
    private readonly Dictionary<ActiveSocket, SocketAuthorization> active = [];
    private long version = 1;
    private sealed record Ticket(SocketAuthorization Grant, DateTimeOffset ExpiresAt);
    private sealed record Attempts(int Count, DateTimeOffset Until);
    private readonly Dictionary<Guid, Session> sessions = [];
    private readonly Dictionary<string, Ticket> tickets = [];
    private readonly Dictionary<string, Attempts> attempts = [];
    private readonly SemaphoreSlim loginSlots = new(4);

    internal Authentication(LaunchOptions options, string? rootToken, TimeProvider clock)
    {
        this.clock = clock;
        store = new(options.StateDirectory);
        if (!string.IsNullOrEmpty(rootToken))
        {
            if (!ValidToken(rootToken)) throw new ArgumentException("EXHYPERV_SERVER_TOKEN 使用 32 至 512 位字母、数字或 -._~+/= 字符。");
            root = Digest(rootToken);
        }
        password = store.Load();
        if (!options.LoopbackOnly && !Required)
            throw new ArgumentException("非回环监听需要凭证，请先设置 EXHYPERV_SERVER_TOKEN 或使用 --set-password 保存访问口令。");
    }

    internal string Mode { get { lock (gate) return (root != null, password != null) switch
        { (true, true) => "token+password", (true, false) => "token", (false, true) => "password", _ => "loopback" }; } }
    internal bool Required { get { lock (gate) return root != null || password != null; } }
    private static bool ValidToken(string? text) => text is { Length: >= 32 and <= 512 }
        && text.All(c => char.IsAsciiLetterOrDigit(c) || "-._~+/=".Contains(c));
    private static byte[] Digest(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private static bool Matches(byte[]? expected, byte[] actual) => expected != null && CryptographicOperations.FixedTimeEquals(expected, actual);

    internal bool Authenticate(string? bearer, out Guid? sessionId)
    {
        sessionId = null;
        if (!ValidToken(bearer)) return false;
        var hash = Digest(bearer!);
        if (Matches(root, hash)) return true;
        lock (gate)
        {
            Prune();
            foreach (var session in sessions.Values)
                if (Matches(session.Hash, hash)) { sessionId = session.Info.SessionId; return true; }
            return false;
        }
    }

    internal LoginReply Login(LoginRequest request, string remote)
    {
        if ((request.Password == null) == (request.Token == null) || request.ClientName is { Length: > 128 })
            throw new ArgumentException("请提供口令或根令牌中的一项，客户端名称最长 128 个字符。");
        if (!loginSlots.Wait(0)) throw new LoginLimitedException();
        try
        {
            PasswordHash? snapshot;
            lock (gate)
            {
                Prune();
                if (attempts.TryGetValue(remote, out var previous) && previous.Count >= 5
                    || !attempts.ContainsKey(remote) && attempts.Count >= 1024) throw new LoginLimitedException();
                snapshot = password;
            }
            bool valid = request.Password is { } supplied ? snapshot?.Verify(supplied) == true
                : ValidToken(request.Token) && Matches(root, Digest(request.Token!));
            lock (gate)
            {
                var now = clock.GetUtcNow();
                if (!valid || snapshot != password)
                {
                    var prior = attempts.GetValueOrDefault(remote);
                    int failures = prior != null && prior.Until > now ? prior.Count + 1 : 1;
                    attempts[remote] = new(failures, now.AddMinutes(1));
                    if (failures >= 5) throw new LoginLimitedException();
                    throw new InvalidCredentialException();
                }
                attempts.Remove(remote);
                if (sessions.Count >= 256) throw new InvalidOperationException("会话已达上限，请先吊销已使用完的会话。");
                string token = Secret();
                var info = new SessionInfo(Guid.NewGuid(), now.AddHours(24), request.ClientName?.Trim() ?? "");
                sessions.Add(info.SessionId, new(Digest(token), info));
                return new(info.SessionId, token, info.ExpiresAt, info.ClientName);
            }
        }
        finally { loginSlots.Release(); }
    }

    internal SessionInfo[] List() { lock (gate) { Prune(); return sessions.Values.Select(s => s.Info).ToArray(); } }
    internal bool Revoke(Guid id)
    {
        ActiveSocket[] sockets;
        lock (gate)
        {
            foreach (string key in tickets.Where(t => t.Value.Grant.SessionId == id).Select(t => t.Key).ToArray()) tickets.Remove(key);
            if (!sessions.Remove(id)) return false;
            sockets = active.Where(pair => pair.Value.SessionId == id).Select(pair => pair.Key).ToArray();
        }
        Task.WhenAll(sockets.Select(socket => socket.Revoke())).GetAwaiter().GetResult();
        return true;
    }

    internal void SetPassword(string value)
    {
        var hash = PasswordHash.Create(value);
        ActiveSocket[] sockets;
        lock (gate)
        {
            store.Save(hash);
            password = hash;
            sessions.Clear();
            tickets.Clear();
            version++;
            sockets = active.Keys.ToArray();
        }
        Task.WhenAll(sockets.Select(socket => socket.Revoke())).GetAwaiter().GetResult();
    }

    internal SocketAuthorization Principal(Guid? sessionId, bool anonymous = false)
    {
        lock (gate)
        {
            Prune();
            if (anonymous && Required) throw new InvalidCredentialException();
            Session? session = null;
            if (sessionId.HasValue && !sessions.TryGetValue(sessionId.Value, out session)) throw new InvalidCredentialException();
            return new(sessionId, sessionId?.ToString("N") ?? (Required ? "root" : "loopback"), null, null, null, ConsoleRights.All, version, null, session?.Info.ExpiresAt);
        }
    }

    internal TicketReply IssueTicket(Guid? sessionId) => IssueGrant(Principal(sessionId));

    internal TicketReply IssueGrant(SocketAuthorization grant)
    {
        lock (gate)
        {
            Prune();
            Validate(grant);
            if (tickets.Count >= 1024) throw new InvalidOperationException("有效票据已达上限，请稍后重试。");
            string ticket = Secret();
            DateTimeOffset expires = clock.GetUtcNow().AddSeconds(30);
            tickets.Add(Convert.ToHexString(Digest(ticket)), new(grant, expires));
            return new(ticket, expires);
        }
    }

    internal SocketAuthorization? ConsumeTicket(string value)
    {
        lock (gate)
        {
            Prune();
            return ValidToken(value) && tickets.Remove(Convert.ToHexString(Digest(value)), out var ticket)
                && (ticket.Grant.SessionId == null || sessions.ContainsKey(ticket.Grant.SessionId.Value)) ? ticket.Grant : null;
        }
    }

    private void Validate(SocketAuthorization grant)
    {
        if (grant.Version != version || grant.SessionId is Guid id && !sessions.ContainsKey(id))
            throw new InvalidCredentialException();
    }

    internal void Register(SocketAuthorization grant, ActiveSocket socket)
    {
        lock (gate) { Prune(); Validate(grant); active.Add(socket, grant); }
    }

    internal void Unregister(ActiveSocket socket)
    {
        socket.Dispose();
        lock (gate) active.Remove(socket);
    }

    internal TimeSpan Remaining(DateTimeOffset expiry) => expiry - clock.GetUtcNow();

    internal ActiveSocket[] Subscribers(Guid sessionId)
    {
        lock (gate) return active.Where(pair => pair.Value.SessionId == sessionId).Select(pair => pair.Key).ToArray();
    }

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (Guid id in sessions.Where(s => s.Value.Info.ExpiresAt <= now).Select(s => s.Key).ToArray()) sessions.Remove(id);
        foreach (string key in tickets.Where(t => t.Value.ExpiresAt <= now).Select(t => t.Key).ToArray()) tickets.Remove(key);
        foreach (string key in attempts.Where(a => a.Value.Until <= now).Select(a => a.Key).ToArray()) attempts.Remove(key);
    }
}
