using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class RegressionTests
{
    private static void Check(bool condition, string scenario) => Assert.IsTrue(condition, scenario);

    [TestCategory("OperationWorkflow")]
    [TestMethod] public Task GpuDeployment_DecisionsCancellationAndRecovery() => GpuWorkflowChecks.Run(Check);
    [TestCategory("OperationQuery")]
    [TestMethod] public void Switches_ValidationAndDefaults() => SwitchRuleChecks.Run(Check);
    [TestCategory("OperationQuery")]
    [TestMethod] public void Host_CompatibilityRules() => HostBusinessRuleChecks.Run(Check);
    [TestCategory("OperationQuery")] [TestCategory("OperationCommand")]
    [TestMethod] public Task VirtualMachines_CreationAndEditingRules() => VmBusinessRuleChecks.Run(Check);
    [TestCategory("OperationQuery")]
    [TestMethod] public Task Network_QueryAndRefreshRules() => QueryNetworkRuleChecks.Run(Check);
    [TestCategory("OperationQuery")]
    [TestMethod] public void Memory_BackingRules() => MemoryBackingRuleChecks.Run(Check);
    [TestCategory("OperationQuery")]
    [TestMethod] public void IpAddresses_SourcePriorityAndFallback() => IpAddressRuleChecks.Run(Check);
    [TestCategory("OperationCommand")]
    [TestMethod] public Task CheckpointsAndClones_OperationOrdering() => SpacetimeWorkflowChecks.Run(Check);
    [TestCategory("OperationWorkflow")]
    [TestMethod] public Task Power_TransitionsAndFailureHandling() => PowerWorkflowChecks.Run(Check);
    [TestCategory("OperationCommand")] [TestCategory("OperationWorkflow")]
    [TestMethod] public Task Devices_PcieWorkflows() => DeviceWorkflowChecks.RunAsync(Check);
    [TestCategory("OperationCommand")] [TestCategory("OperationResource")]
    [TestCategory("OperationQuery")]
    [TestMethod] public void Models_SnapshotIsolationAndReadOnlyCatalogs() => ModelIsolationChecks.Run(Check);
    [TestCategory("OperationQuery")]
    [TestMethod] public Task Language_ContextAndExplicitOverrides() => ResourceCultureChecks.RunAsync(Check);
    [TestCategory("OperationQuery")]
    [TestMethod] public Task Caches_OwnershipAndInvalidation() => CacheOwnershipChecks.RunAsync(Check);
    [TestCategory("OperationQuery")] [TestCategory("OperationCommand")]
    [TestMethod] public Task Configuration_SnapshotsFailuresAndConcurrentWriters() => ConfigurationIsolationChecks.RunAsync(Check);
    [TestCategory("OperationResource")]
    [TestMethod] public Task CpuMonitoring_ShutdownAndInitializationFailure() => CpuMonitorLifecycleChecks.RunAsync(Check);
    [TestCategory("OperationResource")]
    [TestMethod] public Task QueryMonitoring_ShutdownAndResourceOwnership() => VmQueryMonitorLifecycleChecks.RunAsync(Check);
    [TestCategory("OperationResource")]
    [TestMethod] public void NativeModules_OwnershipWithFakeBackend() => NativeLifetimeChecks.Run(Check);
    [TestCategory("OperationQuery")] [TestCategory("OperationCommand")]
    [TestMethod] public Task Configuration_ValidationAndAffinity() => ConfigurationRuleChecks.Run(Check);
    [TestCategory("OperationQuery")]
    [TestMethod] public Task Export_PlanningAndSelection() => ExportPlanChecks.RunAsync(Check);
    [TestCategory("OperationQuery")] [TestCategory("OperationCommand")]
    [TestMethod] public Task Storage_ValidationAndFailureHandling() => StorageWorkflowChecks.Run(Check);
    [TestCategory("OperationQuery")] [TestCategory("OperationCommand")]
    [TestMethod] public Task Configuration_InterdependentSettings() => ConfigurationInteractionChecks.RunAsync(Check);
}
