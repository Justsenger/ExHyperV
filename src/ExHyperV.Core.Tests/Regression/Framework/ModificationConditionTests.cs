using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class ModificationConditionTests
{
    private static readonly VmProcessorService.HostCapabilities Intel = new(true, false, true);

    [TestMethod]
    public void OfflineProcessorConditionMatchesSubmissionForEveryPowerState()
    {
        var current = new VmProcessorSettings { Count = 4, SupportedProps = ["VirtualQuantity"] };
        var requested = current.Clone();
        requested.Count = 8;
        foreach (var state in Enum.GetValues<VmState>())
        {
            var condition = VmProcessorService.GetModificationCondition(current, nameof(current.Count), state, Intel);
            var result = VmProcessorService.ValidateProcessorChange(current, requested, state, Intel);
            Assert.AreEqual(state == VmState.PoweredOff, condition.CanModify, state.ToString());
            Assert.AreEqual(condition.CanModify, result.Success);
            Assert.AreEqual(condition.ErrorKind, result.ErrorKind);
            Assert.AreEqual(condition.Reason, result.Error);
            Assert.AreEqual(state == VmState.PoweredOff ? null : (VmState?)VmState.PoweredOff, condition.RequiredState);
        }
    }

    [TestMethod]
    public void PerfmonToggleConditionsMatchWholeRequestValidation()
    {
        foreach (bool pmu in new[] { false, true })
        foreach (bool pebs in new[] { false, true })
        foreach (bool ipt in new[] { false, true })
        foreach (string name in new[] { nameof(VmProcessorSettings.EnablePerfmonPmu), nameof(VmProcessorSettings.EnablePerfmonPebs), nameof(VmProcessorSettings.EnablePerfmonIpt) })
        {
            var current = new VmProcessorSettings { EnablePerfmonPmu = pmu, EnablePerfmonPebs = pebs, EnablePerfmonIpt = ipt,
                SupportedProps = ["EnablePerfmonPmu", "EnablePerfmonPebs", "EnablePerfmonIpt"] };
            var requested = current.Clone();
            var property = typeof(VmProcessorSettings).GetProperty(name)!;
            property.SetValue(requested, !(bool)property.GetValue(current)!);
            var condition = VmProcessorService.GetModificationCondition(current, name, VmState.PoweredOff, Intel);
            var result = VmProcessorService.ValidateProcessorChange(current, requested, VmState.PoweredOff, Intel);
            Assert.AreEqual(condition.CanModify, result.Success, $"{pmu}/{pebs}/{ipt}: {name}");
            Assert.AreEqual(condition.Reason, result.Error);
        }
    }

    [TestMethod]
    public void RuntimeAndUnsupportedProcessorSettingsAreDistinct()
    {
        var settings = new VmProcessorSettings { SupportedProps = ["Limit", "LpiMode"] };
        Assert.IsTrue(VmProcessorService.GetModificationCondition(settings, nameof(settings.Limit), VmState.Running, Intel).CanModify);
        Assert.AreEqual(ApiErrorKind.Unsupported,
            VmProcessorService.GetModificationCondition(settings, nameof(settings.LpiMode), VmState.PoweredOff, Intel).ErrorKind);
        Assert.AreEqual(ApiErrorKind.Unsupported,
            VmProcessorService.GetModificationCondition(settings, nameof(settings.Count), VmState.PoweredOff, Intel).ErrorKind);
        var clone = settings.Clone();
        clone.SupportedProps.Clear();
        Assert.AreEqual(2, settings.SupportedProps.Count);
    }

    [TestMethod]
    public void MemoryConditionsMatchWritesForStaticAndDynamicMemory()
    {
        foreach (bool dynamic in new[] { false, true })
        foreach (var state in Enum.GetValues<VmState>())
        foreach (string name in new[] { nameof(VmMemorySettings.Startup), nameof(VmMemorySettings.Weight), nameof(VmMemorySettings.DynamicMemoryEnabled) })
        {
            var current = new VmMemorySettings { Startup = 1024, Weight = 5000, DynamicMemoryEnabled = dynamic };
            var requested = current.Clone();
            switch (name)
            {
                case nameof(current.Startup): requested.Startup = 2048; break;
                case nameof(current.Weight): requested.Weight = 6000; break;
                default: requested.DynamicMemoryEnabled = !dynamic; break;
            }
            var condition = VmMemoryService.GetModificationCondition(current, name, state);
            var result = VmMemoryService.ValidateMemoryChange(current, requested, state, name);
            Assert.AreEqual(condition.CanModify, result.Success, $"{dynamic}/{state}/{name}");
            Assert.AreEqual(condition.ErrorKind, result.ErrorKind);
        }
        var missing = new VmMemorySettings { SupportedProps = ["VirtualQuantity"] };
        Assert.AreEqual(ApiErrorKind.Unsupported, VmMemoryService.GetModificationCondition(missing, nameof(missing.CxlEnabled), VmState.PoweredOff).ErrorKind);
        var clone = missing.Clone();
        clone.SupportedProps!.Clear();
        Assert.AreEqual(1, missing.SupportedProps.Count);
    }

    [TestMethod]
    public void PcieAndCompactionStayDisabledBeforeSubmission()
    {
        var pcie = new VmPcieSettings { SystemSettingsAvailable = true, BootPciExpressAvailable = true };
        var device = new VmPcieDeviceSetting { GuestModeAvailable = true };
        var disk = new VmStorageAttachment { DriveType = "HardDisk", DiskType = "Virtual", ControllerType = "IDE", PathOrDiskNumber = @"D:\VM\disk.vhdx" };
        foreach (var state in Enum.GetValues<VmState>())
        {
            var conditions = new[] {
                VmPcieService.GetModificationCondition(pcie, nameof(pcie.Topology), state),
                VmPcieService.GetDeviceModeModificationCondition(device, state),
                VmStorageService.GetCompactionCondition(disk, state),
                VmStorageService.GetRemovalCondition(disk, state),
                VmStorageService.GetMediaModificationCondition(disk, state) };
            foreach (var condition in conditions)
            {
                Assert.AreEqual(state == VmState.PoweredOff, condition.CanModify);
                if (!condition.CanModify) Assert.AreEqual(VmState.PoweredOff, condition.RequiredState);
            }
        }
        Assert.AreEqual(ApiErrorKind.Unsupported, VmPcieService.GetModificationCondition(new(), nameof(pcie.Topology), VmState.PoweredOff).ErrorKind);
        Assert.AreEqual(ApiErrorKind.Unsupported, VmStorageService.GetCompactionCondition(new(), VmState.PoweredOff).ErrorKind);
    }

    [TestMethod]
    public void GenerationAndSecurityDependenciesHaveActionableReasons()
    {
        Assert.IsTrue(VmBootService.GetBootOrderModificationCondition(2, VmState.Running).CanModify);
        Assert.IsFalse(VmBootService.GetBootOrderModificationCondition(1, VmState.Running).CanModify);
        var security = new VmSecurityService.VmSecurityInfo(false, "", false, false, false);
        var condition = VmSecurityService.GetModificationCondition(2, VmState.PoweredOff, security, VmSecurityService.Setting.Encryption);
        Assert.IsFalse(condition.CanModify);
        Assert.IsTrue(condition.Reason.Contains("TPM", StringComparison.Ordinal));
        Assert.IsNull(condition.RequiredState);
        Assert.IsTrue(VmSecurityService.GetModificationCondition(2, VmState.PoweredOff, security, VmSecurityService.Setting.Tpm).CanModify);
    }
    [TestMethod]
    public void SingleStateConditionsCoverEveryPowerState()
    {
        foreach (var state in Enum.GetValues<VmState>())
        {
            foreach (var condition in new[] {
                VmBootService.GetNumLockModificationCondition(state),
                VmDisplaySettingsService.GetModificationCondition(state),
                VmMmioService.GetModificationCondition(state),
                VmNetworkService.GetMacModificationCondition(state),
                VmConsoleSupportService.GetModificationCondition(2, state),
                VmPowerService.GetHibernationModificationCondition(true, state) })
                Assert.AreEqual(state == VmState.PoweredOff, condition.CanModify);
            Assert.AreEqual(ApiErrorKind.Unsupported, VmConsoleSupportService.GetModificationCondition(1, state).ErrorKind);
            Assert.AreEqual(ApiErrorKind.Unsupported, VmPowerService.GetHibernationModificationCondition(false, state).ErrorKind);
        }
    }

}
