using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
[TestCategory("OperationQuery")]
public sealed class DiskSettingsReaderTests
{
    private static void Check(bool condition, string scenario) => Assert.IsTrue(condition, scenario);

    [TestMethod]
    [TestCategory("OperationResource")]
    public void DiskMetadata_ServiceOwnershipAndFailureRecovery() =>
        DiskSettingsReaderChecks.CheckOwnershipAndRecovery(Check);

    [TestMethod]
    public void DiskMetadata_MissingFilesSkipQueriesAndRecover() =>
        DiskSettingsReaderChecks.CheckMissingFiles(Check);

    [TestMethod]
    public void DiskMetadata_CacheHitsAndUncertainFileErrors() =>
        DiskSettingsReaderChecks.CheckCacheHitsAndUncertainErrors(Check);
}
