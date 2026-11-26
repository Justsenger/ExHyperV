using ExHyperV.Services;

namespace ExHyperV.App.UI.Shared.ConsoleHost;

// Statically supplied by the local host. Inspect reads the connector's current public snapshot.
// Guid.Empty requests host status only. Input and cleanup must be bound to the connection's grant.
internal interface IConsoleHostExtensionConnector
{
    string BackendId { get; }
    event Action<Guid>? SnapshotChanged;
    ConsoleExtensionSnapshot Inspect(Guid vmId);
    IReadOnlyList<ConsoleDisplayMode> GetModes(Guid vmId);
    IConsoleConnection Connect(ConsoleConnectionOptions options);
    // Simple connectors construct an unstarted connection immediately. A connector with an
    // asynchronous factory can also cancel negotiation before publishing a connection.
    Task<IConsoleConnection> ConnectAsync(ConsoleConnectionOptions options, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Connect(options));
    }

    // A connector must positively confirm the same guest session and equivalent rights.
    bool AllowsFallback(Guid vmId, string targetBackend) => false;
    string? GetGuestComponentId(Guid vmId, ulong version) => null;
    Task<ConsoleExtensionActionResult> ExecuteActionAsync(ConsoleExtensionActionRequest request, CancellationToken token);
}

internal sealed class ConsoleExtensionCatalog
{
    internal const string External = "External";
    internal static ConsoleExtensionCatalog Local { get; } = new();
    private IConsoleHostExtensionConnector? connector;
    internal event Action<Guid>? Changed;
    internal bool Registered => connector != null;
    internal IEnumerable<string> Backends => ConsoleService.GetBackends().Select(b => b.BackendId).Append(External);

    internal void Register(IConsoleHostExtensionConnector value)
    {
        if (value.BackendId != External || Interlocked.CompareExchange(ref connector, value, null) != null)
            throw new ArgumentException("Invalid or duplicate console backend.", nameof(value));
        value.SnapshotChanged += id => Changed?.Invoke(id);
        Changed?.Invoke(Guid.Empty);
    }

    private static ConsoleExtensionSnapshot Offline(ConsoleExtensionStateCode code)
    {
        var unknown = new ConsoleExtensionStage((int)ConsoleExtensionStateCode.Unverified);
        return new(0, new((int)code), unknown, unknown, unknown, ConsoleTransportKind.Unknown, 0,
            ConsoleCapabilities.None, ConsoleCapabilities.None, new(false, false, 0), [], null,
            code == ConsoleExtensionStateCode.AccessDenied ? "EXT-403" : "EXT-001");
    }

    internal ConsoleExtensionSnapshot Inspect(Guid vmId)
    {
        try { return connector?.Inspect(vmId) ?? Offline(ConsoleExtensionStateCode.Unreachable); }
        catch (UnauthorizedAccessException) { return Offline(ConsoleExtensionStateCode.AccessDenied); }
        catch (Exception) { return Offline(ConsoleExtensionStateCode.Unreachable); }
    }

    internal static string? Diagnostic(string? id) => id is { Length: > 0 and <= 32 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? id : null;

    internal static bool Ready(ConsoleExtensionSnapshot s) => s.Control.CanView &&
        (s.Granted & s.Supported).HasFlag(ConsoleCapabilities.Video) &&
        s.Host.Code == ConsoleExtensionStateCode.Ready && s.Authorization.Code == ConsoleExtensionStateCode.Ready &&
        s.Guest.Code == ConsoleExtensionStateCode.Ready;

    internal IReadOnlyList<ConsoleDisplayMode> GetModes(Guid vmId)
    {
        try { return connector?.GetModes(vmId) ?? []; }
        catch (Exception) { return []; }
    }

    internal bool AllowsFallback(Guid vmId, string targetBackend)
    {
        try { return connector?.AllowsFallback(vmId, targetBackend) == true; }
        catch (Exception) { return false; }
    }

    internal Task<IConsoleConnection> ConnectAsync(string backend, ConsoleConnectionOptions options, CancellationToken token) =>
        Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            if (backend != External) return ConsoleService.CreateConnection(options, backend);
            var provider = connector;
            if (provider == null || !Ready(Inspect(options.VmId))) throw new UnauthorizedAccessException();
            return await provider.ConnectAsync(options, token).ConfigureAwait(false);
        }, token);

    internal static bool IsAvailable(ConsoleExtensionSnapshot s, ConsoleExtensionActionId action, Guid vmId) =>
        s.AvailableActions.Contains(action) && action switch
        {
            ConsoleExtensionActionId.ActivateHost or ConsoleExtensionActionId.MaintainHost => true,
            ConsoleExtensionActionId.DeployGuestComponent or ConsoleExtensionActionId.RetryConnection => vmId != Guid.Empty,
            _ => false
        };

    internal async Task<ConsoleExtensionActionResult?> ExecuteAsync(Guid vmId, ulong version,
        ConsoleExtensionActionId action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var provider = connector;
        var snapshot = Inspect(vmId);
        if (provider == null || snapshot.Version != version || !IsAvailable(snapshot, action, vmId)) return null;
        string? component = action == ConsoleExtensionActionId.DeployGuestComponent ? provider.GetGuestComponentId(vmId, version) : null;
        if (action == ConsoleExtensionActionId.DeployGuestComponent && string.IsNullOrWhiteSpace(component)) return null;
        var request = new ConsoleExtensionActionRequest(Guid.NewGuid(), version, action,
            vmId == Guid.Empty ? null : vmId, component, null);
        var result = await provider.ExecuteActionAsync(request, token);
        return result.RequestId == request.RequestId ? result : null;
    }
}
