using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class StorageCreationOwnershipTests
{
    private static VmCreatedStorageResource Resource(string id, int type = 17, string parent = "") =>
        new(id, $"Msvm_ResourceAllocationSettingData.InstanceID=\"{id}\"", type, parent);

    private static WmiCreationResult Created(VmCreatedStorageResource resource) =>
        new(ApiResponse.Ok(), [resource.Path], false);

    [TestMethod]
    public async Task FailedCall_DoesNotClaimConcurrentCreation()
    {
        var foreign = Resource("foreign");
        var ownership = new VmStorageCreationOwnership();
        Assert.IsNull(ownership.RecordCreation(new(ApiResponse.Fail("failed before create"), [], false), [foreign], 17));
        var removed = new List<string>();
        var result = await ownership.RollbackAsync(
            () => Task.FromResult(ApiResponse<List<VmCreatedStorageResource>>.Ok([foreign])),
            path => { removed.Add(path); return Task.FromResult(ApiResponse.Ok()); },
            _ => throw new AssertFailedException("没有创建文件，不应删除。"));
        Assert.IsTrue(result.Success);
        Assert.IsEmpty(removed);
    }

    [TestMethod]
    public async Task ConcurrentResources_OnlyReceiptIdentityIsRemoved()
    {
        var owned = Resource("owned");
        var foreign = Resource("foreign");
        var inventory = new List<VmCreatedStorageResource> { foreign, owned };
        var ownership = new VmStorageCreationOwnership();
        Assert.AreEqual(owned, ownership.RecordCreation(Created(owned), inventory, 17));
        var removed = new List<string>();
        var result = await ownership.RollbackAsync(
            () => Task.FromResult(ApiResponse<List<VmCreatedStorageResource>>.Ok(inventory.ToList())),
            path => { removed.Add(path); inventory.RemoveAll(item => item.Path == path); return Task.FromResult(ApiResponse.Ok()); },
            _ => Task.CompletedTask);
        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(new[] { owned.Path }, removed);
        CollectionAssert.AreEqual(new[] { foreign }, inventory);
    }

    [TestMethod]
    public async Task RemovedOriginal_DoesNotRemoveReplacementAtItsSlot()
    {
        var original = Resource("original");
        var replacement = Resource("replacement");
        var ownership = new VmStorageCreationOwnership();
        ownership.RecordCreation(Created(original), [original], 17);
        var result = await ownership.RollbackAsync(
            () => Task.FromResult(ApiResponse<List<VmCreatedStorageResource>>.Ok([replacement])),
            _ => throw new AssertFailedException("原资源消失后不能寻找同槽位替代品。"),
            _ => Task.CompletedTask);
        Assert.IsTrue(result.Success);
    }

    [TestMethod]
    [DataRow("HostResource")]
    [DataRow("Parent")]
    [DataRow("ResourceSubType")]
    [DataRow("AddressOnParent")]
    public async Task ReusedIdentityWithChangedConfiguration_IsPreserved(string changedField)
    {
        var original = Resource("same-id", 31, Resource("slot").Path) with
        {
            ResourceSubType = "Microsoft:Hyper-V:Virtual Hard Disk",
            AddressOnParent = "0", HostResources = ["original.vhdx"]
        };
        var replacement = changedField switch
        {
            "HostResource" => original with { HostResources = ["foreign.vhdx"] },
            "Parent" => original with { Parent = Resource("foreign-slot").Path },
            "ResourceSubType" => original with { ResourceSubType = "Microsoft:Hyper-V:Virtual CD/DVD Disk" },
            _ => original with { AddressOnParent = "1" }
        };
        var ownership = new VmStorageCreationOwnership { VhdPath = "original.vhdx" };
        ownership.RecordCreation(Created(original), [original], 31);
        var result = await ownership.RollbackAsync(
            () => Task.FromResult(ApiResponse<List<VmCreatedStorageResource>>.Ok([replacement])),
            _ => throw new AssertFailedException("同 ID 但配置已变化的对象不再属于本次清理。"),
            _ => throw new AssertFailedException("资源归属变化时保留磁盘。"));
        Assert.AreEqual(ApiErrorKind.CleanupRequired, result.ErrorKind);
    }

    [TestMethod]
    public async Task UnknownCreate_PreservesResourcesAndDisk()
    {
        var ownership = new VmStorageCreationOwnership { VhdPath = "owned.vhdx" };
        ownership.RecordCreation(new(ApiResponse.Fail("job still running"), [], true), [], 17);
        var result = await ownership.RollbackAsync(
            () => throw new AssertFailedException("未知作业不能靠扫描认领。"),
            _ => throw new AssertFailedException("未知作业不能删除资源。"),
            _ => throw new AssertFailedException("未知作业不能删除磁盘。"));
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown, result.ErrorKind);
    }

    [TestMethod]
    public async Task FailedDetach_PreservesDisk()
    {
        var owned = Resource("owned");
        var ownership = new VmStorageCreationOwnership { VhdPath = "owned.vhdx" };
        ownership.RecordCreation(Created(owned), [owned], 17);
        var result = await ownership.RollbackAsync(
            () => Task.FromResult(ApiResponse<List<VmCreatedStorageResource>>.Ok([owned])),
            _ => Task.FromResult(ApiResponse.Fail("busy")),
            _ => throw new AssertFailedException("未确认卸载时不能删除磁盘。"));
        Assert.AreEqual(ApiErrorKind.CleanupRequired, result.ErrorKind);
    }

    [TestMethod]
    public async Task ForeignChild_PreservesCreatedControllerAndDisk()
    {
        var controller = Resource("owned-controller", 6);
        var foreignChild = Resource("foreign-slot", 17, controller.Path);
        var ownership = new VmStorageCreationOwnership { VhdPath = "owned.vhdx" };
        ownership.RecordCreation(Created(controller), [controller], 6);
        var result = await ownership.RollbackAsync(
            () => Task.FromResult(ApiResponse<List<VmCreatedStorageResource>>.Ok([controller, foreignChild])),
            _ => throw new AssertFailedException("不能连带删除外来子资源。"),
            _ => throw new AssertFailedException("资源仍在时不清理磁盘。"));
        Assert.AreEqual(ApiErrorKind.CleanupRequired, result.ErrorKind);
    }

    [TestMethod]
    public async Task VerifiedCleanup_RemovesMediaThenSlotThenControllerBeforeFile()
    {
        var controller = Resource("controller", 6);
        var slot = Resource("slot", 17, controller.Path);
        var media = Resource("media", 31, slot.Path);
        var inventory = new List<VmCreatedStorageResource> { controller, slot, media };
        var ownership = new VmStorageCreationOwnership { VhdPath = "owned.vhdx" };
        foreach (var item in inventory) ownership.RecordCreation(Created(item), inventory, item.ResourceType);
        var removed = new List<string>();
        var result = await ownership.RollbackAsync(
            () => Task.FromResult(ApiResponse<List<VmCreatedStorageResource>>.Ok(inventory.ToList())),
            path => { removed.Add(path); inventory.RemoveAll(item => item.Path == path); return Task.FromResult(ApiResponse.Ok()); },
            path => { Assert.IsEmpty(inventory); removed.Add(path); return Task.CompletedTask; });
        Assert.IsTrue(result.Success);
        CollectionAssert.AreEqual(new[] { media.Path, slot.Path, controller.Path, "owned.vhdx" }, removed);
    }

    [TestMethod]
    public async Task ReceiptOutsideTargetVm_IsNotClaimed()
    {
        var foreign = Resource("other-vm");
        var ownership = new VmStorageCreationOwnership();
        Assert.IsNull(ownership.RecordCreation(Created(foreign), [], 17));
        var result = await ownership.RollbackAsync(
            () => throw new AssertFailedException("无归属证据不能回查猜测。"),
            _ => throw new AssertFailedException("不能删除其他 VM 资源。"),
            _ => Task.CompletedTask);
        Assert.AreEqual(ApiErrorKind.OutcomeUnknown, result.ErrorKind);
    }
}
