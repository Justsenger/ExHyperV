using ExHyperV.Services;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

// One dispatcher owns transitions. Canceling a wait never abandons an outstanding close
// or permits a new backend to start before the old subscription has been disposed.
internal sealed class ConsoleSessionLifetime
{
    internal Task Drained { get; private set; } = Task.CompletedTask;

    internal async Task<IConsoleConnection> OpenAsync(Func<CancellationToken, Task<IConsoleConnection>> open, CancellationToken token)
    {
        await Drained.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        var session = await open(token);
        if (!token.IsCancellationRequested) return session;
        await CloseAsync(session);
        throw new OperationCanceledException(token);
    }

    internal Task CloseAsync(IConsoleConnection session) => Drained = Task.Run(async () =>
    {
        try { await session.DisconnectAsync().ConfigureAwait(false); }
        finally { await session.DisposeAsync().ConfigureAwait(false); }
    });
}
