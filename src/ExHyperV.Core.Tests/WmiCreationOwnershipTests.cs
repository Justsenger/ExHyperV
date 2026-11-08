using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class WmiCreationOwnershipTests
{
    private const string Gpu = "Msvm_GpuPartitionSettingData";
    private const string Owned = "Msvm_GpuPartitionSettingData.InstanceID=\"own\"";

    [TestMethod]
    public void AffectedOrModifiedObjectsAreNotCreationReceipts()
    {
        var result = WmiApi.SelectCreatedJobPaths([
            new(Owned, [5]), new("Msvm_GpuPartitionSettingData.InstanceID=\"other\"", [3]),
            new("Msvm_GpuPartitionSettingData.InstanceID=\"unknown\"", []),
            new("Msvm_ComputerSystem.Name=\"vm\"", [5])], Gpu);
        CollectionAssert.AreEqual(new[] { Owned }, result);
    }

    [TestMethod]
    public void MissingOrWrongClassCannotAuthorizeCleanup()
    {
        Assert.IsTrue(WmiApi.CompleteCreationReceipt(ApiResponse.Ok(), [], Gpu).OutcomeUnknown);
        Assert.IsTrue(WmiApi.CompleteCreationReceipt(ApiResponse.Ok(),
            ["Msvm_ComputerSystem.Name=\"vm\""], Gpu).OutcomeUnknown);
        Assert.IsTrue(WmiApi.CompleteCreationReceipt(ApiResponse.Fail("failed"), [], Gpu).OutcomeUnknown);
    }

    [TestMethod]
    public void FailedSettledOperationKeepsOnlyItsExplicitReceipt()
    {
        var result = WmiApi.CompleteCreationReceipt(ApiResponse.Fail("later stage failed"), [Owned], Gpu);
        Assert.IsFalse(result.Response.Success);
        Assert.IsFalse(result.OutcomeUnknown);
        CollectionAssert.AreEqual(new[] { Owned }, result.CreatedPaths);
    }

    [TestMethod]
    public void CompletedStateRequiresZeroErrorCode()
    {
        Assert.IsTrue(WmiApi.EvaluateCreationJob(7, 0, null)!.Response.Success);
        var failed = WmiApi.EvaluateCreationJob(7, 5, "denied")!;
        Assert.IsFalse(failed.Response.Success);
        Assert.IsFalse(failed.OutcomeUnknown);
        Assert.AreEqual(5, failed.Response.Code);
        Assert.IsTrue(WmiApi.EvaluateCreationJob(7, null, null)!.OutcomeUnknown);
    }

    [TestMethod]
    public void RunningAndAbortedStatesDoNotAuthorizeCleanup()
    {
        Assert.IsTrue(WmiApi.EvaluateCreationJob(32768, 0, null)!.Response.Success);
        Assert.IsNull(WmiApi.EvaluateCreationJob(4, 0, null));
        foreach (ushort state in new ushort[] { 9, 10, 11 })
            Assert.IsTrue(WmiApi.EvaluateCreationJob(state, 0, null)!.OutcomeUnknown);
    }

    [TestMethod]
    public void FileCreationOnlyGrantsOwnershipOnConfirmedSuccess()
    {
        Assert.IsFalse(WmiApi.CompleteCreationReceipt(ApiResponse.Ok(), [], null).OutcomeUnknown);
        Assert.IsTrue(WmiApi.CompleteCreationReceipt(ApiResponse.Fail("failed"), [], null).OutcomeUnknown);
    }
}
