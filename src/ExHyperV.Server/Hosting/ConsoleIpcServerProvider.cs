using ExHyperV.ConsoleIpc;
using ExHyperV.Services;

namespace ExHyperV.Server.Hosting;

internal sealed class ConsoleIpcServerProvider(ConsoleIpcDiscovery discovery) : IConsoleExtensionProvider, IDisposable
{
    private readonly ConsoleIpcClient client = new(discovery, 2);
    public string BackendId => ConsoleDirectory.ExtensionSlot;
    internal ConsoleExtensionSnapshot HostStatus => client.Inspect(Guid.Empty, "").Snapshot;

    public ExtensionAccess Inspect(Guid vm, SocketAuthorization viewer)
    {
        var access = client.Inspect(vm, viewer.Subject, wait: true);
        return new(access.Snapshot, access.Modes, access.Guest, access.Domain, access.Revoked) { Ipc = access };
    }

    public IConsoleConnection Connect(ConsoleConnectionOptions options, ExtensionAccess access) =>
        client.Session(options, access.Ipc ?? throw new UnauthorizedAccessException());

    public void Dispose() => client.Dispose();
}
