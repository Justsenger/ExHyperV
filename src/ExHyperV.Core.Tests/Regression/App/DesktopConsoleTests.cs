using System.Text.Json;
using ExHyperV.App;
using ExHyperV.App.UI.Pages.VirtualMachines.Console;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.ConsoleHost;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class DesktopConsoleTests
{
    [TestMethod]
    [DataRow("\"RdpBasic\"", 0)]
    [DataRow("\"RdpEnhanced\"", 1)]
    [DataRow("\"External\"", 2)]
    [DataRow("0", 0)]
    [DataRow("1", 1)]
    [DataRow("2", 2)]
    [DataRow("\"FutureBackend\"", 0)]
    [DataRow("-1", 0)]
    [DataRow("3.5", 0)]
    [DataRow("\"RdpEnhanced, External\"", 0)]
    [DataRow("null", 0)]
    [DataRow("true", 0)]
    [DataRow("{\"nested\":[1,2]}", 0)]
    public void SessionPreferenceReadsIndependently(string field, int expected)
    {
        string json = $$"""{"Theme":2,"AccentColor":"#ABCDEF","DefaultConsoleSession":{{field}},"Language":"zh-CN","LoggingEnabled":false}""";
        var type = DesktopJson.Default.AppearanceSettings;
        var prefs = JsonSerializer.Deserialize(json, type)!;
        Assert.AreEqual((ConsoleSessionMode)expected, prefs.DefaultConsoleSession);
        Assert.AreEqual((2, "#ABCDEF", "zh-CN", false), (prefs.ThemeValue, prefs.AccentColor, prefs.Language, prefs.LoggingEnabled));
        var roundTrip = JsonSerializer.Serialize(prefs, type);
        StringAssert.Contains(roundTrip, $"\"DefaultConsoleSession\":\"{(ConsoleSessionMode)expected}\"");
        Assert.AreEqual(prefs, JsonSerializer.Deserialize(roundTrip, type));
    }

    private static readonly ConsoleFrameRegion A = new(0, 0, 1, 1), B = new(3, 1, 1, 1);
    private static ConsoleFrame Frame(long sequence, long basis, ConsoleFrameUpdateKind kind,
        ConsoleFrameRegion[]? dirty = null, ConsoleFrameMove[]? moves = null) =>
        new(4, 2, 16, new byte[32], sequence, basis, kind, dirty, moves);

    private static ConsoleDamageAccumulator Begin()
    {
        var merge = new ConsoleDamageAccumulator();
        using var full = Frame(1, 0, ConsoleFrameUpdateKind.Full);
        merge.Add(full);
        Assert.IsTrue(merge.Take().Full);
        return merge;
    }

    [TestMethod]
    public void CoalescedFramesKeepDamageSinceRenderedSequence()
    {
        var merge = Begin();
        using var second = Frame(2, 1, ConsoleFrameUpdateKind.Delta, [A]);
        using var third = Frame(3, 2, ConsoleFrameUpdateKind.Delta, [B]);
        merge.Add(second);
        merge.Add(third);
        var result = merge.Take();
        Assert.IsFalse(result.Full);
        Assert.AreEqual(1L, result.BaseSequence);
        CollectionAssert.AreEqual(new[] { A, B }, result.Dirty);
        using var idle = Frame(4, 3, ConsoleFrameUpdateKind.Unchanged);
        merge.Add(idle);
        Assert.IsEmpty(merge.Take().Dirty);
    }

    [TestMethod]
    public void OnlyConsecutiveSingleFrameMovesCanBeReplayed()
    {
        var merge = Begin();
        using var moving = Frame(2, 1, ConsoleFrameUpdateKind.Delta, [A], [new(0, 0, B)]);
        using var dirty = Frame(3, 2, ConsoleFrameUpdateKind.Delta, [B]);
        merge.Add(moving);
        Assert.HasCount(1, merge.Take().Moves);
        merge = Begin();
        merge.Add(moving);
        merge.Add(dirty);
        Assert.IsTrue(merge.Take().Full);
    }

    [TestMethod]
    public void MissingBaseSequenceSizeLimitAndFullFrameForceFullUpload()
    {
        static void Check(ConsoleFrame frame)
        {
            using (frame)
            {
                var merge = Begin();
                merge.Add(frame);
                Assert.IsTrue(merge.Take().Full);
            }
        }
        Check(Frame(3, 2, ConsoleFrameUpdateKind.Delta, [A]));
        Check(Frame(4, 1, ConsoleFrameUpdateKind.Delta, [A]));
        Check(Frame(2, 1, ConsoleFrameUpdateKind.Full));
        Check(Frame(2, 1, ConsoleFrameUpdateKind.Delta, Enumerable.Repeat(A, ConsoleDamageAccumulator.RegionLimit + 1).ToArray()));
    }

    [TestMethod]
    public void ViewersCannotSendOrReleaseInputsOrEscalateByFallingBack()
    {
        var state = new ConsoleSessionState { Preference = ConsoleSessionMode.External };
        var caps = ConsoleCapabilities.Video | ConsoleCapabilities.Input;
        state.Activate("External", caps, caps, new(true, false, 8));
        Assert.IsFalse(state.CanInput);
        Assert.IsFalse(state.TryFallback(ConsoleBackendFailure.BackendUnavailable, true));
        Assert.IsFalse(state.StopInputs());
        state.Activate("External", caps, caps, new(true, true, 9));
        Assert.IsFalse(state.TryFallback(ConsoleBackendFailure.ViewDenied, true));
        Assert.IsFalse(state.TryFallback(ConsoleBackendFailure.Unknown, true));
        Assert.IsFalse(state.TryFallback(ConsoleBackendFailure.ProtocolIncompatible, false));
        state.SetTransport(ConsoleTransportKind.GpuShared);
        state.SetTransport(ConsoleTransportKind.CpuShared);
        Assert.AreEqual("External", state.ActiveBackend);
        Assert.AreEqual(ConsoleSessionMode.External, state.Target);
        Assert.AreEqual(9ul, state.Control.Generation);
        Assert.IsTrue(state.CanInput);
        Assert.IsTrue(state.TryFallback(ConsoleBackendFailure.ProtocolIncompatible, true));
        Assert.AreEqual(ConsoleSessionMode.RdpEnhanced, state.Target);
        Assert.IsTrue(state.StopInputs());
        Assert.IsFalse(state.CanInput);
        Assert.IsFalse(state.StopInputs());
        state.Clear();
        Assert.IsNull(state.ActiveBackend);
        state.Activate("RdpEnhanced", caps, caps, new(true, true, 1));
        Assert.IsTrue(state.TryFallback(ConsoleBackendFailure.BackendUnavailable, true));
        Assert.AreEqual(ConsoleSessionMode.RdpBasic, state.Target);
        Assert.AreEqual(ConsoleSessionMode.External, state.Preference);
    }

    [TestMethod]
    public async Task CatalogChecksSnapshotVersionTargetAndFixedActionBeforeDispatch()
    {
        var catalog = new ConsoleExtensionCatalog();
        var vm = Guid.NewGuid();
        var offline = catalog.Inspect(vm);
        Assert.IsFalse(ConsoleExtensionCatalog.Ready(offline));
        Assert.AreEqual("EXT-001", offline.DiagnosticId);
        Assert.IsEmpty(offline.AvailableActions);
        CollectionAssert.AreEqual(new[] { "RdpBasic", "RdpEnhanced", "External" }, catalog.Backends.ToArray());
        var connector = new Connector();
        catalog.Register(connector);
        Assert.ThrowsExactly<ArgumentException>(() => catalog.Register(connector));
        var snapshot = catalog.Inspect(vm);
        Assert.IsTrue(ConsoleExtensionCatalog.Ready(snapshot));
        Assert.IsNull(ConsoleExtensionCatalog.Diagnostic("private/text"));
        Assert.AreEqual("EXT-123", ConsoleExtensionCatalog.Diagnostic("EXT-123"));
        Assert.IsNull(await catalog.ExecuteAsync(vm, 0, ConsoleExtensionActionId.MaintainHost, default));
        Assert.IsNull(await catalog.ExecuteAsync(vm, 2, ConsoleExtensionActionId.ActivateHost, default));
        Assert.IsNull(await catalog.ExecuteAsync(Guid.Empty, 2, ConsoleExtensionActionId.RetryConnection, default));
        Assert.IsNull(await catalog.ExecuteAsync(vm, 2, (ConsoleExtensionActionId)99, default));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => catalog.ExecuteAsync(vm, 2,
            ConsoleExtensionActionId.MaintainHost, new CancellationToken(true)));
        Assert.IsNull(connector.Last);
        Assert.IsTrue((await catalog.ExecuteAsync(vm, 2, ConsoleExtensionActionId.DeployGuestComponent, default))!.Accepted);
        Assert.AreEqual(vm, connector.Last!.VmId);
        Assert.AreEqual("guest", connector.Last.ComponentId);
    }

    [TestMethod]
    public void GpuFailureRequestsCpuOnThisDeliveryAndKeepsQueuedCpuFrame()
    {
        var queue = new Queue<Action>();
        var gpu = new Delivery();
        var other = new Delivery();
        int presented = 0, pathChanges = 0;
        using var presenter = new FramePresenter(work => { queue.Enqueue(work); return true; },
            (frame, damage) => { Assert.IsTrue(damage.Full); presented++; }, gpu, _ => false, () => pathChanges++);
        using var first = Frame(1, 0, ConsoleFrameUpdateKind.Full);
        presenter.Receive(first);
        gpu.Publish();
        Assert.IsEmpty(queue);
        presenter.Start();
        queue.Dequeue()();
        Assert.AreEqual((1, 1, 1, 0), (presented, pathChanges, gpu.CpuRequests, other.CpuRequests));
        Assert.IsTrue(gpu.Disposed);
        gpu.Publish();
        Assert.IsEmpty(queue);
    }

    [TestMethod]
    public async Task CancellationAndFallbackWaitForBothDisconnectAndDisposal()
    {
        var lifetime = new ConsoleSessionLifetime();
        var old = new DelayedConnection();
        var closing = lifetime.CloseAsync(old);
        await old.Disconnecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        int opened = 0;
        Task<IConsoleConnection> Open(CancellationToken token)
        {
            opened++;
            return Task.FromResult<IConsoleConnection>(new DelayedConnection());
        }
        using var canceled = new CancellationTokenSource();
        var aborted = lifetime.OpenAsync(Open, canceled.Token);
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => aborted);
        var fallback = lifetime.OpenAsync(Open, default);
        Assert.AreEqual(0, opened);
        old.DisconnectedGate.SetResult();
        await old.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(fallback.IsCompleted);
        old.DisposedGate.SetResult();
        await closing;
        await fallback;
        Assert.AreEqual(1, opened);
    }

    [TestMethod]
    public void IntermediateResizeForcesAFullUploadEvenAfterResizingBack()
    {
        var merge = Begin();
        using var larger = new ConsoleFrame(16, 2, 64, new byte[128], 2, 1, ConsoleFrameUpdateKind.Delta,
            [new(15, 0, 1, 1)]);
        using var originalSize = Frame(3, 2, ConsoleFrameUpdateKind.Delta, [A]);
        merge.Add(larger);
        merge.Add(originalSize);
        Assert.IsTrue(merge.Take().Full);
    }

    private sealed class DelayedConnection : IConsoleConnection
    {
        internal readonly TaskCompletionSource Disconnecting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisconnectedGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Disposing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DisposedGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string BackendId => "External";
        public ConsoleConnectionState State => ConsoleConnectionState.Disconnecting;
        public IConsoleVideoSource? Video => null;
        public IConsoleInput? Input => null;
        public IConsoleClipboard? Clipboard => null;
        public IConsoleAudioOutput? AudioOutput => null;
        public IConsoleAudioInput? AudioInput => null;
        public event Action? Connected { add { } remove { } }
        public event Action? LoginCompleted { add { } remove { } }
        public event Action<ConsoleDisconnectInfo>? Disconnected { add { } remove { } }
        public void Start() => throw new NotSupportedException();
        public Task DisconnectAsync() { Disconnecting.SetResult(); return DisconnectedGate.Task; }
        public ValueTask DisposeAsync() { Disposing.SetResult(); return new(DisposedGate.Task); }
        public void Dispose() => throw new NotSupportedException();
    }

    private sealed class Delivery : IConsoleGpuSource, IConsoleGpuDelivery
    {
        internal int CpuRequests;
        internal bool Disposed;
        public IConsoleGpuDelivery OpenDelivery() => this;
        public event Action? Available;
        internal void Publish() => Available?.Invoke();
        public ulong RequestCpu() { CpuRequests++; return 2; }
        public ConsoleGpuFrame TakeLatest() => new(33, 1, 0, 1, 4, 2, new(0, false), 0, 1);
        public void Dispose() => Disposed = true;
    }

    private sealed class Connector : IConsoleHostExtensionConnector
    {
        public string BackendId => "External";
        public event Action<Guid>? SnapshotChanged { add { } remove { } }
        internal ConsoleExtensionActionRequest? Last;
        public ConsoleExtensionSnapshot Inspect(Guid vmId)
        {
            var ready = new ConsoleExtensionStage((int)ConsoleExtensionStateCode.Ready);
            return new(2, ready, ready, ready, ready, ConsoleTransportKind.GpuShared, 4,
                ConsoleCapabilities.Video, ConsoleCapabilities.Video, new(true, false, 1),
                [ConsoleExtensionActionId.MaintainHost, ConsoleExtensionActionId.DeployGuestComponent, ConsoleExtensionActionId.RetryConnection],
                null, "EXT-123");
        }
        public IReadOnlyList<ConsoleDisplayMode> GetModes(Guid vmId) => [];
        public IConsoleConnection Connect(ConsoleConnectionOptions options) => throw new NotSupportedException();
        public string GetGuestComponentId(Guid vmId, ulong version) => "guest";
        public Task<ConsoleExtensionActionResult> ExecuteActionAsync(ConsoleExtensionActionRequest request, CancellationToken token)
        {
            Last = request;
            return Task.FromResult(new ConsoleExtensionActionResult(request.RequestId, true, 2, null, null));
        }
    }
}
