using ExHyperV.Core.Tests.TestExecution;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass, TestCategory("Regression")]
public sealed class TemporaryVmTests
{
    [TestMethod]
    public void RecoveryRequiresTheExactOwnedDirectoryAndGeneratedName()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var journal = new TemporaryVm.Journal("ExHyperV-ApiTest-" + Guid.NewGuid().ToString("N"), Path.Combine(directory, "vm"));
        TemporaryVm.ValidateRoot(directory, journal);
        Assert.ThrowsExactly<IOException>(() => TemporaryVm.ValidateRoot(directory, journal with { Root = directory }));
        Assert.ThrowsExactly<IOException>(() => TemporaryVm.ValidateRoot(directory, journal with { Root = Path.Combine(directory, "vm-other") }));
        Assert.ThrowsExactly<IOException>(() => TemporaryVm.ValidateRoot(directory, journal with { Name = "Existing VM" }));
        Assert.ThrowsExactly<IOException>(() => TemporaryVm.ValidateRoot(directory, journal with { Name = "ExHyperV-ApiTest-invalid" }));
    }
}
