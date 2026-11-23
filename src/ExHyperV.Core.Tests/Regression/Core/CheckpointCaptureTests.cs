using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class CheckpointCaptureTests
{
    [TestMethod]
    public async Task EnabledPoliciesExecuteSingleCaptureWithoutPolicyMutation()
    {
        foreach (ushort policy in new ushort[] { 3, 4, 5 })
        {
            int captures = 0;
            var result = await VmCheckpointService.CaptureWithPolicyAsync(policy,
                () => { captures++; return Task.FromResult(ApiResponse.Ok()); });
            Assert.IsTrue(result.Success);
            Assert.AreEqual(1, captures);
        }
    }

    [TestMethod]
    public async Task DisabledAndUnknownPoliciesRefuseCapture()
    {
        foreach (ushort policy in new ushort[] { 2, 0, 42 })
        {
            var result = await VmCheckpointService.CaptureWithPolicyAsync(policy,
                () => throw new AssertFailedException("Unexpected capture"));
            Assert.AreEqual(policy == 2 ? ApiErrorKind.InvalidState : ApiErrorKind.Unsupported, result.ErrorKind);
        }
    }

    [TestMethod]
    public async Task ThrownCaptureIsUnknownAndNeverRetriggers()
    {
        var result = await VmCheckpointService.CaptureWithPolicyAsync(3,
            () => throw new IOException("lost reply"));
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown, result.ErrorKind);
    }
}
