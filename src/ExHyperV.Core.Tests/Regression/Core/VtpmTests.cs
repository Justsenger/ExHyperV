using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class VtpmTests
{
    [TestMethod]
    public void KeyRepairAndPolicyUseDifferentInterlocks()
    {
        var shielded = new VmSecurityService.VmSecurityInfo(true, "", true, true, true);
        foreach (var state in Enum.GetValues<VmState>())
        {
            Assert.AreEqual(state == VmState.PoweredOff, VmSecurityService.GetKeyChangeCondition(2, state).CanModify);
            Assert.IsFalse(VmSecurityService.GetKeyChangeCondition(1, state).CanModify);
            Assert.IsFalse(VmSecurityService.GetModificationCondition(2, state, shielded, VmSecurityService.Setting.Tpm).CanModify);
        }
    }

    [TestMethod]
    public void UnknownMigrationValueAndMissingGuardianRemainDistinct()
    {
        var host = new VtpmHost("外部宿主", "01", "02", false, true);
        var snapshot = new VmVtpmInfo(true, true, true, true, null, null, [host], false, null, null, "", null, null, null);
        Assert.IsFalse(snapshot.HasLocalKey);
        Assert.IsTrue((snapshot with { AuthorizedHosts = [host with { IsLocal = true }] }).HasLocalKey);
        Assert.IsNull(snapshot.MigrationDisabled);
        Assert.IsNull((snapshot with { MigratablePolicy = 2 }).MigrationDisabled);
        Assert.AreEqual(false, (snapshot with { MigratablePolicy = 0 }).MigrationDisabled);
        Assert.AreEqual(true, (snapshot with { MigratablePolicy = 1 }).MigrationDisabled);
        Assert.AreSame(VmSecurityFields.MigratablePolicy, VmQueryFields.Find("Security.MigratablePolicy"));
    }

    [TestMethod]
    public void OnlyEmptyProtectorIsReportedAsUnconfigured()
    {
        Assert.IsFalse(VmSecurityService.ContainsProtector([]));
        Assert.IsFalse(VmSecurityService.ContainsProtector([0, 0, 0, 0]));
        Assert.IsFalse(VmSecurityService.ContainsProtector([1, 0, 0, 0]));
        Assert.IsTrue(VmSecurityService.ContainsProtector([0, 0, 0, 0, 1]));
    }

    [TestMethod]
    public void RecoveryPointRequiresExactCurrentProtector()
    {
        byte[] old = [4, 3, 2, 1], current = [1, 2, 3, 4];
        var recovery = new VmSecurityService.RecoveryPoint(old, current);
        old[0] = 0;
        Assert.AreEqual((byte)4, recovery.Previous[0]);
        Assert.IsTrue(recovery.Matches(current));
        current[0] = 9;
        Assert.IsFalse(recovery.Matches(current));
    }

    [TestMethod]
    public async Task EmptyIdentityIsRejectedWithoutContactingHyperV()
    {
        var result = await VmSecurityService.GetVtpmStateAsync(Guid.Empty);
        Assert.AreEqual(ApiErrorKind.InvalidArgument, result.ErrorKind);
    }
    [TestMethod]
    public void MalformedKeyOutputAndReadbackHaveDistinctRecoveryResults()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => VmSecurityService.DecodeKeyOutput(null));
        Assert.ThrowsExactly<InvalidDataException>(() => VmSecurityService.DecodeKeyOutput(new byte[] { 9, 9, 9, 9 }));
        Assert.ThrowsExactly<InvalidDataException>(() => VmSecurityService.DecodeKeyOutput("missing"));
        Assert.ThrowsExactly<InvalidDataException>(() => VmSecurityService.DecodeKeyOutput(new byte[] { 1, 2 }));
        Assert.AreEqual(0, VmSecurityService.DecodeKeyOutput(Array.Empty<byte>()).Length);
        byte[] key = [1, 2, 3, 4, 5];
        Assert.IsTrue(VmSecurityService.VerifyKeyWrite(key, () => key.ToArray()).Success);
        Assert.AreEqual(ApiErrorKind.Conflict, VmSecurityService.VerifyKeyWrite(key, () => []).ErrorKind);
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown, VmSecurityService.VerifyKeyWrite(key, () => throw new IOException()).ErrorKind);
        Assert.IsFalse(HgsGuardians.MatchesPair(([1], [2]), [1], [3]));
        Assert.IsFalse(HgsGuardians.MatchesPair(([1], [2]), [2], [1]));
        Assert.IsTrue(HgsGuardians.MatchesPair(([1], [2]), [1], [2]));
    }
}
