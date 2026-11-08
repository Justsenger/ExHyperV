using ExHyperV.Services;

namespace ExHyperV.Web;

/// <summary>One pending lease. Replaced frames contribute their damage before being released.</summary>
internal sealed class FrameMailbox : IDisposable
{
    private readonly object gate = new();
    private ConsoleFrame? pending;
    private ConsoleFrameRegion damage;
    private long lastSequence;
    private int width, height;
    private bool disposed;
    private TaskCompletionSource available = NewSignal();
    public long Received { get; private set; }
    public long Replaced { get; private set; }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Offer(ConsoleFrame frame)
    {
        lock (gate)
        {
            if (disposed) return;
            var retained = frame.Retain();
            bool full = lastSequence == 0 || frame.BaseSequence != lastSequence
                || frame.Width != width || frame.Height != height || frame.HasFullDamage;
            var nextDamage = full ? new ConsoleFrameRegion(0, 0, frame.Width, frame.Height) : frame.Damage;
            damage = pending is null || full ? nextDamage : damage.Union(nextDamage);
            if (pending is not null) { pending.Dispose(); Replaced++; }
            pending = retained;
            lastSequence = frame.Sequence; width = frame.Width; height = frame.Height; Received++;
            available.TrySetResult();
        }
    }

    public async ValueTask<PendingFrame> TakeAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (pending is not null)
                {
                    var result = new PendingFrame(pending, damage, Received, Replaced);
                    pending = null; damage = default; available = NewSignal();
                    return result;
                }
                wait = available.Task;
            }
            await wait.WaitAsync(token);
        }
    }

    public PendingFrame? TryTake()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (pending is null) return null;
            var result = new PendingFrame(pending, damage, Received, Replaced);
            pending = null; damage = default; available = NewSignal();
            return result;
        }
    }

    public Task WaitForFrameAsync(CancellationToken token)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return available.Task.WaitAsync(token);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            pending?.Dispose(); pending = null;
            available.TrySetCanceled();
        }
    }
}

internal sealed record PendingFrame(ConsoleFrame Frame, ConsoleFrameRegion Damage, long Received, long Replaced) : IDisposable
{
    public void Dispose() => Frame.Dispose();
}
