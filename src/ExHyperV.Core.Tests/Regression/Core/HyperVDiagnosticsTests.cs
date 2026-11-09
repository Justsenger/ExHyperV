using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ExHyperV.Core.Tests;

[TestClass]
[TestCategory("Regression")]
public sealed class HyperVDiagnosticsTests
{
    [TestMethod]
    [DataRow(false, HyperVCheckState.NotReady)]
    [DataRow(null, HyperVCheckState.Unknown)]
    public async Task FirstIssueStopsLaterQueries(bool? active, HyperVCheckState expected)
    {
        var backend = new DiagnosticsBackend { Hypervisor = () => Task.FromResult(active) };
        var result = await HyperVEnvironmentInspector.GetAsync(backend, TimeSpan.FromSeconds(1));
        Assert.AreEqual(expected, result.Hypervisor.State);
        Assert.AreEqual(HyperVCheckState.NotChecked, result.Vmms.State);
        Assert.AreEqual(HyperVCheckState.NotChecked, result.Wmi.State);
        Assert.AreEqual(0, backend.ServiceReads);
        Assert.AreEqual(0, backend.WmiReads);
        Assert.IsNull(result.IsInstalled);
        Assert.AreSame(result.Hypervisor, result.FirstIssue);
    }

    [TestMethod]
    public async Task DeniedServiceQueryPreservesUnknownAndOriginalError()
    {
        var backend = new DiagnosticsBackend { Service = () => Task.FromException<VmmsServiceInfo?>(new UnauthorizedAccessException("denied")) };
        var result = await HyperVEnvironmentInspector.GetAsync(backend, TimeSpan.FromSeconds(1));
        Assert.AreEqual(HyperVCheckState.Unknown, result.Vmms.State);
        Assert.AreEqual(HyperVDiagnosticKind.AccessDenied, result.Vmms.DiagnosticKind);
        Assert.AreEqual(ApiErrorKind.AccessDenied, result.Vmms.Failure!.Kind);
        Assert.IsNull(result.IsInstalled);
        Assert.AreEqual(0, backend.WmiReads);
    }

    [TestMethod]
    public async Task DiagnosticTimeoutLeavesUnderlyingReadAlive()
    {
        var pending = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new DiagnosticsBackend { Hypervisor = () => pending.Task };
        try
        {
            var result = await HyperVEnvironmentInspector.GetAsync(backend, TimeSpan.FromMilliseconds(20));
            Assert.AreEqual(HyperVDiagnosticKind.Timeout, result.Hypervisor.DiagnosticKind);
            Assert.IsFalse(pending.Task.IsCompleted);
            Assert.AreEqual(0, backend.ServiceReads);
        }
        finally { pending.TrySetResult(true); }
    }

    [TestMethod]
    public async Task SharedReadReusesOnlyPendingWork()
    {
        var flight = new HyperVEnvironmentInspector.SingleFlight<bool>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = flight.Run(() => { entered.SetResult(); return pending.Task; });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSame(first, flight.Run(() => throw new AssertFailedException("同一在途读取被重复提交")));
        }
        finally { pending.TrySetResult(true); }
        Assert.IsTrue(await first);
        Assert.IsFalse(await flight.Run(() => Task.FromResult(false)));
    }

    [TestMethod]
    public async Task FeatureFailureKeepsRestartRequirementAndPreventsBootWrite()
    {
        var backend = new ConfigurationBackend
        {
            Features = ApiResponse.Fail("component failure", kind: ApiErrorKind.OperationFailed)
                .WithRestartReason(RestartReason.PackageRegistration)
        };
        var result = await HyperVHostService.SetHyperVEnabledAsync(true, backend);
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.RequiresRestart);
        Assert.AreEqual(0, backend.BootReads);
        Assert.AreEqual(0, backend.BootWrites);
    }

    [TestMethod]
    public async Task DisablingChangesBootConfigurationAndRetainsInstalledFeatures()
    {
        var backend = new ConfigurationBackend { Launch = 1, Active = true };
        var result = await HyperVHostService.SetHyperVEnabledAsync(false, backend);
        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.RequiresRestart);
        Assert.AreEqual(0, backend.FeatureCalls);
        Assert.AreEqual(1, backend.BootWrites);
        Assert.AreEqual(false, backend.LastWrite);
        Assert.AreEqual(0, backend.DiagnosticsCalls);
    }

    [TestMethod]
    public async Task AlreadyDisabledBootStillRequiresRestartWhileHypervisorRuns()
    {
        var backend = new ConfigurationBackend { Launch = 0, Active = true };
        var result = await HyperVHostService.SetHyperVEnabledAsync(false, backend);
        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.RequiresRestart);
        Assert.AreEqual(0, backend.BootWrites);
        Assert.AreEqual(0, backend.FeatureCalls);
    }

    [TestMethod]
    public async Task UnknownFastProbeUsesDiagnosticsBeforeDecidingRestart()
    {
        var backend = new ConfigurationBackend { Launch = 0, Active = null };
        var result = await HyperVHostService.SetHyperVEnabledAsync(false, backend);
        Assert.IsTrue(result.Success);
        Assert.IsTrue(result.RequiresRestart);
        Assert.AreEqual(1, backend.DiagnosticsCalls);
        Assert.AreEqual(0, backend.BootWrites);
    }

    [TestMethod]
    public async Task UnrecognizedBootValueIsKeptIntact()
    {
        var backend = new ConfigurationBackend { Launch = 99 };
        var result = await HyperVHostService.SetHyperVEnabledAsync(false, backend);
        Assert.AreEqual(ApiErrorKind.InvalidState, result.ErrorKind);
        Assert.AreEqual(0, backend.BootWrites);
        Assert.IsFalse(result.RequiresRestart);
    }

    private sealed class DiagnosticsBackend : HyperVEnvironmentInspector.IBackend
    {
        internal Func<Task<bool?>> Hypervisor = () => Task.FromResult<bool?>(true);
        internal Func<Task<VmmsServiceInfo?>> Service = () => Task.FromResult<VmmsServiceInfo?>(
            new("Running", "Auto", 0, 0, 42, null, new(VmmsImageState.NotChecked, null)));
        internal int ServiceReads, WmiReads;
        public Task<bool?> ReadHypervisorAsync() => Hypervisor();
        public Task<VmmsServiceInfo?> ReadVmmsAsync() { ServiceReads++; return Service(); }
        public Task<bool> ReadWmiAsync() { WmiReads++; return Task.FromResult(true); }
    }

    private sealed class ConfigurationBackend : HyperVHostService.IConfigurationBackend
    {
        internal ApiResponse Features = ApiResponse.Ok();
        internal ulong Launch = 1;
        internal bool? Active = true, LastWrite;
        internal int FeatureCalls, BootReads, BootWrites, DiagnosticsCalls;
        public Task<ApiResponse> EnableFeaturesAsync() { FeatureCalls++; return Task.FromResult(Features); }
        public ApiResponse<ulong> ReadLaunchType() { BootReads++; return ApiResponse<ulong>.Ok(Launch); }
        public Task<ApiResponse> WriteLaunchTypeAsync(bool enabled) { BootWrites++; LastWrite = enabled; return Task.FromResult(ApiResponse.Ok()); }
        public bool? ReadHypervisorState() => Active;
        public Task<HyperVEnvironmentStatus> GetDiagnosticsAsync()
        {
            DiagnosticsCalls++;
            return HyperVEnvironmentInspector.GetAsync(new DiagnosticsBackend(), TimeSpan.FromSeconds(1));
        }
    }
}
