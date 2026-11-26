using ExHyperV.Services;

namespace ExHyperV.Server.Hosting;

// A trusted local provider supplies the isolation identity and invalidates the grant on changes.
internal sealed record ExtensionAccess(ConsoleExtensionSnapshot Snapshot, IReadOnlyList<ConsoleDisplayMode> Modes,
    string? GuestSession, string? IsolationDomain, CancellationToken Revoked);

internal interface IConsoleExtensionProvider
{
    string BackendId { get; }
    ExtensionAccess Inspect(Guid vm, SocketAuthorization viewer);
    IConsoleConnection Connect(ConsoleConnectionOptions options, ExtensionAccess access);
}

internal sealed class ConsoleDirectory(IConsoleSessionFactory builtin, IEnumerable<IConsoleExtensionProvider> providers)
{
    internal const string ExtensionSlot = "External";
    private readonly Dictionary<string, IConsoleExtensionProvider> extensions = Register(providers);
    private static Dictionary<string, IConsoleExtensionProvider> Register(IEnumerable<IConsoleExtensionProvider> providers)
    {
        var result = new Dictionary<string, IConsoleExtensionProvider>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (provider.BackendId is "RdpBasic" or "RdpEnhanced" || provider.BackendId.Length is < 1 or > 64
                || !provider.BackendId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                || !result.TryAdd(provider.BackendId, provider))
                throw new ArgumentException("扩展后端标识无效或重复。");
        }
        return result;
    }

    internal string[] Backends => ["RdpBasic", "RdpEnhanced", .. ExtensionIds];
    internal string[] ExtensionIds => extensions.Count == 0 ? [ExtensionSlot] : extensions.Keys.Order(StringComparer.Ordinal).ToArray();

    internal object[] HostSummary() => ExtensionIds.Select(id => (object)new
    {
        backendId = id, connectorRegistered = extensions.ContainsKey(id),
        host = new ConsoleExtensionStage((int)(extensions.ContainsKey(id) ? ConsoleExtensionStateCode.Unverified : ConsoleExtensionStateCode.Unreachable)),
        diagnosticId = extensions.ContainsKey(id) ? null : "EXT-001"
    }).ToArray();

    private static ExtensionAccess Offline(ConsoleExtensionStateCode code)
    {
        var unknown = new ConsoleExtensionStage((int)ConsoleExtensionStateCode.Unverified);
        return new(new(1, new((int)code), unknown, unknown, unknown, ConsoleTransportKind.Unknown, 0,
            ConsoleCapabilities.None, ConsoleCapabilities.None, new(false, false, 0), [],
            code == ConsoleExtensionStateCode.AccessDenied ? "访问被拒绝" : "宿主扩展不可达",
            code == ConsoleExtensionStateCode.AccessDenied ? "EXT-403" : "EXT-001"), [], null, null, default);
    }

    internal ExtensionAccess Inspect(string id, Guid vm, SocketAuthorization viewer)
    {
        if (!extensions.TryGetValue(id, out var provider)) return Offline(ConsoleExtensionStateCode.Unreachable);
        try
        {
            var access = provider.Inspect(vm, viewer);
            var s = access.Snapshot;
            string? diagnostic = s.DiagnosticId is { Length: >= 1 and <= 32 } code
                && code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? code : null;
            var safe = new ConsoleExtensionSnapshot(s.Version, s.Host, s.Authorization, s.Guest, s.Transport, s.TransportKind,
                s.SurfaceEpoch, s.Supported, s.Granted, s.Control, [], s.Host.Code switch
                {
                    ConsoleExtensionStateCode.Missing => "宿主扩展未安装",
                    ConsoleExtensionStateCode.Unreachable => "宿主扩展不可达",
                    _ when s.Authorization.Code == ConsoleExtensionStateCode.AccessDenied => "扩展未获授权",
                    _ => null
                }, diagnostic);
            return access with { Snapshot = safe, Modes = SafeModes(access.Modes) };
        }
        catch (UnauthorizedAccessException) { return Offline(ConsoleExtensionStateCode.AccessDenied); }
        catch (Exception) { return Offline(ConsoleExtensionStateCode.Unreachable); }
    }

    internal static ConsoleDisplayMode[] SafeModes(IReadOnlyList<ConsoleDisplayMode> modes)
    {
        if (modes.Count > 256) throw new ArgumentException("显示模式数量超出范围。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return modes.Where(m => m.Id.Length is >= 1 and <= 64 && m.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            && m.Width <= 8192 && m.Height <= 8192 && ids.Add(m.Id)).ToArray();
    }

    internal static bool Builtin(string id) => id is "RdpBasic" or "RdpEnhanced";

    internal static ConsoleRights Rights(ConsoleCapabilities caps)
    {
        return (caps.HasFlag(ConsoleCapabilities.Video) ? ConsoleRights.Watch : 0)
            | (caps.HasFlag(ConsoleCapabilities.Input) ? ConsoleRights.Control : 0)
            | (caps.HasFlag(ConsoleCapabilities.AudioOutput) ? ConsoleRights.AudioOutput : 0)
            | (caps.HasFlag(ConsoleCapabilities.AudioInput) ? ConsoleRights.AudioInput : 0)
            | (caps.HasFlag(ConsoleCapabilities.Clipboard) ? ConsoleRights.Clipboard : 0)
            | (caps.HasFlag(ConsoleCapabilities.DisplayResize) ? ConsoleRights.DisplayMode : 0);
    }

    internal Task<ConsoleVmPolicy> InspectVm(Guid vm, CancellationToken token) => builtin.Inspect(vm, token);

    internal ConsoleRights Allowed(string id, Guid vm, SocketAuthorization viewer, LaunchOptions options, ConsoleVmPolicy target, bool secure, out ExtensionAccess? access)
    {
        access = null;
        if (!options.Toolsets.Contains("console")) return ConsoleRights.None;
        if (viewer.Subject == "loopback")
        {
            if (!Builtin(id)) access = Offline(ConsoleExtensionStateCode.Unverified);
            return ConsoleRights.None;
        }
        ConsoleRights allowed = options.ReadOnly ? ConsoleRights.Watch | ConsoleRights.AudioOutput : ConsoleRights.All;
        if (id == "RdpBasic") return allowed & target.Basic & (ConsoleRights.Watch | ConsoleRights.Control);
        if (id == "RdpEnhanced") return allowed & target.Enhanced;
        if (!ExtensionIds.Contains(id)) throw new ArgumentException("未知的控制台后端。");
        if (!target.ExtensionAllowed) { access = Offline(ConsoleExtensionStateCode.AccessDenied); return ConsoleRights.None; }
        access = Inspect(id, vm, viewer);
        if (!access.Snapshot.Control.CanView || access.Revoked.IsCancellationRequested
            || access.Snapshot.Host.Code != ConsoleExtensionStateCode.Ready || access.Snapshot.Authorization.Code != ConsoleExtensionStateCode.Ready
            || access.Snapshot.Guest.Code != ConsoleExtensionStateCode.Ready) return ConsoleRights.None;
        if (!secure) allowed &= ConsoleRights.Watch | ConsoleRights.AudioOutput;
        return allowed & Rights(access.Snapshot.Granted & access.Snapshot.Supported);
    }

    internal IConsoleConnection Create(ConsoleRequest request, ExtensionAccess? access) => Builtin(request.Backend)
        ? builtin.Create(request.Options, request.Backend)
        : extensions[request.Backend].Connect(request.Options, access!);
}
