using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
[TestCategory("OperationQuery")]
public sealed class SwitchDisplayQueryTests
{
    [TestMethod]
    public void DisplayRead_DoesNotRunReadersOnTheCallingContext()
    {
        Exception? failure = null;
        var caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new RejectingSynchronizationContext());
            int callerThread = Environment.CurrentManagedThreadId;
            void CheckWorker()
            {
                Assert.AreNotEqual(callerThread, Environment.CurrentManagedThreadId);
                Assert.IsNull(SynchronizationContext.Current);
            }

            try
            {
                HyperVSwitchService.ReadNetworkInfoAsync(
                    async () =>
                    {
                        CheckWorker();
                        await Task.Yield();
                        CheckWorker();
                        return new List<SwitchUpstreamAdapter>();
                    },
                    () =>
                    {
                        CheckWorker();
                        return Task.FromResult(ApiResponse<List<IcsConnection>>.Ok([]));
                    },
                    async context =>
                    {
                        CheckWorker();
                        await HyperVSwitchService.ReadSwitchNetworkStateAsync(0, Guid.Empty,
                            () => { CheckWorker(); return Task.FromResult(Guid.Empty); }, context);
                        CheckWorker();
                        return new List<SwitchInfo>();
                    }).GetAwaiter().GetResult();
            }
            catch (Exception ex) { failure = ex; }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        }) { IsBackground = true };

        caller.Start();
        Assert.IsTrue(caller.Join(TimeSpan.FromSeconds(10)), "The display read waited for the caller's context.");
        Assert.IsNull(failure, failure?.ToString());
    }

    [TestMethod]
    public async Task DisplayRead_SharesOneAdapterAndIcsReadAcrossAllSwitches()
    {
        int adapterReads = 0, icsReads = 0, hostReads = 0;
        var adapter = Adapter(Guid.NewGuid());
        var hostId = Guid.NewGuid();
        List<SwitchUpstreamAdapter> adapters = [adapter];
        var pendingAdapters = new TaskCompletionSource<List<SwitchUpstreamAdapter>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var result = await HyperVSwitchService.ReadNetworkInfoAsync(
            () => { Interlocked.Increment(ref adapterReads); return pendingAdapters.Task; },
            () =>
            {
                Interlocked.Increment(ref icsReads);
                return Task.FromResult(ApiResponse<List<IcsConnection>>.Ok(
                    [new(adapter.ConnectionId, "upstream", true, 0), new(hostId, "host", true, 1)]));
            },
            async context =>
            {
                var reads = Enumerable.Range(0, 23).Select(_ =>
                    HyperVSwitchService.ReadSwitchNetworkStateAsync(0, Guid.Empty,
                        () => { Interlocked.Increment(ref hostReads); return Task.FromResult(hostId); }, context)).ToArray();
                pendingAdapters.SetResult(adapters);
                var states = await Task.WhenAll(reads);
                return states.Select(state => new SwitchInfo
                {
                    SwitchType = state.Type, Upstream = state.Upstream, StateError = state.StateError
                }).ToList();
            });

        Assert.AreEqual(1, adapterReads);
        Assert.AreEqual(1, icsReads);
        Assert.AreEqual(23, hostReads, "Per-switch host identity must still be read separately.");
        Assert.AreSame(adapters, result.PhysicalAdapters);
        Assert.AreEqual(23, result.Switches.Count);
        foreach (var item in result.Switches)
        {
            Assert.AreEqual(SwitchMode.NAT, item.SwitchType);
            Assert.AreSame(adapter, item.Upstream);
            Assert.IsNull(item.StateError);
        }
    }

    [TestMethod]
    public async Task DisplayRead_DoesNotReuseSnapshotsAcrossCalls()
    {
        int adapterReads = 0, icsReads = 0;
        var contexts = new List<HyperVSwitchService.SwitchDisplayReadContext>();
        async Task<List<SwitchUpstreamAdapter>> ReadOnceAsync()
        {
            var result = await HyperVSwitchService.ReadNetworkInfoAsync(
                () =>
                {
                    Interlocked.Increment(ref adapterReads);
                    return Task.FromResult(new List<SwitchUpstreamAdapter> { Adapter(Guid.NewGuid()) });
                },
                () =>
                {
                    Interlocked.Increment(ref icsReads);
                    return Task.FromResult(ApiResponse<List<IcsConnection>>.Ok([]));
                },
                async context =>
                {
                    contexts.Add(context);
                    await HyperVSwitchService.ReadSwitchNetworkStateAsync(0, Guid.Empty,
                        () => Task.FromResult(Guid.Empty), context);
                    return new List<SwitchInfo>();
                });
            return result.PhysicalAdapters;
        }

        var first = await ReadOnceAsync();
        var second = await ReadOnceAsync();
        Assert.AreEqual(2, adapterReads);
        Assert.AreEqual(2, icsReads);
        Assert.AreNotSame(first, second);
        Assert.AreNotSame(contexts[0], contexts[1]);
        Assert.AreNotSame(contexts[0].Connections.Value, contexts[1].Connections.Value);
    }

    [TestMethod]
    public async Task DisplayRead_PropagatesPhysicalAdapterFailureWithoutReadingIcs()
    {
        var expected = new InvalidOperationException("adapter query failed");
        Exception? actual = null;
        int icsReads = 0;
        try
        {
            await HyperVSwitchService.ReadNetworkInfoAsync(
                () => Task.FromException<List<SwitchUpstreamAdapter>>(expected),
                () => { Interlocked.Increment(ref icsReads); return Task.FromResult(ApiResponse<List<IcsConnection>>.Ok([])); },
                async context =>
                {
                    await HyperVSwitchService.ReadSwitchNetworkStateAsync(0, Guid.Empty,
                        () => Task.FromResult(Guid.Empty), context);
                    return new List<SwitchInfo>();
                });
        }
        catch (Exception ex) { actual = ex; }

        Assert.AreSame(expected, actual);
        Assert.AreEqual(0, icsReads);
    }

    [TestMethod]
    public async Task DisplayRead_IcsFailureKeepsBridgeInformationAndReportsStateError()
    {
        var adapter = Adapter(Guid.NewGuid());
        var context = new HyperVSwitchService.SwitchDisplayReadContext(
            Task.FromResult(new List<SwitchUpstreamAdapter> { adapter }),
            () => Task.FromResult(ApiResponse<List<IcsConnection>>.Fail("ICS query failed", source: ApiErrorSource.Com)));
        int hostReads = 0;
        var state = await HyperVSwitchService.ReadSwitchNetworkStateAsync(1, adapter.ConnectionId,
            () => { hostReads++; return Task.FromResult(Guid.Empty); }, context);

        Assert.AreEqual(SwitchMode.Bridge, state.Type);
        Assert.AreSame(adapter, state.Upstream);
        Assert.AreEqual("ICS query failed", state.StateError);
        Assert.AreEqual(0, hostReads);
    }

    [TestMethod]
    public async Task DisplayRead_NoParsedSwitchesDoesNotReadIcs()
    {
        int icsReads = 0;
        var result = await HyperVSwitchService.ReadNetworkInfoAsync(
            () => Task.FromResult(new List<SwitchUpstreamAdapter>()),
            () => { icsReads++; return Task.FromResult(ApiResponse<List<IcsConnection>>.Ok([])); },
            _ => Task.FromResult(new List<SwitchInfo>()));

        Assert.AreEqual(0, result.Switches.Count);
        Assert.AreEqual(0, icsReads);
    }

    [TestMethod]
    public async Task DisplayRead_MissingIcsPublicConnectionKeepsExistingDiagnostic()
    {
        var hostId = Guid.NewGuid();
        var context = new HyperVSwitchService.SwitchDisplayReadContext(
            Task.FromResult(new List<SwitchUpstreamAdapter>()),
            () => Task.FromResult(ApiResponse<List<IcsConnection>>.Ok([new(hostId, "host", true, 1)])));
        var state = await HyperVSwitchService.ReadSwitchNetworkStateAsync(0, Guid.Empty,
            () => Task.FromResult(hostId), context);

        Assert.AreEqual(SwitchMode.NAT, state.Type);
        Assert.IsNull(state.Upstream);
        Assert.AreEqual("ICS private connection has no public connection.", state.StateError);
    }

    private static SwitchUpstreamAdapter Adapter(Guid id) => new(id, "device", "adapter", "adapter", "external-port", true);

    private sealed class RejectingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
            => throw new InvalidOperationException("The network reader captured the caller's context.");
    }
}
