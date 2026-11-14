using System.Globalization;
using ExHyperV.WinUI3.UI.Shared;
using System.Reflection;
using System.Collections.Concurrent;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
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
    private bool uncertain { get => unknownOperations.ContainsKey(VmId); set { if (value) unknownOperations.TryAdd(VmId, 0); } }

    public VmProcessorSettingsView(Guid vmId) : base(vmId)
    {
        AddGroup("计算资源",
            Number("核心数 (vCPU)", nameof(VmProcessorSettings.Count), VmProcessorCapability.ProcessorCount),
            Number("预留 (%)", nameof(VmProcessorSettings.Reservation), VmProcessorCapability.ResourceReservation, scale: 1000),
            Number("限制 (%)", nameof(VmProcessorSettings.Limit), VmProcessorCapability.ResourceLimit, scale: 1000),
            Number("权重", nameof(VmProcessorSettings.RelativeWeight), VmProcessorCapability.SchedulingWeight));
        sections.Add(("核心绑定", affinitySection));
        AddGroup("指令集能力",
            Toggle("嵌套虚拟化", nameof(VmProcessorSettings.ExposeVirtualizationExtensions), VmProcessorCapability.NestedVirtualization),
            Toggle("迁移兼容性", nameof(VmProcessorSettings.CompatibilityForMigrationEnabled), VmProcessorCapability.MigrationCompatibility),
            Choice("迁移功能集", nameof(VmProcessorSettings.CompatibilityForMigrationMode), VmProcessorCapability.MigrationFeatureSet,
                Options((VmMigrationCompatibilityMode.MinimumFeatureSet, "最小功能集"), (VmMigrationCompatibilityMode.CommonClusterFeatureSet, "群集共同功能集"))),
            Toggle("旧操作系统兼容", nameof(VmProcessorSettings.CompatibilityForOlderOperatingSystemsEnabled), VmProcessorCapability.LegacyOperatingSystemCompatibility),
            Number("自定义 CPU 名称", nameof(VmProcessorSettings.CpuBrandString), VmProcessorCapability.ProcessorBrand));
        AddGroup("中断控制",
            Choice("中断控制器模式", nameof(VmProcessorSettings.ApicMode), VmProcessorCapability.InterruptControllerMode,
                Options((VmApicMode.Default, "默认"), (VmApicMode.Legacy, "xAPIC"), (VmApicMode.X2Apic, "x2APIC"), (VmApicMode.Apic, "APIC"))),
            Choice("局部外设中断（LPI）", nameof(VmProcessorSettings.LpiMode), VmProcessorCapability.LocalPeripheralInterruptMode,
                Options((LpiMode.Default, "默认"), (LpiMode.Disabled, "禁用"), (LpiMode.Enabled, "启用"))),
            Toggle("主机资源保护", nameof(VmProcessorSettings.EnableHostResourceProtection), VmProcessorCapability.HostResourceProtection));
        AddGroup("处理器拓扑",
            Choice("同时多线程（SMT）", nameof(VmProcessorSettings.SmtMode), VmProcessorCapability.SimultaneousMultithreading,
                Options((SmtMode.Inherit, "继承宿主"), (SmtMode.SingleThread, "单线程"), (SmtMode.MultiThread, "多线程"))),
            Number("每个插槽的最大 NUMA 节点数", nameof(VmProcessorSettings.MaxNumaNodesPerSocket), VmProcessorCapability.NumaNodesPerSocket),
            Number("每个 NUMA 节点的最大处理器数", nameof(VmProcessorSettings.MaxProcessorsPerNumaNode), VmProcessorCapability.ProcessorsPerNumaNode),
            Number("L3 缓存相联路数", nameof(VmProcessorSettings.L3CacheWays), VmProcessorCapability.L3CacheAssociativity),
            Choice("L3 处理器分布", nameof(VmProcessorSettings.L3DistributionPolicy), VmProcessorCapability.L3ProcessorDistribution,
                Options((L3DistributionPolicy.SmallToLarge, "从小到大"), (L3DistributionPolicy.LargeToSmall, "从大到小"),
                    (L3DistributionPolicy.EvenSmallToLarge, "均匀，从小到大"), (L3DistributionPolicy.EvenLargeToSmall, "均匀，从大到小"))),
            Number("每个插槽的最大处理器簇数", nameof(VmProcessorSettings.MaxClusterCountPerSocket), VmProcessorCapability.ProcessorClustersPerSocket),
            Number("每个 L3 缓存域的最大处理器数", nameof(VmProcessorSettings.MaxProcessorCountPerL3), VmProcessorCapability.ProcessorsPerL3Domain));
        AddGroup("性能监控",
            Toggle("性能监控单元（PMU）", nameof(VmProcessorSettings.EnablePerfmonPmu), VmProcessorCapability.PerformanceMonitoring),
            Toggle("体系结构性能监控单元", nameof(VmProcessorSettings.EnablePerfmonArchPmu), VmProcessorCapability.ArchitecturalPerformanceMonitoring),
            Toggle("ACNT / MCNT 计数器", nameof(VmProcessorSettings.AllowAcountMcount), VmProcessorCapability.ActivityCounters),
            Toggle("最后分支记录（LBR）", nameof(VmProcessorSettings.EnablePerfmonLbr), VmProcessorCapability.LastBranchRecording),
            Toggle("精确事件采样（PEBS）", nameof(VmProcessorSettings.EnablePerfmonPebs), VmProcessorCapability.PreciseEventSampling),
            Toggle("处理器跟踪（IPT）", nameof(VmProcessorSettings.EnablePerfmonIpt), VmProcessorCapability.InstructionTracing));
        AddGroup("频率调节",
            Number("频率上限（MHz）", nameof(VmProcessorSettings.PerfCpuFreqCapMhz), VmProcessorCapability.FrequencyMaximum),
            Number("频率下限（MHz）", nameof(VmProcessorSettings.PerfCpuFreqMinMhz), VmProcessorCapability.FrequencyMinimum),
            Number("期望频率（MHz）", nameof(VmProcessorSettings.PerfCpuFreqDesiredMhz), VmProcessorCapability.DesiredFrequency, description: "留空恢复默认。"),
            Number("能效偏好", nameof(VmProcessorSettings.PerfCpuEnergyPerformancePreference), VmProcessorCapability.EnergyPerformancePreference, description: "原始编码；留空恢复默认。"),
            Number("自主活动窗口", nameof(VmProcessorSettings.PerfCpuAutonomousActivityWindow), VmProcessorCapability.AutonomousActivityWindow, description: "原始编码；留空恢复默认。"),
            Toggle("忽略宿主最大频率", nameof(VmProcessorSettings.PerfCpuIgnoreHostMaxFrequency), VmProcessorCapability.IgnoreHostFrequencyMaximum));
        AddGroup("寻址与分页",
            Number("物理地址位宽", nameof(VmProcessorSettings.PhysicalAddressWidth), VmProcessorCapability.PhysicalAddressWidth),
            Choice("大页拆分缓解", nameof(VmProcessorSettings.PageShatterMode), VmProcessorCapability.LargePageMitigation,
                Options((PageShatterMode.Default, "默认"), (PageShatterMode.AlwaysEnabled, "启用"), (PageShatterMode.AlwaysDisabled, "禁用"))));
        AddGroup("安全隔离",
            Toggle("禁用推测执行控制", nameof(VmProcessorSettings.DisableSpeculationControls), VmProcessorCapability.SpeculationControlPolicy),
            Toggle("硬件隔离扩展", nameof(VmProcessorSettings.HardwareIsolationExtensionsEnabled), VmProcessorCapability.HardwareIsolation),
            Number("硬件隔离来宾上限", nameof(VmProcessorSettings.MaxHwIsolatedGuests), VmProcessorCapability.HardwareIsolatedGuestLimit));

        SetSectionedBody(sections);
        BusyChanged += busy => { if (!busy) UpdateGroupActions(); };
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

    private void UpdateGroupActions()
    {
        UpdatePerfmonAvailability();
        foreach (var group in groupActions)
        {
            group.Apply.IsEnabled = snapshot != null && !uncertain && group.Fields.Any(f => f.HasPendingEdit);
            group.Revert.IsEnabled = group.Fields.Any(f => f.HasPendingEdit);
        }
    }

    private static bool IsPerfmonToggle(VmProcessorCapability capability) => capability is
        VmProcessorCapability.PerformanceMonitoring or VmProcessorCapability.PreciseEventSampling or VmProcessorCapability.InstructionTracing;

    private void UpdatePerfmonAvailability()
    {
        if (Updating || snapshot == null || host == null) return;
        var toggles = fields.Where(field => IsPerfmonToggle(field.Capability)).ToArray();
        var draft = snapshot.Clone();
        foreach (var (field, _) in toggles)
            if (field.HasPendingEdit) field.Property.SetValue(draft, field.Read());

        foreach (var (field, _) in toggles)
        {
            var condition = VmProcessorService.GetModificationCondition(draft, field.Property.Name, powerState, host);
            field.SetAvailability(!uncertain && condition.CanModify, condition.CanModify ? null : condition.Reason);
        }
    }

    private void AddGroup(string title, params Grid[] editors)
    {
        var grouped = editors.Select(row => (SettingFieldEditor)row.Tag).ToArray();
        var apply = SettingsActionButton.Create("应用本页修改", "\uE73E", primary: true, iconSize: 24);
        var revert = SettingsActionButton.Create("撤销本页修改", "\uE7A7");
        apply.IsEnabled = revert.IsEnabled = false;
        apply.Click += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped || snapshot == null || uncertain) return;
            // 依赖开关可能因其他草稿值而暂时禁用，已编辑的值仍属于同一次提交。
            var submitted = grouped.Where(f => f.HasPendingEdit).ToArray();
            await RunAsync(() => ApplyAsync(submitted));
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
        foreach (var editor in editors) grid.Children.Add(editor);
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(grid);
        content.Children.Add(actions);
        sections.Add((title, new Border { Child = content, Margin = new Thickness(0, 0, 0, 16) }));
    }

    private static SettingOption[] Options<T>(params (T Value, string Label)[] options) => options.Select(o => new SettingOption(o.Value!, o.Label)).ToArray();
    private Grid Toggle(string title, string property, VmProcessorCapability capability) => Add(title, property, capability, SettingEditorKind.Toggle);
    private Grid Number(string title, string property, VmProcessorCapability capability, decimal scale = 1, string? description = null) => Add(title, property, capability, SettingEditorKind.Text, scale: scale, description: description);
    private Grid Choice(string title, string property, VmProcessorCapability capability, SettingOption[] options) => Add(title, property, capability, SettingEditorKind.Choice, options);
    private Grid Add(string title, string property, VmProcessorCapability capability, SettingEditorKind kind, SettingOption[]? options = null, decimal scale = 1, string? description = null)
    {
        var field = new SettingFieldEditor(typeof(VmProcessorSettings).GetProperty(property)!, kind, options, scale, deferred: true);
        field.HideApplyButton();
        if (kind == SettingEditorKind.Text && field.Property.PropertyType != typeof(string)) field.UseNumericStepper(title);
        field.ValueEdited += UpdateGroupActions;
        fields.Add((field, capability));
        var row = FieldRow(title, string.Join("\n", new[] { ApiFieldHelp.For(field.Property), description }.Where(s => !string.IsNullOrWhiteSpace(s))), field, kind);
        row.Tag = field;
        row.Background = Brush("CardBackgroundFillColorDefaultBrush");
        row.BorderBrush = Brush("CardStrokeColorDefaultBrush");
        row.BorderThickness = new Thickness(1);
        row.CornerRadius = new CornerRadius(8);
        row.Padding = new Thickness(17);
        row.MinHeight = kind == SettingEditorKind.Toggle ? 96 : 128;
        row.RowSpacing = 16;
        if (kind != SettingEditorKind.Toggle)
        {
            row.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            field.VerticalAlignment = VerticalAlignment.Bottom;
        }
        return row;
    }

    protected override async Task LoadAsync()
    {
        snapshot = null;
        foreach (var field in fields) field.Editor.IsEnabled = false;
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
        try
        {
            foreach (var (field, capability) in fields)
            {
                var value = field.Property.GetValue(settings);
                var condition = VmProcessorService.GetModificationCondition(settings, field.Property.Name, powerState, host);
                bool available = observation.States.GetValueOrDefault(field.Property.Name) == VmQueryValueState.Available;
                field.Load(value, !uncertain && available && condition.CanModify,
                    !available ? "此设置未取得有效数据。" : condition.CanModify ? null : condition.Reason);
            }
        }
        finally { Updating = false; }
        UpdateGroupActions();
        if (uncertain) ShowError("此前的处理器修改结果尚未确认，本次程序内已暂停对此虚拟机继续提交。请核对底层操作的最终状态。");
    }

    private async Task ApplyAsync(params SettingFieldEditor[] submitted)
    {
        if (snapshot == null || host == null) return;
        var proposed = snapshot.Clone();
        var changed = new List<string>();
        foreach (var field in submitted)
        {
            object? value = field.Read();
            if (Equals(field.Property.GetValue(snapshot), value)) continue;
            field.Property.SetValue(proposed, value);
            changed.Add(field.Property.Name);
        }
        if (changed.Count == 0) return;
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
        if (result is { Success: false }) throw new InvalidOperationException(result.Error
            + (uncertain ? "\n结果尚未确认，本页已停止继续提交。" : "")
            + (failure == null ? "" : "\n重新读取配置也未完成：" + failure.Message));
        if (failure != null) throw failure;
    }


}

internal enum SettingEditorKind { Text, Toggle, Choice }
internal sealed record SettingOption(object Value, string Label, bool Enabled = true);

/// <summary>设置值与 WinUI 控件之间的显示适配，不包含宿主业务规则。</summary>
internal sealed class SettingFieldEditor : UserControl
{
    private readonly Grid layout = new() { ColumnSpacing = 8 };
    private readonly decimal scale;
    private readonly TextBox? text;
    private readonly ToggleSwitch? toggle;
    private readonly ComboBox? choice;
    private readonly Button? apply;
    private readonly bool deferred;
    private object? loadedValue;
    private Button? decrease;
    private Button? increase;
    private decimal minimum;
    private decimal? maximum;
    private string loadedText = "";
    private bool loading;
    public PropertyInfo Property { get; }
    public event EventHandler? SubmitRequested;
    public event Action<string?>? AvailabilityChanged;
    public event Action? ValueEdited;
    public bool HasPendingEdit => text != null ? text.Text != loadedText
        : toggle != null ? toggle.IsOn != (loadedValue is true)
        : choice != null && !Equals((choice.SelectedItem as ComboBoxItem)?.Tag, loadedValue);

    public SettingFieldEditor(PropertyInfo property, SettingEditorKind kind, SettingOption[]? options = null, decimal scale = 1, bool deferred = false)
    {
        Property = property;
        this.scale = scale;
        this.deferred = deferred;
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Content = layout;
        IsEnabled = false;
        if (kind == SettingEditorKind.Toggle)
        {
            toggle = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0 };
            toggle.Toggled += (_, _) => EditedSelection();
            layout.Children.Add(toggle);
        }
        else if (kind == SettingEditorKind.Choice)
        {
            choice = new ComboBox { MinWidth = 160, MaxWidth = 280 };
            SetOptions(options ?? []);
            choice.SelectionChanged += (_, _) => { if (choice.SelectedItem is ComboBoxItem { IsEnabled: true }) EditedSelection(); };
            layout.Children.Add(choice);
        }
        else
        {
            text = new TextBox { Width = property.PropertyType == typeof(string) ? 260 : 160 };
            apply = new Button { Content = "应用", Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"], IsEnabled = false };
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

    public object? CaptureDraft() => text != null ? text.Text : toggle != null ? toggle.IsOn : (choice?.SelectedItem as ComboBoxItem)?.Tag;

    public void RestoreDraft(object? value)
    {
        loading = true;
        try
        {
            if (text != null) text.Text = value as string ?? "";
            if (toggle != null) toggle.IsOn = value is true;
            if (choice != null) choice.SelectedItem = choice.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, value));
        }
        finally { loading = false; }
        if (apply != null) apply.IsEnabled = HasPendingEdit;
        UpdateStepper();
        ValueEdited?.Invoke();
    }

    public void Revert() => RestoreDraft(text != null ? loadedText : loadedValue);

    public void UseNumericStepper(string title)
    {
        if (text == null || Property.PropertyType == typeof(string)) return;
        minimum = Property.Name is nameof(VmProcessorSettings.Count) or nameof(VmProcessorSettings.RelativeWeight) ? 1 : 0;
        maximum = scale == 1000 ? 100 : null;
        HideApplyButton();
        layout.ColumnSpacing = 6;
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        text.FontSize = 28;
        text.Padding = new Thickness(2, 0, 2, 0);
        text.BorderThickness = new Thickness(0);
        text.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        text.MinWidth = 0;
        text.MinHeight = 44;
        text.VerticalContentAlignment = VerticalAlignment.Center;
        text.PlaceholderText = Nullable.GetUnderlyingType(Property.PropertyType) != null ? "默认" : "";
        decrease = SettingsActionButton.Create("减少" + title, "\uE738", size: 40, iconSize: 16);
        increase = SettingsActionButton.Create("增加" + title, "\uE710", size: 40, iconSize: 16);
        decrease.Click += (_, _) => Step(-1);
        increase.Click += (_, _) => Step(1);
        Grid.SetColumn(decrease, 1);
        Grid.SetColumn(increase, 2);
        layout.Children.Add(decrease);
        layout.Children.Add(increase);
    }

    private void Step(int direction)
    {
        if (text != null && SettingsNumericStep.TryGetValue(text.Text, Property.PropertyType, scale, direction, minimum, maximum, out var value))
            text.Text = value;
    }

    private void UpdateStepper()
    {
        if (text == null || decrease == null || increase == null) return;
        decrease.IsEnabled = SettingsNumericStep.TryGetValue(text.Text, Property.PropertyType, scale, -1, minimum, maximum, out _);
        increase.IsEnabled = SettingsNumericStep.TryGetValue(text.Text, Property.PropertyType, scale, 1, minimum, maximum, out _);
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
        if (choice != null) { choice.MinWidth = 0; choice.MaxWidth = double.PositiveInfinity; choice.HorizontalAlignment = HorizontalAlignment.Stretch; }
    }

    public void SetOptions(IEnumerable<SettingOption> options)
    {
        if (choice == null) return;
        loading = true;
        try
        {
            choice.Items.Clear();
            foreach (var option in options) choice.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Value, IsEnabled = option.Enabled });
        }
        finally { loading = false; }
    }

    public void Load(object? value, bool enabled, string? reason)
    {
        loading = true;
        try
        {
            loadedValue = value;
            if (toggle != null) toggle.IsOn = value is true;
            if (text != null)
            {
                loadedText = value == null ? "" : scale == 1 ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : (Convert.ToDecimal(value) / scale).ToString(CultureInfo.InvariantCulture);
                text.Text = loadedText;
                if (apply != null) apply.IsEnabled = false;
            }
            if (choice != null)
            {
                var item = choice.Items.OfType<ComboBoxItem>().FirstOrDefault(o => Equals(o.Tag, value));
                if (item == null && value != null)
                {
                    item = new ComboBoxItem { Content = $"当前值：{value}", Tag = value, IsEnabled = false };
                    choice.Items.Add(item);
                }
                choice.SelectedItem = item;
            }
            SetAvailability(enabled, reason);
        }
        finally { loading = false; }
        UpdateStepper();
    }

    public void SetAvailability(bool enabled, string? reason)
    {
        IsEnabled = enabled;
        ToolTipService.SetToolTip(this, reason);
        AvailabilityChanged?.Invoke(reason);
    }

    public object? Read()
    {
        if (toggle != null) return toggle.IsOn;
        if (choice != null) return (choice.SelectedItem as ComboBoxItem)?.Tag;
        var type = Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;
        if (type == typeof(string)) return text!.Text;
        if (string.IsNullOrWhiteSpace(text!.Text) && Nullable.GetUnderlyingType(Property.PropertyType) != null) return null;
        try
        {
            if (scale == 1) return Convert.ChangeType(text.Text, type, CultureInfo.InvariantCulture);
            decimal scaled = decimal.Parse(text.Text, CultureInfo.InvariantCulture) * scale;
            if (decimal.Truncate(scaled) != scaled) throw new FormatException();
            return Convert.ChangeType(scaled, type, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        { throw new InvalidOperationException("请输入有效且在范围内的数值。", ex); }
    }


}
