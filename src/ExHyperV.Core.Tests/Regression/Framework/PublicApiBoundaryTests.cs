using System.Reflection;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class PublicApiBoundaryTests
{
    [TestMethod]
    public void UnimplementedCapabilitiesAreNotExported()
    {
        var exported = typeof(ConsoleService).Assembly.GetExportedTypes();
        Assert.IsFalse(exported.Any(type => type.Name.StartsWith("ConsoleAudio", StringComparison.Ordinal)
            || type.Name is "IConsoleAudioInput" or "IConsoleAudioOutput" or "DiskParser" or "MmioPlan" or "PageSizeItem"));
        Assert.IsNull(typeof(IConsoleConnection).GetProperty("AudioInput"));
        Assert.IsNull(typeof(IConsoleConnection).GetProperty("AudioOutput"));
        Assert.IsNull(typeof(ConsoleService).GetField("NimueBackendId"));
        Assert.IsNull(typeof(ConsoleBackendInfo).GetProperty("IsImplemented"));
        Assert.IsNull(typeof(ConsoleBackendInfo).GetProperty("UnavailableReason"));
        CollectionAssert.AreEqual(new[] { "RdpBasic", "RdpEnhanced" }, ConsoleService.GetBackends().Select(item => item.BackendId).ToArray());
        Assert.ThrowsExactly<ArgumentException>(() => ConsoleService.CreateConnection(new() { VmId = Guid.NewGuid() }, "Nimue"));
    }

    [TestMethod]
    public void ImplementationHelpersAreNotPublicOperations()
    {
        foreach (var (type, method) in new (Type, string)[]
        {
            (typeof(VmGpuRepairService), "ResolveVmcxAsync"), (typeof(VmQueryService), "GetVmConfigRootAsync"),
            (typeof(VmStorageService), "GetNewDiskSize"), (typeof(VmProcessorService), "GetProcessorCountOptions"),
            (typeof(HostNetworkService), "NormalizeDynamicMacAddress"), (typeof(VmMemorySettings), "ConfigurePageSizeSupport"),
            (typeof(VmMmioService), "ComputeMmioPlan"),
            (typeof(VmProcessorService), "CanTogglePerfmonPmu"), (typeof(VmProcessorService), "CanTogglePerfmonDependent"),
            (typeof(VmProcessorService), "CanEditOfflineSettings")
        }) Assert.IsFalse(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Any(item => item.Name == method), type.Name + "." + method);
        Assert.IsNull(typeof(CpuAffinityService.AffinityState).GetProperty("ProcessId"));
        Assert.IsNull(typeof(CpuAffinityService.AffinityState).GetProperty("ProcessCreationTime"));
        Assert.IsNull(typeof(VmMemorySettings).GetProperty("AvailablePageSizes"));
        Assert.IsNull(typeof(VmInstance).GetProperty("StateText"));
        Assert.IsNull(typeof(VmInstance).GetProperty("TotalDiskSizeGiB"));
        foreach (var property in new[] { "CanTogglePerfmonPmu", "CanTogglePerfmonPebs", "CanTogglePerfmonIpt" })
            Assert.IsNull(typeof(VmProcessorSettings).GetProperty(property), property);
        Assert.IsFalse(VmQueryFields.All.Any(field => field.Id is "Affinity.ProcessId" or "Affinity.ProcessCreationTime"));
        Assert.IsNull(typeof(VmGpuPerformanceFields).GetField("IsDriverBound"));
        Assert.IsTrue(VmQueryFields.All.Any(field => field.Id == "GpuPerformance.HasGpuEngineInstance"));
    }

    [TestMethod]
    public void ProviderFilePathsResolveWithoutGuessingTheFileName()
    {
        Assert.AreEqual(@"D:\VMs\example\Virtual Machines\actual-name.vmcx",
            VmConfigurationPaths.Resolve(@"Virtual Machines\actual-name.vmcx", @"D:\VMs\example"));
        Assert.AreEqual(@"E:\different\settings.vmcx", VmConfigurationPaths.Resolve(@"E:\different\settings.vmcx", null));
        foreach (var file in new[] { "", ".", "..", "Virtual Machines\\", @"\unknown.vmcx", @"D:unknown.vmcx" })
            Assert.ThrowsExactly<InvalidDataException>(() => VmConfigurationPaths.Resolve(file, @"D:\VMs"), file);
        Assert.ThrowsExactly<InvalidDataException>(() => VmConfigurationPaths.Resolve("settings.vmcx", null));
        Assert.ThrowsExactly<InvalidDataException>(() => VmConfigurationPaths.Resolve("settings.vmcx", "relative-root"));
    }

    [TestMethod]
    public void InternalInputHelpersRetainTheirBehavior()
    {
        CollectionAssert.AreEqual(new[] { 1, 2, 4, 6 }, VmProcessorService.GetProcessorCountOptions(6).ToArray());
        foreach (var value in new[] { "", "0", "-2", "invalid" }) Assert.AreEqual(128, VmStorageService.GetNewDiskSize(value));
        Assert.AreEqual(64, VmStorageService.GetNewDiskSize("64"));
        Assert.AreEqual("00ABCD123456", HostNetworkService.NormalizeDynamicMacAddress("00:ab:cd:12:34:56"));
        Assert.IsNull(HostNetworkService.NormalizeDynamicMacAddress("not-a-mac"));
        var memory = new VmMemorySettings();
        memory.ConfigurePageSizeSupport(false);
        Assert.IsFalse(memory.SupportsBackingPageSize);
        memory.ConfigurePageSizeSupport(true);
        Assert.IsTrue(memory.SupportsBackingPageSize);
    }
}
