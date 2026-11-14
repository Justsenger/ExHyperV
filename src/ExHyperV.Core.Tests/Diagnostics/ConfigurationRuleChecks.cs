using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using R = ExHyperV.Properties.Resources;

internal static class ConfigurationRuleChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        check(VmProcessorService.GetProcessorCountOptions(12).SequenceEqual(new[] { 1, 2, 4, 8, 12 }),
            "CPU capabilities: non-power-of-two host count remains selectable");
        check(VmProcessorService.GetProcessorCountOptions(8).SequenceEqual(new[] { 1, 2, 4, 8 }),
            "CPU capabilities: the maximum is not duplicated");
        var intel = VmProcessorService.GetHostCapabilities(HostProcessorPlatform.Intel, true, new[] { "TDX" });
        var arm = VmProcessorService.GetHostCapabilities(HostProcessorPlatform.Arm64, true, new[] { "RME" });
        check(intel.IsIntel && intel.IsX64 && intel.HardwareIsolationSupported && arm.IsArm64 && !arm.HardwareIsolationSupported,
            "CPU capabilities: preserve Intel/ARM visibility and SNP/TDX-only hardware-isolation gate");
        var before = new VmProcessorSettings { Count = 4, Limit = 100000, EnablePerfmonPmu = false, EnablePerfmonPebs = false };
        var after = new VmProcessorSettings { Count = 8, Limit = 100000, EnablePerfmonPmu = false, EnablePerfmonPebs = false };
        check(VmProcessorService.GetChangeError(before, after, false, intel) == R.Warn_VmOffRequired,
            "CPU rules: processor-count changes require fully powered off state");
        foreach (var state in Enum.GetValues<VmState>())
        {
            var validation = VmProcessorService.ValidateProcessorChange(before, after, state, intel);
            check(state == VmState.PoweredOff ? validation.Success
                    : !validation.Success && validation.ErrorKind == ApiErrorKind.InvalidState && validation.Error == R.Warn_VmOffRequired,
                $"CPU write validation: {state} is checked inside Core before changing processor count");
        }
        after.Count = 4;
        after.Limit = 70000;
        check(VmProcessorService.GetChangeError(before, after, false, intel) == null,
            "CPU rules: live CPU-limit changes remain allowed");
        check(VmProcessorService.ValidateProcessorChange(before, after, VmState.Running, intel).Success,
            "CPU write validation: a runtime CPU-limit change does not require shutdown");
        after.EnablePerfmonPebs = true;
        check(VmProcessorService.GetChangeError(before, after, true, intel) == R.Cpu_PerfmonPmuRequired,
            "CPU rules: creating a PEBS dependency without PMU is rejected");
        check(VmProcessorService.ValidateProcessorChange(before, after, VmState.PoweredOff, intel).ErrorKind == ApiErrorKind.InvalidArgument,
            "CPU write validation: an invalid PMU combination is not reported as a power-state problem");
        before.EnablePerfmonPebs = true;
        before.EnablePerfmonIpt = true;
        after.EnablePerfmonIpt = false;
        check(VmProcessorService.GetChangeError(before, after, true, intel) == null,
            "CPU rules: repairing a legacy broken PMU combination remains allowed");
        var monitoring = new VmProcessorSettings { EnablePerfmonPmu = true, EnablePerfmonPebs = true, EnablePerfmonIpt = true };
        check(VmProcessorService.GetChangeError(monitoring,
                new() { EnablePerfmonPmu = false, EnablePerfmonPebs = true, EnablePerfmonIpt = true }, true, intel) == R.Cpu_PerfmonPmuRequired,
            "CPU rules: disabling PMU alone cannot leave active dependents");
        var disabled = new VmProcessorSettings { EnablePerfmonPmu = false, EnablePerfmonPebs = false, EnablePerfmonIpt = false };
        check(VmProcessorService.GetChangeError(monitoring, disabled, true, intel) == null
            && VmProcessorService.GetChangeError(disabled, monitoring, true, intel) == null,
            "CPU rules: a complete PMU/PEBS/IPT change is validated as one configuration");
        check(VmProcessorService.GetChangeError(new(), new() { CpuBrandString = "Custom" }, true, arm) == R.Error_VerNotSupport,
            "CPU rules: x64-only CPU-brand changes cannot bypass the platform capability");
        check(VmProcessorService.ValidateProcessorChange(new(), new() { CpuBrandString = "Custom" }, VmState.PoweredOff, arm).ErrorKind == ApiErrorKind.Unsupported,
            "CPU write validation: an unsupported platform is distinguishable from a shutdown requirement");
        check(VmProcessorService.GetChangeError(new() { CpuBrandString = "" }, new() { CpuBrandString = null }, false, arm) == null,
            "CPU rules: empty/null brand roundtrips retain native compatibility");

        var memory = new VmMemorySettings { Startup = 2048, DynamicMemoryEnabled = false, Weight = 5000 };
        var changed = memory.Clone();
        changed.Startup = 4096;
        check(VmMemoryService.GetChangeError(memory, changed, true) == null,
            "Memory rules: static startup allocation remains adjustable while running");
        memory.DynamicMemoryEnabled = changed.DynamicMemoryEnabled = true;
        check(VmMemoryService.GetChangeError(memory, changed, true) == R.Warn_DynRamRunning,
            "Memory rules: dynamic startup allocation is locked while running");
        changed.Startup = memory.Startup;
        changed.Maximum = 8192;
        check(VmMemoryService.GetChangeError(memory, changed, true) == null,
            "Memory rules: dynamic limits remain adjustable while running");
        changed.BackingPageSize = 2;
        check(VmMemoryService.GetChangeError(memory, changed, true) == R.Warn_VmOffRequired,
            "Memory rules: a full request cannot silently include offline-only page-size changes");
        check(VmMemoryService.GetChangeError(memory, memory.Clone(), true, nameof(VmMemorySettings.BackingType)) == R.Warn_VmOffRequired,
            "Memory rules: automatic offline-only writes are rejected independently of caller state hints");
        check(!VmMemoryService.GetCapabilities(true, true).Startup
            && VmMemoryService.GetCapabilities(true, true).DynamicLimits
            && !VmMmioService.CanEdit(true) && VmMmioService.CanEdit(false),
            "Memory/MMIO capabilities: presentation and service share the original runtime gates");

        var unshielded = new VmSecurityService.VmSecurityInfo(true, "template", true, false, false);
        var shielded = unshielded with { Shielded = true };
        check(VmSecurityService.GetSettingError(2, VmState.PoweredOff, unshielded, VmSecurityService.Setting.SecureBootTemplate) == null,
            "Security rules: TPM presence does not invent a secure-boot-template lock");
        check(VmSecurityService.GetSettingError(2, VmState.PoweredOff, shielded, VmSecurityService.Setting.Tpm) != null
            && VmSecurityService.GetSettingError(2, VmState.PoweredOff, shielded, VmSecurityService.Setting.Shielding) == null,
            "Security rules: shielding locks dependent toggles but allows changing shielding itself");
        check(!VmSecurityService.CanEdit(1, VmState.PoweredOff) && !VmSecurityService.CanEdit(2, VmState.Running)
            && VmSecurityService.GetSettingError(2, VmState.PoweredOff, unshielded with { TpmEnabled = false }, VmSecurityService.Setting.Encryption) != null,
            "Security rules: generation, runtime, and TPM prerequisites are enforced");
        check(!VmConsoleSupportService.CanToggleConsoleSupport(1, false) && VmConsoleSupportService.CanToggleConsoleSupport(2, false)
            && !VmConsoleSupportService.CanToggleConsoleSupport(2, true),
            "Console rules: only offline generation-2 console devices can be changed");
        check(!VmBootService.CanChangeBootOrder(1, true) && VmBootService.CanChangeBootOrder(1, false)
            && VmBootService.CanChangeBootOrder(2, true) && !VmDisplaySettingsService.CanEdit(true),
            "Boot/video rules: retain generation-2 live boot reorder and offline video edits");
        check(!VmNetworkService.CanEditMacAddress(true) && VmNetworkService.CanEditMacAddress(false),
            "Network rules: GUI and static/dynamic MAC write entry share the non-running requirement");
        check(VmPowerService.CanEditHibernation(true, false) && !VmPowerService.CanEditHibernation(true, true) && !VmPowerService.CanEditHibernation(false, false),
            "Hibernation requires an available field and an inactive VM");

        await CheckAffinityAsync(check);
        var firstFailure = await VmNetworkService.ApplySecurityAndOffloadSettingsWithResultAsync(
            () => Task.FromResult(ApiResponse.Fail("security failed")),
            () => throw new Exception("offload must not run after a security failure"));
        check(!firstFailure.Success && !firstFailure.SecurityApplied && !firstFailure.OffloadApplied,
            "Network composite: security failure reports no completed step and prevents offload");
        var partial = await VmNetworkService.ApplySecurityAndOffloadSettingsWithResultAsync(
            () => Task.FromResult(ApiResponse.Ok()), () => Task.FromResult(ApiResponse.Fail("offload failed")));
        check(!partial.Success && partial.SecurityApplied && !partial.OffloadApplied,
            "Network composite: offload failure explicitly preserves the completed security step");
        var complete = await VmNetworkService.ApplySecurityAndOffloadSettingsWithResultAsync(
            () => Task.FromResult(ApiResponse.Ok()), () => Task.FromResult(ApiResponse.Ok()));
        check(complete.Success && complete.SecurityApplied && complete.OffloadApplied,
            "Network composite: success reports both completed steps");
    }

    private static async Task CheckAffinityAsync(Action<bool, string> check)
    {
        string? persisted = null;
        int writes = 0;
        Task<bool> Persist(string updated) { persisted = updated; writes++; return Task.FromResult(true); }
        var queued = await CpuAffinityService.SaveAsync(new() { 1, 3 }, false, HyperVSchedulerType.Root,
            () => Task.FromResult(false), Persist);
        check(queued.Status == CpuAffinityService.SaveStatus.Queued && queued.MetadataSaved && writes == 1
            && persisted == "[1,3]",
            "Affinity save: offline Root selections are queued and stored as metadata JSON");
        string savedAffinity = persisted!;
        var failed = await CpuAffinityService.SaveAsync(new() { 2 }, true, HyperVSchedulerType.Root,
            () => Task.FromResult(false), Persist);
        check(failed.Status == CpuAffinityService.SaveStatus.Failed && !failed.MetadataSaved && persisted == savedAffinity && writes == 1,
            "Affinity save: runtime application failure does not overwrite saved selection");
        var cleared = await CpuAffinityService.SaveAsync(new(), true, HyperVSchedulerType.Root,
            () => Task.FromResult(true), Persist);
        check(cleared.Status == CpuAffinityService.SaveStatus.Applied && cleared.MetadataSaved && persisted == "[]",
            "Affinity save: clearing an applied affinity clears its saved startup constraint");
        var saveFailure = await CpuAffinityService.SaveAsync(new() { 0 }, true, HyperVSchedulerType.Root,
            () => Task.FromResult(true), _ => Task.FromResult(false));
        check(saveFailure.Status == CpuAffinityService.SaveStatus.Applied && !saveFailure.MetadataSaved,
            "Affinity save: result exposes persistence failure separately from application success");

        var delays = new List<TimeSpan>();
        int attempts = 0;
        Task Wait(TimeSpan delay, CancellationToken _) { delays.Add(delay); return Task.CompletedTask; }
        var legacy = await CpuAffinityService.ApplySavedAffinityAfterStartAsync(savedAffinity, HyperVSchedulerType.Root,
            () => throw new Exception("legacy metadata cannot read runtime state"),
            _ => { attempts++; return Task.FromResult(true); }, Wait);
        check(legacy == CpuAffinityService.StartupApplyStatus.ReselectionRequired && attempts == 0 && delays.Count == 0,
            "Affinity startup requires reselection when saved cores have no verified host topology");
        var invalid = await CpuAffinityService.ApplySavedAffinityAfterStartAsync("[x]", HyperVSchedulerType.Root,
            () => throw new Exception("must not query"), _ => throw new Exception("must not apply"), Wait);
        check(invalid == CpuAffinityService.StartupApplyStatus.InvalidConfiguration,
            "Affinity startup: malformed saved metadata prevents hardware operations");
        var skipped = await CpuAffinityService.ApplySavedAffinityAfterStartAsync("[]", HyperVSchedulerType.Root,
            () => throw new Exception("must not query"), _ => throw new Exception("must not apply"), Wait);
        check(skipped == CpuAffinityService.StartupApplyStatus.NotRequired,
            "Affinity startup: empty selection schedules no operation");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool canceled = false;
        try
        {
            await CpuAffinityService.ApplySavedAffinityAfterStartAsync(savedAffinity, HyperVSchedulerType.Root,
                () => throw new Exception("must not query"), _ => throw new Exception("must not apply"), Wait, cancellation.Token);
        }
        catch (OperationCanceledException) { canceled = true; }
        check(canceled, "Affinity startup: callers can cancel the finite operation before any wait or write");
    }
}
