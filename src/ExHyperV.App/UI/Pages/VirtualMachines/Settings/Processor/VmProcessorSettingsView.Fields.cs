
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Processor;

public sealed partial class VmProcessorSettingsView
{
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
        sections.Add((title, new Border { Child = content }));
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
}
