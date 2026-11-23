using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class VirtualDiskCompactorTests
{
    [TestMethod]
    public async Task CompactDiskAsync_RejectsBlankAndMissingPathsBeforeNativeCall()
    {
        var blank = await VmStorageService.CompactDiskAsync("   ");
        Assert.IsFalse(blank.Success);
        Assert.AreEqual(ApiErrorKind.InvalidArgument, blank.ErrorKind);

        string missing = Path.Combine(Path.GetTempPath(), "ExHyperV-missing-" + Guid.NewGuid().ToString("N") + ".vhdx");
        var notFound = await VmStorageService.CompactDiskAsync(missing);
        Assert.IsFalse(notFound.Success);
        Assert.AreEqual(ApiErrorKind.NotFound, notFound.ErrorKind);
    }

    [TestMethod]
    public void ExecuteCompactionFlow_RunsStage1ThenStage2WithRetryHint()
    {
        var calls = new List<string>();
        bool? offlineRetryHint = null;

        var result = VirtualDiskCompactor.ExecuteCompactionFlow(
            () =>
            {
                calls.Add("stage1");
                return VirtualDiskCompactor.StageOutcome.Ok();
            },
            waitForDetach =>
            {
                offlineRetryHint = waitForDetach;
                calls.Add("stage2");
                return VirtualDiskCompactor.StageOutcome.Ok();
            });

        Assert.IsTrue(result.Success);
        Assert.AreEqual(true, offlineRetryHint);
        CollectionAssert.AreEqual(new[] { "stage1", "stage2" }, calls);
    }

    [TestMethod]
    public void ExecuteCompactionFlow_SkipsOfflineStageWhenHostAlreadyAttachedDisk()
    {
        bool stage2Called = false;

        var result = VirtualDiskCompactor.ExecuteCompactionFlow(
            () => VirtualDiskCompactor.StageOutcome.Ok(wasAlreadyAttached: true),
            _ =>
            {
                stage2Called = true;
                return VirtualDiskCompactor.StageOutcome.Ok();
            });

        Assert.IsTrue(result.Success);
        Assert.IsFalse(stage2Called);
    }

    [TestMethod]
    public void ExecuteCompactionFlow_FallsBackToOfflineWhenAttachFailsNonFatally()
    {
        bool? offlineRetryHint = null;
        var result = VirtualDiskCompactor.ExecuteCompactionFlow(
            () => VirtualDiskCompactor.StageOutcome.Fail(50 /* ERROR_NOT_SUPPORTED */, "Attach"),
            waitForDetach =>
            {
                offlineRetryHint = waitForDetach;
                return VirtualDiskCompactor.StageOutcome.Ok();
            });

        Assert.IsTrue(result.Success);
        Assert.AreEqual(false, offlineRetryHint);
    }

    [TestMethod]
    public void ExecuteCompactionFlow_StopsImmediatelyOnLockedOrMissingOpenFailure()
    {
        bool stage2Called = false;

        var result = VirtualDiskCompactor.ExecuteCompactionFlow(
            () => VirtualDiskCompactor.StageOutcome.Fail(VirtualDiskCompactor.ErrorSharingViolation, "Open"),
            _ =>
            {
                stage2Called = true;
                return VirtualDiskCompactor.StageOutcome.Ok();
            });

        Assert.IsFalse(result.Success);
        Assert.AreEqual(ApiErrorKind.InvalidState, result.ErrorKind);
        Assert.AreEqual(unchecked((int)VirtualDiskCompactor.ErrorSharingViolation), result.Code);
        Assert.IsFalse(stage2Called);
    }
    [TestMethod]
    [DataRow("Probe", ApiErrorKind.OperationFailed)]
    [DataRow("Detach", ApiErrorKind.CleanupRequired)]
    public void UnknownAttachmentOrFailedDetachNeverRunsOfflinePass(string stage, ApiErrorKind kind)
    {
        var result = VirtualDiskCompactor.ExecuteCompactionFlow(() => VirtualDiskCompactor.StageOutcome.Fail(5, stage),
            _ => throw new AssertFailedException("Unexpected second pass"));
        Assert.AreEqual(kind, result.ErrorKind);
        Assert.AreEqual(5, result.Code);
    }
    [TestMethod]
    public void UnconfirmedNativeRequestStopsAtTheFirstStage()
    {
        var result = VirtualDiskCompactor.ExecuteCompactionFlow(
            () => VirtualDiskCompactor.StageOutcome.Fail(VirtualDiskCompactor.ErrorIoPending, "Compact"),
            _ => throw new AssertFailedException("Unexpected second pass"));
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown, result.ErrorKind);
    }
}
