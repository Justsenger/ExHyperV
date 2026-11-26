using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
[TestCategory("OperationResource")]
public sealed class ConsoleConnectionTests
{
    private static void Check(bool condition, string scenario) => Assert.IsTrue(condition, scenario);

    [TestMethod] public void InvalidOptions_AreRejectedBeforeNativeAllocation() => ConsoleConnectionChecks.CheckValidation(Check);
    [TestMethod] public void Clipboard_PreservesFormatsAndRejectsMalformedPayloads() => ConsoleConnectionChecks.CheckClipboardPayloads(Check);
    [TestMethod] public void Audio_ValidatesPayloadsAndCopiesIndependentSamples() => ConsoleConnectionChecks.CheckAudioPayloads(Check);
    [TestMethod] public Task Audio_DeliversOutputAndAcceptsInputOnlyForEnhancedConnections() => ConsoleConnectionChecks.CheckAudioStreams(Check);
    [TestCategory("OperationQuery")]
    [TestMethod] public Task StatePolling_PreservesMatchingAndFailureSemanticsWithoutUiWork() => ConsoleConnectionChecks.CheckConsoleStateQuery(Check);
    [TestMethod] public Task Connections_KeepIndependentStateAndFrames() => ConsoleConnectionChecks.CheckIndependentConnections(Check);
    [TestMethod] public Task FrameLeases_ReleaseCorrectlyAcrossObserversAndShutdown() => ConsoleConnectionChecks.CheckFrameLeases(Check);
    [TestMethod] public Task Input_IsForwardedOnlyWhileConnected() => ConsoleConnectionChecks.CheckInput(Check);
    [TestMethod] public Task Disconnect_WaitsForNativeCleanup() => ConsoleConnectionChecks.CheckShutdown(Check);
    [TestMethod] public Task DisconnectDuringCreation_CleansUpTheNewHandle() => ConsoleConnectionChecks.CheckStopDuringCreation(Check);
    [TestMethod] public Task NativeFailures_AreReportedAndCleanedUp() => ConsoleConnectionChecks.CheckFailures(Check);
    [TestMethod] public Task DisposeFromCallback_DoesNotDeadlock() => ConsoleConnectionChecks.CheckCallbackDisposal(Check);
    [TestMethod] public Task InputRacingWithDisconnect_DoesNotUseFreedHandle() => ConsoleConnectionChecks.CheckSendShutdownRace(Check);
    [TestMethod] public Task HandshakeTimeout_StopsConnectingButNotEstablishedConnections() => ConsoleConnectionChecks.CheckConnectTimeout(Check);
    [TestMethod] public Task RejectedResize_PreservesConnection() => ConsoleConnectionChecks.CheckResizeRejection(Check);
    [TestMethod] public Task ClipboardRevision_DiscardsStaleUpdates() => ConsoleConnectionChecks.CheckClipboardRevision(Check);
    [TestMethod] public Task SpecialKeys_AreEncodedCorrectly() => ConsoleConnectionChecks.CheckSpecialKeyboardInput(Check);
    [TestMethod] public Task GpuBridge_OpensPreStartDecodesSlotDimsAndFallsBackToCpu() => ConsoleConnectionChecks.CheckGpuBridge(Check);

    [TestMethod]
    public void GpuSource_WrapsOnlyEnhancedConsoleConnections()
    {
        ExHyperV.Services.ConsoleConnectionOptions options = new() { VmId = Guid.NewGuid() };
        using var enhanced = ExHyperV.Services.ConsoleService.CreateConnection(options, ExHyperV.Services.ConsoleService.RdpEnhancedBackendId);
        using var basic = ExHyperV.Services.ConsoleService.CreateConnection(options, ExHyperV.Services.ConsoleService.RdpBasicBackendId);
        Assert.IsNotNull(ExHyperV.ConsoleIpc.ConsoleCoreGpuSource.Wrap(enhanced));
        Assert.IsNull(ExHyperV.ConsoleIpc.ConsoleCoreGpuSource.Wrap(basic));
    }
}
