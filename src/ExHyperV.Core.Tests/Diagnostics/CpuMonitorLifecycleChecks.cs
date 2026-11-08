using System.Collections.Concurrent;
using System.Threading.Channels;
using ExHyperV.Services;
using ExHyperV.Models;

internal static class CpuMonitorLifecycleChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(Action<bool, string> check)
    {
        await CheckNormalLifetimeAsync(check);
        await CheckPendingMaintenanceAsync(check);
        await CheckPendingSampleAsync(check);
        await CheckPendingEnumerationAsync(check);
        await CheckWarmupAndRebuildAsync(check);
        await CheckClosingDuringWarmupAsync(check);
    }

    private static async Task CheckNormalLifetimeAsync(Action<bool, string> check)
    {
        var host = new Harness
        {
            HostInstances = ["LP 0"],
            VmInstances = ["Active:Hv VP 0"],
            VirtualMachines = _ => Task.FromResult<IReadOnlyList<HyperVCpuMonitorService.VmCpuInfo>?>([new(Guid.NewGuid(), "Active", 1, true), new(Guid.Parse("18e56694-cf45-46b1-b04a-38b25fa909f5"), "Idle", 2, false)])
        };
        await using var service = new HyperVCpuMonitorService(host.CreateBackend());
        await host.NextDelayAsync();
        var warmup = service.GetCpuUsage();
        check(warmup.Count(m => m.Sample.State == PerformanceSampleState.WarmingUp) == 2
            && warmup.Count(m => m.Sample.State == PerformanceSampleState.NotApplicable) == 2,
            "CPU initial samples distinguish warming counters from powered-off virtual machines");
        var metrics = service.GetCpuUsage();
        check(metrics.Count == 4 && metrics.Any(m => m.VmName == "Host" && m.CoreId == 0 && m.Usage == 25) &&
            metrics.Any(m => m.VmName == "Active" && m.CoreId == 0 && m.Usage == 25) &&
            metrics.Count(m => m.VmName == "Idle" && m.Usage == 0) == 2,
            "CPU lifecycle path preserves host, active VM and stopped VM sample values");
        check(host.Delays.All(delay => delay == TimeSpan.FromSeconds(2)),
            "CPU maintenance keeps the original two-second delay");

        var closing = Enumerable.Range(0, 6).Select(_ => service.DisposeAsync().AsTask()).ToArray();
        await Task.WhenAll(closing).WaitAsync(Timeout);
        service.Dispose();
        check(host.Counters.Count == 2 && host.Counters.All(c => c.Releases == 1),
            "CPU concurrent async and repeated sync disposal release each owned counter once");
        check(IsDisposed(service.GetCpuUsage), "CPU a disposed instance rejects further samples");

        var replacementHost = new Harness { HostInstances = ["LP 0"] };
        await using var replacement = new HyperVCpuMonitorService(replacementHost.CreateBackend());
        await replacementHost.NextDelayAsync();
        check(replacement.GetCpuUsage().Single().Sample.State == PerformanceSampleState.WarmingUp,
            "CPU a replacement instance establishes its own sampling baseline");
        check(replacement.GetCpuUsage().Single().Usage == 25 && replacementHost.Counters.Single().Releases == 0,
            "CPU a fresh instance works after another instance has been permanently disposed");
    }

    private static async Task CheckPendingMaintenanceAsync(Action<bool, string> check)
    {
        var readEntered = Signal();
        var readFinished = new TaskCompletionSource<IReadOnlyList<HyperVCpuMonitorService.VmCpuInfo>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken readToken = default;
        var host = new Harness
        {
            HostInstances = ["LP 0"],
            VirtualMachines = token => { readToken = token; readEntered.TrySetResult(); return readFinished.Task; }
        };
        await using var service = new HyperVCpuMonitorService(host.CreateBackend());
        try
        {
            await readEntered.Task.WaitAsync(Timeout);
            var closing = service.DisposeAsync().AsTask();
            check(readToken.IsCancellationRequested && !closing.IsCompleted && host.Counters.IsEmpty,
                "CPU disposal waits for the initial identity query before any counters are allocated");
            var syncClosing = Task.Run(service.Dispose);
            check(IsDisposed(service.GetCpuUsage), "CPU stopping rejects a new sample while maintenance is still draining");
            readFinished.TrySetResult([new(Guid.NewGuid(), "late result", 3)]);
            await Task.WhenAll(closing, syncClosing).WaitAsync(Timeout);
            check(host.Counters.IsEmpty && host.Enumerations == 0,
                "CPU shutdown prevents counter enumeration after the pending identity query returns");
        }
        finally { readFinished.TrySetResult([]); }
    }

    private static async Task CheckPendingSampleAsync(Action<bool, string> check)
    {
        using var releaseSample = new ManualResetEventSlim();
        var sampleEntered = Signal();
        var host = new Harness { HostInstances = ["LP 0"] };
        host.OnSample = (_, call) =>
        {
            if (call == 2)
            {
                sampleEntered.TrySetResult();
                if (!releaseSample.Wait(Timeout)) throw new TimeoutException("Sample release was not signaled");
            }
            return 25;
        };
        await using var service = new HyperVCpuMonitorService(host.CreateBackend());
        await host.NextDelayAsync();
        check(service.GetCpuUsage().Single().Sample.State == PerformanceSampleState.WarmingUp,
            "CPU the held sample follows initial baseline establishment");
        var sample = Task.Run(service.GetCpuUsage);
        try
        {
            await sampleEntered.Task.WaitAsync(Timeout);
            var closing = service.DisposeAsync().AsTask();
            check(!closing.IsCompleted && host.Counters.Single().Releases == 0,
                "CPU stopping never releases a counter while its admitted sample is running");
            check(IsDisposed(service.GetCpuUsage), "CPU a sample arriving after close is rejected without entering the held counter lock");
            releaseSample.Set();
            var metrics = await sample.WaitAsync(Timeout);
            await closing.WaitAsync(Timeout);
            check(metrics.Single().Usage == 25 && host.Counters.Single().Releases == 1,
                "CPU the admitted sample finishes before final counter release");
        }
        finally { releaseSample.Set(); }
    }

    private static async Task CheckPendingEnumerationAsync(Action<bool, string> check)
    {
        using var releaseEnumeration = new ManualResetEventSlim();
        var enumerationEntered = Signal();
        var host = new Harness { HostInstances = ["LP 0"] };
        host.BeforeEnumeration = _ =>
        {
            enumerationEntered.TrySetResult();
            if (!releaseEnumeration.Wait(Timeout)) throw new TimeoutException("Enumeration release was not signaled");
        };
        await using var service = new HyperVCpuMonitorService(host.CreateBackend());
        try
        {
            await enumerationEntered.Task.WaitAsync(Timeout);
            var closing = service.DisposeAsync().AsTask();
            check(!closing.IsCompleted, "CPU disposal waits for an in-flight counter enumeration");
            releaseEnumeration.Set();
            await closing.WaitAsync(Timeout);
            check(host.Counters.IsEmpty && host.Enumerations == 2 && host.VmReads == 1,
                "CPU a completed enumeration after close creates no counters and starts no further identity query");
        }
        finally { releaseEnumeration.Set(); }
    }

    private static async Task CheckWarmupAndRebuildAsync(Action<bool, string> check)
    {
        var host = new Harness { HostInstances = ["LP 0", "LP 1"] };
        host.OnSample = (name, _) => name == "LP 1" ? throw new InvalidOperationException("Warmup failed") : 25;
        await using var service = new HyperVCpuMonitorService(host.CreateBackend());
        var nextCycle = await host.NextDelayAsync();
        var failed = host.Counters.Single(c => c.Name == "LP 1");
        var old = host.Counters.Single(c => c.Name == "LP 0");
        var first = service.GetCpuUsage();
        check(failed.Releases == 1 && old.Releases == 0 && first.Count == 2
            && first.Single(m => m.CoreId == 1).Sample.State == PerformanceSampleState.Failed
            && first.Single(m => m.CoreId == 0).Sample.State == PerformanceSampleState.WarmingUp,
            "CPU failed warmup releases its counter and reports failure alongside the healthy core");

        host.HostInstances = ["LP 2"];
        nextCycle.TrySetResult();
        await host.NextDelayAsync();
        var current = host.Counters.Single(c => c.Name == "LP 2");
        check(old.Releases == 1 && current.Releases == 0 && service.GetCpuUsage().Single().CoreId == 2,
            "CPU rebuilding releases vanished counters and retains the new live counter");
        await service.DisposeAsync();
        check(host.Counters.All(c => c.Releases == 1), "CPU final disposal does not release failed or previously removed counters twice");
    }

    private static async Task CheckClosingDuringWarmupAsync(Action<bool, string> check)
    {
        using var releaseWarmup = new ManualResetEventSlim();
        var warmupEntered = Signal();
        var host = new Harness { HostInstances = ["LP 0"] };
        host.OnSample = (_, _) =>
        {
            warmupEntered.TrySetResult();
            if (!releaseWarmup.Wait(Timeout)) throw new TimeoutException("Warmup release was not signaled");
            return 25;
        };
        await using var service = new HyperVCpuMonitorService(host.CreateBackend());
        try
        {
            await warmupEntered.Task.WaitAsync(Timeout);
            var closing = service.DisposeAsync().AsTask();
            check(!closing.IsCompleted && host.Counters.Single().Releases == 0,
                "CPU a counter warming up remains owned until its native call returns");
            releaseWarmup.Set();
            await closing.WaitAsync(Timeout);
            check(host.Counters.Single().Releases == 1 && host.VmReads == 1 && IsDisposed(service.GetCpuUsage),
                "CPU closing during warmup disposes the temporary counter instead of publishing it into the stopped instance");
        }
        finally { releaseWarmup.Set(); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static bool IsDisposed(Func<object> action)
    {
        try { action(); return false; }
        catch (ObjectDisposedException) { return true; }
    }

    internal sealed class Harness
    {
        internal string[] HostInstances = [];
        internal string[] VmInstances = [];
        internal Func<CancellationToken, Task<IReadOnlyList<HyperVCpuMonitorService.VmCpuInfo>?>> VirtualMachines =
            _ => Task.FromResult<IReadOnlyList<HyperVCpuMonitorService.VmCpuInfo>?>([]);
        internal Func<string, int, float> OnSample = (_, _) => 25;
        internal Action<string>? BeforeEnumeration;
        internal readonly ConcurrentBag<CounterProbe> Counters = [];
        internal readonly ConcurrentBag<TimeSpan> Delays = [];
        internal int Enumerations;
        internal int VmReads;
        private readonly Channel<TaskCompletionSource> _delays = Channel.CreateUnbounded<TaskCompletionSource>();

        internal HyperVCpuMonitorService.Backend CreateBackend() => new(
            category =>
            {
                Interlocked.Increment(ref Enumerations);
                BeforeEnumeration?.Invoke(category);
                return category.EndsWith("Logical Processor", StringComparison.Ordinal) ? HostInstances : VmInstances;
            },
            (_, name) =>
            {
                var counter = new CounterProbe(name, OnSample);
                Counters.Add(counter);
                return new HyperVCpuMonitorService.Counter(counter.Sample, counter.Release);
            },
            token => { Interlocked.Increment(ref VmReads); return VirtualMachines(token); },
            async (duration, token) =>
            {
                Delays.Add(duration);
                var next = Signal();
                _delays.Writer.TryWrite(next);
                await next.Task.WaitAsync(token);
            });

        internal Task<TaskCompletionSource> NextDelayAsync() => _delays.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
    }

    internal sealed class CounterProbe(string name, Func<string, int, float> sample)
    {
        internal string Name { get; } = name;
        internal int Releases;
        private int _samples;
        internal float Sample()
        {
            if (Volatile.Read(ref Releases) != 0) throw new InvalidOperationException("Sampled a released counter");
            return sample(Name, Interlocked.Increment(ref _samples));
        }
        internal void Release() => Interlocked.Increment(ref Releases);
    }
}
