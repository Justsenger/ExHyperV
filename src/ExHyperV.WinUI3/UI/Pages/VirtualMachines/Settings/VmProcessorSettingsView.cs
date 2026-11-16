using System.Globalization;
using ExHyperV.WinUI3.Localization;
using ExHyperV.WinUI3.UI.Shared;
using System.Reflection;
using System.Collections.Concurrent;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed class VmProcessorSettingsView : VmSettingsView
{
    private readonly List<(SettingFieldEditor Editor, VmProcessorCapability Capability)> fields = [];
    private readonly List<(string Title, Border Content)> sections = [];
    private readonly List<(SettingFieldEditor[] Fields, Button Apply, Button Revert)> groupActions = [];
    private readonly Border affinitySection = new();
    private VmCpuAffinitySettingsView? affinityView;
    private VmProcessorSettings? snapshot;
    private VmProcessorService.HostCapabilities? host;
    private VmState powerState;
    private static readonly ConcurrentDictionary<Guid, byte> unknownOperations = new();
    private bool uncertain
    {
        get => unknownOperations.ContainsKey(VmId);
        set { if (value) unknownOperations.TryAdd(VmId, 0); else unknownOperations.TryRemove(VmId, out _); }
    }

    // 指令集能力「跨机迁移兼容」联动控件
    private SettingsSegmentedPill? isaMigPill;
    private Grid? isaMigLabel;
    private SettingFieldEditor? isaMigEnabledField;
    private SettingFieldEditor? isaMigModeField;
    private bool updatingIsaMig;

    // 处理器拓扑联动控件
    private VmProcessorTopologyPreview? topologyPreview;
    private SettingsEnergySlider? topoSocketsSlider;
    private TextBlock? topoSocketsValText;
    private Grid? topoSocketsHeader;
    private SettingInfoButton? topoSocketsInfo;
    private SettingsEnergySlider? topoNumaSlider;
    private TextBlock? topoNumaValText;
    private Grid? topoNumaHeader;
    private SettingInfoButton? topoNumaInfo;
    private SettingsEnergySlider? topoL3Slider;
    private TextBlock? topoL3ValText;
    private Grid? topoL3Header;
    private SettingInfoButton? topoL3Info;
    private SettingsEnergySlider? topoClusterSlider;
    private TextBlock? topoClusterValText;
    private Grid? topoClusterHeader;
    private SettingInfoButton? topoClusterInfo;
    private SettingsEnergySlider? topoWaysSlider;
    private TextBlock? topoWaysValText;
    private Grid? topoWaysHeader;
    private SettingInfoButton? topoWaysInfo;
    private SettingInfoButton? topoL3PolicyInfo;
    private bool updatingTopologySliders;

    // 频率调节「调频方式」联动控件
    private SettingsSegmentedPill? freqModePill;
    private Grid? freqDesiredRow;
    private Grid? freqEppRow;
    private Grid? freqWindowRow;
    private uint lastDesiredFreqMhz = 3500;
    private bool updatingFreqMode;

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

    private void BuildIsaGroup()
    {
        var nestedRow = Toggle(L10n.Get("Cpu_Nested_Title"), nameof(VmProcessorSettings.ExposeVirtualizationExtensions), VmProcessorCapability.NestedVirtualization,
            L10n.Get("Cpu_Nested_Desc"));
        var legacyRow = Toggle(L10n.Get("Cpu_LegacyOs_Title"), nameof(VmProcessorSettings.CompatibilityForOlderOperatingSystemsEnabled), VmProcessorCapability.LegacyOperatingSystemCompatibility,
            L10n.Get("Cpu_LegacyOs_Desc"));

        isaMigEnabledField = CreateHiddenField(nameof(VmProcessorSettings.CompatibilityForMigrationEnabled), VmProcessorCapability.MigrationCompatibility);
        isaMigModeField = CreateHiddenField(nameof(VmProcessorSettings.CompatibilityForMigrationMode), VmProcessorCapability.MigrationFeatureSet);

        isaMigPill = new SettingsSegmentedPill { Width = 310, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        isaMigPill.SetOptions(Options<object>(
            ("off", L10n.Get("Cpu_Mig_Off")),
            (VmMigrationCompatibilityMode.MinimumFeatureSet, L10n.Get("Cpu_Mig_Minimum")),
            (VmMigrationCompatibilityMode.CommonClusterFeatureSet, L10n.Get("Cpu_Mig_Cluster"))));
        isaMigPill.SelectedValue = "off";
        isaMigPill.SelectionChanged += selected =>
        {
            if (updatingIsaMig || isaMigEnabledField == null || isaMigModeField == null) return;
            if (selected is VmMigrationCompatibilityMode mode)
            {
                isaMigEnabledField.RestoreDraft(true);
                isaMigModeField.RestoreDraft(mode);
            }
            else
            {
                isaMigEnabledField.RestoreDraft(false);
                isaMigModeField.Revert();
            }
            UpdateGroupActions(includePerfmon: false);
        };

        var migRow = CreateSingleRowCard(L10n.Get("Cpu_Migration_Title"),
            L10n.Get("Cpu_Migration_Desc"),
            isaMigPill, span2: true, out isaMigLabel, editor: isaMigEnabledField);

        var brandRow = Text(L10n.Get("Cpu_Brand_Title"), nameof(VmProcessorSettings.CpuBrandString), VmProcessorCapability.ProcessorBrand,
            L10n.Get("Cpu_Brand_Desc"));

        var grouped = new[]
        {
            (SettingFieldEditor)nestedRow.Tag,
            (SettingFieldEditor)legacyRow.Tag,
            isaMigEnabledField,
            isaMigModeField,
            (SettingFieldEditor)brandRow.Tag
        };

        var apply = SettingsActionButton.CreateRect(L10n.Get("Common_Apply"), primary: true);
        var revert = SettingsActionButton.CreateRect(L10n.Get("Common_Revert"));
        apply.IsEnabled = revert.IsEnabled = false;
        apply.Click += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped || snapshot == null || uncertain) return;
            var submitted = grouped.Where(f => f.HasPendingEdit).ToArray();
            await RunAsync(async () =>
            {
                if (await ApplyAsync(submitted)) ShowAppliedToast(L10n.Get("Common_Applied"));
            });
        };
        revert.Click += (_, _) =>
        {
            foreach (var field in grouped) field.Revert();
            SyncIsaMigrationPill();
            UpdateGroupActions();
        };
        groupActions.Add((grouped, apply, revert));

        var grid = new SettingsFieldPanel();
        grid.Children.Add(nestedRow);
        grid.Children.Add(legacyRow);
        grid.Children.Add(migRow);
        grid.Children.Add(brandRow);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 2, 0, 0) };
        actions.Children.Add(revert);
        actions.Children.Add(apply);
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(grid);
        content.Children.Add(actions);
        sections.Add((L10n.Get("Cpu_Group_Isa"), new Border { Child = content, Margin = new Thickness(0, 0, 0, 16) }));
    }

    private void BuildTopologyGroup()
    {
        var smtRow = Choice(L10n.Get("Cpu_Smt_Title"), nameof(VmProcessorSettings.SmtMode), VmProcessorCapability.SimultaneousMultithreading,
            Options((SmtMode.Inherit, L10n.Get("Cpu_Smt_Inherit")), (SmtMode.SingleThread, L10n.Get("Cpu_Smt_Single")), (SmtMode.MultiThread, L10n.Get("Cpu_Smt_Multi"))),
            description: L10n.Get("Cpu_Smt_Desc"));

        // 隐藏的真实 WMI 绑定字段，由意图驱动拓扑滑块驱动
        var numaPerSocketField = CreateHiddenField(nameof(VmProcessorSettings.MaxNumaNodesPerSocket), VmProcessorCapability.NumaNodesPerSocket);
        var procPerNumaField = CreateHiddenField(nameof(VmProcessorSettings.MaxProcessorsPerNumaNode), VmProcessorCapability.ProcessorsPerNumaNode);
        var l3Field = CreateHiddenField(nameof(VmProcessorSettings.MaxProcessorCountPerL3), VmProcessorCapability.ProcessorsPerL3Domain);
        var clusterField = CreateHiddenField(nameof(VmProcessorSettings.MaxClusterCountPerSocket), VmProcessorCapability.ProcessorClustersPerSocket);
        var l3WaysField = CreateHiddenField(nameof(VmProcessorSettings.L3CacheWays), VmProcessorCapability.L3CacheAssociativity);

        var (socketsRow, socketsSlider, socketsTxt, socketsHdr, socketsInfo) = BuildTopologyDiscreteSliderCard(
            L10n.Get("Cpu_Sockets_Title"), L10n.Get("Cpu_Sockets_Desc"),
            onSliderPosChanged: pos =>
            {
                if (updatingTopologySliders) return;
                int vcpu = ReadVmCoreCount();
                var counts = VmProcessorTopologyPreview.GetAchievableCounts(vcpu);
                int desiredSockets = counts[Math.Clamp(pos, 0, counts.Length - 1)];
                int curPerNuma = Math.Max(1, ReadFieldInt(procPerNumaField, vcpu));
                int curNumas = Math.Max(1, (vcpu + curPerNuma - 1) / curPerNuma);
                if (desiredSockets > curNumas)
                {
                    curNumas = desiredSockets;
                    int nextPerNuma = Math.Max(1, (vcpu + curNumas - 1) / curNumas);
                    procPerNumaField.RestoreDraft(nextPerNuma.ToString(CultureInfo.InvariantCulture));
                }
                int nextNumaPerSocket = Math.Max(1, (curNumas + desiredSockets - 1) / desiredSockets);
                numaPerSocketField.RestoreDraft(nextNumaPerSocket.ToString(CultureInfo.InvariantCulture));
                UpdateDependentAvailability();
                SyncTopologySlidersAndPreview();
            },
            pos =>
            {
                var counts = VmProcessorTopologyPreview.GetAchievableCounts(ReadVmCoreCount());
                return L10n.Format("Cpu_Sockets_Value", counts[Math.Clamp(pos, 0, counts.Length - 1)]);
            });
        topoSocketsSlider = socketsSlider;
        topoSocketsValText = socketsTxt;
        topoSocketsHeader = socketsHdr;
        topoSocketsInfo = socketsInfo;

        var (numaRow, numaSlider, numaTxt, numaHdr, numaInfo) = BuildTopologyDiscreteSliderCard(
            L10n.Get("Cpu_Numa_Title"), L10n.Get("Cpu_Numa_Desc"),
            onSliderPosChanged: pos =>
            {
                if (updatingTopologySliders) return;
                int vcpu = ReadVmCoreCount();
                var counts = VmProcessorTopologyPreview.GetAchievableCounts(vcpu);
                int desiredNumas = counts[Math.Clamp(pos, 0, counts.Length - 1)];
                int nextPerNuma = Math.Max(1, (vcpu + desiredNumas - 1) / desiredNumas);
                int curNumaPerSocket = Math.Max(1, ReadFieldInt(numaPerSocketField, 1));
                int curSockets = Math.Max(1, (desiredNumas + curNumaPerSocket - 1) / curNumaPerSocket);
                if (curSockets > desiredNumas) curSockets = desiredNumas;
                int nextNumaPerSocket = Math.Max(1, (desiredNumas + curSockets - 1) / curSockets);
                procPerNumaField.RestoreDraft(nextPerNuma.ToString(CultureInfo.InvariantCulture));
                numaPerSocketField.RestoreDraft(nextNumaPerSocket.ToString(CultureInfo.InvariantCulture));
                UpdateDependentAvailability();
                SyncTopologySlidersAndPreview();
            },
            pos =>
            {
                var counts = VmProcessorTopologyPreview.GetAchievableCounts(ReadVmCoreCount());
                return L10n.Format("Cpu_Numa_Value", counts[Math.Clamp(pos, 0, counts.Length - 1)]);
            });
        topoNumaSlider = numaSlider;
        topoNumaValText = numaTxt;
        topoNumaHeader = numaHdr;
        topoNumaInfo = numaInfo;

        var (l3Row, l3Slider, l3Txt, l3Hdr, l3Info) = BuildTopologyDiscreteSliderCard(
            L10n.Get("Cpu_L3_Title"), L10n.Get("Cpu_L3_Desc"),
            onSliderPosChanged: pos =>
            {
                if (updatingTopologySliders) return;
                int vcpu = ReadVmCoreCount();
                int vpPerNode = Math.Max(1, ReadFieldInt(procPerNumaField, vcpu));
                if (pos <= 1)
                {
                    l3Field.RestoreDraft((string?)null);
                }
                else
                {
                    int maxPerL3 = Math.Max(1, (vpPerNode + pos - 1) / pos);
                    l3Field.RestoreDraft(maxPerL3.ToString(CultureInfo.InvariantCulture));
                }
                UpdateDependentAvailability();
                SyncTopologySlidersAndPreview();
            },
            pos => pos <= 1 ? L10n.Get("Common_Auto") : L10n.Format("Cpu_L3_Value", pos));
        topoL3Slider = l3Slider;
        topoL3ValText = l3Txt;
        topoL3Header = l3Hdr;
        topoL3Info = l3Info;

        var (clusterRow, clusterSlider, clusterTxt, clusterHdr, clusterInfo) = BuildTopologyDiscreteSliderCard(
            L10n.Get("Cpu_Cluster_Title"), L10n.Get("Cpu_Cluster_Desc"),
            onSliderPosChanged: pos =>
            {
                if (updatingTopologySliders) return;
                clusterField.RestoreDraft(pos <= 0 ? (string?)null : Math.Clamp(pos, 1, 8).ToString(CultureInfo.InvariantCulture));
                SyncTopologySlidersAndPreview();
            },
            pos => pos <= 0 ? L10n.Get("Common_Auto") : L10n.Format("Cpu_Cluster_Value", pos));
        topoClusterSlider = clusterSlider;
        topoClusterValText = clusterTxt;
        topoClusterHeader = clusterHdr;
        topoClusterInfo = clusterInfo;

        var l3PolicyRow = Choice(L10n.Get("Cpu_L3Policy_Title"), nameof(VmProcessorSettings.L3DistributionPolicy), VmProcessorCapability.L3ProcessorDistribution,
            Options((L3DistributionPolicy.SmallToLarge, L10n.Get("Cpu_L3Pol_S2L")), (L3DistributionPolicy.LargeToSmall, L10n.Get("Cpu_L3Pol_L2S")),
                (L3DistributionPolicy.EvenSmallToLarge, L10n.Get("Cpu_L3Pol_EvenS2L")), (L3DistributionPolicy.EvenLargeToSmall, L10n.Get("Cpu_L3Pol_EvenL2S"))),
            description: L10n.Get("Cpu_L3Policy_Desc"));
        topoL3PolicyInfo = (l3PolicyRow.Children.FirstOrDefault() as Grid)?.Children.OfType<SettingInfoButton>().FirstOrDefault();

        var (l3WaysRow, waysSlider, waysTxt, waysHdr, waysInfo) = BuildTopologyDiscreteSliderCard(
            L10n.Get("Cpu_Ways_Title"), L10n.Get("Cpu_Ways_Desc"),
            onSliderPosChanged: pos =>
            {
                if (updatingTopologySliders) return;
                l3WaysField.RestoreDraft(Math.Max(0, pos).ToString(CultureInfo.InvariantCulture));
                SyncTopologySlidersAndPreview();
            },
            pos => pos <= 0 ? L10n.Get("Common_Default") : L10n.Format("Cpu_Ways_Value", pos));
        SettingsFieldPanel.SetSpan2(l3WaysRow, true);
        topoWaysSlider = waysSlider;
        topoWaysValText = waysTxt;
        topoWaysHeader = waysHdr;
        topoWaysInfo = waysInfo;

        foreach (var liveRow in new[] { smtRow, l3PolicyRow })
            ((SettingFieldEditor)liveRow.Tag).ValueEdited += () => SyncTopologySlidersAndPreview();

        topologyPreview = new VmProcessorTopologyPreview();
        SettingsFieldPanel.SetSpan2(topologyPreview, true);

        var grouped = new[]
        {
            (SettingFieldEditor)smtRow.Tag,
            numaPerSocketField,
            procPerNumaField,
            l3Field,
            clusterField,
            (SettingFieldEditor)l3PolicyRow.Tag,
            l3WaysField
        };

        var apply = SettingsActionButton.CreateRect(L10n.Get("Common_Apply"), primary: true);
        var revert = SettingsActionButton.CreateRect(L10n.Get("Common_Revert"));
        apply.IsEnabled = revert.IsEnabled = false;
        apply.Click += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped || snapshot == null || uncertain) return;
            var submitted = grouped.Where(f => f.HasPendingEdit).ToArray();
            await RunAsync(async () =>
            {
                if (await ApplyAsync(submitted)) ShowAppliedToast(L10n.Get("Common_Applied"));
            });
        };
        revert.Click += (_, _) =>
        {
            foreach (var field in grouped) field.Revert();
            SyncTopologySlidersAndPreview();
            UpdateGroupActions();
        };
        groupActions.Add((grouped, apply, revert));

        var grid = new SettingsFieldPanel { Spacing = 8 };
        grid.Children.Add(smtRow);
        grid.Children.Add(socketsRow);
        grid.Children.Add(numaRow);
        grid.Children.Add(l3Row);
        grid.Children.Add(clusterRow);
        grid.Children.Add(l3PolicyRow);
        grid.Children.Add(l3WaysRow);
        grid.Children.Add(topologyPreview);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 2, 0, 0) };
        actions.Children.Add(revert);
        actions.Children.Add(apply);
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(grid);
        content.Children.Add(actions);
        sections.Add((L10n.Get("Cpu_Group_Topology"), new Border { Child = content, Margin = new Thickness(0, 0, 0, 16) }));
    }

    private void BuildFrequencyGroup()
    {
        const int FreqMin = 400;
        const int FreqMax = 10000;
        const int FreqStep = 100;
        const int FreqSteps = (FreqMax - FreqMin) / FreqStep; // 96

        var minRow = HeaderValueCustomSlider(L10n.Get("Cpu_FreqMin_Title"), nameof(VmProcessorSettings.PerfCpuFreqMinMhz), VmProcessorCapability.FrequencyMinimum,
            sliderMin: 0, sliderMax: FreqSteps + 1, unitText: "MHz", placeholder: L10n.Get("Common_Auto"), maxTypedValue: 20000, allowAutoZero: true,
            toSliderPos: val => val is uint u && u > 0 ? 1 + Math.Clamp((int)Math.Round((double)(Math.Clamp((int)u, FreqMin, FreqMax) - FreqMin) / FreqStep), 0, FreqSteps) : 0,
            fromSliderPos: pos => pos <= 0 ? 0u : (uint)(FreqMin + (pos - 1) * FreqStep),
            formatTooltip: pos => pos <= 0 ? L10n.Get("Cpu_FreqTooltip_Unlimited") : $"{FreqMin + (pos - 1) * FreqStep} MHz",
            description: L10n.Get("Cpu_FreqMin_Desc"));

        var capRow = HeaderValueCustomSlider(L10n.Get("Cpu_FreqMax_Title"), nameof(VmProcessorSettings.PerfCpuFreqCapMhz), VmProcessorCapability.FrequencyMaximum,
            sliderMin: 0, sliderMax: FreqSteps + 1, unitText: "MHz", placeholder: L10n.Get("Common_Auto"), maxTypedValue: 20000, allowAutoZero: true,
            toSliderPos: val => val is uint u && u > 0 ? 1 + Math.Clamp((int)Math.Round((double)(Math.Clamp((int)u, FreqMin, FreqMax) - FreqMin) / FreqStep), 0, FreqSteps) : 0,
            fromSliderPos: pos => pos <= 0 ? 0u : (uint)(FreqMin + (pos - 1) * FreqStep),
            formatTooltip: pos => pos <= 0 ? L10n.Get("Cpu_FreqTooltip_Unlimited") : $"{FreqMin + (pos - 1) * FreqStep} MHz",
            description: L10n.Get("Cpu_FreqMax_Desc"));

        freqDesiredRow = HeaderValueCustomSlider(L10n.Get("Cpu_FreqDesired_Title"), nameof(VmProcessorSettings.PerfCpuFreqDesiredMhz), VmProcessorCapability.DesiredFrequency,
            sliderMin: 0, sliderMax: FreqSteps, unitText: "MHz", placeholder: "3500", maxTypedValue: 20000, allowAutoZero: false,
            toSliderPos: val => val is uint u && u > 0 ? Math.Clamp((int)Math.Round((double)(Math.Clamp((int)u, FreqMin, FreqMax) - FreqMin) / FreqStep), 0, FreqSteps) : 31,
            fromSliderPos: pos => (uint)(FreqMin + pos * FreqStep),
            formatTooltip: pos => $"{FreqMin + pos * FreqStep} MHz",
            description: L10n.Get("Cpu_FreqDesired_Desc"));
        var desiredEditor = (SettingFieldEditor)freqDesiredRow.Tag;

        freqModePill = new SettingsSegmentedPill { Width = 310, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        freqModePill.SetOptions(Options(("auto", L10n.Get("Cpu_FreqMode_Auto")), ("manual", L10n.Get("Cpu_FreqMode_Manual"))));
        freqModePill.SelectedValue = "auto";
        var modeRow = CreateSingleRowCard(L10n.Get("Cpu_FreqMode_Title"),
            L10n.Get("Cpu_FreqMode_Desc"),
            freqModePill, span2: true, editor: desiredEditor);

        freqEppRow = SingleRowCustomSlider(L10n.Get("Cpu_Epp_Title"), nameof(VmProcessorSettings.PerfCpuEnergyPerformancePreference), VmProcessorCapability.EnergyPerformancePreference,
            sliderMin: 0, sliderMax: 256,
            toSliderPos: val => val is uint u ? 256 - Math.Clamp((int)u, 0, 255) : 0,
            fromSliderPos: pos => pos <= 0 ? null : (uint)(256 - Math.Clamp(pos, 1, 256)),
            formatTooltip: pos => pos <= 0 ? L10n.Get("Common_Auto") : pos == 1 ? L10n.Get("Cpu_Epp_Power") : pos <= 85 ? L10n.Get("Cpu_Epp_MorePower") : pos <= 171 ? L10n.Get("Cpu_Epp_Balanced") : pos < 256 ? L10n.Get("Cpu_Epp_MorePerf") : L10n.Get("Cpu_Epp_Perf"),
            description: L10n.Get("Cpu_Epp_Desc"));

        freqWindowRow = Number(L10n.Get("Cpu_Window_Title"), nameof(VmProcessorSettings.PerfCpuAutonomousActivityWindow), VmProcessorCapability.AutonomousActivityWindow,
            description: L10n.Get("Cpu_Window_Desc"));

        var ignoreHostRow = Toggle(L10n.Get("Cpu_IgnoreHostMax_Title"), nameof(VmProcessorSettings.PerfCpuIgnoreHostMaxFrequency), VmProcessorCapability.IgnoreHostFrequencyMaximum,
            L10n.Get("Cpu_IgnoreHostMax_Desc"));

        var minEditor = (SettingFieldEditor)minRow.Tag;

        freqModePill.SelectionChanged += selected =>
        {
            if (updatingFreqMode) return;
            bool manual = Equals(selected, "manual");
            if (manual)
            {
                uint minVal = minEditor.Read() is uint m && m > 0 ? m : 0;
                uint next = lastDesiredFreqMhz > 0 ? lastDesiredFreqMhz : (minVal > 0 ? minVal : 3500u);
                desiredEditor.RestoreDraft(next);
            }
            else
            {
                if (desiredEditor.Read() is uint cur && cur > 0) lastDesiredFreqMhz = cur;
                desiredEditor.RestoreDraft(null);
            }
            UpdateFrequencyModeVisibility();
            UpdateGroupActions(includePerfmon: false);
        };

        AddGroup(L10n.Get("Cpu_Group_Frequency"), useRectActions: true,
            new Grid[] { minRow, capRow, modeRow, freqDesiredRow, freqEppRow, freqWindowRow, ignoreHostRow },
            new[]
            {
                (SettingFieldEditor)minRow.Tag,
                (SettingFieldEditor)capRow.Tag,
                desiredEditor,
                (SettingFieldEditor)freqEppRow.Tag,
                (SettingFieldEditor)freqWindowRow.Tag,
                (SettingFieldEditor)ignoreHostRow.Tag
            });
    }

    private (Grid Row, SettingsEnergySlider Slider, TextBlock ValText, Grid Header, SettingInfoButton Info) BuildTopologyDiscreteSliderCard(
        string title, string description, Action<int> onSliderPosChanged, Func<int, string>? customHintFormatter = null)
    {
        var row = new Grid();
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var valText = new TextBlock
        {
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(SettingLabel(title, description, out var info));
        Grid.SetColumn(valText, 1);
        header.Children.Add(valText);

        var slider = new SettingsEnergySlider(EnergySliderKind.Custom, compact: true) { HorizontalAlignment = HorizontalAlignment.Stretch };
        slider.CustomHintFormatter = customHintFormatter ?? (pos =>
        {
            var counts = VmProcessorTopologyPreview.GetAchievableCounts(ReadVmCoreCount());
            int count = counts[Math.Clamp(pos, 0, counts.Length - 1)];
            return L10n.Format("Cpu_Numa_Value", count);
        });
        slider.ValueChanged += pos => onSliderPosChanged(pos);

        Grid.SetRow(header, 0);
        Grid.SetRow(slider, 1);
        row.Children.Add(header);
        row.Children.Add(slider);
        StyleCardBorder(row, minHeight: 74, rowSpacing: 6, padding: new Thickness(14, 10, 14, 10));
        return (row, slider, valText, header, info);
    }

    private SettingFieldEditor CreateHiddenField(string property, VmProcessorCapability capability)
    {
        var field = new SettingFieldEditor(typeof(VmProcessorSettings).GetProperty(property)!, SettingEditorKind.Text, deferred: true);
        field.ValueEdited += () => UpdateGroupActions(includePerfmon: false);
        fields.Add((field, capability));
        return field;
    }

    private int ReadVmCoreCount()
    {
        var countEditor = fields.FirstOrDefault(f => f.Editor.Property.Name == nameof(VmProcessorSettings.Count)).Editor;
        return Math.Max(1, countEditor?.ReadEnergyInt(Environment.ProcessorCount) ?? Environment.ProcessorCount);
    }

    private static int ReadFieldInt(SettingFieldEditor editor, int fallback) => editor.Read() switch
    {
        int i => i,
        uint u => (int)Math.Min(u, int.MaxValue),
        ulong ul => (int)Math.Min(ul, int.MaxValue),
        long l => (int)Math.Clamp(l, 0, int.MaxValue),
        _ => fallback
    };

    private void SyncTopologySlidersAndPreview()
    {
        if (topologyPreview == null) return;
        int vcpu = ReadVmCoreCount();
        var smtEditor = fields.FirstOrDefault(f => f.Editor.Property.Name == nameof(VmProcessorSettings.SmtMode)).Editor;
        var numaPerSocketEditor = fields.FirstOrDefault(f => f.Editor.Property.Name == nameof(VmProcessorSettings.MaxNumaNodesPerSocket)).Editor;
        var procPerNumaEditor = fields.FirstOrDefault(f => f.Editor.Property.Name == nameof(VmProcessorSettings.MaxProcessorsPerNumaNode)).Editor;
        var l3Editor = fields.FirstOrDefault(f => f.Editor.Property.Name == nameof(VmProcessorSettings.MaxProcessorCountPerL3)).Editor;
        var clusterEditor = fields.FirstOrDefault(f => f.Editor.Property.Name == nameof(VmProcessorSettings.MaxClusterCountPerSocket)).Editor;
        var l3PolicyEditor = fields.FirstOrDefault(f => f.Editor.Property.Name == nameof(VmProcessorSettings.L3DistributionPolicy)).Editor;
        var l3WaysEditor = fields.FirstOrDefault(f => f.Editor.Property.Name == nameof(VmProcessorSettings.L3CacheWays)).Editor;

        var smt = smtEditor?.Read() is SmtMode s ? s : SmtMode.Inherit;
        int maxPerNuma = Math.Max(1, ReadFieldInt(procPerNumaEditor!, vcpu));
        int maxNumaPerSocket = Math.Max(1, ReadFieldInt(numaPerSocketEditor!, 1));
        int? maxPerL3 = l3Editor?.Read() is uint l3 && l3 > 0 ? (int)l3 : null;
        int? maxClusters = clusterEditor?.Read() is uint cl && cl > 0 ? (int)cl : null;
        int l3Ways = l3WaysEditor?.Read() is uint lw && lw > 0 ? (int)lw : 0;
        var l3Policy = l3PolicyEditor?.Read() is L3DistributionPolicy p ? p : L3DistributionPolicy.SmallToLarge;

        int numaCount = Math.Max(1, (vcpu + maxPerNuma - 1) / maxPerNuma);
        int socketCount = Math.Max(1, (numaCount + maxNumaPerSocket - 1) / maxNumaPerSocket);
        int vpPerNode = Math.Max(1, (vcpu + numaCount - 1) / numaCount);
        int l3PerNode = maxPerL3 is > 0 ? Math.Max(1, (vpPerNode + maxPerL3.Value - 1) / maxPerL3.Value) : 1;

        var counts = VmProcessorTopologyPreview.GetAchievableCounts(vcpu);
        int maxIdx = Math.Max(0, counts.Length - 1);
        int sIdx = Math.Clamp(Array.IndexOf(counts, socketCount), 0, maxIdx);
        int nIdx = Math.Clamp(Array.IndexOf(counts, numaCount), 0, maxIdx);

        updatingTopologySliders = true;
        try
        {
            static string? GetDisabledTip(SettingFieldEditor? ed) => ed != null ? ToolTipService.GetToolTip(ed) as string : null;

            bool baseSocketsOk = !uncertain && (procPerNumaEditor?.IsFieldEnabled ?? false) && (numaPerSocketEditor?.IsFieldEnabled ?? false);
            bool socketsEnabled = baseSocketsOk && counts.Length > 1;
            string? socketsReason = uncertain ? null
                : !baseSocketsOk ? (procPerNumaEditor is { IsFieldEnabled: false } ? GetDisabledTip(procPerNumaEditor) : GetDisabledTip(numaPerSocketEditor))
                : (counts.Length <= 1 ? L10n.Get("Cpu_Cond_Only1Vcpu") : null);
            if (topoSocketsSlider != null)
            {
                topoSocketsSlider.SetRange(0, Math.Max(1, maxIdx));
                topoSocketsSlider.Value = counts.Length > 1 ? sIdx : 0;
            }
            SyncTopoCardAvailability(topoSocketsSlider, topoSocketsHeader, topoSocketsInfo, L10n.Get("Cpu_Sockets_Desc"), socketsEnabled, socketsReason);
            if (topoSocketsValText != null) topoSocketsValText.Text = L10n.Format("Cpu_Sockets_Value", socketCount);

            bool baseNumaOk = !uncertain && (procPerNumaEditor?.IsFieldEnabled ?? false);
            bool numaEnabled = baseNumaOk && counts.Length > 1;
            string? numaReason = uncertain ? null
                : !baseNumaOk ? GetDisabledTip(procPerNumaEditor)
                : (counts.Length <= 1 ? L10n.Get("Cpu_Cond_Only1Vcpu") : null);
            if (topoNumaSlider != null)
            {
                topoNumaSlider.SetRange(0, Math.Max(1, maxIdx));
                topoNumaSlider.Value = counts.Length > 1 ? nIdx : 0;
            }
            SyncTopoCardAvailability(topoNumaSlider, topoNumaHeader, topoNumaInfo, L10n.Get("Cpu_Numa_Desc"), numaEnabled, numaReason);
            if (topoNumaValText != null) topoNumaValText.Text = L10n.Format("Cpu_Numa_Value", numaCount);

            bool baseL3Ok = !uncertain && (l3Editor?.IsFieldEnabled ?? false);
            bool l3Enabled = baseL3Ok && vpPerNode > 1;
            string? l3Reason = uncertain ? null
                : !baseL3Ok ? GetDisabledTip(l3Editor)
                : (vpPerNode <= 1 ? L10n.Get(vcpu <= 1 ? "Cpu_Cond_Only1Vcpu" : "Cpu_Cond_Only1PerNuma") : null);
            if (topoL3Slider != null)
            {
                topoL3Slider.SetRange(1, Math.Max(2, vpPerNode));
                topoL3Slider.Value = Math.Clamp(l3PerNode, 1, Math.Max(2, vpPerNode));
            }
            SyncTopoCardAvailability(topoL3Slider, topoL3Header, topoL3Info, L10n.Get("Cpu_L3_Desc"), l3Enabled, l3Reason);
            if (topoL3ValText != null) topoL3ValText.Text = l3PerNode > 1 ? L10n.Format("Cpu_L3_Value", l3PerNode) : L10n.Get("Common_Auto");

            bool clusterEnabled = !uncertain && (clusterEditor?.IsFieldEnabled ?? false);
            string? clusterReason = uncertain || clusterEnabled ? null : GetDisabledTip(clusterEditor);
            if (topoClusterSlider != null)
            {
                topoClusterSlider.SetRange(0, 8);
                topoClusterSlider.Value = Math.Clamp(maxClusters ?? 0, 0, 8);
            }
            SyncTopoCardAvailability(topoClusterSlider, topoClusterHeader, topoClusterInfo, L10n.Get("Cpu_Cluster_Desc"), clusterEnabled, clusterReason);
            if (topoClusterValText != null) topoClusterValText.Text = maxClusters is > 0 ? L10n.Format("Cpu_Cluster_Value", maxClusters) : L10n.Get("Common_Auto");

            bool waysEnabled = !uncertain && (l3WaysEditor?.IsFieldEnabled ?? false);
            string? waysReason = uncertain || waysEnabled ? null : GetDisabledTip(l3WaysEditor);
            if (topoWaysSlider != null)
            {
                topoWaysSlider.SetRange(0, 64);
                topoWaysSlider.Value = Math.Clamp(l3Ways, 0, 64);
            }
            SyncTopoCardAvailability(topoWaysSlider, topoWaysHeader, topoWaysInfo, L10n.Get("Cpu_Ways_Desc"), waysEnabled, waysReason);
            if (topoWaysValText != null) topoWaysValText.Text = l3Ways > 0 ? L10n.Format("Cpu_Ways_Value", l3Ways) : L10n.Get("Common_Default");

            if (topoL3PolicyInfo != null && l3PolicyEditor != null)
            {
                if (l3PolicyEditor.IsFieldEnabled && l3PerNode > 1 && maxPerL3 is > 0)
                {
                    var sizes = VmProcessorTopologyPreview.SplitIntoL3Groups(vpPerNode, maxPerL3.Value, l3Policy);
                    topoL3PolicyInfo.SetDescription(
                        $"{L10n.Get("Cpu_L3Policy_Desc")}\n\n{L10n.Format("Cpu_L3Policy_Current", string.Join(" + ", sizes), sizes.Length)}",
                        null);
                }
                else
                {
                    topoL3PolicyInfo.SetDescription(
                        L10n.Get("Cpu_L3Policy_Desc"),
                        l3PolicyEditor.IsFieldEnabled ? null : GetDisabledTip(l3PolicyEditor));
                }
            }
        }
        finally { updatingTopologySliders = false; }

        topologyPreview.Update(vcpu, smt, maxPerNuma, maxNumaPerSocket, maxPerL3, maxClusters, l3Policy);
    }

    private static void SyncTopoCardAvailability(
        SettingsEnergySlider? slider, Grid? header, SettingInfoButton? info,
        string baseDescription, bool enabled, string? reason)
    {
        string? tooltip = enabled || string.IsNullOrWhiteSpace(reason) ? null : reason;
        if (slider != null)
        {
            slider.IsEnabled = enabled;
            ToolTipService.SetToolTip(slider, tooltip);
        }
        if (header != null)
        {
            header.Opacity = enabled ? 1.0 : 0.45;
            ToolTipService.SetToolTip(header, tooltip);
        }
        info?.SetDescription(baseDescription, tooltip);
    }

    private void UpdateFrequencyModeVisibility()
    {
        if (freqDesiredRow == null || freqEppRow == null || freqWindowRow == null || freqModePill == null) return;
        var desiredEditor = (SettingFieldEditor)freqDesiredRow.Tag;
        bool isManual = desiredEditor.Read() is uint d && d > 0;
        if (isManual && desiredEditor.Read() is uint cur) lastDesiredFreqMhz = cur;

        updatingFreqMode = true;
        try { freqModePill.SelectedValue = isManual ? "manual" : "auto"; }
        finally { updatingFreqMode = false; }

        bool pillEnabled = !uncertain && desiredEditor.IsFieldEnabled;
        freqModePill.SetControlEnabled(pillEnabled);
        ToolTipService.SetToolTip(freqModePill, pillEnabled ? null : ToolTipService.GetToolTip(desiredEditor));
        freqDesiredRow.Visibility = isManual ? Visibility.Visible : Visibility.Collapsed;
        freqEppRow.Visibility = isManual ? Visibility.Collapsed : Visibility.Visible;
        freqWindowRow.Visibility = isManual ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SyncIsaMigrationPill()
    {
        if (isaMigPill == null || isaMigEnabledField == null || isaMigModeField == null) return;
        bool migOn = isaMigEnabledField.Read() is true;
        object selected = migOn
            ? (isaMigModeField.Read() is VmMigrationCompatibilityMode mode ? mode : VmMigrationCompatibilityMode.MinimumFeatureSet)
            : "off";

        updatingIsaMig = true;
        try { isaMigPill.SelectedValue = selected; }
        finally { updatingIsaMig = false; }

        bool enabled = !uncertain && isaMigEnabledField.IsFieldEnabled;
        isaMigPill.SetControlEnabled(enabled);
        if (isaMigLabel != null) isaMigLabel.Opacity = enabled ? 1.0 : 0.45;
        string? reason = ToolTipService.GetToolTip(isaMigEnabledField) as string;
        ToolTipService.SetToolTip(isaMigPill, enabled ? null : reason);
        if (isaMigLabel != null) ToolTipService.SetToolTip(isaMigLabel, enabled ? null : reason);
    }

    private static string FormatAddrSpace(int bits)
    {
        if (bits < 30 || bits > 56) return L10n.Get("Cpu_InvalidBitWidth");
        if (bits < 40) return $"{1L << (bits - 30)} GB";
        if (bits < 50) return $"{1L << (bits - 40)} TB";
        return $"{1L << (bits - 50)} PB";
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

    private void UpdateDependentAvailability()
    {
        if (Updating || snapshot == null || host == null) return;
        var draft = snapshot.Clone();
        foreach (var (field, _) in fields)
            if (field.HasPendingEdit) field.Property.SetValue(draft, field.Read());

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
                field.SetAvailability(!uncertain && condition.CanModify, reason);
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
                hwGuestLimitField.SetAvailability(!uncertain, null);
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
                l3PolicyField.SetAvailability(!uncertain, null);
        }
    }

    private void AddGroup(string title, bool useRectActions, params Grid[] editors)
        => AddGroup(title, useRectActions, editors, editors.Select(row => (SettingFieldEditor)row.Tag).ToArray());

    private void AddGroup(string title, bool useRectActions, Grid[] rows, SettingFieldEditor[] grouped)
    {
        var apply = useRectActions ? SettingsActionButton.CreateRect(L10n.Get("Common_Apply"), primary: true) : SettingsActionButton.Create(L10n.Get("Action_ApplyPageChanges"), "\uE73E", primary: true, iconSize: 24);
        var revert = useRectActions ? SettingsActionButton.CreateRect(L10n.Get("Common_Revert")) : SettingsActionButton.Create(L10n.Get("Action_RevertPageChanges"), "\uE7A7");
        apply.IsEnabled = revert.IsEnabled = false;
        apply.Click += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped || snapshot == null || uncertain) return;
            var submitted = grouped.Where(f => f.HasPendingEdit).ToArray();
            await RunAsync(async () =>
            {
                if (await ApplyAsync(submitted)) ShowAppliedToast(L10n.Get("Common_Applied"));
            });
        };
        revert.Click += (_, _) =>
        {
            foreach (var field in grouped) field.Revert();
            UpdateGroupActions();
        };
        groupActions.Add((grouped, apply, revert));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 2, 0, 0) };
        actions.Children.Add(revert);
        actions.Children.Add(apply);
        var grid = new SettingsFieldPanel();
        foreach (var row in rows) grid.Children.Add(row);
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(grid);
        content.Children.Add(actions);
        sections.Add((title, new Border { Child = content, Margin = new Thickness(0, 0, 0, 16) }));
    }

    private static SettingOption[] Options<T>(params (T Value, string Label)[] options) => options.Select(o => new SettingOption(o.Value!, o.Label)).ToArray();

    private Grid Toggle(string title, string property, VmProcessorCapability capability, string? description = null)
        => Add(title, property, capability, SettingEditorKind.Toggle, description: description);

    private Grid Text(string title, string property, VmProcessorCapability capability, string? description = null)
        => Add(title, property, capability, SettingEditorKind.Text, description: description);

    private Grid Number(string title, string property, VmProcessorCapability capability, decimal scale = 1, string? description = null)
        => Add(title, property, capability, SettingEditorKind.Text, scale: scale, description: description);

    private Grid Energy(string title, string property, VmProcessorCapability capability, EnergySliderKind energyKind, decimal scale = 1, string? description = null)
        => Add(title, property, capability, SettingEditorKind.Energy, scale: scale, description: description, energyKind: energyKind);

    private Grid Choice(string title, string property, VmProcessorCapability capability, SettingOption[] options, string? description = null)
        => Add(title, property, capability, SettingEditorKind.Choice, options, description: description);

    private Grid SingleRowCustomSlider(
        string title, string property, VmProcessorCapability capability,
        int sliderMin, int sliderMax,
        Func<object?, int> toSliderPos,
        Func<int, object?> fromSliderPos,
        Func<int, string> formatTooltip,
        string? description = null)
    {
        var adapter = new CustomSliderAdapter(sliderMin, sliderMax, toSliderPos, fromSliderPos, formatTooltip);
        var field = new SettingFieldEditor(typeof(VmProcessorSettings).GetProperty(property)!, SettingEditorKind.SingleRowSlider, deferred: true, customSlider: adapter);
        field.HideApplyButton();
        field.ValueEdited += () => UpdateGroupActions(IsDependencyTrigger(capability));
        fields.Add((field, capability));

        string? help = !string.IsNullOrWhiteSpace(description) ? description : ApiFieldHelp.For(field.Property);
        var row = FieldRow(title, help, field, SettingEditorKind.Toggle);
        field.Width = 310;
        field.HorizontalAlignment = HorizontalAlignment.Right;
        SettingsFieldPanel.SetSpan2(row, true);
        StyleCardBorder(row, minHeight: 68, padding: new Thickness(17, 14, 17, 14));
        row.Tag = field;
        return row;
    }

    private Grid HeaderValueCustomSlider(
        string title, string property, VmProcessorCapability capability,
        int sliderMin, int sliderMax, string unitText, string placeholder, int maxTypedValue, bool allowAutoZero,
        Func<object?, int> toSliderPos,
        Func<int, object?> fromSliderPos,
        Func<int, string> formatTooltip,
        string? description = null)
    {
        var adapter = new CustomSliderAdapter(sliderMin, sliderMax, toSliderPos, fromSliderPos, formatTooltip, unitText, placeholder, maxTypedValue, allowAutoZero);
        var field = new SettingFieldEditor(typeof(VmProcessorSettings).GetProperty(property)!, SettingEditorKind.HeaderSlider, deferred: true, customSlider: adapter);
        field.HideApplyButton();
        field.ValueEdited += () => UpdateGroupActions(IsDependencyTrigger(capability));
        fields.Add((field, capability));

        string? help = !string.IsNullOrWhiteSpace(description) ? description : ApiFieldHelp.For(field.Property);
        var row = EnergyFieldRow(title, help, field);
        StyleCardBorder(row, minHeight: 114, rowSpacing: 12);
        row.Tag = field;
        return row;
    }

    private Grid CreateSingleRowCard(string title, string description, FrameworkElement rightControl, bool span2, SettingFieldEditor? editor = null)
        => CreateSingleRowCard(title, description, rightControl, span2, out _, editor);

    private Grid CreateSingleRowCard(string title, string description, FrameworkElement rightControl, bool span2, out Grid label, SettingFieldEditor? editor = null)
    {
        var row = new Grid { ColumnSpacing = 12, MinHeight = 40 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label = SettingLabel(title, description, editor);
        row.Children.Add(label);
        Grid.SetColumn(rightControl, 1);
        row.Children.Add(rightControl);
        if (span2) SettingsFieldPanel.SetSpan2(row, true);
        StyleCardBorder(row, minHeight: 68, padding: new Thickness(17, 14, 17, 14));
        return row;
    }

    private Grid Add(string title, string property, VmProcessorCapability capability, SettingEditorKind kind, SettingOption[]? options = null, decimal scale = 1, string? description = null, EnergySliderKind? energyKind = null)
    {
        var field = new SettingFieldEditor(typeof(VmProcessorSettings).GetProperty(property)!, kind, options, scale, deferred: true, energyKind: energyKind);
        field.HideApplyButton();
        if (kind == SettingEditorKind.Text && field.Property.PropertyType != typeof(string)) field.UseNumericStepper(title);
        field.ValueEdited += () => UpdateGroupActions(IsDependencyTrigger(capability));
        fields.Add((field, capability));
        string? help = !string.IsNullOrWhiteSpace(description)
            ? description
            : ApiFieldHelp.For(field.Property);

        Grid row;
        if (kind == SettingEditorKind.Energy)
        {
            row = EnergyFieldRow(title, help, field);
            StyleCardBorder(row, minHeight: 114, rowSpacing: 12);
        }
        else if (kind == SettingEditorKind.Choice)
        {
            row = FieldRow(title, help, field, SettingEditorKind.Toggle);
            field.HorizontalAlignment = HorizontalAlignment.Right;
            field.Width = (options?.Length ?? 0) >= 4 ? 420 : 320;
            SettingsFieldPanel.SetSpan2(row, true);
            StyleCardBorder(row, minHeight: 68, padding: new Thickness(17, 14, 17, 14));
        }
        else if (kind == SettingEditorKind.Toggle)
        {
            row = FieldRow(title, help, field, SettingEditorKind.Toggle);
            StyleCardBorder(row, minHeight: 68, padding: new Thickness(17, 14, 17, 14));
        }
        else if (field.Property.PropertyType == typeof(string))
        {
            row = FieldRow(title, help, field, SettingEditorKind.Toggle);
            field.Width = 310;
            field.HorizontalAlignment = HorizontalAlignment.Right;
            SettingsFieldPanel.SetSpan2(row, true);
            StyleCardBorder(row, minHeight: 68, padding: new Thickness(17, 14, 17, 14));
        }
        else
        {
            row = FieldRow(title, help, field, SettingEditorKind.Toggle);
            field.HorizontalAlignment = HorizontalAlignment.Right;
            StyleCardBorder(row, minHeight: 68, padding: new Thickness(17, 14, 17, 14));
        }

        row.Tag = field;
        return row;
    }

    private async Task UnlockAndReloadAsync()
    {
        uncertain = false;
        await RunAsync(LoadAsync);
    }

    protected override async Task LoadAsync()
    {
        snapshot = null;
        foreach (var field in fields) field.Editor.SetAvailability(false, null);
        var settingsTask = VmProcessorService.GetVmProcessorAsync(VmId);
        var hostTask = ReadHostCapabilitiesAsync();
        var contextTask = ReadContextAsync();
        await Task.WhenAll(settingsTask, hostTask, contextTask);
        var observation = (await settingsTask).RequireData();
        var settings = observation.Values;
        host = await hostTask;
        powerState = (await contextTask).State;
        if (IsStopped) return;
        snapshot = settings;
        Updating = true;
        bool hasUnavailable = false;
        try
        {
            foreach (var (field, capability) in fields)
            {
                var value = field.Property.GetValue(settings);
                var condition = VmProcessorService.GetModificationCondition(settings, field.Property.Name, powerState, host);
                bool available = observation.States.GetValueOrDefault(field.Property.Name) == VmQueryValueState.Available;
                if (!available) hasUnavailable = true;
                field.Load(value, !uncertain && available && condition.CanModify,
                    !available ? L10n.Get("Err_NoValidData") : condition.CanModify ? null : CoreMessageResolver.ResolveConditionReason(condition));
            }
        }
        finally { Updating = false; }
        UpdateGroupActions();
        if (uncertain)
            ShowError(L10n.Get("Err_UncertainLock"), L10n.Get("Action_RefreshStatus"), UnlockAndReloadAsync);
        else if (hasUnavailable)
            ShowError(L10n.Get("Err_PartialReadUnavailable"), L10n.Get("Action_RefreshStatus"), UnlockAndReloadAsync);
    }

    private async Task<bool> ApplyAsync(params SettingFieldEditor[] submitted)
    {
        if (snapshot == null || host == null) return false;
        var proposed = snapshot.Clone();
        var changed = new List<string>();
        foreach (var field in submitted)
        {
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
        try { await LoadAsync(); }
        catch (Exception ex) { failure ??= ex; }
        foreach (var draft in otherDrafts) draft.Field.RestoreDraft(draft.Draft);
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
        int maxLogical = Math.Max(1, Environment.ProcessorCount);
        if (changed.Contains(nameof(VmProcessorSettings.Count)) && (proposed.Count < 1 || proposed.Count > maxLogical))
            throw new InvalidOperationException(L10n.Format("Cpu_Val_CountRange", maxLogical));
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

internal enum SettingEditorKind { Text, Toggle, Choice, Energy, SingleRowSlider, HeaderSlider }
internal sealed record SettingOption(object Value, string Label, bool Enabled = true);

internal sealed record CustomSliderAdapter(
    int SliderMin,
    int SliderMax,
    Func<object?, int> ToSliderPos,
    Func<int, object?> FromSliderPos,
    Func<int, string> FormatTooltip,
    string UnitText = "",
    string Placeholder = "",
    int MaxTypedValue = 0,
    bool AllowAutoZero = true,
    Func<int, int>? SnapValue = null);

/// <summary>设置值与 WinUI 控件之间的显示适配，不包含宿主业务规则。</summary>
internal sealed class SettingFieldEditor : UserControl
{
    private readonly Grid layout = new() { ColumnSpacing = 8 };
    private readonly decimal scale;
    private readonly TextBox? text;
    private readonly SettingsEnergySlider? energySlider;
    private readonly CustomSliderAdapter? customSlider;
    private readonly TextBlock? customUnitText;
    private readonly ToggleSwitch? toggle;
    private readonly SettingsSegmentedPill? pillChoice;
    private readonly Button? apply;
    private readonly bool deferred;
    private object? loadedValue;
    private object? currentCustomValue;
    private Button? decrease;
    private Button? increase;
    private Border? stepperBox;
    private decimal minimum;
    private decimal? maximum;
    private string loadedText = "";
    private bool loading;
    public PropertyInfo Property { get; }
    public bool IsFieldEnabled { get; private set; }
    public FrameworkElement? HeaderValueControl { get; }
    public event EventHandler? SubmitRequested;
    public event Action<string?>? AvailabilityChanged;
    public event Action? ValueEdited;

    public bool HasPendingEdit => customSlider != null ? !Equals(NormalizeCompare(currentCustomValue), NormalizeCompare(loadedValue))
        : text != null ? text.Text != loadedText
        : toggle != null ? toggle.IsOn != (loadedValue is true)
        : pillChoice != null && !Equals(pillChoice.SelectedValue, loadedValue);

    public string TextPlaceholder
    {
        get => text?.PlaceholderText ?? "";
        set { if (text != null) text.PlaceholderText = value; }
    }

    private object? NormalizeCompare(object? v)
    {
        if (customSlider is { AllowAutoZero: true } && v is uint u && u == 0) return null;
        return v;
    }

    private static object ConvertSliderTypedValue(long value, Type propertyType)
    {
        var underlying = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (underlying == typeof(int)) return checked((int)value);
        if (underlying == typeof(uint)) return checked((uint)Math.Max(0, value));
        if (underlying == typeof(long)) return value;
        if (underlying == typeof(ulong)) return checked((ulong)Math.Max(0, value));
        if (underlying == typeof(double)) return (double)value;
        return Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
    }

    private static string FormatCustomSliderValue(object? value, bool allowAutoZero)
    {
        if (value == null) return "";
        if (allowAutoZero && value is uint u && u == 0) return "";
        if (value is double d) return Math.Round(d).ToString("0", CultureInfo.InvariantCulture);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    public SettingFieldEditor(
        PropertyInfo property,
        SettingEditorKind kind,
        SettingOption[]? options = null,
        decimal scale = 1,
        bool deferred = false,
        EnergySliderKind? energyKind = null,
        CustomSliderAdapter? customSlider = null)
    {
        Property = property;
        this.scale = scale;
        this.deferred = deferred;
        this.customSlider = customSlider;
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Content = layout;
        IsEnabled = false;

        if (kind == SettingEditorKind.Energy && energyKind is { } ek)
        {
            text = new TextBox
            {
                FontSize = 20,
                Padding = new Thickness(4, 0, 4, 0),
                MinWidth = 28,
                MinHeight = 30,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            MakeBorderlessTransparent(text);
            var valueStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            valueStack.Children.Add(text);
            if (scale == 1000)
            {
                valueStack.Children.Add(new TextBlock
                {
                    Text = "%",
                    FontSize = 13,
                    Opacity = 0.72,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            HeaderValueControl = valueStack;

            energySlider = new SettingsEnergySlider(ek) { HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumnSpan(energySlider, 2);
            layout.Children.Add(energySlider);

            energySlider.ValueChanged += val =>
            {
                if (loading) return;
                loading = true;
                try { text.Text = val.ToString(CultureInfo.InvariantCulture); }
                finally { loading = false; }
                ValueEdited?.Invoke();
            };
            text.TextChanged += (_, _) =>
            {
                if (loading) return;
                if (decimal.TryParse(text.Text.Trim(), NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out decimal parsed))
                    energySlider.Value = (int)Math.Round(parsed);
                ValueEdited?.Invoke();
            };
        }
        else if (kind == SettingEditorKind.SingleRowSlider && customSlider != null)
        {
            energySlider = new SettingsEnergySlider(EnergySliderKind.Custom, compact: false)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CustomHintFormatter = customSlider.FormatTooltip
            };
            energySlider.SetRange(customSlider.SliderMin, customSlider.SliderMax);
            Grid.SetColumnSpan(energySlider, 2);
            layout.Children.Add(energySlider);

            energySlider.ValueChanged += pos =>
            {
                if (loading) return;
                currentCustomValue = customSlider.FromSliderPos(pos);
                ValueEdited?.Invoke();
            };
        }
        else if (kind == SettingEditorKind.HeaderSlider && customSlider != null)
        {
            text = new TextBox
            {
                FontSize = 20,
                Padding = new Thickness(4, 0, 4, 0),
                MinWidth = 42,
                MinHeight = 30,
                TextAlignment = TextAlignment.Right,
                PlaceholderText = customSlider.Placeholder,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            MakeBorderlessTransparent(text);
            customUnitText = new TextBlock
            {
                Text = customSlider.UnitText,
                FontSize = 13,
                Opacity = 0.72,
                VerticalAlignment = VerticalAlignment.Center
            };
            var valueStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            valueStack.Children.Add(text);
            valueStack.Children.Add(customUnitText);
            HeaderValueControl = valueStack;

            energySlider = new SettingsEnergySlider(EnergySliderKind.Custom)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CustomHintFormatter = customSlider.FormatTooltip,
                SnapValue = customSlider.SnapValue
            };
            energySlider.SetRange(customSlider.SliderMin, customSlider.SliderMax);
            Grid.SetColumnSpan(energySlider, 2);
            layout.Children.Add(energySlider);

            energySlider.ValueChanged += pos =>
            {
                if (loading) return;
                currentCustomValue = customSlider.FromSliderPos(pos);
                loading = true;
                try
                {
                    text.Text = FormatCustomSliderValue(currentCustomValue, customSlider.AllowAutoZero);
                    if (customUnitText != null)
                        customUnitText.Visibility = string.IsNullOrEmpty(text.Text) ? Visibility.Collapsed : Visibility.Visible;
                }
                finally { loading = false; }
                ValueEdited?.Invoke();
            };
            text.TextChanged += (_, _) =>
            {
                if (loading) return;
                string raw = text.Text.Trim();
                if (string.IsNullOrEmpty(raw) && customSlider.AllowAutoZero)
                {
                    currentCustomValue = ConvertSliderTypedValue(0, Property.PropertyType);
                    loading = true;
                    try { energySlider.Value = customSlider.SliderMin; }
                    finally { loading = false; }
                    if (customUnitText != null) customUnitText.Visibility = Visibility.Collapsed;
                    ValueEdited?.Invoke();
                    return;
                }
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                {
                    if (customSlider.MaxTypedValue > 0 && parsed > customSlider.MaxTypedValue)
                        parsed = customSlider.MaxTypedValue;
                    if (!customSlider.AllowAutoZero && parsed < 0)
                        parsed = 0;
                    currentCustomValue = ConvertSliderTypedValue(parsed, Property.PropertyType);
                    loading = true;
                    try
                    {
                        int pos = customSlider.ToSliderPos(currentCustomValue);
                        if (!customSlider.AllowAutoZero && pos > customSlider.SliderMax)
                            energySlider.SetRange(customSlider.SliderMin, pos);
                        energySlider.Value = pos;
                    }
                    finally { loading = false; }
                    if (customUnitText != null) customUnitText.Visibility = Visibility.Visible;
                    ValueEdited?.Invoke();
                }
            };
        }
        else if (kind == SettingEditorKind.Toggle)
        {
            toggle = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0 };
            toggle.Toggled += (_, _) => EditedSelection();
            layout.Children.Add(toggle);
        }
        else if (kind == SettingEditorKind.Choice)
        {
            pillChoice = new SettingsSegmentedPill { HorizontalAlignment = HorizontalAlignment.Stretch };
            pillChoice.SetOptions(options ?? []);
            pillChoice.SelectionChanged += _ => EditedSelection();
            Grid.SetColumnSpan(pillChoice, 2);
            layout.Children.Add(pillChoice);
        }
        else
        {
            text = new TextBox { Width = property.PropertyType == typeof(string) ? 310 : 160 };
            apply = new Button { Content = L10n.Get("Common_Apply"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], IsEnabled = false };
            text.TextChanged += (_, _) =>
            {
                if (loading) return;
                apply.IsEnabled = HasPendingEdit;
                UpdateStepper();
                ValueEdited?.Invoke();
            };
            apply.Click += (_, _) => SubmitRequested?.Invoke(this, EventArgs.Empty);
            layout.Children.Add(text);
            Grid.SetColumn(apply, 1);
            layout.Children.Add(apply);
        }
    }

    private void EditedSelection()
    {
        if (loading) return;
        ValueEdited?.Invoke();
        if (!deferred) SubmitRequested?.Invoke(this, EventArgs.Empty);
    }

    public object? CaptureDraft() => customSlider != null ? currentCustomValue
        : text != null ? text.Text
        : toggle != null ? toggle.IsOn
        : pillChoice?.SelectedValue;

    public void RestoreDraft(object? value)
    {
        loading = true;
        try
        {
            if (customSlider != null)
            {
                currentCustomValue = value;
                if (energySlider != null)
                {
                    int pos = customSlider.ToSliderPos(value);
                    if (!customSlider.AllowAutoZero)
                        energySlider.SetRange(customSlider.SliderMin, Math.Max(customSlider.SliderMax, pos));
                    energySlider.Value = pos;
                }
                if (text != null)
                {
                    text.Text = FormatCustomSliderValue(value, customSlider.AllowAutoZero);
                    if (customUnitText != null)
                        customUnitText.Visibility = string.IsNullOrEmpty(text.Text) ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            else if (text != null)
            {
                text.Text = value is string s ? s
                    : value == null ? ""
                    : scale == 1 ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
                    : (Convert.ToDecimal(value) / scale).ToString(CultureInfo.InvariantCulture);
                if (energySlider != null && decimal.TryParse(text.Text, NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out decimal sliderVal))
                    energySlider.Value = (int)Math.Round(sliderVal);
            }
            if (toggle != null) toggle.IsOn = value is true;
            if (pillChoice != null) pillChoice.SelectedValue = value;
        }
        finally { loading = false; }
        if (apply != null) apply.IsEnabled = HasPendingEdit;
        UpdateStepper();
        ValueEdited?.Invoke();
    }

    public void Revert() => RestoreDraft(customSlider != null ? loadedValue : text != null ? loadedText : loadedValue);

    private static void MakeBorderlessTransparent(TextBox box, bool clearInteractive = false)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        box.BorderThickness = new Thickness(0);
        box.Background = transparent;
        box.Resources["TextControlBackground"] = transparent;
        box.Resources["TextControlBackgroundDisabled"] = transparent;
        box.Resources["TextControlBorderBrushDisabled"] = transparent;
        if (clearInteractive)
        {
            box.Resources["TextControlBackgroundPointerOver"] = transparent;
            box.Resources["TextControlBackgroundFocused"] = transparent;
            box.Resources["TextControlBorderBrushPointerOver"] = transparent;
            box.Resources["TextControlBorderBrushFocused"] = transparent;
        }
    }

    private static Button CreateStepperButton(string tooltip, string glyph)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var btn = new Button
        {
            Width = 28,
            Height = 34,
            MinWidth = 28,
            MinHeight = 34,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = transparent,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = new FontIcon { Glyph = glyph, FontSize = 11 }
        };
        btn.Resources["ButtonBackground"] = transparent;
        btn.Resources["ButtonBackgroundDisabled"] = transparent;
        btn.Resources["ButtonBorderBrush"] = transparent;
        btn.Resources["ButtonBorderBrushPointerOver"] = transparent;
        btn.Resources["ButtonBorderBrushPressed"] = transparent;
        btn.Resources["ButtonBorderBrushDisabled"] = transparent;
        ToolTipService.SetToolTip(btn, tooltip);
        return btn;
    }

    public void UseNumericStepper(string title)
    {
        if (text == null || Property.PropertyType == typeof(string)) return;
        minimum = Property.Name is nameof(VmProcessorSettings.Count) or nameof(VmProcessorSettings.RelativeWeight) or nameof(VmProcessorSettings.MaxProcessorCountPerL3) or nameof(VmProcessorSettings.MaxClusterCountPerSocket) ? 1 : 0;
        maximum = Property.Name == nameof(VmProcessorSettings.MaxClusterCountPerSocket) ? 8
            : Property.Name == nameof(VmProcessorSettings.L3CacheWays) ? 1024
            : Property.Name == nameof(VmProcessorSettings.MaxHwIsolatedGuests) ? 64
            : scale == 1000 ? 100 : null;
        HideApplyButton();
        // Clear layout before re-parenting `text` into `inner`, otherwise WinUI throws E_FAIL / 0x800F1000.
        layout.Children.Clear();
        layout.ColumnDefinitions.Clear();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnSpacing = 0;

        MakeBorderlessTransparent(text, clearInteractive: true);
        text.FontSize = 15;
        text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        text.Padding = new Thickness(0);
        text.MinWidth = 48;
        text.Width = 48;
        text.MinHeight = 0;
        text.Height = double.NaN;
        text.TextAlignment = TextAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.VerticalContentAlignment = VerticalAlignment.Center;
        text.PlaceholderText = Nullable.GetUnderlyingType(Property.PropertyType) != null ? L10n.Get("Common_Auto") : "";
        text.Loaded += (_, _) =>
        {
            if (VisualTreeHelper.GetChildrenCount(text) == 0) return;
            var templateRoot = VisualTreeHelper.GetChild(text, 0);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(templateRoot); i++)
            {
                if (VisualTreeHelper.GetChild(templateRoot, i) is not FrameworkElement child) continue;
                if (child.Name is "ContentElement" or "PlaceholderTextContentPresenter")
                    child.VerticalAlignment = VerticalAlignment.Center;
                else if (child.Name == "DeleteButton")
                {
                    child.Width = 0;
                    child.MinWidth = 0;
                    child.MaxWidth = 0;
                    child.Height = 0;
                    child.MinHeight = 0;
                    child.MaxHeight = 0;
                    child.Opacity = 0;
                    child.IsHitTestVisible = false;
                }
            }
        };

        var textHost = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        textHost.PointerPressed += (_, _) =>
        {
            if (text.IsEnabled && text.FocusState == FocusState.Unfocused)
                text.Focus(FocusState.Pointer);
        };
        textHost.Children.Add(text);

        decrease = CreateStepperButton(L10n.Format("Step_Decrease", title), "\uE738");
        increase = CreateStepperButton(L10n.Format("Step_Increase", title), "\uE710");
        decrease.Click += (_, _) => Step(-1);
        increase.Click += (_, _) => Step(1);

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        Grid.SetColumn(decrease, 0);
        Grid.SetColumn(textHost, 1);
        Grid.SetColumn(increase, 2);
        inner.Children.Add(decrease);
        inner.Children.Add(textHost);
        inner.Children.Add(increase);

        stepperBox = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = inner,
            VerticalAlignment = VerticalAlignment.Center
        };
        void ApplyBoxTheme()
        {
            bool light = stepperBox.ActualTheme == ElementTheme.Light;
            stepperBox.Background = new SolidColorBrush(light
                ? Windows.UI.Color.FromArgb(8, 0, 0, 0)
                : Windows.UI.Color.FromArgb(61, 0, 0, 0));
            stepperBox.BorderBrush = new SolidColorBrush(light
                ? Windows.UI.Color.FromArgb(20, 0, 0, 0)
                : Windows.UI.Color.FromArgb(22, 255, 255, 255));
        }
        ApplyBoxTheme();
        stepperBox.ActualThemeChanged += (_, _) => ApplyBoxTheme();

        layout.Children.Add(stepperBox);
        UpdateStepper();
    }

    private void Step(int direction)
    {
        if (text != null && SettingsNumericStep.TryGetValue(text.Text, Property.PropertyType, scale, direction, minimum, maximum, out var value))
            text.Text = value;
    }

    private void UpdateStepper()
    {
        if (stepperBox != null) stepperBox.Opacity = IsFieldEnabled ? 1.0 : 0.45;
        if (text == null || decrease == null || increase == null) return;
        decrease.IsEnabled = IsFieldEnabled && SettingsNumericStep.TryGetValue(text.Text, Property.PropertyType, scale, -1, minimum, maximum, out _);
        increase.IsEnabled = IsFieldEnabled && SettingsNumericStep.TryGetValue(text.Text, Property.PropertyType, scale, 1, minimum, maximum, out _);
    }

    public void HideApplyButton()
    {
        if (apply == null) return;
        apply.Visibility = Visibility.Collapsed;
        layout.ColumnSpacing = 0;
    }

    public void UseAvailableWidth()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Center;
        if (text != null) { text.Width = double.NaN; text.MinWidth = 0; }
    }

    public void SetOptions(IEnumerable<SettingOption> options)
    {
        if (pillChoice == null) return;
        loading = true;
        try
        {
            pillChoice.SetOptions(options);
        }
        finally { loading = false; }
    }

    public void Load(object? value, bool enabled, string? reason)
    {
        loading = true;
        try
        {
            loadedValue = value;
            if (customSlider != null)
            {
                currentCustomValue = value;
                if (energySlider != null)
                {
                    int pos = customSlider.ToSliderPos(value);
                    if (!customSlider.AllowAutoZero)
                        energySlider.SetRange(customSlider.SliderMin, Math.Max(customSlider.SliderMax, pos));
                    energySlider.Value = pos;
                }
                if (text != null)
                {
                    loadedText = FormatCustomSliderValue(value, customSlider.AllowAutoZero);
                    text.Text = loadedText;
                    if (customUnitText != null)
                        customUnitText.Visibility = string.IsNullOrEmpty(loadedText) ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            else
            {
                if (toggle != null) toggle.IsOn = value is true;
                if (text != null)
                {
                    loadedText = value == null ? "" : scale == 1 ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : (Convert.ToDecimal(value) / scale).ToString(CultureInfo.InvariantCulture);
                    text.Text = loadedText;
                    if (energySlider != null && decimal.TryParse(loadedText, NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out decimal sliderVal))
                        energySlider.Value = (int)Math.Round(sliderVal);
                    if (apply != null) apply.IsEnabled = false;
                }
                if (pillChoice != null) pillChoice.SelectedValue = value;
            }
            SetAvailability(enabled, reason);
        }
        finally { loading = false; }
        UpdateStepper();
    }

    public int ReadEnergyInt(int fallback) =>
        text != null && decimal.TryParse(text.Text.Trim(), NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out decimal val)
            ? (int)Math.Round(val) : fallback;

    public void UpdateEnergyContext(int vmCoreCount, int hostLogicalCount) => energySlider?.SetContext(vmCoreCount, hostLogicalCount);

    public void SetEnergyConstraint(int? minAllowed, int? maxAllowed, string? reason) => energySlider?.SetCoupledBounds(minAllowed, maxAllowed, reason);

    public void SetAvailability(bool enabled, string? reason)
    {
        IsFieldEnabled = enabled;
        IsEnabled = enabled;
        if (text != null) text.IsEnabled = enabled;
        if (energySlider != null) energySlider.IsEnabled = enabled;
        pillChoice?.SetControlEnabled(enabled);
        ToolTipService.SetToolTip(this, reason);
        AvailabilityChanged?.Invoke(reason);
        UpdateStepper();
    }

    public object? Read()
    {
        if (customSlider != null) return currentCustomValue;
        if (toggle != null) return toggle.IsOn;
        if (pillChoice != null) return pillChoice.SelectedValue;
        var type = Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;
        if (type == typeof(string)) return text!.Text;
        if (string.IsNullOrWhiteSpace(text!.Text) && Nullable.GetUnderlyingType(Property.PropertyType) != null) return null;
        try
        {
            if (type.IsEnum) return Enum.Parse(type, text.Text);
            if (scale == 1) return Convert.ChangeType(text.Text, type, CultureInfo.InvariantCulture);
            decimal scaled = decimal.Parse(text.Text, CultureInfo.InvariantCulture) * scale;
            if (decimal.Truncate(scaled) != scaled) throw new FormatException();
            return Convert.ChangeType(scaled, type, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        { throw new InvalidOperationException(L10n.Get("Editor_InvalidNumberRange"), ex); }
    }
}
