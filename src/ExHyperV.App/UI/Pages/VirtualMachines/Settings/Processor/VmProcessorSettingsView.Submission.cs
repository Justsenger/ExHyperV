
using System.Diagnostics;
using ExHyperV.App.Diagnostics;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Processor;

public sealed partial class VmProcessorSettingsView
{
    private async Task UnlockAndReloadAsync()
    {
        uncertain = false;
        await RunAsync(LoadAsync);
    }

    private IReadOnlyDictionary<string, VmQueryValueState> fieldStates = new Dictionary<string, VmQueryValueState>();

    private static bool IsOptionalDefaultWhenNull(string propertyName) => propertyName is
        nameof(VmProcessorSettings.PageShatterMode) or
        nameof(VmProcessorSettings.L3CacheWays) or
        nameof(VmProcessorSettings.PerfCpuFreqMinMhz) or
        nameof(VmProcessorSettings.PerfCpuFreqDesiredMhz) or
        nameof(VmProcessorSettings.PerfCpuEnergyPerformancePreference) or
        nameof(VmProcessorSettings.PerfCpuAutonomousActivityWindow) or
        nameof(VmProcessorSettings.PerfCpuIgnoreHostMaxFrequency);

    private static bool IsStateReadable(string propertyName, VmQueryValueState state) =>
        state == VmQueryValueState.Available ||
        (state == VmQueryValueState.Unavailable && IsOptionalDefaultWhenNull(propertyName));

    protected override async Task LoadAsync()
    {
        snapshot = null;
        foreach (var field in fields) field.Editor.SetAvailability(false, null);
        var settingsTask = VmProcessorService.GetVmProcessorAsync(VmId, includePreviewSettings: true);
        var hostTask = ReadHostCapabilitiesAsync();
        var contextTask = ReadContextAsync();
        var maxCpuTask = VmProcessorService.GetHostMaxVirtualCpuCountAsync(VmId);
        await Task.WhenAll(settingsTask, hostTask, contextTask, maxCpuTask).WaitAsync(CancellationToken);
        var observation = (await settingsTask).RequireData();
        var settings = observation.Values;
        host = await hostTask;
        powerState = (await contextTask).State;
        int hostLogicalCpus = Math.Max(1, Environment.ProcessorCount);
        int? reportedMaximum = await maxCpuTask;
        hostMaxVirtualCpus = reportedMaximum is > 0
            ? Math.Min(hostLogicalCpus, reportedMaximum.Value)
            : hostLogicalCpus;
        if (IsStopped) return;

        fieldStates = observation.States;
        snapshot = settings;
        Updating = true;
        List<string>? unavailableFields = null;
        try
        {
            foreach (var (field, capability) in fields)
            {
                var value = field.Property.GetValue(settings);
                if (field.Property.Name == nameof(VmProcessorSettings.Count))
                {
                    field.UpdateSliderBounds(1, hostMaxVirtualCpus);
                    if (reportedMaximum == null)
                    {
                        AppDiagnostics.Write("UI", "ProcessorMaxCountUnavailable", level: TraceEventType.Warning);
                        field.Load(value, false, L10n.Get("Err_NoValidData"));
                        continue;
                    }
                }
                var condition = VmProcessorService.GetModificationCondition(settings, field.Property.Name, powerState, host);
                if (!condition.CanModify)
                {
                    field.Load(value, false, CoreMessageResolver.ResolveConditionReason(condition));
                    continue;
                }

                var state = observation.States.GetValueOrDefault(field.Property.Name, VmQueryValueState.Unavailable);
                if (state == VmQueryValueState.Unsupported)
                {
                    field.Load(value, false, L10n.Get("Cond_HostSettingMissing"));
                    continue;
                }

                bool unavailable = !IsStateReadable(field.Property.Name, state);
                if (unavailable) (unavailableFields ??= []).Add(field.Property.Name);
                object? displayValue = value;
                if (displayValue == null && field.Property.Name == nameof(VmProcessorSettings.PageShatterMode))
                    displayValue = PageShatterMode.Default;
                field.Load(displayValue, !uncertain && !unavailable, unavailable ? L10n.Get("Err_NoValidData") : null);
            }
        }
        finally { Updating = false; }
        UpdateGroupActions();
        if (uncertain)
            ShowError(L10n.Get("Err_UncertainLock"), L10n.Get("Action_RefreshStatus"), UnlockAndReloadAsync);
        else if (unavailableFields is { Count: > 0 })
        {
            AppDiagnostics.Write("UI", "ProcessorSettingsUnavailable",
                $"fields={string.Join(',', unavailableFields)}", TraceEventType.Warning);
            ShowError(L10n.Get("Err_PartialReadUnavailable"), L10n.Get("Action_RefreshStatus"), UnlockAndReloadAsync);
        }
    }

    private async Task<bool> ApplyAsync(params SettingFieldEditor[] submitted)
    {
        if (snapshot == null || host == null) return false;
        var proposed = snapshot.Clone();
        var changed = new List<string>();
        foreach (var field in submitted)
        {
            if (!field.IsFieldEnabled || !fieldStates.TryGetValue(field.Property.Name, out var availability) || !IsStateReadable(field.Property.Name, availability))
                throw new InvalidOperationException(L10n.Get("Err_NoValidData"));
            object? value = field.Read();
            if (Equals(field.Property.GetValue(snapshot), value)) continue;
            field.Property.SetValue(proposed, value);
            changed.Add(field.Property.Name);
        }
        if (changed.Count == 0) return false;
        ValidateProcessorSubmission(proposed, changed);
        var otherDrafts = fields.Select(f => f.Editor).Where(f => f.HasPendingEdit && !submitted.Contains(f))
            .Select(f => (Field: f, Draft: f.CaptureDraft())).ToArray();
        ApiResponse? result = null;
        Exception? failure = null;
        try
        {
            result = await VmProcessorService.SetVmProcessorAsync(VmId, proposed, changed);
            if (result.ErrorKind == ApiErrorKind.OutcomeUnknown) uncertain = true;
            if (result.Success) NotifyChanged();
        }
        catch (Exception ex) { failure = ex; if (ex is CoreOperationException core && core.Result.ErrorKind == ApiErrorKind.OutcomeUnknown) uncertain = true; }
        try { if (!IsStopped) await LoadAsync(); }
        catch (OperationCanceledException) when (IsStopped) { }
        catch (Exception ex) { failure ??= ex; }
        foreach (var draft in otherDrafts)
            if (draft.Field.IsFieldEnabled) draft.Field.RestoreDraft(draft.Draft);
        UpdateGroupActions();
        if (result is { Success: false }) throw CreateApplyFailure(CoreMessageResolver.ResolveError(result), failure);
        if (failure != null) throw new InvalidOperationException(CoreMessageResolver.ResolveException(failure), failure);
        return result?.Success ?? false;
    }

    private Exception CreateApplyFailure(string primaryMessage, Exception? reloadFailure)
    {
        var message = uncertain ? L10n.Format("Err_UncertainAfterFail", primaryMessage) : primaryMessage;
        if (reloadFailure != null)
            message = L10n.Format("SettingsView_ReadbackSuffix", message, CoreMessageResolver.ResolveException(reloadFailure));
        return new InvalidOperationException(message);
    }

    private static void ValidateProcessorSubmission(VmProcessorSettings proposed, IReadOnlyCollection<string> changed)
    {
        if (changed.Contains(nameof(VmProcessorSettings.Count)) && proposed.Count < 1)
            throw new InvalidOperationException(L10n.Get("Cpu_Val_CountPositive"));
        if (changed.Contains(nameof(VmProcessorSettings.Reservation)) && proposed.Reservation > 100000)
            throw new InvalidOperationException(L10n.Get("Cpu_Val_ResRange"));
        if (changed.Contains(nameof(VmProcessorSettings.Limit)) && proposed.Limit > 100000)
            throw new InvalidOperationException(L10n.Get("Cpu_Val_LimRange"));
        if (changed.Contains(nameof(VmProcessorSettings.RelativeWeight)) && (proposed.RelativeWeight < 1 || proposed.RelativeWeight > 10000))
            throw new InvalidOperationException(L10n.Get("Cpu_Val_WeightRange"));
        if ((changed.Contains(nameof(VmProcessorSettings.Reservation)) || changed.Contains(nameof(VmProcessorSettings.Limit)))
            && proposed.Reservation > proposed.Limit)
            throw new InvalidOperationException(L10n.Format("Cpu_Val_ResGtLim", $"{proposed.Reservation / 1000m:0.##}", $"{proposed.Limit / 1000m:0.##}"));

        if (changed.Any(name => name is nameof(VmProcessorSettings.PerfCpuFreqCapMhz)
            or nameof(VmProcessorSettings.PerfCpuFreqMinMhz)
            or nameof(VmProcessorSettings.PerfCpuFreqDesiredMhz)))
        {
            uint cap = proposed.PerfCpuFreqCapMhz ?? 0;
            uint minFreq = proposed.PerfCpuFreqMinMhz ?? 0;
            uint desired = proposed.PerfCpuFreqDesiredMhz ?? 0;
            if (minFreq > 0 && cap > 0 && minFreq > cap)
                throw new InvalidOperationException(L10n.Format("Cpu_Val_FreqMinGtCap", minFreq, cap));
            if (desired > 0 && cap > 0 && desired > cap)
                throw new InvalidOperationException(L10n.Format("Cpu_Val_FreqDesiredGtCap", desired, cap));
            if (desired > 0 && minFreq > 0 && desired < minFreq)
                throw new InvalidOperationException(L10n.Format("Cpu_Val_FreqDesiredLtMin", desired, minFreq));
        }
    }
}
