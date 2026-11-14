using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed class VmAdvancedSettingsView : VmSettingsView
{
    private const string DefaultResolution = "默认（自适应）";
    private readonly ToggleSwitch console = Switch();
    private readonly ToggleSwitch numLock = Switch();
    private readonly ToggleSwitch lockOnDisconnect = Switch();
    private readonly ToggleSwitch hibernation = Switch();
    private readonly ToggleSwitch turnOffOnRestart = Switch();
    private readonly ToggleSwitch fullScsi = Switch();
    private readonly ToggleSwitch battery = Switch();
    private readonly ComboBox resolution = new()
    {
        IsEditable = true, MinWidth = 150, IsEnabled = false,
        ItemsSource = new[] { DefaultResolution, "3840 x 2160", "2560 x 1440", "1920 x 1200", "1920 x 1080", "1600 x 900", "1366 x 768", "1280 x 1024", "1280 x 720", "1024 x 768", "800 x 600" }
    };
    private readonly Button applyResolution = new() { Content = "应用", IsEnabled = false };
    private readonly Dictionary<ToggleSwitch, bool> applied = [];
    private VmDisplayResolution? appliedResolution;
    private string appliedResolutionText = string.Empty;

    public VmAdvancedSettingsView(Guid vmId) : base(vmId)
    {
        var resolutionEditor = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        resolutionEditor.Children.Add(resolution);
        resolutionEditor.Children.Add(applyResolution);
        applyResolution.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
        var content = new StackPanel { Margin = new Thickness(24, 12, 24, 24) };
        content.Children.Add(Card(
            Row("控制台支持", "仅第 2 代虚拟机有效。关闭后无法经控制台访问，适合串流/RDP 直连。", console),
            Row("基本会话默认分辨率", "默认、预设或手动输入，宽高不可为奇数。", resolutionEditor),
            Row("开机时启用数字小键盘", null, numLock),
            Row("断开时锁定", "断开虚拟机控制台时锁定虚拟机系统", lockOnDisconnect),
            Row("虚拟机休眠（S4）", "允许虚拟机使用 S4 休眠和快速启动", hibernation),
            Row("重启时改为关机", "虚拟机系统请求重启时，关闭虚拟机而不是重新启动", turnOffOnRestart),
            Row("完整 SCSI 命令集", "允许虚拟机向挂载的物理磁盘发送未经筛选的 SCSI 命令", fullScsi),
            Row("电池状态透传", "允许虚拟机读取主机的电池状态和剩余电量", battery)));
        SetBody(content);
        console.Toggled += async (_, _) => await ApplyAsync(() => VmConsoleSupportService.SetConsoleSupportAsync(VmId, console.IsOn));
        numLock.Toggled += async (_, _) => await ApplyAsync(() => VmBootService.SetBootNumLockAsync(VmId, numLock.IsOn));
        lockOnDisconnect.Toggled += async (_, _) => await ApplyAsync(() => VmConsoleSupportService.SetLockOnDisconnectAsync(VmId, lockOnDisconnect.IsOn));
        hibernation.Toggled += async (_, _) => await ApplyAsync(() => VmPowerService.SetHibernationEnabledAsync(VmId, hibernation.IsOn));
        turnOffOnRestart.Toggled += async (_, _) => await ApplyAsync(() => VmPowerService.SetTurnOffOnGuestRestartAsync(VmId, turnOffOnRestart.IsOn));
        fullScsi.Toggled += async (_, _) => await ApplyAsync(() => VmStorageService.SetFullScsiCommandSetEnabledAsync(VmId, fullScsi.IsOn));
        battery.Toggled += async (_, _) => await ApplyAsync(() => VmPowerService.SetBatteryEnabledAsync(VmId, battery.IsOn));
        applyResolution.Click += async (_, _) => await RunAsync(ApplyResolutionAsync);
    }

    private Task ApplyAsync(Func<Task<ApiResponse>> write)
    {
        if (Updating || IsBusy || IsStopped) return Task.CompletedTask;
        return RunAsync(async () =>
        {
            ApiResponse result;
            try { result = await write(); }
            catch { RestoreApplied(); throw; }
            if (!result.Success) RestoreApplied();
            if (result.Success) NotifyChanged();
            await ReloadAfterWriteAsync(result);
        });
    }

    private void RestoreApplied()
    {
        if (IsStopped) return;
        Updating = true;
        try { foreach (var (toggle, value) in applied) toggle.IsOn = value; }
        finally { Updating = false; }
    }

    protected override async Task LoadAsync()
    {
        var contextTask = ReadContextAsync();
        var queryTask = QueryAsync(VmConsoleFields.BasicEnabled, VmBootGlobalFields.NumLock,
            VmConsoleFields.LockOnDisconnect, VmPowerBatteryFields.EnableHibernation,
            VmPowerBatteryFields.TurnOffOnGuestRestart, VmStorageGlobalFields.AllowFullScsiCommandSet,
            VmPowerBatteryFields.BatteryEnabled, VmDisplayFields.ResolutionType, VmDisplayFields.Width, VmDisplayFields.Height);
        await Task.WhenAll(contextTask, queryTask);
        var context = await contextTask;
        var row = await queryTask;
        if (IsStopped) return;
        Updating = true;
        try
        {
            var errors = new List<string>();
            SetSwitch(console, row.Get(VmConsoleFields.BasicEnabled), VmConsoleSupportService.GetModificationCondition(context.Generation, context.State), errors, "控制台支持");
            SetSwitch(numLock, row.Get(VmBootGlobalFields.NumLock), VmBootService.GetNumLockModificationCondition(context.State), errors, "数字小键盘");
            SetSwitch(lockOnDisconnect, row.Get(VmConsoleFields.LockOnDisconnect), true, errors, "断开时锁定");
            var hibernationValue = row.Get(VmPowerBatteryFields.EnableHibernation);
            SetSwitch(hibernation, hibernationValue, VmPowerService.GetHibernationModificationCondition(hibernationValue.HasValue, context.State), errors, "虚拟机休眠");
            SetSwitch(turnOffOnRestart, row.Get(VmPowerBatteryFields.TurnOffOnGuestRestart), true, errors, "重启时改为关机");
            SetSwitch(fullScsi, row.Get(VmStorageGlobalFields.AllowFullScsiCommandSet), true, errors, "完整 SCSI 命令集");
            SetSwitch(battery, row.Get(VmPowerBatteryFields.BatteryEnabled), true, errors, "电池状态透传");

            var type = row.Get(VmDisplayFields.ResolutionType);
            var width = row.Get(VmDisplayFields.Width);
            var height = row.Get(VmDisplayFields.Height);
            bool hasResolution = type.HasValue && width.HasValue && height.HasValue;
            if (hasResolution)
            {
                appliedResolution = new(type.Value, width.Value, height.Value);
                appliedResolutionText = type.Value switch
                {
                    2 => $"最大分辨率（{width.Value} x {height.Value}）",
                    3 => $"{width.Value} x {height.Value}",
                    4 => DefaultResolution,
                    _ => $"当前策略 {type.Value}（{width.Value} x {height.Value}）"
                };
                resolution.Text = appliedResolutionText;
            }
            else appliedResolution = null;
            bool knownResolution = hasResolution && type.Value is 2 or 3 or 4;
            var condition = VmDisplaySettingsService.GetModificationCondition(context.State);
            resolution.IsEnabled = applyResolution.IsEnabled = knownResolution && condition.CanModify;
            ToolTipService.SetToolTip(resolution, !hasResolution ? type.Error ?? "当前虚拟机没有可用的显示分辨率配置。"
                : !knownResolution ? "当前分辨率策略尚不支持编辑，已保留原值。" : condition.CanModify ? null : condition.Reason);
            if (!hasResolution && type.State == VmQueryValueState.Failed) errors.Add("分辨率：" + type.Error);
            if (errors.Count > 0) ShowError(string.Join(Environment.NewLine, errors));
        }
        finally { Updating = false; }
    }

    private void SetSwitch(ToggleSwitch target, VmQueryValue<bool> value, VmModificationCondition condition, List<string> errors, string title)
    {
        SetSwitch(target, value, condition.CanModify, errors, title);
        if (value.HasValue && !condition.CanModify) ToolTipService.SetToolTip(target, condition.Reason);
    }

    private void SetSwitch(ToggleSwitch target, VmQueryValue<bool> value, bool editable, List<string> errors, string title)
    {
        // 缺失字段不伪装为 false；保留已有显示，并禁用该项。
        if (value.HasValue) { target.IsOn = value.Value; applied[target] = value.Value; }
        target.IsEnabled = value.HasValue && editable;
        ToolTipService.SetToolTip(target, value.HasValue ? null : value.Error ?? "当前宿主未提供此设置。");
        if (!value.HasValue && value.State is VmQueryValueState.Failed or VmQueryValueState.Unavailable or VmQueryValueState.Ambiguous)
            errors.Add(title + "：" + (value.Error ?? "读取失败"));
    }

    private async Task ApplyResolutionAsync()
    {
        var text = resolution.Text.Trim();
        if (appliedResolution is not { } previous || previous.ResolutionType is not (2 or 3 or 4)) return;
        if (string.Equals(text, appliedResolutionText, StringComparison.Ordinal)) return;
        int type = 4, width = 0, height = 0;
        if (!string.Equals(text, DefaultResolution, StringComparison.Ordinal))
        {
            var parts = text.Split(['x', 'X', '×', '*', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !int.TryParse(parts[0], out width) || !int.TryParse(parts[1], out height))
                throw new InvalidOperationException("请输入有效分辨率，如 1280 x 720。");
            type = 3;
        }
        if (VmDisplaySettingsService.GetResolutionError(type, width, height) is { } error)
            throw new InvalidOperationException(error);
        if (type == previous.ResolutionType && (type != 3 || width == previous.Width && height == previous.Height)) return;
        var result = await VmDisplaySettingsService.SetResolutionAsync(VmId, type, width, height);
        if (result.Success) NotifyChanged();
        await ReloadAfterWriteAsync(result);
    }
}
