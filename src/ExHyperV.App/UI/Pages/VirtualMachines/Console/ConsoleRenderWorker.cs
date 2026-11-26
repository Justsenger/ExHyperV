using System.Collections.Concurrent;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

/// <summary>Runs D3D11 console frame presentation off the WinUI 3 UI thread.</summary>
internal sealed class ConsoleRenderWorker : IDisposable
{
    private readonly ConcurrentQueue<Action> queue = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread thread;
    private volatile bool disposed;

    internal ConsoleRenderWorker()
    {
        thread = new(Loop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "ExHyperV Console Render"
        };
        thread.Start();
    }

    internal bool Enqueue(Action work)
    {
        if (disposed) return false;
        queue.Enqueue(work);
        wake.Set();
        return true;
    }

    internal Task<T?> InvokeAsync<T>(Func<T?> work)
    {
        if (disposed) return Task.FromResult(default(T));
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!Enqueue(() =>
        {
            if (disposed) { tcs.TrySetResult(default); return; }
            try { tcs.TrySetResult(work()); }
            catch (Exception error) { tcs.TrySetException(error); }
        }))
            tcs.TrySetResult(default);
        return tcs.Task;
    }

    private void Loop()
    {
        while (true)
        {
            while (queue.TryDequeue(out var work))
            {
                try { work(); }
                catch (Exception) { /* Callers isolate fatal device errors inside renderer or task completion. */ }
            }
            if (disposed && queue.IsEmpty) break;
            wake.WaitOne();
        }
    }

    internal void DisposeWith(Action? cleanup)
    {
        if (disposed) return;
        if (cleanup != null) queue.Enqueue(cleanup);
        disposed = true;
        wake.Set();
        thread.Join(1000);
        wake.Dispose();
    }

    public void Dispose() => DisposeWith(null);
}
