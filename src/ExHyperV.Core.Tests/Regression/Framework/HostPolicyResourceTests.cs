using ExHyperV.Core.Tests.ResourceInspection;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class HostPolicyResourceTests
{
    [TestMethod]
    public void SuccessfulReadOfDisabledPolicyKeepsPcieBlocked()
    {
        Assert.AreEqual(InspectionStatus.Missing, ResourceProbes.EvaluateAssignmentPolicy(ApiResponse<bool>.Ok(false)).Status);
        Assert.AreEqual(InspectionStatus.Ready, ResourceProbes.EvaluateAssignmentPolicy(ApiResponse<bool>.Ok(true)).Status);
    }

    [TestMethod]
    public void PolicyReadFailureRemainsAnInspectionError()
    {
        var error = Assert.Throws<InvalidOperationException>(() => ResourceProbes.EvaluateAssignmentPolicy(ApiResponse<bool>.Fail("读取失败")));
        StringAssert.Contains(error.Message, "读取失败");
    }
}
