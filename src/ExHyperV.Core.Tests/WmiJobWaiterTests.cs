using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class WmiJobWaiterTests
{
    [TestMethod]
    public async Task CompletedHyperVExceptionPreservesItsNativeFailureAndAllowsRecovery()
    {
        var observation = new WmiJobObservation(10, 32773, "native-invalid-parameter", 100, [17, 6]);
        var result = await WmiJobWaiter.WaitAsync(() => observation, CancellationToken.None, hyperV: true);
        Assert.AreEqual(ApiErrorKind.OperationFailed, result.ErrorKind);
        Assert.AreEqual(ApiErrorSource.Wmi, result.ErrorSource);
        Assert.AreEqual(32773, result.Code);
        Assert.AreEqual("native-invalid-parameter", result.Error);
        Assert.IsFalse(result.Success);
        Assert.IsFalse(WmiApi.EvaluateCreationJob(observation)!.OutcomeUnknown);
        Assert.AreEqual(ApiErrorKind.OperationFailed, VmImportService.EvaluateImportJobCompletion(observation)!.ErrorKind);
    }

    [TestMethod]
    public void ExceptionAloneAndIncompleteOrConflictingEvidenceRemainUnknown()
    {
        foreach (ushort[]? statuses in new ushort[]?[] { null, [], [6], [17], [17, 2] })
            Assert.AreEqual(ApiErrorKind.OutcomeUnknown,
                WmiJobWaiter.Evaluate(new(10, 32773, "error", 100, statuses), true)!.ErrorKind);
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown,
            WmiJobWaiter.Evaluate(new(10, null, "missing error code", 100, [17, 6]), true)!.ErrorKind);
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown,
            WmiJobWaiter.Evaluate(new(10, 32773, "other provider", 100, [17, 6]), false)!.ErrorKind);
    }

    [TestMethod]
    public void CompletionEvidenceNeverOverridesAnActiveOrKilledJob()
    {
        foreach (ushort state in new ushort[] { 2, 3, 4, 5, 6 })
            Assert.IsNull(WmiJobWaiter.Evaluate(new(state, 32773, "transient", 100, [17, 6]), true));
        foreach (ushort state in new ushort[] { 9, 11 })
            Assert.AreEqual(ApiErrorKind.OutcomeUnknown,
                WmiJobWaiter.Evaluate(new(state, 32773, "unsettled", 100, [17, 6]), true)!.ErrorKind);
    }
}
