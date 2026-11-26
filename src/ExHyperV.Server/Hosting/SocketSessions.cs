using Microsoft.Extensions.Hosting;

namespace ExHyperV.Server.Hosting;

// Kestrel can finish its own drain when its deadline expires. A native session still
// owns its callbacks and sampler until the request's asynchronous cleanup returns.
internal sealed class SocketSessions : IHostedService
{
    private readonly object gate = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int count;
    private bool stopping;

    internal IDisposable? Enter()
    {
        lock (gate)
        {
            if (stopping) return null;
            count++;
            return new Registration(this);
        }
    }

    private sealed class Registration(SocketSessions owner) : IDisposable
    {
        public void Dispose()
        {
            lock (owner.gate)
                if (--owner.count == 0 && owner.stopping) owner.drained.TrySetResult();
        }
    }

    internal void BeginStop()
    {
        lock (gate)
        {
            stopping = true;
            if (count == 0) drained.TrySetResult();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken)
    {
        BeginStop();
        return drained.Task;
    }
}
