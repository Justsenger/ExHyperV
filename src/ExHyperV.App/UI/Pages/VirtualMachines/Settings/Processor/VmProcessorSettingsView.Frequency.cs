
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Processor;

public sealed partial class VmProcessorSettingsView
{
    // 频率调节「调频方式」联动控件
    private SettingsSegmentedPill? freqModePill;
    private Grid? freqDesiredRow;
    private Grid? freqEppRow;
    private Grid? freqWindowRow;
    private uint lastDesiredFreqMhz = 3500;
    private bool updatingFreqMode;

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
}
