using ExHyperV.Models;
using ExHyperV.WinUI3.UI.Shared;
using System.Collections.Concurrent;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed class VmMemorySettingsView : VmSettingsView
{
    private readonly List<SettingFieldEditor> fields = [];
    private readonly List<SettingFieldEditor> mmioFields = [];
    private VmMemorySettings? snapshot;
    private VmMmioSettings? mmioSnapshot;
    private bool isRunning;
    private static readonly ConcurrentDictionary<Guid, byte> unknownOperations = new();
    private bool uncertain { get => unknownOperations.ContainsKey(VmId); set { if (value) unknownOperations.TryAdd(VmId, 0); } }

    public VmMemorySettingsView(Guid vmId) : base(vmId)
    {
        var panel = new StackPanel { Margin = new Thickness(24, 12, 24, 24) };
        AddSection(panel, "计算资源", Card(
            Number("启动内存（MiB）", nameof(VmMemorySettings.Startup)),
            Number("内存权重", nameof(VmMemorySettings.Weight), "内存竞争时的分配权重。"),
            Toggle("动态内存", nameof(VmMemorySettings.DynamicMemoryEnabled)),
            Number("最小内存（MiB）", nameof(VmMemorySettings.Minimum)),
            Number("最大内存（MiB）", nameof(VmMemorySettings.Maximum)),
            Number("内存缓冲（%）", nameof(VmMemorySettings.Buffer))));
        AddSection(panel, "MMIO 地址空间", Card(
            Mmio("低位 MMIO 大小（MiB）", nameof(VmMmioSettings.LowSizeMiB)),
            Mmio("高位 MMIO 大小（MiB）", nameof(VmMmioSettings.HighSizeMiB)),
            Mmio("高位 MMIO 基址（MiB）", nameof(VmMmioSettings.HighBaseMiB))));
        AddSection(panel, "内存页管理", Card(
            Choice("后备页大小", nameof(VmMemorySettings.BackingPageSize), []),
            Number("每个 NUMA 节点的内存块上限", nameof(VmMemorySettings.MaxMemoryBlocksPerNumaNode)),
            Choice("动态内存操作对齐", nameof(VmMemorySettings.DynMemOperationAlignment),
                [new(0u, "禁用"), new(1u, "标准"), new(2u, "大"), new(3u, "巨大")])));
        AddSection(panel, "内存映射", Card(
            Choice("内存后备方式", nameof(VmMemorySettings.BackingType),
                [new((byte)0, "物理后备"), new((byte)1, "虚拟后备"), new((byte)2, "混合（按节点配置）", false)]),
            Toggle("冷页提示", nameof(VmMemorySettings.EnableColdHint)),
            Toggle("热页提示", nameof(VmMemorySettings.EnableHotHint)),
            Toggle("EPF", nameof(VmMemorySettings.EnableEpf)),
            Toggle("私有压缩存储", nameof(VmMemorySettings.EnablePrivateCompressionStore)),
            Toggle("GPA 固定", nameof(VmMemorySettings.EnableGpaPinning))));
        AddSection(panel, "内存访问跟踪", Card(
            Choice("访问跟踪", nameof(VmMemorySettings.MemoryAccessTrackingState),
                [new((byte)0, "禁用"), new((byte)1, "启用"), new((byte)2, "按 NUMA 节点配置", false)]),
            Choice("跟踪粒度", nameof(VmMemorySettings.MemoryAccessTrackingPolicy),
                [new((byte)0, "自动"), new((byte)1, "标准"), new((byte)2, "大"), new((byte)3, "巨大")])));
        AddSection(panel, "内存加密与机密计算", Card(
            Choice("内存加密策略", nameof(VmMemorySettings.MemoryEncryptionPolicy),
                [new((byte)0, "禁用"), new((byte)1, "支持时启用"), new((byte)2, "始终启用")]),
            Toggle("SGX", nameof(VmMemorySettings.SgxEnabled)),
            Choice("SGX 启动控制", nameof(VmMemorySettings.SgxLaunchControlMode),
                [new(0u, "拒绝访问"), new(1u, "只读"), new(2u, "读写")]),
            Number("SGX 内存大小（MiB）", nameof(VmMemorySettings.SgxSize)),
            Number("SGX 默认启动控制值", nameof(VmMemorySettings.SgxLaunchControlDefault))));
        AddSection(panel, "硬件扩展", Card(Toggle("CXL 内存扩展", nameof(VmMemorySettings.CxlEnabled),
            "实验性设置：部分 Windows 版本保存此项可能导致虚拟机管理服务崩溃；属性存在不代表硬件支持。")));
        SetVirtualizedBody(panel);
    }

    private Grid Number(string title, string property, string? description = null) => Add(title, property, SettingEditorKind.Text, description: description);
    private Grid Toggle(string title, string property, string? description = null) => Add(title, property, SettingEditorKind.Toggle, description: description);
    private Grid Choice(string title, string property, SettingOption[] options) => Add(title, property, SettingEditorKind.Choice, options);
    private Grid Add(string title, string property, SettingEditorKind kind, SettingOption[]? options = null, string? description = null)
    {
        var field = new SettingFieldEditor(typeof(VmMemorySettings).GetProperty(property)!, kind, options);
        field.SubmitRequested += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped || !IsLoaded || snapshot == null || uncertain) return;
            await RunAsync(() => ApplyMemoryAsync(field));
        };
        fields.Add(field);
        return Row(title, description, field);
    }

    private Grid Mmio(string title, string property)
    {
        var field = new SettingFieldEditor(typeof(VmMmioSettings).GetProperty(property)!, SettingEditorKind.Text);
        field.SubmitRequested += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped || !IsLoaded || mmioSnapshot == null || uncertain) return;
            await RunAsync(async () =>
            {
                var value = field.Read();
                if (value == null) throw new InvalidOperationException("请输入 MMIO 数值。");
                if (Equals(field.Property.GetValue(mmioSnapshot), value)) return;
                var request = mmioSnapshot.Clone();
                field.Property.SetValue(request, value);
                await WriteAndReloadAsync(() => VmMmioService.SetSettingAsync(VmId, request, property));
            });
        };
        mmioFields.Add(field);
        return Row(title, null, field);
    }

    protected override async Task LoadAsync()
    {
        snapshot = null;
        mmioSnapshot = null;
        foreach (var field in fields.Concat(mmioFields)) field.IsEnabled = false;
        var memoryTask = VmMemoryService.GetVmMemorySettingsAsync(VmId);
        var mmioTask = VmMmioService.GetSettingsAsync(VmId);
        var contextTask = ReadContextAsync();
        var hostTask = VmProcessorService.GetHostCapabilitiesAsync();
        // 基础值的设置模型有历史默认值；使用查询状态确认其确实已读到。
        var validityTask = QueryAsync(VmMemoryFields.Startup, VmMemoryFields.DynamicMemoryEnabled,
            VmMemoryFields.Minimum, VmMemoryFields.Maximum, VmMemoryFields.Buffer, VmMemoryFields.Weight);
        await Task.WhenAll(memoryTask, mmioTask, contextTask, hostTask, validityTask);
        var memory = (await memoryTask).RequireData();
        var mmio = (await mmioTask).RequireData();
        var host = (await hostTask).RequireData();
        isRunning = (await contextTask).IsRunning;
        var validity = await validityTask;
        var validBase = new Dictionary<string, bool>
        {
            [nameof(VmMemorySettings.Startup)] = validity.Get(VmMemoryFields.Startup).HasValue,
            [nameof(VmMemorySettings.DynamicMemoryEnabled)] = validity.Get(VmMemoryFields.DynamicMemoryEnabled).HasValue,
            [nameof(VmMemorySettings.Minimum)] = validity.Get(VmMemoryFields.Minimum).HasValue,
            [nameof(VmMemorySettings.Maximum)] = validity.Get(VmMemoryFields.Maximum).HasValue,
            [nameof(VmMemorySettings.Buffer)] = validity.Get(VmMemoryFields.Buffer).HasValue,
            [nameof(VmMemorySettings.Weight)] = validity.Get(VmMemoryFields.Weight).HasValue
        };
        if (IsStopped) return;
        snapshot = memory;
        mmioSnapshot = mmio;
        var capabilities = VmMemoryService.GetCapabilities(isRunning, memory.DynamicMemoryEnabled);
        Updating = true;
        try
        {
            foreach (var field in fields)
            {
                var name = field.Property.Name;
                object? value = field.Property.GetValue(memory);
                if (name == nameof(VmMemorySettings.BackingPageSize))
                    field.SetOptions(memory.AvailablePageSizes.Select(p => new SettingOption(p.Value, p.Description)));
                string? error = value == null || validBase.TryGetValue(name, out bool valid) && !valid
                    ? "此设置未取得有效数据。" : null;
                if (error == null)
                {
                    var probe = memory.Clone();
                    field.Property.SetValue(probe, field.ProbeValue(value));
                    error = VmMemoryService.GetChangeError(memory, probe, isRunning, name);
                    if (error == null)
                    {
                        var requested = field.Property.GetValue(probe);
                        VmMemoryService.NormalizeMemoryBackingSettings(probe, name);
                        if (!Equals(requested, field.Property.GetValue(probe))) error = "当前后备方式不支持此选项。";
                    }
                }
                if (name is nameof(VmMemorySettings.Minimum) or nameof(VmMemorySettings.Maximum) or nameof(VmMemorySettings.Buffer))
                    if (!capabilities.DynamicLimits) error = "启用动态内存后可配置。";
                if (name == nameof(VmMemorySettings.MemoryAccessTrackingPolicy) && memory.MemoryAccessTrackingState != 1)
                    error = "启用全局内存访问跟踪后可配置。";
                if (name is nameof(VmMemorySettings.SgxEnabled) or nameof(VmMemorySettings.SgxLaunchControlMode) or nameof(VmMemorySettings.SgxSize) or nameof(VmMemorySettings.SgxLaunchControlDefault))
                {
                    if (!host.IsIntel) error = "当前宿主平台不支持 SGX。";
                    else if (name != nameof(VmMemorySettings.SgxEnabled) && memory.SgxEnabled != true) error = "启用 SGX 后可配置。";
                }
                field.Load(value, error == null && !uncertain, error);
            }
            foreach (var field in mmioFields)
            {
                var value = field.Property.GetValue(mmio);
                var error = value == null ? "当前宿主未提供此设置。" : !VmMmioService.CanEdit(isRunning) ? "请先关闭虚拟机。" : null;
                field.Load(value, error == null && !uncertain, error);
            }
            if (validBase.Any(pair => !pair.Value)) ShowError("部分内存配置未读到有效值，相关编辑已禁用。请重新进入页面读取。");
        }
        finally { Updating = false; }
        if (uncertain) ShowError("此前的内存修改结果尚未确认，本次程序内已暂停对此虚拟机继续提交。请核对底层操作的最终状态。");
    }

    private async Task ApplyMemoryAsync(SettingFieldEditor field)
    {
        if (snapshot == null) return;
        object? value = field.Read();
        if (value == null) throw new InvalidOperationException("请输入配置值；空值不会重置此设置。");
        if (Equals(value, field.Property.GetValue(snapshot))) return;
        var request = snapshot.Clone();
        field.Property.SetValue(request, value);
        if (VmMemoryService.GetChangeError(snapshot, request, isRunning, field.Property.Name) is { } error)
            throw new InvalidOperationException(error);
        // 仅提交用户选择的字段；必要联动由 Core 在读取最新配置后执行。
        await WriteAndReloadAsync(() => VmMemoryService.SetVmMemorySettingsAsync(VmId, request, isRunning, [field.Property.Name]));
    }

    private async Task WriteAndReloadAsync(Func<Task<ApiResponse>> write)
    {
        ApiResponse? result = null;
        Exception? failure = null;
        try
        {
            result = await write();
            if (result.ErrorKind == ApiErrorKind.OutcomeUnknown || result.Steps.Any(s => s.State == OperationStepState.OutcomeUnknown)) uncertain = true;
            if (result.Success || result.Steps.Any(s => s.State == OperationStepState.Succeeded)) NotifyChanged();
        }
        catch (Exception ex) { failure = ex; if (ex is CoreOperationException core && core.Result.ErrorKind == ApiErrorKind.OutcomeUnknown) uncertain = true; }
        try { await LoadAsync(); }
        catch (Exception ex) { failure ??= ex; }
        if (result is { Success: false })
        {
            string detail = string.Join(Environment.NewLine, result.Steps.Select(s => $"{s.Name}：{s.State}" + (s.Failure == null ? "" : $"，{s.Failure.Message}")));
            throw new InvalidOperationException(result.Error + (detail.Length == 0 ? "" : "\n" + detail)
                + (uncertain ? "\n结果尚未确认，本页已停止继续提交。" : "")
                + (failure == null ? "" : "\n重新读取配置也未完成：" + failure.Message));
        }
        if (failure != null) throw failure;
    }
}
