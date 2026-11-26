using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class ConsoleExtensionContractTests
{
    [TestMethod]
    public void CommonOptionsValidateIdentityAndTimeoutIndependentlyOfRdpGeometry()
    {
        var options = new ConsoleConnectionOptions { VmId = Guid.NewGuid(), Width = -1, Height = 0,
            DesktopScale = uint.MaxValue, ConnectTimeout = TimeSpan.FromMinutes(2) };
        options.Validate();
        (options with { ConnectTimeout = TimeSpan.FromTicks(1) }).Validate();
        Assert.ThrowsExactly<ArgumentException>(() => (options with { VmId = Guid.Empty }).Validate());
        foreach (var timeout in new[] { TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.FromMinutes(2) + TimeSpan.FromTicks(1) })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => (options with { ConnectTimeout = timeout }).Validate());
        foreach (var backend in ConsoleService.GetBackends())
        {
            var valid = options with { Width = 200, Height = 8192, DesktopScale = 500 };
            using var connection = ConsoleService.CreateConnection(valid, backend.BackendId);
            foreach (var invalid in new[] { valid with { Width = 199 }, valid with { Height = 8193 },
                valid with { Width = 4096, Height = 4096 }, valid with { DesktopScale = 99 }, valid with { DesktopScale = 501 } })
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ConsoleService.CreateConnection(invalid, backend.BackendId));
        }
    }

    [TestMethod]
    public void DisplayModePreservesFractionalRatesAndCursorPreservesUnrecognizedCoordinates()
    {
        var mode = new ConsoleDisplayMode("A", 1, 1, 60000, 1001);
        Assert.AreEqual(("A", 1, 1, 60000u, 1001u), (mode.Id, mode.Width, mode.Height, mode.RefreshNumerator, mode.RefreshDenominator));
        Assert.ThrowsExactly<ArgumentException>(() => new ConsoleDisplayMode(" ", 1, 1, 1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ConsoleDisplayMode("A", 0, 1, 1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ConsoleDisplayMode("A", 1, -1, 1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ConsoleDisplayMode("A", 1, 1, 0, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ConsoleDisplayMode("A", 1, 1, 1, 0));
        var cursor = new ConsoleCursorState(int.MinValue, int.MaxValue, false, true, (ConsoleCoordinateSpace)157, 0);
        Assert.AreEqual((int.MinValue, int.MaxValue, false, true, 157, 0ul),
            (cursor.X, cursor.Y, cursor.Visible, cursor.Composited, (int)cursor.CoordinateSpace, cursor.ShapeVersion));
    }

    [TestMethod]
    public void SnapshotKeepsRawStateAndFreezesOnlyRecognizedActions()
    {
        var stage = new ConsoleExtensionStage(501);
        var denied = new ConsoleExtensionStage((int)ConsoleExtensionStateCode.AccessDenied);
        var control = new ConsoleControlState(true, false, ulong.MaxValue);
        var actions = new[] { ConsoleExtensionActionId.Unknown, (ConsoleExtensionActionId)781,
            ConsoleExtensionActionId.RetryConnection, ConsoleExtensionActionId.RetryConnection };
        ConsoleExtensionSnapshot Make(ConsoleExtensionStage host, IReadOnlyList<ConsoleExtensionActionId> available) =>
            new(0, host, denied, stage, stage, (ConsoleTransportKind)99, ulong.MaxValue,
                ConsoleCapabilities.Video, ConsoleCapabilities.None, control, available, null, "d1");
        var snapshot = Make(stage, actions);
        Array.Clear(actions);
        Assert.AreEqual((501, ConsoleExtensionStateCode.Unknown, ConsoleExtensionStateCode.AccessDenied),
            (snapshot.Host.RawCode, snapshot.Host.Code, snapshot.Authorization.Code));
        Assert.AreEqual(99, (int)snapshot.TransportKind);
        Assert.AreEqual(ulong.MaxValue, snapshot.SurfaceEpoch);
        Assert.AreEqual(ConsoleCapabilities.None, snapshot.Granted);
        CollectionAssert.AreEqual(new[] { ConsoleExtensionActionId.RetryConnection }, snapshot.AvailableActions.ToArray());
        Assert.ThrowsExactly<ArgumentNullException>(() => Make(null!, []));
        Assert.ThrowsExactly<ArgumentNullException>(() => Make(stage, null!));
        Assert.ThrowsExactly<ArgumentException>(() => new ConsoleControlState(false, true, 1));
    }

    [TestMethod]
    public void ActionRequestsEnforceKnownActionsRequestIdentityAndTargetScope()
    {
        Guid id = Guid.NewGuid(), vm = Guid.NewGuid();
        ConsoleExtensionActionRequest Make(ConsoleExtensionActionId action, Guid? target = null, string? component = null) =>
            new(id, ulong.MaxValue, action, target, component, "viewer");
        Assert.AreEqual(vm, Make(ConsoleExtensionActionId.RetryConnection, vm).VmId);
        Assert.AreEqual("guest", Make(ConsoleExtensionActionId.DeployGuestComponent, vm, "guest").ComponentId);
        Assert.IsNull(Make(ConsoleExtensionActionId.MaintainHost).VmId);
        Assert.ThrowsExactly<ArgumentException>(() => new ConsoleExtensionActionRequest(Guid.Empty, 0, ConsoleExtensionActionId.ActivateHost, null, null, null));
        foreach (var action in new[] { ConsoleExtensionActionId.Unknown, (ConsoleExtensionActionId)143 })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Make(action));
        Assert.ThrowsExactly<ArgumentException>(() => Make(ConsoleExtensionActionId.DeployGuestComponent, null, "guest"));
        Assert.ThrowsExactly<ArgumentException>(() => Make(ConsoleExtensionActionId.DeployGuestComponent, vm, " "));
        Assert.ThrowsExactly<ArgumentException>(() => Make(ConsoleExtensionActionId.RetryConnection, Guid.Empty));
        Assert.ThrowsExactly<ArgumentException>(() => Make(ConsoleExtensionActionId.ActivateHost, component: "guest"));
    }

    [TestMethod]
    public void ClipboardProviderRequiresVmAndSessionIdentityAndPreservesIndependentDirections()
    {
        Guid vm = Guid.NewGuid();
        var info = new ConsoleClipboardProviderInfo("guest", vm, "session", false, true, false);
        Assert.AreEqual((false, true, false), (info.IsReady, info.CanRead, info.CanWrite));
        var output = info with { IsReady = true, CanRead = false, CanWrite = true };
        Assert.AreEqual(("guest", vm, "session", true, false, true),
            (output.ProviderId, output.VmId, output.GuestSessionId, output.IsReady, output.CanRead, output.CanWrite));
        Assert.ThrowsExactly<ArgumentException>(() => new ConsoleClipboardProviderInfo("", vm, "session", false, false, false));
        Assert.ThrowsExactly<ArgumentException>(() => new ConsoleClipboardProviderInfo("guest", Guid.Empty, "session", false, false, false));
        Assert.ThrowsExactly<ArgumentException>(() => new ConsoleClipboardProviderInfo("guest", vm, " ", false, false, false));
        Assert.ThrowsExactly<ArgumentException>(() => _ = info with { GuestSessionId = "" });
    }
}
