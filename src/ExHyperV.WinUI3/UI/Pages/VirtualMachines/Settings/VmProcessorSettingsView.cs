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
    private readonly VmTabs tabs = new();
    private readonly List<(string Title, Border Content)> sections = [];
    private readonly ContentControl sectionHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Button applyResources = new() { Content = "应用", HorizontalAlignment = HorizontalAlignment.Right, IsEnabled = false };
    private SettingFieldEditor[] resourceFields = [];
    private VmProcessorSettings? snapshot;
    private VmProcessorService.HostCapabilities? host;
    private bool poweredOff;
    private static readonly ConcurrentDictionary<Guid, byte> unknownOperations = new();
    private bool uncertain { get => unknownOperations.ContainsKey(VmId); set { if (value) unknownOperations.TryAdd(VmId, 0); } }

    public event EventHandler? AffinityRequested;

    public VmProcessorSettingsView(Guid vmId) : base(vmId)
    {
        var affinity = new Button { Content = "配置亲和性" };
        affinity.Click += (_, _) => AffinityRequested?.Invoke(this, EventArgs.Empty);
        var affinityRow = Row("CPU 绑定", "配置虚拟机与物理核心的亲和性", affinity);
        affinityRow.Margin = new Thickness(0, 8, 0, 0);
        applyResources.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        applyResources.Click += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped || snapshot == null || uncertain) return;
            await RunAsync(() => ApplyAsync(resourceFields.Where(f => f.IsEnabled).ToArray()));
        };
        var resourceFooter = new StackPanel { Spacing = 12 };
        resourceFooter.Children.Add(applyResources);
        resourceFooter.Children.Add(affinityRow);
        AddGroup("计算资源", "\uE9D9", resourceFooter,
            Number("核心数 (vCPU)", nameof(VmProcessorSettings.Count), VmProcessorCapability.ProcessorCount),
            Number("预留 (%)", nameof(VmProcessorSettings.Reservation), VmProcessorCapability.ResourceReservation, scale: 1000),
            Number("限制 (%)", nameof(VmProcessorSettings.Limit), VmProcessorCapability.ResourceLimit, scale: 1000),
            Number("权重", nameof(VmProcessorSettings.RelativeWeight), VmProcessorCapability.SchedulingWeight));
        resourceFields = fields.Skip(1).Select(f => f.Editor).ToArray();
        foreach (var field in resourceFields)
        {
            field.HideApplyButton();
            field.ValueEdited += UpdateResourceApply;
        }
        AddGroup("指令集能力", "\uE8F1", null,
            Toggle("嵌套虚拟化", nameof(VmProcessorSettings.ExposeVirtualizationExtensions), VmProcessorCapability.NestedVirtualization),
            Toggle("迁移兼容性", nameof(VmProcessorSettings.CompatibilityForMigrationEnabled), VmProcessorCapability.MigrationCompatibility),
            Choice("迁移功能集", nameof(VmProcessorSettings.CompatibilityForMigrationMode), VmProcessorCapability.MigrationFeatureSet,
                Options((VmMigrationCompatibilityMode.MinimumFeatureSet, "最小功能集"), (VmMigrationCompatibilityMode.CommonClusterFeatureSet, "群集共同功能集"))),
            Toggle("旧操作系统兼容", nameof(VmProcessorSettings.CompatibilityForOlderOperatingSystemsEnabled), VmProcessorCapability.LegacyOperatingSystemCompatibility),
            Number("自定义 CPU 名称", nameof(VmProcessorSettings.CpuBrandString), VmProcessorCapability.ProcessorBrand));
        AddGroup("中断控制", "\uE945", null,
            Choice("中断控制器模式", nameof(VmProcessorSettings.ApicMode), VmProcessorCapability.InterruptControllerMode,
                Options((VmApicMode.Default, "默认"), (VmApicMode.Legacy, "xAPIC"), (VmApicMode.X2Apic, "x2APIC"), (VmApicMode.Apic, "APIC"))),
            Choice("局部外设中断（LPI）", nameof(VmProcessorSettings.LpiMode), VmProcessorCapability.LocalPeripheralInterruptMode,
                Options((LpiMode.Default, "默认"), (LpiMode.Disabled, "禁用"), (LpiMode.Enabled, "启用"))),
            Toggle("主机资源保护", nameof(VmProcessorSettings.EnableHostResourceProtection), VmProcessorCapability.HostResourceProtection));
        AddGroup("处理器拓扑", "\uE8D4", null,
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
        AddGroup("性能监控", "\uE9D9", null,
            Toggle("性能监控单元（PMU）", nameof(VmProcessorSettings.EnablePerfmonPmu), VmProcessorCapability.PerformanceMonitoring),
            Toggle("体系结构性能监控单元", nameof(VmProcessorSettings.EnablePerfmonArchPmu), VmProcessorCapability.ArchitecturalPerformanceMonitoring),
            Toggle("ACNT / MCNT 计数器", nameof(VmProcessorSettings.AllowAcountMcount), VmProcessorCapability.ActivityCounters),
            Toggle("最后分支记录（LBR）", nameof(VmProcessorSettings.EnablePerfmonLbr), VmProcessorCapability.LastBranchRecording),
            Toggle("精确事件采样（PEBS）", nameof(VmProcessorSettings.EnablePerfmonPebs), VmProcessorCapability.PreciseEventSampling),
            Toggle("处理器跟踪（IPT）", nameof(VmProcessorSettings.EnablePerfmonIpt), VmProcessorCapability.InstructionTracing));
        AddGroup("频率调节", "\uE945", null,
            Number("频率上限（MHz）", nameof(VmProcessorSettings.PerfCpuFreqCapMhz), VmProcessorCapability.FrequencyMaximum),
            Number("频率下限（MHz）", nameof(VmProcessorSettings.PerfCpuFreqMinMhz), VmProcessorCapability.FrequencyMinimum),
            Number("期望频率（MHz）", nameof(VmProcessorSettings.PerfCpuFreqDesiredMhz), VmProcessorCapability.DesiredFrequency, description: "留空恢复默认。"),
            Number("能效偏好", nameof(VmProcessorSettings.PerfCpuEnergyPerformancePreference), VmProcessorCapability.EnergyPerformancePreference, description: "原始编码；留空恢复默认。"),
            Number("自主活动窗口", nameof(VmProcessorSettings.PerfCpuAutonomousActivityWindow), VmProcessorCapability.AutonomousActivityWindow, description: "原始编码；留空恢复默认。"),
            Toggle("忽略宿主最大频率", nameof(VmProcessorSettings.PerfCpuIgnoreHostMaxFrequency), VmProcessorCapability.IgnoreHostFrequencyMaximum));
        AddGroup("寻址与分页", "\uE8A5", null,
            Number("物理地址位宽", nameof(VmProcessorSettings.PhysicalAddressWidth), VmProcessorCapability.PhysicalAddressWidth),
            Choice("大页拆分缓解", nameof(VmProcessorSettings.PageShatterMode), VmProcessorCapability.LargePageMitigation,
                Options((PageShatterMode.Default, "默认"), (PageShatterMode.AlwaysEnabled, "启用"), (PageShatterMode.AlwaysDisabled, "禁用"))));
        AddGroup("安全隔离", "\uE72E", null,
            Toggle("禁用推测执行控制", nameof(VmProcessorSettings.DisableSpeculationControls), VmProcessorCapability.SpeculationControlPolicy),
            Toggle("硬件隔离扩展", nameof(VmProcessorSettings.HardwareIsolationExtensionsEnabled), VmProcessorCapability.HardwareIsolation),
            Number("硬件隔离来宾上限", nameof(VmProcessorSettings.MaxHwIsolatedGuests), VmProcessorCapability.HardwareIsolatedGuestLimit));

        var layout = new Grid { RowSpacing = 20 };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        tabs.SetItems(sections.Select(s => s.Title).ToArray());
        tabs.SelectionRequested += ShowSection;
        scroll.Content = sectionHost;
        Grid.SetRow(scroll, 1);
        layout.Children.Add(tabs);
        layout.Children.Add(scroll);
        ShowSection(0);
        SetBody(layout, handlesScrolling: true);
    }

    private void UpdateResourceApply()
    {
        applyResources.IsEnabled = snapshot != null && !uncertain && resourceFields.Any(field => field.IsEnabled && field.HasPendingEdit);
    }

    private void ShowSection(int index)
    {
        tabs.Select(index);
        sectionHost.Content = sections[index].Content;
        scroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void AddGroup(string title, string glyph, UIElement? footer, params Grid[] editors)
    {
        var content = new StackPanel { Spacing = 20 };
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heading.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16 });
        heading.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        content.Children.Add(heading);
        var grid = new SettingsFieldPanel();
        foreach (var editor in editors) grid.Children.Add(editor);
        content.Children.Add(grid);
        if (footer != null) content.Children.Add(footer);
        sections.Add((title, new Border
        {
            Background = Brush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = Brush("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(20), Margin = new Thickness(0, 0, 0, 24), Child = content
        }));
    }

    private static string? DescriptionFor(string property) => property switch
    {
        nameof(VmProcessorSettings.ExposeVirtualizationExtensions) => "开启后透传虚拟化指令集，支持在虚拟机内运行虚拟机",
        nameof(VmProcessorSettings.CompatibilityForMigrationEnabled) => "屏蔽 CPU 指令集，以便在不同版本的主机间迁移",
        nameof(VmProcessorSettings.CompatibilityForMigrationMode) => "选择固定的最小特征集，或群集节点共同支持的特征集",
        nameof(VmProcessorSettings.CompatibilityForOlderOperatingSystemsEnabled) => "屏蔽 CPU 指令集，提高对老旧操作系统的兼容性",
        nameof(VmProcessorSettings.ApicMode) => "切换虚拟机的中断控制器模式，影响兼容性与可扩展性",
        nameof(VmProcessorSettings.LpiMode) => "设置虚拟机使用 LPI 接收设备中断的方式",
        nameof(VmProcessorSettings.EnableHostResourceProtection) => "监控 VMBus 通信，防止中断风暴影响主机",
        nameof(VmProcessorSettings.SmtMode) => "控制向虚拟机公开的每个核心硬件线程数",
        nameof(VmProcessorSettings.MaxNumaNodesPerSocket) => "控制虚拟 CPU 插槽包含的 NUMA 分组数",
        nameof(VmProcessorSettings.MaxProcessorsPerNumaNode) => "控制虚拟处理器的 NUMA 分组大小",
        nameof(VmProcessorSettings.L3CacheWays) => "设置虚拟机可见的 L3 缓存路数",
        nameof(VmProcessorSettings.L3DistributionPolicy) => "设置虚拟处理器在 L3 缓存域之间的分布方式",
        nameof(VmProcessorSettings.MaxClusterCountPerSocket) => "设置每个虚拟 CPU 插槽允许包含的最大处理器簇数",
        nameof(VmProcessorSettings.MaxProcessorCountPerL3) => "设置每个虚拟 L3 缓存包含的最大虚拟处理器数",
        nameof(VmProcessorSettings.EnablePerfmonPmu) => "允许虚拟机访问通用硬件性能计数器",
        nameof(VmProcessorSettings.EnablePerfmonArchPmu) => "允许虚拟机访问架构性能监控计数器",
        nameof(VmProcessorSettings.AllowAcountMcount) => "允许虚拟机读取 ACOUNT/MCOUNT 频率监视寄存器",
        nameof(VmProcessorSettings.EnablePerfmonLbr) => "允许虚拟机读取最近执行的分支记录",
        nameof(VmProcessorSettings.EnablePerfmonPebs) => "允许虚拟机采集精确的性能事件样本，需要同时开启 PMU",
        nameof(VmProcessorSettings.EnablePerfmonIpt) => "允许虚拟机记录处理器指令执行流，需要同时开启 PMU",
        nameof(VmProcessorSettings.PerfCpuFreqCapMhz) => "设置虚拟机 CPU 可使用的最高频率",
        nameof(VmProcessorSettings.PerfCpuFreqMinMhz) => "设置虚拟机 CPU 可使用的最低频率",
        nameof(VmProcessorSettings.PerfCpuFreqDesiredMhz) => "设置虚拟机 CPU 的目标运行频率",
        nameof(VmProcessorSettings.PerfCpuEnergyPerformancePreference) => "设置虚拟机 CPU 在性能和能效之间的偏好",
        nameof(VmProcessorSettings.PerfCpuAutonomousActivityWindow) => "设置虚拟机 CPU 自动调节频率时的活动评估窗口",
        nameof(VmProcessorSettings.PerfCpuIgnoreHostMaxFrequency) => "允许虚拟机 CPU 忽略主机设置的最高频率",
        nameof(VmProcessorSettings.PhysicalAddressWidth) => "设置虚拟机可见的物理地址宽度",
        nameof(VmProcessorSettings.PageShatterMode) => "设置二级地址转换中大内存页的拆分方式",
        nameof(VmProcessorSettings.DisableSpeculationControls) => "禁用漏洞补丁 (如 Spectre)，提升性能但降低安全性",
        nameof(VmProcessorSettings.HardwareIsolationExtensionsEnabled) => "允许虚拟机向嵌套虚拟机暴露硬件隔离扩展",
        nameof(VmProcessorSettings.MaxHwIsolatedGuests) => "设置允许创建的嵌套硬件隔离虚拟机数量上限",
        _ => null
    };

    private static SettingOption[] Options<T>(params (T Value, string Label)[] options) => options.Select(o => new SettingOption(o.Value!, o.Label)).ToArray();
    private Grid Toggle(string title, string property, VmProcessorCapability capability) => Add(title, property, capability, SettingEditorKind.Toggle);
    private Grid Number(string title, string property, VmProcessorCapability capability, decimal scale = 1, string? description = null) => Add(title, property, capability, SettingEditorKind.Text, scale: scale, description: description);
    private Grid Choice(string title, string property, VmProcessorCapability capability, SettingOption[] options) => Add(title, property, capability, SettingEditorKind.Choice, options);
    private Grid Add(string title, string property, VmProcessorCapability capability, SettingEditorKind kind, SettingOption[]? options = null, decimal scale = 1, string? description = null)
    {
        var field = new SettingFieldEditor(typeof(VmProcessorSettings).GetProperty(property)!, kind, options, scale);
        field.SubmitRequested += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped || !IsLoaded || snapshot == null || uncertain) return;
            await RunAsync(() => ApplyAsync(field));
        };
        fields.Add((field, capability));
        field.UseAvailableWidth();
        var row = new Grid { RowSpacing = 8, ColumnSpacing = 12, MinHeight = 56 };
        var label = SettingLabel(title, string.Join("\n", new[] { DescriptionFor(property), description }.Where(s => !string.IsNullOrWhiteSpace(s))), field);
        if (kind == SettingEditorKind.Toggle)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(field, 1);
        }
        else
        {
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(field, 1);
        }
        row.Children.Add(label);
        row.Children.Add(field);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(field, title);
        return row;
    }

    protected override async Task LoadAsync()
    {
        snapshot = null;
        foreach (var field in fields) field.Editor.IsEnabled = false;
        var settingsTask = VmProcessorService.GetVmProcessorAsync(VmId);
        var hostTask = VmProcessorService.GetHostCapabilitiesAsync();
        var contextTask = ReadContextAsync();
        await Task.WhenAll(settingsTask, hostTask, contextTask);
        var settings = (await settingsTask).RequireData();
        host = (await hostTask).RequireData();
        poweredOff = (await contextTask).State == VmState.PoweredOff;
        if (IsStopped) return;
        snapshot = settings;
        Updating = true;
        try
        {
            foreach (var (field, capability) in fields)
            {
                var value = field.Property.GetValue(settings);
                string? error = settings.Supports(capability) ? null : "当前宿主未提供此设置。";
                if (error == null)
                {
                    var proposed = Copy(settings);
                    field.Property.SetValue(proposed, field.ProbeValue(value));
                    error = VmProcessorService.GetChangeError(settings, proposed, poweredOff, host);
                }
                field.Load(value, !uncertain && error == null, error);
            }
        }
        finally { Updating = false; }
        UpdateResourceApply();
        if (uncertain) ShowError("此前的处理器修改结果尚未确认，本次程序内已暂停对此虚拟机继续提交。请核对底层操作的最终状态。");
    }

    private async Task ApplyAsync(params SettingFieldEditor[] submitted)
    {
        if (snapshot == null || host == null) return;
        var proposed = Copy(snapshot);
        var changed = new List<string>();
        foreach (var field in submitted)
        {
            object? value = field.Read();
            if (Equals(field.Property.GetValue(snapshot), value)) continue;
            field.Property.SetValue(proposed, value);
            changed.Add(field.Property.Name);
        }
        if (changed.Count == 0) return;
        if (VmProcessorService.GetChangeError(snapshot, proposed, poweredOff, host) is { } error)
            throw new InvalidOperationException(error);
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
        if (result is { Success: false }) throw new InvalidOperationException(result.Error
            + (uncertain ? "\n结果尚未确认，本页已停止继续提交。" : "")
            + (failure == null ? "" : "\n重新读取配置也未完成：" + failure.Message));
        if (failure != null) throw failure;
    }

    private static VmProcessorSettings Copy(VmProcessorSettings source)
    {
        var copy = new VmProcessorSettings();
        foreach (var property in typeof(VmProcessorSettings).GetProperties().Where(p => p.SetMethod?.IsPublic == true))
            property.SetValue(copy, property.GetValue(source));
        return copy;
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
    private string loadedText = "";
    private bool loading;
    public PropertyInfo Property { get; }
    public event EventHandler? SubmitRequested;
    public event Action<string?>? AvailabilityChanged;
    public event Action? ValueEdited;
    public bool HasPendingEdit => text != null && text.Text != loadedText;

    public SettingFieldEditor(PropertyInfo property, SettingEditorKind kind, SettingOption[]? options = null, decimal scale = 1)
    {
        Property = property;
        this.scale = scale;
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Content = layout;
        IsEnabled = false;
        if (kind == SettingEditorKind.Toggle)
        {
            toggle = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0 };
            toggle.Toggled += (_, _) => { if (!loading) SubmitRequested?.Invoke(this, EventArgs.Empty); };
            layout.Children.Add(toggle);
        }
        else if (kind == SettingEditorKind.Choice)
        {
            choice = new ComboBox { MinWidth = 160, MaxWidth = 280 };
            SetOptions(options ?? []);
            choice.SelectionChanged += (_, _) => { if (!loading && choice.SelectedItem is ComboBoxItem { IsEnabled: true }) SubmitRequested?.Invoke(this, EventArgs.Empty); };
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
                ValueEdited?.Invoke();
            };
            apply.Click += (_, _) => SubmitRequested?.Invoke(this, EventArgs.Empty);
            layout.Children.Add(text);
            Grid.SetColumn(apply, 1);
            layout.Children.Add(apply);
        }
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
            IsEnabled = enabled;
            ToolTipService.SetToolTip(this, reason);
            AvailabilityChanged?.Invoke(reason);
        }
        finally { loading = false; }
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

    public object ProbeValue(object? current)
    {
        var type = Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;
        if (type == typeof(bool)) return current is not true;
        if (type == typeof(string)) return (current as string ?? "") + " ";
        if (type.IsEnum) return Enum.GetValues(type).Cast<object>().First(v => !Equals(v, current));
        return Convert.ChangeType(current == null || Convert.ToDecimal(current) == 0 ? 1 : 0, type, CultureInfo.InvariantCulture);
    }
}
