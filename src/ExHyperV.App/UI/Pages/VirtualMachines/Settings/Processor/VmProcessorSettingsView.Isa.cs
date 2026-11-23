
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Processor;

public sealed partial class VmProcessorSettingsView
{
    // 指令集能力「跨机迁移兼容」联动控件
    private SettingsSegmentedPill? isaMigPill;
    private Grid? isaMigLabel;
    private SettingFieldEditor? isaMigEnabledField;
    private SettingFieldEditor? isaMigModeField;
    private bool updatingIsaMig;

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
        sections.Add((L10n.Get("Cpu_Group_Isa"), new Border { Child = content }));
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
}
