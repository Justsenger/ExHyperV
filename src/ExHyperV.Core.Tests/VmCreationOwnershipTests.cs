using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
[TestCategory("OperationResource")]
public sealed class VmCreationOwnershipTests
{
    [TestMethod]
    public async Task ConcurrentReservationsUseDifferentDirectories()
    {
        using var fixture = new DirectoryFixture();
        int initialLookups = 0;
        var allLookedUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> NameExists(string name)
        {
            if (name == "guest")
            {
                if (Interlocked.Increment(ref initialLookups) == 12) allLookedUp.SetResult();
                await allLookedUp.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return false;
        }
        // 所有调用者先看到原目录不存在，再一起竞争创建，稳定复现旧实现的检查/创建竞态。
        var reservations = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            VmCreateService.ReserveVmDirectoryAsync("guest", fixture.Root, NameExists)));

        Assert.AreEqual(12, reservations.Select(value => value.Directory).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.IsTrue(reservations.Any(value => value.Name == "guest"));
        Assert.IsTrue(reservations.All(value => Directory.Exists(value.Directory)));
    }

    [TestMethod]
    public void FailedDefinitionCanStillIdentifyExactlyOneCreatedVm()
    {
        string path = "Msvm_ComputerSystem.Name=\"" + Guid.NewGuid().ToString("D") + "\"";
        var receipt = new WmiCreationResult(ApiResponse.Fail("configuration failed"), [path], false);

        Assert.AreEqual(path, VmCreateService.GetConfirmedCreatedVmPath(receipt));
    }

    [TestMethod]
    public void UncertainOrAmbiguousDefinitionNeverSelectsARollbackVm()
    {
        string first = "Msvm_ComputerSystem.Name=\"" + Guid.NewGuid().ToString("D") + "\"";
        string second = "Msvm_ComputerSystem.Name=\"" + Guid.NewGuid().ToString("D") + "\"";
        Assert.IsNull(VmCreateService.GetConfirmedCreatedVmPath(new(ApiResponse.Fail("still running"), [first], true)));
        Assert.IsNull(VmCreateService.GetConfirmedCreatedVmPath(new(ApiResponse.Fail("ambiguous"), [first, second], false)));
        Assert.IsNull(VmCreateService.GetConfirmedCreatedVmPath(new(ApiResponse.Fail("rejected"), [], false)));
    }

    [TestMethod]
    public async Task ExistingEmptyDirectoryAndRegisteredNameKeepRenameBehavior()
    {
        using var fixture = new DirectoryFixture();
        string existing = Path.Combine(fixture.Root, "guest");
        Directory.CreateDirectory(existing);

        var reserved = await VmCreateService.ReserveVmDirectoryAsync("guest", fixture.Root,
            name => Task.FromResult(name == "guest (2)"));

        Assert.AreEqual("guest (3)", reserved.Name);
        Assert.IsTrue(Directory.Exists(existing));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(existing));
    }

    [TestMethod]
    public async Task ReservationSupportsPathsLongerThanTheLegacyWindowsLimit()
    {
        using var fixture = new DirectoryFixture();
        string parent = fixture.Root;
        while (parent.Length < 280)
            parent = Path.Combine(parent, new string('p', 40));

        var reserved = await VmCreateService.ReserveVmDirectoryAsync("guest", parent, _ => Task.FromResult(false));

        Assert.IsTrue(reserved.Directory.Length > 260);
        Assert.IsTrue(Directory.Exists(reserved.Directory));
        Assert.IsFalse(VmCreationDirectory.TryCreateLeaf(reserved.Directory));
        var remaining = await VmCreateService.CleanupOwnedCreationArtifactsAsync(null, null, reserved.Directory, true);
        Assert.IsEmpty(remaining);
        Assert.IsFalse(Directory.Exists(reserved.Directory));
    }

    [TestMethod]
    public async Task CleanupRemovesRecordedDiskButPreservesUnrelatedFiles()
    {
        using var fixture = new DirectoryFixture();
        var reserved = await VmCreateService.ReserveVmDirectoryAsync("guest", fixture.Root, _ => Task.FromResult(false));
        string ownedDisk = Path.Combine(reserved.Directory, "guest.vhdx");
        string unrelated = Path.Combine(reserved.Directory, "other.txt");
        await File.WriteAllTextAsync(ownedDisk, "created by this operation");
        await File.WriteAllTextAsync(unrelated, "another operation's data");

        var remaining = await VmCreateService.CleanupOwnedCreationArtifactsAsync(
            ownedDisk, null, reserved.Directory, true);

        Assert.IsFalse(File.Exists(ownedDisk));
        Assert.AreEqual("another operation's data", await File.ReadAllTextAsync(unrelated));
        CollectionAssert.AreEqual(new[] { reserved.Directory }, remaining);
    }

    [TestMethod]
    public async Task CleanupNeverClaimsAFileMerelyBecauseItAppearedAfterFailure()
    {
        using var fixture = new DirectoryFixture();
        var reserved = await VmCreateService.ReserveVmDirectoryAsync("guest", fixture.Root, _ => Task.FromResult(false));
        string unconfirmedDisk = Path.Combine(reserved.Directory, "guest.vhdx");
        await File.WriteAllTextAsync(unconfirmedDisk, "creation was not confirmed");

        var remaining = await VmCreateService.CleanupOwnedCreationArtifactsAsync(
            null, null, reserved.Directory, true);

        Assert.IsTrue(File.Exists(unconfirmedDisk));
        CollectionAssert.AreEqual(new[] { reserved.Directory }, remaining);
    }

    [TestMethod]
    public async Task CleanupRemovesOnlyOwnedEmptyDirectories()
    {
        using var fixture = new DirectoryFixture();
        var reserved = await VmCreateService.ReserveVmDirectoryAsync("guest", fixture.Root, _ => Task.FromResult(false));
        string ownedChild = Path.Combine(reserved.Directory, "disks");
        Assert.IsTrue(VmCreationDirectory.TryCreateLeaf(ownedChild));

        var remaining = await VmCreateService.CleanupOwnedCreationArtifactsAsync(
            null, ownedChild, reserved.Directory, true);

        Assert.IsEmpty(remaining);
        Assert.IsFalse(Directory.Exists(reserved.Directory));
        Assert.IsTrue(Directory.Exists(fixture.Root));

        string existing = Path.Combine(fixture.Root, "existing");
        Directory.CreateDirectory(existing);
        Assert.IsFalse(VmCreationDirectory.TryCreateLeaf(existing));
        remaining = await VmCreateService.CleanupOwnedCreationArtifactsAsync(null, null, existing, false);
        Assert.IsEmpty(remaining);
        Assert.IsTrue(Directory.Exists(existing));
    }

    private sealed class DirectoryFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ExHyperV-creation-ownership-" + Guid.NewGuid().ToString("N"));

        internal DirectoryFixture() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            string fullRoot = Path.GetFullPath(Root);
            string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(fullRoot).StartsWith("ExHyperV-creation-ownership-", StringComparison.Ordinal))
                throw new InvalidOperationException("拒绝清理临时测试目录以外的路径。");
            if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, true);
        }
    }
}
