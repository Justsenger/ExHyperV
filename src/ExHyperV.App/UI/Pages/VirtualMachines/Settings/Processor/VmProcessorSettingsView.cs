using System.Collections.Concurrent;
using System.Globalization;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Processor;

public sealed partial class VmProcessorSettingsView : VmSettingsView
{
    private readonly List<(SettingFieldEditor Editor, VmProcessorCapability Capability)> fields = [];
    private readonly List<(string Title, Border Content)> sections = [];
    private readonly List<(SettingFieldEditor[] Fields, Button Apply, Button Revert)> groupActions = [];
    private readonly Border affinitySection = new();
    private VmCpuAffinitySettingsView? affinityView;
    private VmProcessorSettings? snapshot;
    private VmProcessorService.HostCapabilities? host;
    private VmState powerState;
    private int hostMaxVirtualCpus = Math.Max(1, Environment.ProcessorCount);
    private static readonly ConcurrentDictionary<Guid, byte> unknownOperations = new();
    private bool uncertain
    {
        get => unknownOperations.ContainsKey(VmId);
        set { if (value) unknownOperations.TryAdd(VmId, 0); else unknownOperations.TryRemove(VmId, out _); }
    }

    public VmProcessorSettingsView(Guid vmId) : base(vmId)
    {
        AddGroup(L10n.Get("Cpu_Group_Compute"), useRectActions: true,
            Energy(L10n.Get("Cpu_Count_Title"), nameof(VmProcessorSettings.Count), VmProcessorCapability.ProcessorCount, EnergySliderKind.Count,
                description: L10n.Get("Cpu_Count_Desc")),
            Energy(L10n.Get("Cpu_Reserve_Title"), nameof(VmProcessorSettings.Reservation), VmProcessorCapability.ResourceReservation, EnergySliderKind.Reservation, scale: 1000,
                description: L10n.Get("Cpu_Reserve_Desc")),
            Energy(L10n.Get("Cpu_Limit_Title"), nameof(VmProcessorSettings.Limit), VmProcessorCapability.ResourceLimit, EnergySliderKind.Limit, scale: 1000,
                description: L10n.Get("Cpu_Limit_Desc")),
            Energy(L10n.Get("Cpu_Weight_Title"), nameof(VmProcessorSettings.RelativeWeight), VmProcessorCapability.SchedulingWeight, EnergySliderKind.RelativeWeight,
                description: L10n.Get("Cpu_Weight_Desc")));

        sections.Add((L10n.Get("Cpu_Group_Affinity"), affinitySection));

        BuildIsaGroup();

        BuildTopologyGroup();

        AddGroup(L10n.Get("Cpu_Group_Interrupt"), useRectActions: true,
            Choice(L10n.Get("Cpu_Apic_Title"), nameof(VmProcessorSettings.ApicMode), VmProcessorCapability.InterruptControllerMode,
                Options((VmApicMode.Default, L10n.Get("Common_Default")), (VmApicMode.Legacy, L10n.Get("Cpu_Apic_Legacy")), (VmApicMode.Apic, "xAPIC"), (VmApicMode.X2Apic, "x2APIC")),
                description: L10n.Get("Cpu_Apic_Desc")),
            Choice(L10n.Get("Cpu_Lpi_Title"), nameof(VmProcessorSettings.LpiMode), VmProcessorCapability.LocalPeripheralInterruptMode,
                Options((LpiMode.Default, L10n.Get("Common_Default")), (LpiMode.Disabled, L10n.Get("Common_Disable")), (LpiMode.Enabled, L10n.Get("Common_Enable"))),
                description: L10n.Get("Cpu_Lpi_Desc")),
            Toggle(L10n.Get("Cpu_HostProtect_Title"), nameof(VmProcessorSettings.EnableHostResourceProtection), VmProcessorCapability.HostResourceProtection,
                L10n.Get("Cpu_HostProtect_Desc")));

        AddGroup(L10n.Get("Cpu_Group_Perfmon"), useRectActions: true,
            Toggle(L10n.Get("Cpu_Acount_Title"), nameof(VmProcessorSettings.AllowAcountMcount), VmProcessorCapability.ActivityCounters,
                L10n.Get("Cpu_Acount_Desc")),
            Toggle(L10n.Get("Cpu_Pmu_Title"), nameof(VmProcessorSettings.EnablePerfmonPmu), VmProcessorCapability.PerformanceMonitoring,
                L10n.Get("Cpu_Pmu_Desc")),
            Toggle(L10n.Get("Cpu_Lbr_Title"), nameof(VmProcessorSettings.EnablePerfmonLbr), VmProcessorCapability.LastBranchRecording,
                L10n.Get("Cpu_Lbr_Desc")),
            Toggle(L10n.Get("Cpu_Pebs_Title"), nameof(VmProcessorSettings.EnablePerfmonPebs), VmProcessorCapability.PreciseEventSampling,
                L10n.Get("Cpu_Pebs_Desc")),
            Toggle(L10n.Get("Cpu_Ipt_Title"), nameof(VmProcessorSettings.EnablePerfmonIpt), VmProcessorCapability.InstructionTracing,
                L10n.Get("Cpu_Ipt_Desc")));

        BuildFrequencyGroup();

        AddGroup(L10n.Get("Cpu_Group_Addressing"), useRectActions: true,
            Choice(L10n.Get("Cpu_PageShatter_Title"), nameof(VmProcessorSettings.PageShatterMode), VmProcessorCapability.LargePageMitigation,
                Options((PageShatterMode.Default, L10n.Get("Common_Default")), (PageShatterMode.AlwaysEnabled, L10n.Get("Common_Enable")), (PageShatterMode.AlwaysDisabled, L10n.Get("Common_Disable"))),
                description: L10n.Get("Cpu_PageShatter_Desc")),
            SingleRowCustomSlider(L10n.Get("Cpu_PaWidth_Title"), nameof(VmProcessorSettings.PhysicalAddressWidth), VmProcessorCapability.PhysicalAddressWidth,
                sliderMin: 0, sliderMax: 17,
                toSliderPos: val => val is uint u && u > 0 ? Math.Clamp((int)u - 35, 1, 17) : 0,
                fromSliderPos: pos => pos <= 0 ? 0u : (uint)(35 + pos),
                formatTooltip: pos => pos <= 0 ? L10n.Get("Common_Auto") : L10n.Format("Cpu_PaWidth_Tooltip", 35 + pos, FormatAddrSpace(35 + pos)),
                description: L10n.Get("Cpu_PaWidth_Desc")));

        AddGroup(L10n.Get("Cpu_Group_Security"), useRectActions: true,
            Toggle(L10n.Get("Cpu_DisableSpec_Title"), nameof(VmProcessorSettings.DisableSpeculationControls), VmProcessorCapability.SpeculationControlPolicy,
                L10n.Get("Cpu_DisableSpec_Desc")),
            Toggle(L10n.Get("Cpu_HwIso_Title"), nameof(VmProcessorSettings.HardwareIsolationExtensionsEnabled), VmProcessorCapability.HardwareIsolation,
                L10n.Get("Cpu_HwIso_Desc")),
            Number(L10n.Get("Cpu_HwIsoLimit_Title"), nameof(VmProcessorSettings.MaxHwIsolatedGuests), VmProcessorCapability.HardwareIsolatedGuestLimit,
                description: L10n.Get("Cpu_HwIsoLimit_Desc")));

        SetSectionedBody(sections);
        BusyChanged += busy => { if (!busy && !IsStopped && snapshot != null) UpdateGroupActions(); };
    }

    protected override void OnSectionShown(int index)
    {
        if (index != 1 || affinityView != null || IsStopped) return;
        affinityView = new VmCpuAffinitySettingsView(VmId);
        affinityView.Changed += (_, _) => NotifyChanged();
        affinityView.BusyChanged += SetNestedBusy;
        affinitySection.Child = affinityView;
    }

    public override async Task StopAsync()
    {
        if (affinityView != null) await affinityView.StopAsync();
        await base.StopAsync();
    }

    private void UpdateGroupActions() => UpdateGroupActions(includePerfmon: true);

    private void UpdateGroupActions(bool includePerfmon)
    {
        if (includePerfmon) UpdateDependentAvailability();
        UpdateEnergyContexts();
        SyncTopologySlidersAndPreview();
        UpdateFrequencyModeVisibility();
        SyncIsaMigrationPill();
        foreach (var group in groupActions)
        {
            group.Apply.IsEnabled = snapshot != null && !uncertain && group.Fields.Any(f => f.HasPendingEdit);
            group.Revert.IsEnabled = group.Fields.Any(f => f.HasPendingEdit);
        }
    }

    private void UpdateEnergyContexts()
    {
        int count = ReadVmCoreCount();
        foreach (var (editor, _) in fields)
            editor.UpdateEnergyContext(count, Environment.ProcessorCount);

        var reservationField = fields.FirstOrDefault(f => f.Editor.Property.Name == nameof(VmProcessorSettings.Reservation)).Editor;
        var limitField = fields.FirstOrDefault(f => f.Editor.Property.Name == nameof(VmProcessorSettings.Limit)).Editor;
        if (reservationField != null && limitField != null)
        {
            int resPct = Math.Clamp(reservationField.ReadEnergyInt(0), 0, 100);
            int limPct = Math.Clamp(limitField.ReadEnergyInt(100), 0, 100);
            reservationField.SetEnergyConstraint(0, limPct, L10n.Format("Cpu_ReserveMaxLimit", limPct));
            limitField.SetEnergyConstraint(resPct, 100, L10n.Format("Cpu_LimitMinReserve", resPct));
        }
    }

    private static bool IsDependencyTrigger(VmProcessorCapability capability) => capability is
        VmProcessorCapability.PerformanceMonitoring or VmProcessorCapability.PreciseEventSampling or VmProcessorCapability.InstructionTracing or
        VmProcessorCapability.MigrationCompatibility or VmProcessorCapability.MigrationFeatureSet or
        VmProcessorCapability.HardwareIsolation or VmProcessorCapability.HardwareIsolatedGuestLimit or
        VmProcessorCapability.ProcessorsPerL3Domain;

    private void SetDraftAvailability(SettingFieldEditor field, bool allowed, string? reason)
    {
        bool observed = fieldStates.TryGetValue(field.Property.Name, out var state) && IsStateReadable(field.Property.Name, state);
        field.SetAvailability(!uncertain && observed && allowed, observed ? reason : L10n.Get("Err_NoValidData"));
    }

    private void UpdateDependentAvailability()
    {
        if (Updating || snapshot == null || host == null) return;
        var draft = snapshot.Clone();
        foreach (var (field, _) in fields)
            if (field.HasPendingEdit && field.TryRead(out var edited)) field.Property.SetValue(draft, edited);

        // 1. 性能监控（PMU -> PEBS / IPT）
        foreach (var (field, capability) in fields)
        {
            if (capability is VmProcessorCapability.PerformanceMonitoring or VmProcessorCapability.PreciseEventSampling or VmProcessorCapability.InstructionTracing)
            {
                var condition = VmProcessorService.GetModificationCondition(draft, field.Property.Name, powerState, host);
                string? reason = condition.CanModify ? null : CoreMessageResolver.ResolveConditionReason(condition);
                if (!condition.CanModify &&
                    capability is VmProcessorCapability.PreciseEventSampling or VmProcessorCapability.InstructionTracing &&
                    draft.EnablePerfmonPmu != true)
                {
                    var withPmu = draft.Clone();
                    withPmu.EnablePerfmonPmu = true;
                    if (VmProcessorService.GetModificationCondition(withPmu, field.Property.Name, powerState, host).CanModify)
                        reason = L10n.Get("Cond_RequiresPerfmonPmu");
                }
                SetDraftAvailability(field, condition.CanModify, reason);
            }
        }

        // 2. 安全隔离（硬件隔离扩展 -> 硬件隔离来宾上限）
        var hwGuestLimitField = fields.FirstOrDefault(f => f.Capability == VmProcessorCapability.HardwareIsolatedGuestLimit).Editor;
        if (hwGuestLimitField != null)
        {
            var baseCond = VmProcessorService.GetModificationCondition(draft, hwGuestLimitField.Property.Name, powerState, host);
            if (!baseCond.CanModify)
                hwGuestLimitField.SetAvailability(false, CoreMessageResolver.ResolveConditionReason(baseCond));
            else if (draft.HardwareIsolationExtensionsEnabled != true)
                hwGuestLimitField.SetAvailability(false, L10n.Get("Cpu_Cond_NeedHwIso"));
            else
                SetDraftAvailability(hwGuestLimitField, true, null);
        }

        // 4. 处理器拓扑（每节点 L3 域 -> L3 余数分配）
        var l3PolicyField = fields.FirstOrDefault(f => f.Capability == VmProcessorCapability.L3ProcessorDistribution).Editor;
        if (l3PolicyField != null)
        {
            var baseCond = VmProcessorService.GetModificationCondition(draft, l3PolicyField.Property.Name, powerState, host);
            int vcpu = ReadVmCoreCount();
            var procPerNumaField = fields.FirstOrDefault(f => f.Editor.Property.Name == nameof(VmProcessorSettings.MaxProcessorsPerNumaNode)).Editor;
            int maxPerNuma = procPerNumaField != null ? Math.Max(1, ReadFieldInt(procPerNumaField, vcpu)) : vcpu;
            int numaCount = Math.Max(1, (vcpu + maxPerNuma - 1) / maxPerNuma);
            int vpPerNode = Math.Max(1, (vcpu + numaCount - 1) / numaCount);
            int? maxPerL3 = draft.MaxProcessorCountPerL3 is null or 0 ? null : Math.Max(1, Convert.ToInt32(draft.MaxProcessorCountPerL3, CultureInfo.InvariantCulture));
            int l3PerNode = maxPerL3 is > 0 ? Math.Max(1, (vpPerNode + maxPerL3.Value - 1) / maxPerL3.Value) : 1;

            if (!baseCond.CanModify)
                l3PolicyField.SetAvailability(false, CoreMessageResolver.ResolveConditionReason(baseCond));
            else if (maxPerL3 is null)
                l3PolicyField.SetAvailability(false, L10n.Get("Cpu_Cond_L3Auto"));
            else if (l3PerNode <= 1)
                l3PolicyField.SetAvailability(false, L10n.Get("Cpu_Cond_L3SingleGroup"));
            else
                SetDraftAvailability(l3PolicyField, true, null);
        }
    }
}
