using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class CheckpointCaptureTests
{
    [TestMethod]
    public async Task ProductionRequestUsesProductionOnlyThenRestoresStandardPolicy()
    {
        var calls = new List<string>();
        var result = await VmSpacetimeService.CaptureWithPolicyAsync(5, 4,
            (before, after) => { calls.Add($"{before}>{after}"); return Task.FromResult(ApiResponse.Ok()); },
            () => { calls.Add("capture"); return Task.FromResult(ApiResponse.Ok()); });
        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(new[] { "5>4", "capture", "4>5" }, calls);
    }

    [TestMethod]
    public async Task StandardRequestOverridesProductionPolicyAndRestoresIt()
    {
        var calls = new List<string>();
        var result = await VmSpacetimeService.CaptureWithPolicyAsync(4, 5,
            (before, after) => { calls.Add($"{before}>{after}"); return Task.FromResult(ApiResponse.Ok()); },
            () => { calls.Add("capture"); return Task.FromResult(ApiResponse.Ok()); });
        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(new[] { "4>5", "capture", "5>4" }, calls);
    }

    [TestMethod]
    public async Task SamePolicyRequiresOnlyOneCapture()
    {
        int captures = 0;
        var result = await VmSpacetimeService.CaptureWithPolicyAsync(4, 4,
            (_, _) => throw new AssertFailedException("Unnecessary policy mutation"),
            () => { captures++; return Task.FromResult(ApiResponse.Ok()); });
        Assert.IsTrue(result.Success);
        Assert.AreEqual(1, captures);
    }

    [TestMethod]
    public async Task DisabledAndUnknownPoliciesRefuseCapture()
    {
        foreach (ushort policy in new ushort[] { 2, 0, 42 })
        {
            var result = await VmSpacetimeService.CaptureWithPolicyAsync(policy, 4,
                (_, _) => throw new AssertFailedException("Unexpected mutation"),
                () => throw new AssertFailedException("Unexpected capture"));
            Assert.AreEqual(policy == 2 ? ApiErrorKind.InvalidState : ApiErrorKind.Unsupported, result.ErrorKind);
        }
    }

    [TestMethod]
    public async Task SettledCaptureFailureRestoresPolicyWithoutStandardFallback()
    {
        int mutations = 0, captures = 0;
        var result = await VmSpacetimeService.CaptureWithPolicyAsync(5, 4,
            (_, _) => { mutations++; return Task.FromResult(ApiResponse.Ok()); },
            () => { captures++; return Task.FromResult(ApiResponse.Fail("vss-failed", 32773, ApiErrorSource.Wmi, kind: ApiErrorKind.OperationFailed)); });
        Assert.AreEqual(2, mutations);
        Assert.AreEqual(1, captures);
        Assert.AreEqual(32773, result.Code);
        Assert.AreEqual("vss-failed", result.Error);
    }

    [TestMethod]
    public async Task UnknownCapturePreservesPolicyForReconciliation()
    {
        int mutations = 0;
        var result = await VmSpacetimeService.CaptureWithPolicyAsync(5, 4,
            (_, _) => { mutations++; return Task.FromResult(ApiResponse.Ok()); },
            () => Task.FromResult(ApiResponse.Fail("timeout", kind: ApiErrorKind.OutcomeUnknown)));
        Assert.AreEqual(1, mutations);
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown, result.ErrorKind);
        StringAssert.Contains(result.Error, "原检查点策略为 5");
    }

    [TestMethod]
    public async Task UnknownPolicyWritePreservesEvidenceAndDoesNotCapture()
    {
        int mutations = 0;
        var result = await VmSpacetimeService.CaptureWithPolicyAsync(5, 4,
            (_, _) => { mutations++; return Task.FromResult(ApiResponse.Fail("timeout", kind: ApiErrorKind.OutcomeUnknown)); },
            () => throw new AssertFailedException("Unexpected capture"));
        Assert.AreEqual(1, mutations);
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown, result.ErrorKind);
    }

    [TestMethod]
    public async Task RestorationFailureIsReportedAfterSuccessfulCapture()
    {
        int mutations = 0;
        var result = await VmSpacetimeService.CaptureWithPolicyAsync(5, 4,
            (_, _) => Task.FromResult(++mutations == 1 ? ApiResponse.Ok() : ApiResponse.Fail("external-change", kind: ApiErrorKind.Conflict)),
            () => Task.FromResult(ApiResponse.Ok()));
        Assert.AreEqual(ApiErrorKind.CleanupRequired, result.ErrorKind);
        StringAssert.Contains(result.Error, "external-change");
    }

    [TestMethod]
    public async Task ThrownCaptureIsUnknownAndNeverRetriggersOrRestores()
    {
        int mutations = 0;
        var result = await VmSpacetimeService.CaptureWithPolicyAsync(5, 4,
            (_, _) => { mutations++; return Task.FromResult(ApiResponse.Ok()); },
            () => throw new IOException("lost reply"));
        Assert.AreEqual(1, mutations);
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown, result.ErrorKind);
    }
}
