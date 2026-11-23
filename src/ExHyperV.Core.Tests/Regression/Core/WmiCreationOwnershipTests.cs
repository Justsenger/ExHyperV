using ExHyperV.Models;
using ExHyperV.Services;
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

    [TestMethod]
    public async Task NetworkAdapterCreationSupportsBothSyntheticAndLegacyEmulatedPorts()
    {
        var vmId = Guid.NewGuid();
        var synDeviceId = Guid.NewGuid();
        var emuDeviceId = Guid.NewGuid();
        string synInstanceId = $"Microsoft:{vmId:D}\\{synDeviceId:D}";
        string emuInstanceId = $"Microsoft:{vmId:D}\\{emuDeviceId:D}\\0";

        var synModel = new VmNetworkAdapter { Id = synInstanceId };
        var emuModel = new VmNetworkAdapter { Id = emuInstanceId };
        Assert.IsFalse(synModel.IsLegacy);
        Assert.AreEqual(synDeviceId, synModel.DeviceId);
        Assert.IsTrue(emuModel.IsLegacy);
        Assert.AreEqual(emuDeviceId, emuModel.DeviceId);

        Assert.IsFalse((bool)VmNetworkFields.IsLegacy.ConvertValue!(synInstanceId)!);
        Assert.IsTrue((bool)VmNetworkFields.IsLegacy.ConvertValue!(emuInstanceId)!);

        foreach (var (portClass, instanceId, expectedDeviceId) in new[]
        {
            (VmNetworkAdapterCreation.PortClass, synInstanceId, synDeviceId),
            (VmNetworkAdapterCreation.EmulatedPortClass, emuInstanceId, emuDeviceId)
        })
        {
            string escapedPortId = instanceId.Replace("\\", "\\\\");
            string portPath = $"{portClass}.InstanceID=\"{escapedPortId}\"";
            string allocInstanceId = $"{instanceId}\\alloc";
            string escapedAllocId = allocInstanceId.Replace("\\", "\\\\");
            string allocPath = $"{VmNetworkAdapterCreation.AllocationClass}.InstanceID=\"{escapedAllocId}\"";
            var inventory = new List<VmCreatedNetworkResource>();

            var created = await VmNetworkAdapterCreation.ExecuteAsync(
                vmId,
                () =>
                {
                    inventory.Add(new(portPath, instanceId, portClass, "cfg-port", []));
                    return Task.FromResult(new WmiCreationResult(ApiResponse.Ok(), [portPath], false));
                },
                _ =>
                {
                    inventory[0] = inventory[0] with { Children = [allocPath] };
                    inventory.Add(new(allocPath, allocInstanceId, VmNetworkAdapterCreation.AllocationClass, "cfg-alloc", []));
                    return Task.FromResult(new WmiCreationResult(ApiResponse.Ok(), [allocPath], false));
                },
                () => Task.FromResult(ApiResponse<List<VmCreatedNetworkResource>>.Ok(inventory.ToList())),
                path =>
                {
                    inventory.RemoveAll(r => r.Path == path);
                    return Task.FromResult(ApiResponse.Ok());
                },
                portClass);

            Assert.IsTrue(created.Success, portClass);
            Assert.AreEqual(expectedDeviceId, created.DeviceId);
            Assert.AreEqual(VmNetworkAdapterCreationState.Present, created.AdapterState);
            Assert.AreEqual(VmNetworkAdapterInitializationState.Completed, created.InitializationState);

            // Stage-2 failure must automatically roll back the created port for both Synthetic and Emulated classes
            inventory.Clear();
            var rolledBack = await VmNetworkAdapterCreation.ExecuteAsync(
                vmId,
                () =>
                {
                    inventory.Add(new(portPath, instanceId, portClass, "cfg-port", []));
                    return Task.FromResult(new WmiCreationResult(ApiResponse.Ok(), [portPath], false));
                },
                _ => Task.FromResult(new WmiCreationResult(ApiResponse.Fail("allocation failed"), [], false)),
                () => Task.FromResult(ApiResponse<List<VmCreatedNetworkResource>>.Ok(inventory.ToList())),
                path =>
                {
                    inventory.RemoveAll(r => r.Path == path);
                    return Task.FromResult(ApiResponse.Ok());
                },
                portClass);

            Assert.IsFalse(rolledBack.Success, portClass);
            Assert.AreEqual(expectedDeviceId, rolledBack.DeviceId);
            Assert.AreEqual(VmNetworkAdapterCreationState.Removed, rolledBack.AdapterState);
            Assert.IsEmpty(rolledBack.RetainedDeviceIds);
            Assert.IsEmpty(inventory);
        }
    }
}
