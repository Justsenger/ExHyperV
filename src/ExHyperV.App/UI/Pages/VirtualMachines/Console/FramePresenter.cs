using ExHyperV.App.UI.Shared.ConsoleHost;
using ExHyperV.Services;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

internal sealed class FramePresenter : IDisposable
{
    private readonly Func<Action, bool> enqueue;
    private readonly Action<ConsoleFrame, ConsoleFrameDamage> present;
    private readonly Func<ConsoleGpuFrame, bool> presentGpu;
    private readonly Action cpuRequested;
    private readonly ConsoleDamageAccumulator damage = new();
    private readonly object gate = new();
    private IConsoleGpuDelivery? delivery;
    private ConsoleFrame? latest;
    private bool gpuPending, scheduled, disposed, started, showingGpu;

    internal FramePresenter(Func<Action, bool> enqueue, Action<ConsoleFrame, ConsoleFrameDamage> present,
        IConsoleGpuSource? source, Func<ConsoleGpuFrame, bool> presentGpu, Action cpuRequested)
    {
        this.enqueue = enqueue;
        this.present = present;
        this.presentGpu = presentGpu;
        this.cpuRequested = cpuRequested;
        try { delivery = source?.OpenDelivery(); }
        catch (Exception) { cpuRequested(); }
        if (delivery != null)
        {
            delivery.Available += Available;
            Available();
        }
    }

    internal void Start()
    {
        lock (gate)
        {
            started = true;
            if (latest != null || gpuPending) Schedule();
        }
    }

    internal void Receive(ConsoleFrame frame)
    {
        lock (gate)
        {
            if (disposed || showingGpu) return;
            damage.Add(frame);
            latest?.Dispose();
            latest = frame.Retain();
            Schedule();
        }
    }

    private void Available()
    {
        lock (gate)
        {
            if (disposed || delivery == null) return;
            gpuPending = true;
            Schedule();
        }
    }

    private void Schedule()
    {
        if (!started || scheduled) return;
        scheduled = enqueue(PresentLatest);
        if (!scheduled) { latest?.Dispose(); latest = null; }
    }

    private void PresentLatest()
    {
        ConsoleFrame? frame;
        ConsoleFrameDamage? update;
        IConsoleGpuDelivery? gpu;
        lock (gate)
        {
            if (disposed) return;
            frame = latest;
            latest = null;
            update = frame == null ? null : damage.Take();
            gpu = gpuPending ? delivery : null;
            scheduled = gpuPending = false;
        }
        using (frame)
        {
            if (gpu != null)
            {
                bool failed;
                try
                {
                    using var shared = gpu.TakeLatest();
                    failed = shared != null && !presentGpu(shared);
                    if (shared != null && !failed)
                    {
                        lock (gate) showingGpu = delivery == gpu;
                        return;
                    }
                }
                catch (Exception) { failed = true; }
                if (failed) UseCpu(gpu);
            }
            if (!showingGpu && frame != null) present(frame, update!);
        }
    }

    internal void RequestCpu()
    {
        if (delivery is { } gpu) UseCpu(gpu);
    }

    private void UseCpu(IConsoleGpuDelivery gpu)
    {
        lock (gate) { delivery = null; gpuPending = showingGpu = false; }
        gpu.Available -= Available;
        try { gpu.RequestCpu(); }
        catch (Exception) { /* The source may already have closed this delivery. */ }
        finally { gpu.Dispose(); cpuRequested(); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            latest?.Dispose();
            latest = null;
        }
        if (delivery != null) { delivery.Available -= Available; delivery.Dispose(); delivery = null; }
    }
}
