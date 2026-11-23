using System.Collections.Concurrent;
using System.Globalization;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmMemorySettingsView : VmSettingsView
{
    private readonly List<SettingFieldEditor> fields = [];
    private readonly List<SettingFieldEditor> mmioFields = [];
    private readonly Dictionary<string, SettingFieldEditor> byName = new(StringComparer.Ordinal);
    private readonly List<(string Title, Border Content)> sections = [];
    private readonly List<(SettingFieldEditor[] Fields, Button Apply, Button Revert, bool IsMmio)> groupActions = [];
    private VmMemorySettings? snapshot;
    private VmMmioSettings? mmioSnapshot;
    private VmProcessorService.HostCapabilities? host;
    private Dictionary<string, VmQueryValueState> fieldStates = new(StringComparer.Ordinal);
    private VmState powerState;
    private static readonly ConcurrentDictionary<Guid, byte> unknownOperations = new();
    private bool uncertain
    {
        get => unknownOperations.ContainsKey(VmId);
        set
        {
            if (value) unknownOperations.TryAdd(VmId, 0);
            else unknownOperations.TryRemove(VmId, out _);
        }
    }

    private Task UnlockAndReloadAsync()
    {
        uncertain = false;
        return RunAsync(LoadAsync);
    }

    private Exception CreateApplyFailure(string? message, Exception? reloadFailure)
    {
        string detail = string.IsNullOrWhiteSpace(message) ? L10n.Get("Err_GenericDefault") : message;
        if (uncertain) detail = L10n.Format("Err_UncertainAfterFail", detail);
        if (reloadFailure != null) detail += "\n" + L10n.Format("Err_ReloadFailed", CoreMessageResolver.ResolveException(reloadFailure));
        return uncertain
            ? new ActionableException(detail, L10n.Get("Action_RefreshStatus"), UnlockAndReloadAsync)
            : new InvalidOperationException(detail);
    }
    private bool syncingLinks;

    public VmMemorySettingsView(Guid vmId) : base(vmId)
    {
        BuildComputeGroup();
        BuildMmioGroup();
        BuildPagesGroup();
        BuildMappingGroup();
        BuildSecurityGroup();
        BuildExtensionsGroup();
        SetSectionedBody(sections);
    }

    private static (int StartupMaxMiB, int DynamicMaxMiB) GetHostMemorySliderBounds()
    {
        var hostMem = HostSystemInfoService.TryReadHostMemoryMiB();
        if (hostMem is { TotalMiB: > 0 } hm)
        {
            int physicalMax = (int)Math.Clamp(hm.TotalMiB, 512L, 1048576L);
            int dynamicMax = (int)Math.Clamp(Math.Max(physicalMax, hm.TotalMiB * 2L), 1024L, 1048576L);
            return (physicalMax, dynamicMax);
        }
        return (65536, 131072);
    }

    private void BuildComputeGroup()
    {
        var (hostMemMax, dynamicMaxLimit) = GetHostMemorySliderBounds();

        var startup = MemSlider(L10n.Get("Mem_Startup_Title"), nameof(VmMemorySettings.Startup),
            32, hostMemMax, "MiB", maxTyped: 1048576,
            v => $"{v} MiB ({FormatGb(v, 1)})",
            v => v <= 80 ? 32 : (int)Math.Round(v / 128.0) * 128,
            L10n.Get("Mem_Startup_Desc"));

        var weight = MemSlider(L10n.Get("Mem_Weight_Title"), nameof(VmMemorySettings.Weight),
            0, 10000, "", maxTyped: 10000,
            FormatMemoryWeightTooltip,
            v => (int)Math.Round(v / 100.0) * 100,
            L10n.Get("Mem_Weight_Desc"));

        var dynSwitch = MemToggle(L10n.Get("Mem_Dynamic_Title"), nameof(VmMemorySettings.DynamicMemoryEnabled),
            L10n.Get("Mem_Dynamic_Desc"));
        SettingsFieldPanel.SetSpan2(dynSwitch, true);

        var minimum = MemSlider(L10n.Get("Mem_Min_Title"), nameof(VmMemorySettings.Minimum),
            32, hostMemMax, "MiB", maxTyped: 1048576,
            v => $"{v} MiB ({FormatGb(v, 1)})",
            v => v <= 80 ? 32 : (int)Math.Round(v / 128.0) * 128,
            L10n.Get("Mem_Min_Desc"));

        var maximum = MemSlider(L10n.Get("Mem_Max_Title"), nameof(VmMemorySettings.Maximum),
            32, dynamicMaxLimit, "MiB", maxTyped: 1048576,
            v => $"{v} MiB ({FormatGb(v, 1)})",
            v => v <= 144 ? 32 : (int)Math.Round(v / 256.0) * 256,
            L10n.Get("Mem_Max_Desc"));

        var buffer = MemSlider(L10n.Get("Mem_Buffer_Title"), nameof(VmMemorySettings.Buffer),
            5, 2000, "%", maxTyped: 2000,
            v => $"{v}%",
            v => Math.Clamp((int)Math.Round(v / 5.0) * 5, 5, 2000),
            L10n.Get("Mem_Buffer_Desc"));
        SettingsFieldPanel.SetSpan2(buffer, true);

        AddPanelGroup(L10n.Get("Mem_Group_Compute"), isMmio: false, startup, weight, dynSwitch, minimum, maximum, buffer);
    }

    private void BuildMmioGroup()
    {
        var lowSize = MmioSlider(L10n.Get("Mem_LowMmio_Title"), nameof(VmMmioSettings.LowSizeMiB),
            128, 3584, "MiB", maxTyped: 3584,
            v => $"{v} MiB ({FormatGb(v, 2)})",
            v => (int)Math.Round(v / 128.0) * 128,
            L10n.Get("Mem_LowMmio_Desc"));

        var highSize = MmioSlider(L10n.Get("Mem_HighMmio_Title"), nameof(VmMmioSettings.HighSizeMiB),
            512, 262656, "MiB", maxTyped: 262656,
            v => $"{v} MiB ({FormatGb(v, 1)})",
            v => (int)Math.Round(v / 512.0) * 512,
            L10n.Get("Mem_HighMmio_Desc"));

        var highBase = MmioSlider(L10n.Get("Mem_HighBase_Title"), nameof(VmMmioSettings.HighBaseMiB),
            4096, 262144, "MiB", maxTyped: 1073741824,
            v => $"{v} MiB ({FormatGb(v, 1)})",
            v => (int)Math.Round(v / 1024.0) * 1024,
            L10n.Get("Mem_HighBase_Desc"));

        AddPanelGroup(L10n.Get("Mem_Group_Mmio"), isMmio: true, lowSize, highSize, highBase);
    }

    private void BuildPagesGroup()
    {
        var dynAlignment = MemChoice(L10n.Get("Mem_DynAlign_Title"), nameof(VmMemorySettings.DynMemOperationAlignment),
            [
                new(0u, L10n.Get("Mem_Align_Disabled")),
                new(1u, L10n.Get("Mem_Align_Standard")),
                new(2u, L10n.Get("Mem_Align_Large")),
                new(3u, L10n.Get("Mem_Align_Huge"))
            ],
            width: 400, span2: true,
            description: L10n.Get("Mem_DynAlign_Desc"));

        var maxNumaBlocks = MemSlider(L10n.Get("Mem_NumaBlocks_Title"), nameof(VmMemorySettings.MaxMemoryBlocksPerNumaNode),
            32, 262144, "MiB", maxTyped: 1048576,
            v => $"{v} MiB ({FormatGb(v, 1)})",
            v => v <= 512 ? 32 : (int)Math.Round(v / 1024.0) * 1024,
            L10n.Get("Mem_NumaBlocks_Desc"));
        SettingsFieldPanel.SetSpan2(maxNumaBlocks, true);

        AddPanelGroup(L10n.Get("Mem_Group_Pages"), isMmio: false, dynAlignment, maxNumaBlocks);
    }

    private void BuildMappingGroup()
    {
        var backingType = MemChoice(L10n.Get("Mem_BackingType_Title"), nameof(VmMemorySettings.BackingType),
            [
                new((byte)0, L10n.Get("Mem_Backing_Physical")),
                new((byte)1, L10n.Get("Mem_Backing_Virtual")),
                new((byte)2, L10n.Get("Mem_Backing_Hybrid"), Enabled: false)
            ],
            width: 430, span2: true,
            description: L10n.Get("Mem_BackingType_Desc"));

        var backingPageSize = MemChoice(L10n.Get("Mem_BackingPageSize_Title"), nameof(VmMemorySettings.BackingPageSize),
            [
                new((byte)0, L10n.Get("Mem_PageSize_Default")),
                new((byte)1, L10n.Get("Mem_PageSize_Large")),
                new((byte)2, L10n.Get("Mem_PageSize_Huge"))
            ],
            width: 390, span2: true,
            description: L10n.Get("Mem_BackingPageSize_Desc"));

        var coldHint = MemToggle(L10n.Get("Mem_ColdHint_Title"), nameof(VmMemorySettings.EnableColdHint),
            L10n.Get("Mem_ColdHint_Desc"));
        var hotHint = MemToggle(L10n.Get("Mem_HotHint_Title"), nameof(VmMemorySettings.EnableHotHint),
            L10n.Get("Mem_HotHint_Desc"));

        var epf = MemToggle(L10n.Get("Mem_Epf_Title"), nameof(VmMemorySettings.EnableEpf),
            L10n.Get("Mem_Epf_Desc"));
        var compression = MemToggle(L10n.Get("Mem_Compression_Title"), nameof(VmMemorySettings.EnablePrivateCompressionStore),
            L10n.Get("Mem_Compression_Desc"));
        var gpaPinning = MemToggle(L10n.Get("Mem_GpaPinning_Title"), nameof(VmMemorySettings.EnableGpaPinning),
            L10n.Get("Mem_GpaPinning_Desc"));
        SettingsFieldPanel.SetSpan2(gpaPinning, true);

        AddPanelGroup(L10n.Get("Mem_Group_Mapping"), isMmio: false, backingType, backingPageSize, coldHint, hotHint, epf, compression, gpaPinning);
    }

    private void BuildSecurityGroup()
    {
        var encPolicy = MemChoice(L10n.Get("Mem_EncPolicy_Title"), nameof(VmMemorySettings.MemoryEncryptionPolicy),
            [
                new((byte)0, L10n.Get("Mem_Enc_Disabled")),
                new((byte)1, L10n.Get("Mem_Enc_IfSupported")),
                new((byte)2, L10n.Get("Mem_Enc_Always"))
            ],
            width: 400, span2: true,
            description: L10n.Get("Mem_EncPolicy_Desc"));

        var sgxEnabled = MemToggle(L10n.Get("Mem_Sgx_Title"), nameof(VmMemorySettings.SgxEnabled),
            L10n.Get("Mem_Sgx_Desc"));
        SettingsFieldPanel.SetSpan2(sgxEnabled, true);

        var sgxControlMode = MemChoice(L10n.Get("Mem_SgxMode_Title"), nameof(VmMemorySettings.SgxLaunchControlMode),
            [
                new(0u, L10n.Get("Mem_SgxMode_Deny")),
                new(1u, L10n.Get("Mem_SgxMode_ReadOnly")),
                new(2u, L10n.Get("Mem_SgxMode_ReadWrite"))
            ],
            width: 360, span2: true,
            description: L10n.Get("Mem_SgxMode_Desc"));

        var sgxSize = MemSlider(L10n.Get("Mem_SgxSize_Title"), nameof(VmMemorySettings.SgxSize),
            2, 4096, "MiB", maxTyped: 1048576,
            v => v >= 1024 ? $"{v} MiB ({FormatGb(v, 1)})" : $"{v} MiB",
            v => Math.Max(2, (int)Math.Round(v / 2.0) * 2),
            L10n.Get("Mem_SgxSize_Desc"));
        SettingsFieldPanel.SetSpan2(sgxSize, true);

        var sgxDefault = MemString(L10n.Get("Mem_SgxDefault_Title"), nameof(VmMemorySettings.SgxLaunchControlDefault),
            width: 310, placeholder: L10n.Get("Mem_SgxDefault_Placeholder"),
            description: L10n.Get("Mem_SgxDefault_Desc"));
        SettingsFieldPanel.SetSpan2(sgxDefault, true);

        AddPanelGroup(L10n.Get("Mem_Group_Security"), isMmio: false, encPolicy, sgxEnabled, sgxControlMode, sgxSize, sgxDefault);
    }

    private void BuildExtensionsGroup()
    {
        var trackingState = MemChoice(L10n.Get("Mem_TrackingState_Title"), nameof(VmMemorySettings.MemoryAccessTrackingState),
            [
                new((byte)0, L10n.Get("Mem_Tracking_Disabled")),
                new((byte)1, L10n.Get("Mem_Tracking_Enabled")),
                new((byte)2, L10n.Get("Mem_Tracking_PerNuma"), Enabled: false)
            ],
            width: 430, span2: true,
            description: L10n.Get("Mem_TrackingState_Desc"));

        var trackingPolicy = MemChoice(L10n.Get("Mem_TrackingPolicy_Title"), nameof(VmMemorySettings.MemoryAccessTrackingPolicy),
            [
                new((byte)0, L10n.Get("Common_Auto")),
                new((byte)1, L10n.Get("Mem_Align_Standard")),
                new((byte)2, L10n.Get("Mem_Tracking_Large")),
                new((byte)3, L10n.Get("Mem_Tracking_Huge"))
            ],
            width: 400, span2: true,
            description: L10n.Get("Mem_TrackingPolicy_Desc"));

        var cxl = MemToggle(L10n.Get("Mem_Cxl_Title"), nameof(VmMemorySettings.CxlEnabled),
            L10n.Get("Mem_Cxl_Desc"));
        SettingsFieldPanel.SetSpan2(cxl, true);

        AddPanelGroup(L10n.Get("Mem_Group_Extensions"), isMmio: false, trackingState, trackingPolicy, cxl);
    }

    private void AddPanelGroup(string title, bool isMmio, params Grid[] rows)
    {
        var panel = new SettingsFieldPanel();
        foreach (var row in rows) panel.Children.Add(row);
        AddCustomGroup(title, panel, rows.Select(r => (SettingFieldEditor)r.Tag).ToArray(), isMmio);
    }

    private void AddCustomGroup(string title, UIElement bodyContent, SettingFieldEditor[] grouped, bool isMmio)
    {
        var apply = SettingsActionButton.CreateRect(L10n.Get("Common_Apply"), primary: true);
        var revert = SettingsActionButton.CreateRect(L10n.Get("Common_Revert"));
        apply.IsEnabled = revert.IsEnabled = false;
        apply.Click += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped || uncertain) return;
            if (!isMmio && snapshot == null) return;
            if (isMmio && mmioSnapshot == null) return;
            var submitted = grouped.Where(f => f.HasPendingEdit).ToArray();
            await RunAsync(async () =>
            {
                bool ok = isMmio ? await ApplyMmioAsync(submitted) : await ApplyMemoryAsync(submitted);
                if (ok) ShowAppliedToast();
            });
        };
        revert.Click += (_, _) =>
        {
            foreach (var field in grouped) field.Revert();
            UpdateGroupActions();
        };
        groupActions.Add((grouped, apply, revert, isMmio));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 2, 0, 0)
        };
        actions.Children.Add(revert);
        actions.Children.Add(apply);

        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(bodyContent);
        content.Children.Add(actions);
        sections.Add((title, new Border { Child = content }));
    }

    private Grid MemSlider(
        string title, string property,
        int sliderMin, int sliderMax, string unitText, int maxTyped,
        Func<int, string> formatTooltip, Func<int, int> snapValue,
        string description)
    {
        var prop = typeof(VmMemorySettings).GetProperty(property)!;
        var adapter = new CustomSliderAdapter(
            sliderMin, sliderMax,
            val => val == null ? sliderMin : Math.Max(sliderMin, Convert.ToInt32(Math.Round(Convert.ToDouble(val, CultureInfo.InvariantCulture)))),
            pos => ConvertToPropertyTypedValue(pos, prop.PropertyType),
            formatTooltip,
            UnitText: unitText,
            Placeholder: "",
            MaxTypedValue: maxTyped,
            AllowAutoZero: false,
            SnapValue: snapValue);

        var field = new SettingFieldEditor(prop, SettingEditorKind.HeaderSlider, deferred: true, customSlider: adapter);
        field.HideApplyButton();
        field.ValueEdited += () => OnFieldEdited(property);
        fields.Add(field);
        byName[property] = field;

        var row = EnergyFieldRow(title, description, field);
        StyleCardBorder(row, minHeight: 114, rowSpacing: 12);
        row.Tag = field;
        return row;
    }

    private Grid MmioSlider(
        string title, string property,
        int sliderMin, int sliderMax, string unitText, int maxTyped,
        Func<int, string> formatTooltip, Func<int, int> snapValue,
        string description)
    {
        var prop = typeof(VmMmioSettings).GetProperty(property)!;
        var adapter = new CustomSliderAdapter(
            sliderMin, sliderMax,
            val => val == null ? sliderMin : Math.Max(sliderMin, Convert.ToInt32(Math.Min(int.MaxValue, Convert.ToUInt64(val, CultureInfo.InvariantCulture)))),
            pos => ConvertToPropertyTypedValue(pos, prop.PropertyType),
            formatTooltip,
            UnitText: unitText,
            Placeholder: "",
            MaxTypedValue: maxTyped,
            AllowAutoZero: false,
            SnapValue: snapValue);

        var field = new SettingFieldEditor(prop, SettingEditorKind.HeaderSlider, deferred: true, customSlider: adapter);
        field.HideApplyButton();
        field.ValueEdited += () => UpdateGroupActions();
        mmioFields.Add(field);

        var row = EnergyFieldRow(title, description, field);
        StyleCardBorder(row, minHeight: 114, rowSpacing: 12);
        row.Tag = field;
        return row;
    }

    private Grid MemToggle(string title, string property, string description)
    {
        var prop = typeof(VmMemorySettings).GetProperty(property)!;
        var field = new SettingFieldEditor(prop, SettingEditorKind.Toggle, deferred: true);
        field.HideApplyButton();
        field.ValueEdited += () => OnFieldEdited(property);
        fields.Add(field);
        byName[property] = field;

        var row = FieldRow(title, description, field, SettingEditorKind.Toggle);
        StyleCardBorder(row, minHeight: 68, padding: new Thickness(17, 14, 17, 14));
        row.Tag = field;
        return row;
    }

    private Grid MemChoice(string title, string property, SettingOption[] options, double width, bool span2, string description)
    {
        var prop = typeof(VmMemorySettings).GetProperty(property)!;
        var field = new SettingFieldEditor(prop, SettingEditorKind.Choice, options, deferred: true);
        field.HideApplyButton();
        field.ValueEdited += () => OnFieldEdited(property);
        fields.Add(field);
        byName[property] = field;

        var row = FieldRow(title, description, field, SettingEditorKind.Toggle);
        field.HorizontalAlignment = HorizontalAlignment.Right;
        field.Width = width;
        if (span2) SettingsFieldPanel.SetSpan2(row, true);
        StyleCardBorder(row, minHeight: 68, padding: new Thickness(17, 14, 17, 14));
        row.Tag = field;
        return row;
    }

    private Grid MemString(string title, string property, double width, string placeholder, string description)
    {
        var prop = typeof(VmMemorySettings).GetProperty(property)!;
        var field = new SettingFieldEditor(prop, SettingEditorKind.Text, deferred: true);
        field.HideApplyButton();
        field.ValueEdited += () => OnFieldEdited(property);
        fields.Add(field);
        byName[property] = field;

        var row = FieldRow(title, description, field, SettingEditorKind.Toggle);
        field.HorizontalAlignment = HorizontalAlignment.Right;
        field.Width = width;
        field.TextPlaceholder = placeholder;
        StyleCardBorder(row, minHeight: 68, padding: new Thickness(17, 14, 17, 14));
        row.Tag = field;
        return row;
    }

    private void OnFieldEdited(string property)
    {
        if (Updating || syncingLinks) return;
        syncingLinks = true;
        try
        {
            if (property == nameof(VmMemorySettings.BackingType))
            {
                var bt = byName[nameof(VmMemorySettings.BackingType)].Read() as byte?;
                if (bt == 0)
                {
                    foreach (string depName in BackingFeatureProps)
                    {
                        if (byName.TryGetValue(depName, out var depField) && depField.Read() is true)
                            depField.RestoreDraft(false);
                    }
                }
                else if (bt == 1 && byName.TryGetValue(nameof(VmMemorySettings.BackingPageSize), out var pageField) && pageField.Read() is byte p && p == 2)
                {
                    pageField.RestoreDraft((byte)1);
                    ShowInformation(L10n.Get("Mem_Link_VirtualNo1Gb"));
                }
            }
            else if (property == nameof(VmMemorySettings.BackingPageSize))
            {
                if (byName[nameof(VmMemorySettings.BackingPageSize)].Read() is byte page && page == 2)
                {
                    if (byName.TryGetValue(nameof(VmMemorySettings.BackingType), out var btField) && btField.Read() is not (byte)0)
                    {
                        btField.RestoreDraft((byte)0);
                        foreach (string depName in BackingFeatureProps)
                        {
                            if (byName.TryGetValue(depName, out var depField) && depField.Read() is true)
                                depField.RestoreDraft(false);
                        }
                        ShowInformation(L10n.Get("Mem_Link_1GbNeedsPhysical"));
                    }
                }
            }
            else if (property == nameof(VmMemorySettings.SgxEnabled))
            {
                if (byName[nameof(VmMemorySettings.SgxEnabled)].Read() is true &&
                    byName.TryGetValue(nameof(VmMemorySettings.SgxSize), out var sizeField))
                {
                    var curSize = sizeField.Read() as double?;
                    if (curSize == null || curSize < 2)
                        sizeField.RestoreDraft(2d);
                }
            }
        }
        finally
        {
            syncingLinks = false;
        }
        UpdateGroupActions();
    }

    private static readonly string[] BackingFeatureProps =
    [
        nameof(VmMemorySettings.EnableColdHint),
        nameof(VmMemorySettings.EnableHotHint),
        nameof(VmMemorySettings.EnableEpf),
        nameof(VmMemorySettings.EnablePrivateCompressionStore)
    ];

    private void UpdateGroupActions()
    {
        UpdateDependentAvailability();
        foreach (var group in groupActions)
        {
            bool dirty = group.Fields.Any(f => f.HasPendingEdit);
            group.Revert.IsEnabled = !Updating && dirty;
            bool snapshotReady = group.IsMmio ? mmioSnapshot != null : snapshot != null;
            group.Apply.IsEnabled = !Updating && snapshotReady && !uncertain && dirty;
        }
    }

    private void UpdateDependentAvailability()
    {
        if (Updating || snapshot == null) return;
        var draft = snapshot.Clone();
        foreach (var field in fields)
            if (field.HasPendingEdit) field.Property.SetValue(draft, field.Read());

        bool dynOn = draft.DynamicMemoryEnabled;
        foreach (string propName in new[] { nameof(VmMemorySettings.Minimum), nameof(VmMemorySettings.Maximum), nameof(VmMemorySettings.Buffer) })
            ApplyDependencyAvailability(propName, !dynOn, L10n.Get("Mem_Cond_NeedDynRam"), s => s.DynamicMemoryEnabled = true);

        bool physicalBacking = draft.BackingType == 0;
        foreach (string propName in BackingFeatureProps)
            ApplyDependencyAvailability(propName, physicalBacking, L10n.Get("Mem_Cond_NeedVirtualBacking"), s => s.BackingType = 1);

        bool trackingDisabled = draft.MemoryAccessTrackingState != 1;
        ApplyDependencyAvailability(nameof(VmMemorySettings.MemoryAccessTrackingPolicy), trackingDisabled, L10n.Get("Mem_Cond_NeedTracking"), s => s.MemoryAccessTrackingState = 1);

        bool sgxOff = draft.SgxEnabled != true;
        foreach (string propName in new[] { nameof(VmMemorySettings.SgxLaunchControlMode), nameof(VmMemorySettings.SgxSize), nameof(VmMemorySettings.SgxLaunchControlDefault) })
            ApplyDependencyAvailability(propName, sgxOff, L10n.Get("Mem_Cond_NeedSgx"), s => s.SgxEnabled = true);
    }

    private void ApplyDependencyAvailability(string propName, bool blockedByPrereq, string prereqReason, Action<VmMemorySettings> enablePrereq)
    {
        if (!byName.TryGetValue(propName, out var editor) || snapshot == null) return;
        var withPrereq = snapshot.Clone();
        enablePrereq(withPrereq);
        var (canModify, reason) = GetEffectiveCondition(withPrereq, propName, powerState);
        if (!canModify)
            editor.SetAvailability(false, reason);
        else if (fieldStates.GetValueOrDefault(propName) != VmQueryValueState.Available)
            editor.SetAvailability(false, L10n.Get("Err_NoValidData"));
        else if (blockedByPrereq)
            editor.SetAvailability(false, prereqReason);
        else
            editor.SetAvailability(!uncertain, null);
    }

    private (bool CanModify, string? Reason) GetEffectiveCondition(VmMemorySettings mem, string propName, VmState state)
    {
        var cond = VmMemoryService.GetModificationCondition(mem, propName, state);
        if (!cond.CanModify) return (false, CoreMessageResolver.ResolveConditionReason(cond));
        if (host is { IsIntel: false } && propName is nameof(VmMemorySettings.SgxEnabled)
            or nameof(VmMemorySettings.SgxSize) or nameof(VmMemorySettings.SgxLaunchControlMode) or nameof(VmMemorySettings.SgxLaunchControlDefault))
        {
            return propName == nameof(VmMemorySettings.SgxEnabled) && mem.SgxEnabled == true
                ? (true, null)
                : (false, L10n.Get("Cond_RequiresIntel"));
        }
        return (true, null);
    }

    protected override async Task LoadAsync()
    {
        snapshot = null;
        mmioSnapshot = null;
        foreach (var field in fields.Concat(mmioFields)) field.SetAvailability(false, null);

        var memoryTask = VmMemoryService.GetVmMemorySettingsAsync(VmId);
        var mmioTask = VmMmioService.GetSettingsAsync(VmId);
        var hostTask = ReadHostCapabilitiesAsync();
        var contextTask = ReadContextAsync();
        await Task.WhenAll(memoryTask, mmioTask, hostTask, contextTask);

        var observation = (await memoryTask).RequireData();
        var memory = observation.Values;
        var mmio = (await mmioTask).RequireData();
        host = await hostTask;
        powerState = (await contextTask).State;
        if (IsStopped) return;

        snapshot = memory;
        mmioSnapshot = mmio;
        fieldStates = observation.States.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        var (hostMemMax, dynamicMaxLimit) = GetHostMemorySliderBounds();
        Updating = true;
        try
        {
            foreach (var field in fields)
            {
                var name = field.Property.Name;
                if (name is nameof(VmMemorySettings.Startup) or nameof(VmMemorySettings.Minimum))
                    field.UpdateSliderBounds(32, hostMemMax);
                else if (name == nameof(VmMemorySettings.Maximum))
                    field.UpdateSliderBounds(32, dynamicMaxLimit);
                object? value = field.Property.GetValue(memory);
                if (name == nameof(VmMemorySettings.BackingPageSize))
                    field.SetOptions(memory.SupportsBackingPageSize
                        ? (SettingOption[])[new((byte)0, L10n.Get("Mem_PageSize_Default")), new((byte)1, L10n.Get("Mem_PageSize_Large")), new((byte)2, L10n.Get("Mem_PageSize_Huge"))]
                        : (SettingOption[])[new((byte)1, L10n.Get("Mem_PageSize_Large")), new((byte)2, L10n.Get("Mem_PageSize_Huge"))]);
                var (canModify, condReason) = GetEffectiveCondition(memory, name, powerState);
                string? error = !canModify
                    ? condReason
                    : observation.States.GetValueOrDefault(name) != VmQueryValueState.Available
                        ? L10n.Get("Err_NoValidData")
                        : null;
                field.Load(value, error == null && !uncertain, error);
            }
            foreach (var field in mmioFields)
            {
                var value = field.Property.GetValue(mmio);
                var condition = VmMmioService.GetModificationCondition(powerState);
                var error = value == null
                    ? L10n.Get("Cond_HostSettingMissing")
                    : condition.CanModify ? null : CoreMessageResolver.ResolveConditionReason(condition);
                field.Load(value, error == null && !uncertain, error);
            }
            if (observation.States.Any(pair => pair.Value == VmQueryValueState.Unavailable))
                ShowError(L10n.Get("Err_PartialReadUnavailable"), L10n.Get("Action_RefreshStatus"), UnlockAndReloadAsync);
        }
        finally
        {
            Updating = false;
        }
        UpdateGroupActions();
        if (uncertain) ShowError(L10n.Get("Err_UncertainLock"), L10n.Get("Action_RefreshStatus"), UnlockAndReloadAsync);
        else if (host is { IsIntel: false } && memory.SgxEnabled == true && !observation.States.Any(pair => pair.Value == VmQueryValueState.Unavailable))
            ShowInformation(L10n.Get("Mem_Info_SgxUnsupportedEnabled"));
    }

    private async Task<bool> ApplyMemoryAsync(SettingFieldEditor[] submitted)
    {
        if (snapshot == null) return false;
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
        ValidateMemoryConstraints(proposed, changed);

        var otherDrafts = fields.Where(f => f.HasPendingEdit && !submitted.Contains(f))
            .Select(f => (Field: f, Draft: f.CaptureDraft())).ToArray();

        ApiResponse? result = null;
        Exception? failure = null;
        try
        {
            result = await VmMemoryService.SetVmMemorySettingsAsync(VmId, proposed, changed);
            if (result.ErrorKind == ApiErrorKind.OutcomeUnknown) uncertain = true;
            if (result.Success || result.Steps.Any(s => s.State == OperationStepState.Succeeded && s.Name != "RecoverVmms")) NotifyChanged();
        }
        catch (Exception ex) { failure = ex; if (ex is CoreOperationException core && core.Result.ErrorKind == ApiErrorKind.OutcomeUnknown) uncertain = true; }
        try { await LoadAsync(); }
        catch (Exception ex) { failure ??= ex; }

        Updating = true;
        try
        {
            foreach (var (field, draft) in otherDrafts)
                if (field.IsFieldEnabled && !Equals(field.Read(), draft)) field.RestoreDraft(draft);
        }
        finally { Updating = false; }
        UpdateGroupActions();

        if (result is { Success: false }) throw CreateApplyFailure(CoreMessageResolver.ResolveError(result), failure);
        if (failure != null) throw uncertain ? new ActionableException(CoreMessageResolver.ResolveException(failure), L10n.Get("Action_RefreshStatus"), UnlockAndReloadAsync) : failure;
        return result?.Success ?? false;
    }

    private async Task<bool> ApplyMmioAsync(SettingFieldEditor[] submitted)
    {
        if (mmioSnapshot == null) return false;
        var proposed = mmioSnapshot.Clone();
        var changed = new List<SettingFieldEditor>();
        foreach (var field in submitted)
        {
            object? value = field.Read();
            if (value == null) throw new InvalidOperationException(L10n.Get("Mem_Err_MmioEmpty"));
            if (Equals(field.Property.GetValue(mmioSnapshot), value)) continue;
            field.Property.SetValue(proposed, value);
            changed.Add(field);
        }
        if (changed.Count == 0) return false;

        var otherMemoryDrafts = fields.Where(f => f.HasPendingEdit)
            .Select(f => (Field: f, Draft: f.CaptureDraft())).ToArray();

        ApiResponse? result = null;
        Exception? failure = null;
        bool anySucceeded = false;
        try
        {
            foreach (var field in changed)
            {
                result = await VmMmioService.SetSettingAsync(VmId, proposed, field.Property.Name);
                if (result.ErrorKind == ApiErrorKind.OutcomeUnknown)
                {
                    uncertain = true;
                    break;
                }
                if (!result.Success) break;
                anySucceeded = true;
            }
            if (anySucceeded) NotifyChanged();
        }
        catch (Exception ex) { failure = ex; if (ex is CoreOperationException core && core.Result.ErrorKind == ApiErrorKind.OutcomeUnknown) uncertain = true; }
        try { await LoadAsync(); }
        catch (Exception ex) { failure ??= ex; }

        Updating = true;
        try
        {
            foreach (var (field, draft) in otherMemoryDrafts)
                if (field.IsFieldEnabled && !Equals(field.Read(), draft)) field.RestoreDraft(draft);
        }
        finally { Updating = false; }
        UpdateGroupActions();

        if (result is { Success: false }) throw CreateApplyFailure(CoreMessageResolver.ResolveError(result), failure);
        if (failure != null) throw uncertain ? new ActionableException(CoreMessageResolver.ResolveException(failure), L10n.Get("Action_RefreshStatus"), UnlockAndReloadAsync) : failure;
        return result?.Success ?? false;
    }

    private static void ValidateMemoryConstraints(VmMemorySettings proposed, IReadOnlyCollection<string> changed)
    {
        if (changed.Contains(nameof(VmMemorySettings.Startup)) && proposed.Startup <= 0)
            throw new InvalidOperationException(L10n.Get("Mem_Val_StartupPositive"));
        if (changed.Contains(nameof(VmMemorySettings.Minimum)) && proposed.Minimum <= 0)
            throw new InvalidOperationException(L10n.Get("Mem_Val_MinPositive"));
        if (changed.Contains(nameof(VmMemorySettings.Maximum)) && proposed.Maximum <= 0)
            throw new InvalidOperationException(L10n.Get("Mem_Val_MaxPositive"));
        if (changed.Contains(nameof(VmMemorySettings.Weight)) && proposed.Weight > 10000)
            throw new InvalidOperationException(L10n.Get("Mem_Val_WeightRange"));
        if (changed.Contains(nameof(VmMemorySettings.Buffer)) && proposed.Buffer is < 5 or > 2000)
            throw new InvalidOperationException(L10n.Get("Mem_Val_BufferRange"));

        if (proposed.DynamicMemoryEnabled && changed.Any(p => p is nameof(VmMemorySettings.Minimum)
            or nameof(VmMemorySettings.Maximum) or nameof(VmMemorySettings.Startup) or nameof(VmMemorySettings.DynamicMemoryEnabled) or nameof(VmMemorySettings.Buffer)))
        {
            if (proposed.Minimum > proposed.Maximum)
                throw new InvalidOperationException(L10n.Format("Mem_Val_MinGtMax", proposed.Minimum, proposed.Maximum));
            if (proposed.Minimum > proposed.Startup)
                throw new InvalidOperationException(L10n.Format("Mem_Val_MinGtStartup", proposed.Minimum, proposed.Startup));
            if (proposed.Startup > proposed.Maximum)
                throw new InvalidOperationException(L10n.Format("Mem_Val_StartupGtMax", proposed.Startup, proposed.Maximum));
        }
    }

    private static string FormatGb(int mib, int maxDecimals)
    {
        double gb = mib / 1024.0;
        if (mib % 1024 == 0) return $"{(int)gb} GB";
        return gb.ToString("0." + new string('#', maxDecimals), CultureInfo.InvariantCulture) + " GB";
    }

    private static string FormatMemoryWeightTooltip(int val)
    {
        if (val == 5000) return L10n.Get("Mem_Weight_Equal");
        double mult = val / 5000.0;
        string multStr = mult < 1
            ? mult.ToString("0.##", CultureInfo.InvariantCulture)
            : mult.ToString("0.#", CultureInfo.InvariantCulture);
        return val < 5000 ? L10n.Format("Mem_Weight_Low", multStr) : L10n.Format("Mem_Weight_High", multStr);
    }

    private static object ConvertToPropertyTypedValue(int value, Type propertyType)
    {
        var underlying = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (underlying == typeof(long)) return (long)value;
        if (underlying == typeof(int)) return value;
        if (underlying == typeof(uint)) return (uint)Math.Max(0, value);
        if (underlying == typeof(ulong)) return (ulong)Math.Max(0, value);
        if (underlying == typeof(double)) return (double)value;
        return Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
    }
}
