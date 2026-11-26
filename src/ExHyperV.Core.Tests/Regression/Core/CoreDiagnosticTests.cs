using System.ComponentModel;
using System.Management;
using System.Runtime.InteropServices;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests.Regression.Core;

[TestClass]
public class CoreDiagnosticTests
{
    [TestMethod]
    public void Classifier_IdentifiesServiceShutdownAndRpcUnavailableAcrossCultures()
    {
        Assert.IsTrue(CoreErrorClassifier.IsServiceUnavailable((int)ManagementStatus.ShuttingDown, ApiErrorSource.Wmi));
        Assert.IsTrue(CoreErrorClassifier.IsServiceUnavailable(unchecked((int)0x800706BA), ApiErrorSource.Com));
        Assert.IsTrue(CoreErrorClassifier.IsServiceUnavailable(1062, ApiErrorSource.Win32));
        Assert.IsTrue(CoreErrorClassifier.IsServiceUnavailable(1722, ApiErrorSource.Win32));

        var wrapped = new InvalidOperationException("outer", new COMException("rpc down", unchecked((int)0x8007045B)));
        Assert.IsTrue(CoreErrorClassifier.IsServiceUnavailable(-1, ApiErrorSource.None, wrapped));
        Assert.AreEqual(CoreErrorCode.VmmsUnavailableOrShuttingDown, CoreErrorClassifier.ClassifyException(wrapped));
    }

    [TestMethod]
    public void Classifier_SeparatesWmiInvalidState5FromWin32AccessDenied5()
    {
        var wmiState5 = ApiResponse.Fail("Invalid state", 5, ApiErrorSource.Wmi, kind: ApiErrorKind.InvalidState);
        Assert.AreEqual(CoreErrorCode.InvalidState, wmiState5.DiagnosticCode);

        var win32Denied5 = ApiResponse.Fail("Access denied", 5, ApiErrorSource.Win32);
        Assert.AreEqual(CoreErrorCode.AccessDenied, win32Denied5.DiagnosticCode);

        var comDenied = new UnauthorizedAccessException();
        Assert.AreEqual(CoreErrorCode.AccessDenied, CoreErrorClassifier.ClassifyException(comDenied));

        var shuttingDown = ApiResponse.Fail("Shutting down", (int)ManagementStatus.ShuttingDown, ApiErrorSource.Wmi);
        Assert.AreEqual(CoreErrorCode.VmmsUnavailableOrShuttingDown, shuttingDown.DiagnosticCode);
        Assert.AreEqual(CoreErrorCode.OperationTimedOut, ApiResponse.Fail("Timed out", 32772, ApiErrorSource.Wmi).DiagnosticCode);
        Assert.AreEqual(CoreErrorCode.Unsupported, ApiResponse.Fail("Not supported", 32770, ApiErrorSource.Wmi, kind: ApiErrorKind.Unsupported).DiagnosticCode);
    }

    [TestMethod]
    public void Classifier_MapsStandardExceptions()
    {
        Assert.AreEqual(CoreErrorCode.OperationCancelled, CoreErrorClassifier.ClassifyException(new OperationCanceledException()));
        Assert.AreEqual(CoreErrorCode.VmmsUnavailableOrShuttingDown, CoreErrorClassifier.ClassifyException(new Win32Exception(1722)));
    }

    [TestMethod]
    public void Classifier_ExtractsLanguageNeutralEmbeddedHResultsFromWmiDescriptions()
    {
        var outOfMem = ApiResponse.Fail(
            "\"test\"无法初始化内存: 内存资源不足，无法完成此操作。(0x8007000E)。",
            32768,
            ApiErrorSource.Wmi,
            kind: ApiErrorKind.OperationFailed);
        Assert.AreEqual(CoreErrorCode.InsufficientMemory, outOfMem.DiagnosticCode);

        var notSupported = ApiResponse.Fail(
            "\"test\"无法启动工作进程: The request is not supported.  (0x80070032)。",
            32768,
            ApiErrorSource.Wmi);
        Assert.AreEqual(CoreErrorCode.Unsupported, notSupported.DiagnosticCode);

        Assert.AreEqual(CoreErrorCode.FileInUse, ApiResponse.Fail("Disk locked (0x80070020)", 32768, ApiErrorSource.Wmi).DiagnosticCode);
        Assert.AreEqual(CoreErrorCode.FileNotFound, ApiResponse.Fail("Missing vhdx (0x80070002)", 32768, ApiErrorSource.Wmi).DiagnosticCode);
        Assert.AreEqual(CoreErrorCode.DiskFull, ApiResponse.Fail("No space (0x80070070)", 32768, ApiErrorSource.Wmi).DiagnosticCode);
    }

    [TestMethod]
    public void MemorySgx_AllowsDisablingWhenAlreadyEnabledOnNonIntelAndBlocksReEnabling()
    {
        var nonIntelHost = new VmProcessorService.HostCapabilities(IsIntel: false, IsArm64: true, HardwareIsolationSupported: false);
        var intelHost = new VmProcessorService.HostCapabilities(IsIntel: true, IsArm64: false, HardwareIsolationSupported: false);

        var currentlyOff = new VmMemorySettings
        {
            Startup = 2048,
            SgxEnabled = false,
            SupportedProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "VirtualQuantity", "SgxEnabled" }
        };
        var enableAttempt = new VmMemorySettings { Startup = 2048, SgxEnabled = true };
        var blockedOnNonIntel = VmMemoryService.ValidateMemoryChange(currentlyOff, enableAttempt, VmState.PoweredOff, host: nonIntelHost);
        Assert.IsFalse(blockedOnNonIntel.Success);

        var allowedOnIntel = VmMemoryService.ValidateMemoryChange(currentlyOff, enableAttempt, VmState.PoweredOff, host: intelHost);
        Assert.IsTrue(allowedOnIntel.Success);

        var currentlyOn = new VmMemorySettings
        {
            Startup = 2048,
            SgxEnabled = true,
            SupportedProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "VirtualQuantity", "SgxEnabled" }
        };
        var disableAttempt = new VmMemorySettings { Startup = 2048, SgxEnabled = false };
        var escapeDisableAllowed = VmMemoryService.ValidateMemoryChange(currentlyOn, disableAttempt, VmState.PoweredOff, host: nonIntelHost);
        Assert.IsTrue(escapeDisableAllowed.Success);
    }

    [TestMethod]
    public async Task PowerWorkflow_RetainsDiagnosedStartErrorsWithoutStaleDeviceProbes()
    {
        var failures = new List<string>();
        await PowerWorkflowChecks.Run((ok, name) => { if (!ok) failures.Add(name); });
        Assert.AreEqual(0, failures.Count, string.Join(", ", failures));

        var vmId = Guid.NewGuid();
        var backend = new PowerWorkflowChecks.Backend();
        backend.Gpus.Add(new VmGpuRepairService.StaleGpuPartition(vmId, 1, Guid.NewGuid().ToString("D"), "old", null));
        backend.Disks.Add(new VmStorageAttachment { DiskNumber = 1 });
        backend.Execute = () => Task.FromResult(ApiResponse.Fail(
            "Insufficient host memory (0x8007000E)",
            32768,
            ApiErrorSource.Wmi,
            kind: ApiErrorKind.OperationFailed));

        var session = new VmPowerOperationSession(vmId, VmPowerAction.Start, backend, "test");
        await session.AdvanceAsync();

        Assert.AreEqual(VmPowerOperationStage.Failed, session.Stage);
        Assert.AreEqual(CoreErrorCode.InsufficientMemory, session.Failure?.DiagnosticCode);
        Assert.AreEqual(0, backend.GpuQueries);
        Assert.AreEqual(0, backend.DiskQueries);
    }


    [TestMethod]
    public void ProcessorConditions_ReturnAccurateReasonCodesInsteadOfUnsupportedVersion()
    {
        var settings = new VmProcessorSettings
        {
            Count = 2,
            SmtMode = SmtMode.Inherit,
            ApicMode = VmApicMode.Default,
            LpiMode = LpiMode.Default,
            PageShatterMode = PageShatterMode.Default,
            EnablePerfmonPmu = false,
            EnablePerfmonPebs = false,
            EnablePerfmonIpt = false,
            HardwareIsolationExtensionsEnabled = false,
            SupportedProps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ApicMode",
                "LpiMode",
                "EnablePerfmonPmu",
                "EnablePerfmonPebs",
                "ExtendedVirtualizationExtensions",
                "VirtualQuantity"
            },
        };

        var armHost = new VmProcessorService.HostCapabilities(IsIntel: false, IsArm64: true, HardwareIsolationSupported: false);
        var intelHost = new VmProcessorService.HostCapabilities(IsIntel: true, IsArm64: false, HardwareIsolationSupported: false);
        var amdHost = new VmProcessorService.HostCapabilities(IsIntel: false, IsArm64: false, HardwareIsolationSupported: false);

        var apicOnArm = VmProcessorService.GetModificationCondition(settings, nameof(VmProcessorSettings.ApicMode), VmState.PoweredOff, armHost);
        Assert.IsFalse(apicOnArm.CanModify);
        Assert.AreEqual(ConditionReasonCode.RequiresX64, apicOnArm.ReasonCode);

        var lpiOnIntel = VmProcessorService.GetModificationCondition(settings, nameof(VmProcessorSettings.LpiMode), VmState.PoweredOff, intelHost);
        Assert.IsFalse(lpiOnIntel.CanModify);
        Assert.AreEqual(ConditionReasonCode.RequiresArm64, lpiOnIntel.ReasonCode);

        var pmuOnAmd = VmProcessorService.GetModificationCondition(settings, nameof(VmProcessorSettings.EnablePerfmonPmu), VmState.PoweredOff, amdHost);
        Assert.IsFalse(pmuOnAmd.CanModify);
        Assert.AreEqual(ConditionReasonCode.RequiresIntel, pmuOnAmd.ReasonCode);

        var hwIso = VmProcessorService.GetModificationCondition(settings, nameof(VmProcessorSettings.HardwareIsolationExtensionsEnabled), VmState.PoweredOff, amdHost);
        Assert.IsFalse(hwIso.CanModify);
        Assert.AreEqual(ConditionReasonCode.RequiresHardwareIsolation, hwIso.ReasonCode);

        var pebsWithoutPmu = VmProcessorService.GetModificationCondition(settings, nameof(VmProcessorSettings.EnablePerfmonPebs), VmState.PoweredOff, intelHost);
        Assert.IsFalse(pebsWithoutPmu.CanModify);
        Assert.AreEqual(ConditionReasonCode.RequiresPerfmonPmu, pebsWithoutPmu.ReasonCode);

        var countWhileRunning = VmProcessorService.GetModificationCondition(settings, nameof(VmProcessorSettings.Count), VmState.Running, intelHost);
        Assert.IsFalse(countWhileRunning.CanModify);
        Assert.AreEqual(ConditionReasonCode.PoweredOffRequired, countWhileRunning.ReasonCode);

        var missingFreq = VmProcessorService.GetModificationCondition(settings, nameof(VmProcessorSettings.PerfCpuFreqCapMhz), VmState.PoweredOff, intelHost);
        Assert.IsFalse(missingFreq.CanModify);
        Assert.AreEqual(ConditionReasonCode.HostSettingMissing, missingFreq.ReasonCode);
    }

    [TestMethod]
    public void Classifier_AppliesEmpiricalFaultMatrixSignaturesWithoutCrossMisclassification()
    {
        // NET-01: Missing virtual switch emits WMI 32788 + 0x800705AA; must NOT be misclassified as InsufficientMemory.
        var net01 = ApiResponse.Fail(
            "\"vm\"在连接到虚拟网络时，无法分配资源: 系统资源不足，无法完成请求的服务。 (0x800705AA) 。以太网交换机可能不存在。",
            32788,
            ApiErrorSource.Wmi,
            kind: ApiErrorKind.OperationFailed);
        Assert.AreEqual(CoreErrorCode.VirtualSwitchNotFound, net01.DiagnosticCode);

        // MEM-01 / GPU-03: Insufficient host RAM emits WMI 32778 + 0x8007000E.
        var mem01 = ApiResponse.Fail(
            "系统内存不足，无法启动虚拟机 vm，需要内存大小 32768 MB。 (0x8007000E)。",
            32778,
            ApiErrorSource.Wmi,
            kind: ApiErrorKind.OperationFailed);
        Assert.AreEqual(CoreErrorCode.InsufficientMemory, mem01.DiagnosticCode);

        // MEM-09: SGX/unsupported hardware feature emits WMI 32768 + 0xC0350005.
        var mem09 = ApiResponse.Fail(
            "\"vm\"无法启动工作进程: 虚拟机监控程序无法执行此操作，因为指定的参数无效。 (0xC0350005)。",
            32768,
            ApiErrorSource.Wmi,
            kind: ApiErrorKind.OperationFailed);
        Assert.AreEqual(CoreErrorCode.Unsupported, mem09.DiagnosticCode);

        // STO-05: Missing differencing parent emits WMI 32779 + 0xC03A000D (must beat 0x80070002 / generic FileNotFound).
        var sto05 = ApiResponse.Fail(
            "虚拟硬盘的链已断开。系统无法找到差异磁盘的父级虚拟硬盘。(0xC03A000D) (0x80070002)",
            32779,
            ApiErrorSource.Wmi,
            kind: ApiErrorKind.OperationFailed);
        Assert.AreEqual(CoreErrorCode.DifferencingParentNotFound, sto05.DiagnosticCode);

        // BOOT-02 / STO-01 / STO-02 / STO-03 language-neutral numeric WMI fallbacks without English/Chinese text dependence.
        Assert.AreEqual(CoreErrorCode.NotFound, ApiResponse.Fail("Synchronous 0x80070490", 32771, ApiErrorSource.Wmi).DiagnosticCode);
        Assert.AreEqual(CoreErrorCode.FileNotFound, ApiResponse.Fail("missing", 32779, ApiErrorSource.Wmi).DiagnosticCode);
        Assert.AreEqual(CoreErrorCode.FileInUse, ApiResponse.Fail("locked", 32774, ApiErrorSource.Wmi).DiagnosticCode);
        Assert.AreEqual(CoreErrorCode.DiskFull, ApiResponse.Fail("full", 32794, ApiErrorSource.Wmi).DiagnosticCode);
    }

    [TestMethod]
    public async Task ProcessorValidationAndPowerSession_MatchFaultMatrixContracts()
    {
        var intelHost = new VmProcessorService.HostCapabilities(IsIntel: true, IsArm64: false, HardwareIsolationSupported: false);
        var current = new VmProcessorSettings { Count = 4, Limit = 100000, RelativeWeight = 100, SmtMode = SmtMode.Inherit };

        var invalidSmt = VmProcessorService.ValidateProcessorChange(
            current,
            new VmProcessorSettings { Count = 4, Limit = 100000, RelativeWeight = 100, SmtMode = (SmtMode)3 },
            VmState.PoweredOff,
            intelHost);
        Assert.IsFalse(invalidSmt.Success);
        Assert.AreEqual(ApiErrorKind.InvalidArgument, invalidSmt.ErrorKind);

        var invalidLimit = VmProcessorService.ValidateProcessorChange(
            current,
            new VmProcessorSettings { Count = 4, Limit = 200000, RelativeWeight = 100, SmtMode = SmtMode.Inherit },
            VmState.PoweredOff,
            intelHost);
        Assert.IsFalse(invalidLimit.Success);
        Assert.AreEqual(ApiErrorKind.InvalidArgument, invalidLimit.ErrorKind);

        var vmId = Guid.NewGuid();
        var backend = new PowerWorkflowChecks.Backend();
        backend.Gpus.Add(new VmGpuRepairService.StaleGpuPartition(vmId, 0, Guid.NewGuid().ToString("D"), "old", null));
        backend.Execute = () => Task.FromResult(ApiResponse.Fail("Missing switch (0x800705AA)", 32788, ApiErrorSource.Wmi));

        var session = new VmPowerOperationSession(vmId, VmPowerAction.Start, backend, "net01");
        await session.AdvanceAsync();

        Assert.AreEqual(VmPowerOperationStage.Failed, session.Stage);
        Assert.AreEqual(CoreErrorCode.VirtualSwitchNotFound, session.Failure?.DiagnosticCode);
        Assert.AreEqual(0, backend.GpuQueries);
        Assert.AreEqual(0, backend.DiskQueries);

        var gen1VmId = Guid.NewGuid();
        var gen1Backend = new PowerWorkflowChecks.Backend();
        gen1Backend.Gpus.Add(new VmGpuRepairService.StaleGpuPartition(gen1VmId, 0, Guid.NewGuid().ToString("D"), "old", null));
        gen1Backend.Execute = () => Task.FromResult(
            ApiResponse.Fail("Firmware DLL missing (0x80070002)", 32779, ApiErrorSource.Wmi)
                .WithDiagnostic(CoreErrorCode.StartFailedGen1OnArm64));

        var gen1Session = new VmPowerOperationSession(gen1VmId, VmPowerAction.Start, gen1Backend, "arm-gen1");
        await gen1Session.AdvanceAsync();

        Assert.AreEqual(VmPowerOperationStage.Failed, gen1Session.Stage);
        Assert.AreEqual(CoreErrorCode.StartFailedGen1OnArm64, gen1Session.Failure?.DiagnosticCode);
        Assert.AreEqual(0, gen1Backend.GpuQueries);
        Assert.AreEqual(0, gen1Backend.DiskQueries);

        var armHost = new VmProcessorService.HostCapabilities(IsIntel: false, IsArm64: true, HardwareIsolationSupported: false);
        var invalidLpi = VmProcessorService.ValidateProcessorChange(
            current,
            new VmProcessorSettings { Count = 4, Limit = 100000, RelativeWeight = 100, LpiMode = (LpiMode)99 },
            VmState.PoweredOff,
            armHost);
        Assert.IsFalse(invalidLpi.Success);
        Assert.AreEqual(ApiErrorKind.InvalidArgument, invalidLpi.ErrorKind);
    }

    [TestMethod]
    public async Task HostMemoryAndCaseInsensitiveProcessorSnapshot_WorkCleanly()
    {
        var hostMem = HostSystemInfoService.TryReadHostMemoryMiB();
        Assert.IsNotNull(hostMem);
        Assert.IsTrue(hostMem.Value.TotalMiB > 0);
        Assert.IsTrue(hostMem.Value.AvailableMiB >= 0 && hostMem.Value.AvailableMiB <= hostMem.Value.TotalMiB);

        Assert.IsNull(await VmProcessorService.GetHostMaxVirtualCpuCountAsync(Guid.Empty));

        var rawMixedCase = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["VirtualQuantity"] = 4UL,
            ["Reservation"] = 0UL,
            ["Limit"] = 100000UL,
            ["Weight"] = 100U,
            ["AllowACountMCount"] = null
        };
        var snapshot = VmProcessorService.MapProcessorSnapshot(new VmProcessorSettings { Count = 4 }, rawMixedCase);
        Assert.AreEqual(VmQueryValueState.Unavailable, snapshot.States[nameof(VmProcessorSettings.AllowAcountMcount)]);

        var enriched = ApiResponse.Fail("Out of memory (0x8007000E)", 32768, ApiErrorSource.Wmi)
            .WithDiagnostic(CoreErrorCode.InsufficientMemory, "16384", "6000", "15975", "10384");
        Assert.AreEqual(CoreErrorCode.InsufficientMemory, enriched.DiagnosticCode);
        CollectionAssert.AreEqual(new[] { "16384", "6000", "15975", "10384" }, enriched.DiagnosticArgs.ToArray());

        var emptyPreview = await VmProcessorService.GetVmProcessorAsync(Guid.Empty, includePreviewSettings: true);
        Assert.IsFalse(emptyPreview.Success);
        Assert.AreEqual(ApiErrorKind.InvalidArgument, emptyPreview.ErrorKind);

        var nativeCoreTypes = HostCpuEtwNative.ReadCoreTypesByGroupBit();
        foreach (var (key, expectedType) in nativeCoreTypes)
            Assert.AreEqual(expectedType, HyperVCpuMonitorService.GetCoreType(key.Bit + key.Group * 64));
        Assert.AreEqual(CpuCoreType.Unknown, HyperVCpuMonitorService.GetCoreType(-1));
        Assert.AreEqual(CpuCoreType.Unknown, HyperVCpuMonitorService.GetCoreType(int.MaxValue));
    }
}
