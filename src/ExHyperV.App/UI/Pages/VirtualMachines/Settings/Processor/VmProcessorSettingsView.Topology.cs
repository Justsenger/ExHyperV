using System.Globalization;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Processor;

public sealed partial class VmProcessorSettingsView
{
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
        sections.Add((L10n.Get("Cpu_Group_Topology"), new Border { Child = content }));
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

        var slider = new SettingsEnergySlider(EnergySliderKind.Custom, compact: false) { HorizontalAlignment = HorizontalAlignment.Stretch };
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
        return Math.Clamp(countEditor?.ReadEnergyInt(snapshot?.Count ?? 1) ?? 1, 1, hostMaxVirtualCpus);
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
}
