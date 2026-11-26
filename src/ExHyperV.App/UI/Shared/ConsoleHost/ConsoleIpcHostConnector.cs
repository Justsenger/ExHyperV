using System.Security.Principal;
using ExHyperV.ConsoleIpc;
using ExHyperV.Services;

namespace ExHyperV.App.UI.Shared.ConsoleHost;

internal sealed class ConsoleIpcHostConnector(ConsoleIpcClient client) : IConsoleHostExtensionConnector
{
    private static readonly Lazy<string> Subject = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new UnauthorizedAccessException();
    });

    public string BackendId => ConsoleExtensionCatalog.External;
    public event Action<Guid>? SnapshotChanged { add => client.SnapshotChanged += value; remove => client.SnapshotChanged -= value; }
    public ConsoleExtensionSnapshot Inspect(Guid vmId) => client.Inspect(vmId, Subject.Value).Snapshot;
    public IReadOnlyList<ConsoleDisplayMode> GetModes(Guid vmId) => client.Inspect(vmId, Subject.Value).Modes;
    public bool AllowsFallback(Guid vmId, string targetBackend) => client.AllowsFallback(vmId, targetBackend, Subject.Value);
    public IConsoleConnection Connect(ConsoleConnectionOptions options) => client.Session(options, client.Inspect(options.VmId, Subject.Value, wait: true, fresh: true));
}
