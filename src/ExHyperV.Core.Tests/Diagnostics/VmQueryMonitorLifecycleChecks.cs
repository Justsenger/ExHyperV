using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using System.Collections.Concurrent;

internal static class VmQueryMonitorLifecycleChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(Action<bool, string> check)
    {
        await CheckOwnershipAsync(check);
        await CheckGpuShutdownAsync(check);
        await CheckSynchronousContextShutdownAsync(check);
        await CheckNetworkShutdownAsync(check);
        await CheckCreationFailuresAsync(check);
        await CheckReleaseFailureAsync(check);
    }

    private static async Task CheckOwnershipAsync(Action<bool, string> check)
    {
        using var first = new Fixture();
        using var second = new Fixture();
        await first.Query.UpdateNetworkPerformanceAsync([first.Vm]);
        check(!first.Adapter.HasNetworkSpeedSample && first.Counters.Count == 2,
            "network initialization still warms up both counters without publishing a rate");
        await first.Query.UpdateNetworkPerformanceAsync([first.Vm]);
        var warmup = await first.Query.GetGpuPerformanceAsync([first.Vm]);
        check(warmup[first.Vm.Id].Sample?.State == PerformanceSampleState.WarmingUp, "GPU first sample reports its warm-up state");
        var gpu = await first.Query.GetGpuPerformanceAsync([first.Vm]);
        check(first.Adapter.HasNetworkSpeedSample && first.Adapter.SendBytesPerSecond == 123 && first.Adapter.ReceiveBytesPerSecond == 456 &&
            gpu[first.Vm.Id].Gpu3d == 25 && gpu[first.Vm.Id].IsDriverBound,
            "counter lifetime changes preserve network samples and GPU aggregation");
        await second.Query.UpdateNetworkPerformanceAsync([second.Vm]);
        await first.Query.DisposeAsync();
        first.Query.Dispose();
        check(first.Counters.All(counter => counter.Disposals == 1) && second.Counters.All(counter => counter.Disposals == 0),
            "disposing one query releases its owned GPU and network counters exactly once without touching another instance");
        await second.Query.UpdateNetworkPerformanceAsync([second.Vm]);
        check(second.Adapter.HasNetworkSpeedSample,
            "another query instance continues sampling after the first is released");
        check(await IsDisposedAsync(() => first.Query.GetGpuPerformanceAsync([])) &&
            await IsDisposedAsync(() => first.Query.UpdateNetworkPerformanceAsync([])),
            "disposed query objects reject even empty counter requests before touching native providers");
    }

    private static async Task CheckGpuShutdownAsync(Action<bool, string> check)
    {
        using var fixture = new Fixture();
        await fixture.Query.GetGpuPerformanceAsync([fixture.Vm]);
        using var release = new ManualResetEventSlim();
        var entered = NewSignal();
        fixture.ReadCounter = (counter, read) =>
        {
            if (counter.Kind == "Utilization Percentage" && read == 2)
            {
                entered.TrySetResult(true);
                if (!release.Wait(Timeout)) throw new TimeoutException();
            }
            return counter.DefaultValue;
        };
        var active = Task.Run(() => fixture.Query.GetGpuPerformanceAsync([fixture.Vm]));
        Task? queued = null;
        Task? synchronousDispose = null;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            queued = fixture.Query.GetGpuPerformanceAsync([fixture.Vm]);
            var closing = fixture.Query.DisposeAsync().AsTask();
            var repeated = fixture.Query.DisposeAsync().AsTask();
            var syncStarted = NewSignal();
            synchronousDispose = Task.Run(() => { syncStarted.SetResult(true); fixture.Query.Dispose(); });
            await syncStarted.Task.WaitAsync(Timeout);
            check(ReferenceEquals(closing, repeated) && !closing.IsCompleted && !synchronousDispose.IsCompleted &&
                fixture.Counters.Single().Disposals == 0,
                "all query disposal callers wait on the same completion while an admitted GPU sample is still using its counter");
            check(await IsDisposedAsync(() => fixture.Query.GetGpuPerformanceAsync([fixture.Vm])) &&
                await IsDisposedAsync(() => fixture.Query.UpdateNetworkPerformanceAsync([fixture.Vm])),
                "starting query shutdown immediately rejects new GPU and network samples");
            release.Set();
            var result = await active.WaitAsync(Timeout);
            check(await IsDisposedAsync(() => queued),
                "a GPU sample queued before shutdown leaves the gate without sampling or rebuilding counters");
            await Task.WhenAll(closing, repeated, synchronousDispose).WaitAsync(Timeout);
            check(result[fixture.Vm.Id].Gpu3d == 25 && fixture.Counters.Count == 1 && fixture.Counters.Single().Disposals == 1,
                "the admitted GPU sample finishes before its counter is released and queued calls cannot recreate resources");
        }
        finally
        {
            release.Set();
            await IgnoreFailureAsync(active);
            if (queued != null) await IgnoreFailureAsync(queued);
            if (synchronousDispose != null) await IgnoreFailureAsync(synchronousDispose);
            await fixture.Query.DisposeAsync();
        }
    }

    private static async Task CheckSynchronousContextShutdownAsync(Action<bool, string> check)
    {
        var processRead = new TaskCompletionSource<ApiResponse<List<VmQueryService.GpuProcessInfo>>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(() => processRead.Task);
        var callerContext = new QueuedContext();
        var started = NewSignal();
        var finished = NewSignal();
        var caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(callerContext);
            try
            {
                var sampling = fixture.Query.GetGpuPerformanceAsync([fixture.Vm]);
                _ = fixture.Query.DisposeAsync();
                started.TrySetResult(true);
                fixture.Query.Dispose();
                sampling.GetAwaiter().GetResult();
                finished.TrySetResult(true);
            }
            catch (Exception error) { finished.TrySetException(error); }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }) { IsBackground = true, Name = "query disposal context check" };
        caller.Start();
        try
        {
            await started.Task.WaitAsync(Timeout);
            check(!finished.Task.IsCompleted && fixture.Counters.Count == 0,
                "synchronous query disposal waits for an unfinished PID read without allocating counters");
            processRead.TrySetResult(ApiResponse<List<VmQueryService.GpuProcessInfo>>.Ok([new(17, 1, "vmwp.exe", fixture.Vm.Id.ToString())]));
            await finished.Task.WaitAsync(Timeout);
            check(callerContext.Posts == 0 && fixture.Counters.Count == 0,
                "an asynchronous PID read completes query disposal without posting back to the blocked caller context");
        }
        finally
        {
            processRead.TrySetResult(ApiResponse<List<VmQueryService.GpuProcessInfo>>.Ok([]));
            // A regressed await may have posted its continuation to this deliberately
            // unpumped context. Rescue it on failure so the check cannot strand a thread.
            callerContext.Drain();
            if (!caller.Join(Timeout)) throw new TimeoutException("Query disposal caller did not finish");
            await fixture.Query.DisposeAsync();
        }
    }

    private static async Task CheckNetworkShutdownAsync(Action<bool, string> check)
    {
        using var fixture = new Fixture();
        using var release = new ManualResetEventSlim();
        var entered = NewSignal();
        await fixture.Query.UpdateNetworkPerformanceAsync([fixture.Vm]);
        fixture.ReadCounter = (counter, read) =>
        {
            if (counter.Kind == "Bytes Sent/sec" && read == 2)
            {
                entered.TrySetResult(true);
                if (!release.Wait(Timeout)) throw new TimeoutException();
            }
            return counter.DefaultValue;
        };
        var active = Task.Run(() => fixture.Query.UpdateNetworkPerformanceAsync([fixture.Vm]));
        Task? queued = null;
        try
        {
            await entered.Task.WaitAsync(Timeout);
            queued = fixture.Query.UpdateNetworkPerformanceAsync([fixture.Vm]);
            var closing = fixture.Query.DisposeAsync().AsTask();
            check(!closing.IsCompleted && fixture.Counters.All(counter => counter.Disposals == 0) &&
                fixture.Counters.Single(counter => counter.Kind == "Bytes Sent/sec").Reads == 2,
                "network requests serialize and shutdown leaves both counters alive until the active sample ends");
            release.Set();
            await active.WaitAsync(Timeout);
            check(await IsDisposedAsync(() => queued),
                "a queued network request exits without accessing counters after shutdown starts");
            await closing.WaitAsync(Timeout);
            check(fixture.Adapter.HasNetworkSpeedSample && fixture.Counters.All(counter => counter.Disposals == 1),
                "network shutdown waits for both rate reads and then releases both counters exactly once");
        }
        finally
        {
            release.Set();
            await IgnoreFailureAsync(active);
            if (queued != null) await IgnoreFailureAsync(queued);
            await fixture.Query.DisposeAsync();
        }
    }

    private static async Task CheckCreationFailuresAsync(Action<bool, string> check)
    {
        using (var fixture = new Fixture())
        {
            fixture.CreateFailure = kind => kind == "Bytes Received/sec";
            await fixture.Query.UpdateNetworkPerformanceAsync([fixture.Vm]);
            check(fixture.Counters.Count == 1 && fixture.Counters[0].Disposals == 1 && !fixture.Adapter.HasNetworkSpeedSample,
                "failed receive-counter construction releases the already-created send counter");
        }

        foreach (string failedKind in new[] { "Bytes Sent/sec", "Bytes Received/sec" })
        {
            using var fixture = new Fixture();
            fixture.ReadCounter = (counter, _) => counter.Kind == failedKind
                ? throw new InvalidOperationException("expected warm-up failure") : counter.DefaultValue;
            await fixture.Query.UpdateNetworkPerformanceAsync([fixture.Vm]);
            check(fixture.Counters.Count == 2 && fixture.Counters.All(counter => counter.Disposals == 1) &&
                !fixture.Adapter.HasNetworkSpeedSample,
                "failed network warm-up releases both newly-created counters: " + failedKind);
        }

        using (var fixture = new Fixture())
        {
            fixture.ReadCounter = (_, _) => throw new InvalidOperationException("expected GPU warm-up failure");
            await fixture.Query.GetGpuPerformanceAsync([fixture.Vm]);
            check(fixture.Counters.Count == 1 && fixture.Counters[0].Disposals == 1,
                "failed GPU warm-up releases the counter before ownership can transfer");
            fixture.ReadCounter = (counter, _) => counter.DefaultValue;
            var retried = await fixture.Query.GetGpuPerformanceAsync([fixture.Vm]);
            check(fixture.Counters.Count == 2 && retried[fixture.Vm.Id].Sample?.State == PerformanceSampleState.WarmingUp
                && fixture.Counters[1].Disposals == 0,
                "failed GPU counter setup retries by establishing a fresh baseline");
            var recovered = await fixture.Query.GetGpuPerformanceAsync([fixture.Vm]);
            check(recovered[fixture.Vm.Id].Sample?.IsValid == true && recovered[fixture.Vm.Id].Gpu3d == 25,
                "GPU the next successful sample publishes the recovered rate");
        }

        using (var fixture = new Fixture())
        {
            await fixture.Query.UpdateNetworkPerformanceAsync([fixture.Vm]);
            fixture.ReadCounter = (_, _) => throw new InvalidOperationException("expected active network sample failure");
            await fixture.Query.UpdateNetworkPerformanceAsync([fixture.Vm]);
            check(fixture.Counters.All(counter => counter.Disposals == 1) && !fixture.Adapter.HasNetworkSpeedSample,
                "an active network read failure removes and releases the complete counter pair");
            fixture.ReadCounter = (counter, _) => counter.DefaultValue;
            await fixture.Query.UpdateNetworkPerformanceAsync([fixture.Vm]);
            await fixture.Query.UpdateNetworkPerformanceAsync([fixture.Vm]);
            check(fixture.Counters.Count == 4 && fixture.Adapter.HasNetworkSpeedSample,
                "subsequent network samples rebuild after the original failure invalidation");
        }
    }

    private static async Task CheckReleaseFailureAsync(Action<bool, string> check)
    {
        using var fixture = new Fixture();
        await fixture.Query.UpdateNetworkPerformanceAsync([fixture.Vm]);
        await fixture.Query.GetGpuPerformanceAsync([fixture.Vm]);
        fixture.Counters.First().FailDispose = true;
        var closing = fixture.Query.DisposeAsync().AsTask();
        bool reported = false;
        try { await closing; }
        catch (AggregateException) { reported = true; }
        check(reported && fixture.Counters.All(counter => counter.Disposals == 1) &&
            ReferenceEquals(closing, fixture.Query.DisposeAsync().AsTask()),
            "one release failure does not skip other counters and repeated disposal reports the same completed result");
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<bool> IsDisposedAsync(Func<Task> action)
    {
        try { await action().WaitAsync(Timeout); return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private static async Task IgnoreFailureAsync(Task task)
    {
        try { await task.WaitAsync(Timeout); }
        catch (ObjectDisposedException) { }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Guid _adapterId = Guid.NewGuid();
        public VmInstance Vm { get; } = new() { Id = Guid.NewGuid(), StateCode = 2, GpuName = "Fixture GPU" };
        public VmNetworkAdapter Adapter { get; }
        public VmQueryService Query { get; }
        public List<Counter> Counters { get; } = [];
        public Func<string, bool> CreateFailure { get; set; } = _ => false;
        public Func<Counter, int, float> ReadCounter { get; set; } = (counter, _) => counter.DefaultValue;

        public Fixture(Func<Task<ApiResponse<List<VmQueryService.GpuProcessInfo>>>>? queryProcesses = null)
        {
            Adapter = new() { Id = $"{Vm.Id}\\{_adapterId}" };
            Vm.NetworkAdapters.Add(Adapter);
            Query = new VmQueryService(_ => true,
                category => category == "GPU Engine" ? ["pid_17_luid_0_engtype_3D"] : [$"fixture-{_adapterId}"],
                (_, kind, instance) =>
                {
                    if (CreateFailure(kind)) throw new InvalidOperationException("expected construction failure");
                    var counter = new Counter(this, kind, instance);
                    Counters.Add(counter);
                    return counter;
                }, queryProcesses ?? (() => Task.FromResult(ApiResponse<List<VmQueryService.GpuProcessInfo>>.Ok(
                    [new(17, 1, "vmwp.exe", Vm.Id.ToString(), "20200101000000.000000+000")]))));
        }

        public void Dispose()
        {
            try { Query.Dispose(); }
            catch (AggregateException) when (Counters.Any(counter => counter.FailDispose)) { }
        }
    }

    private sealed class Counter(Fixture fixture, string kind, string instance) : VmQueryService.IUsageCounter
    {
        public string Kind { get; } = kind;
        public string InstanceName { get; } = instance;
        public int Reads;
        public int Disposals;
        public bool FailDispose;
        public float DefaultValue => Kind switch { "Bytes Sent/sec" => 123, "Bytes Received/sec" => 456, _ => 25 };

        public float NextValue()
        {
            if (Volatile.Read(ref Disposals) != 0) throw new InvalidOperationException("sampled a disposed fixture counter");
            return fixture.ReadCounter(this, Interlocked.Increment(ref Reads));
        }

        public void Dispose()
        {
            Interlocked.Increment(ref Disposals);
            if (FailDispose) throw new InvalidOperationException("expected release failure");
        }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _pending = new();
        private readonly object _gate = new();
        private bool _released;
        public int Posts;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref Posts);
            lock (_gate)
            {
                if (!_released) { _pending.Enqueue((callback, state)); return; }
            }
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        public void Drain()
        {
            lock (_gate) _released = true;
            while (_pending.TryDequeue(out var pending)) pending.Callback(pending.State);
        }
    }
}
