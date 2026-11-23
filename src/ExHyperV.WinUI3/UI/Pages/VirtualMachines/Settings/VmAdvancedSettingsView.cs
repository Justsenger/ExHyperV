using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmAdvancedSettingsView : VmSettingsView
{
    public enum Section { All, Console, Power, Other }

    private const string DefaultResolution = "默认";

    private sealed record CardMeta(string Title, string Description, Grid Label, SettingInfoButton Info);

    private readonly ToggleSwitch console = Switch();
    private readonly ToggleSwitch numLock = Switch();
    private readonly ToggleSwitch lockOnDisconnect = Switch();
    private readonly ToggleSwitch hibernation = Switch();
    private readonly ToggleSwitch turnOffOnRestart = Switch();
    private readonly ToggleSwitch bootVpci = Switch();
    private readonly ToggleSwitch fullScsi = Switch();
    private readonly ToggleSwitch battery = Switch();
    private readonly ComboBox resolution = new()
    {
        IsEditable = true,
        MinWidth = 170,
        IsEnabled = false,
        ItemsSource = new[]
        {
            DefaultResolution, "3840 x 2160", "2560 x 1440", "1920 x 1200", "1920 x 1080",
            "1600 x 900", "1366 x 768", "1280 x 1024", "1280 x 720", "1024 x 768", "800 x 600"
        }
    };
    private readonly Button applyResolution = new() { Content = "应用", IsEnabled = false };
    private readonly Dictionary<ToggleSwitch, bool> applied = [];
    private readonly Dictionary<FrameworkElement, CardMeta> cardMeta = [];
    private VmDisplayResolution? appliedResolution;
    private string appliedResolutionText = string.Empty;

    private readonly Section activeSection;

    public VmAdvancedSettingsView(Guid vmId, Section section = Section.All) : base(vmId)
    {
        activeSection = section;
        var resolutionEditor = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        resolutionEditor.Children.Add(resolution);
        resolutionEditor.Children.Add(applyResolution);
        applyResolution.Style = (Style)Application.Current.Resources["AccentButtonStyle"];

        Border MakeToggleCard(string title, string desc, ToggleSwitch toggle, bool span2 = false)
        {
            var card = UniformCard(title, desc, toggle, out var label, out var info, span2);
            cardMeta[toggle] = new(title, desc, label, info);
            return card;
        }

        Border MakeResolutionCard()
        {
            const string title = "默认分辨率";
            const string desc = "控制台使用基本会话时的默认窗口分辨率，需在关机状态下修改";
            var card = UniformCard(title, desc, resolutionEditor, out var label, out var info, span2: true);
            cardMeta[resolution] = new(title, desc, label, info);
            return card;
        }

        var panel = new SettingsFieldPanel { Spacing = 12 };
        switch (section)
        {
            case Section.Power:
                panel.Children.Add(MakeToggleCard(
                    "允许休眠",
                    "向虚拟机开放 ACPI 休眠（S4）能力，允许来宾系统启用休眠或 Windows 快速启动",
                    hibernation));
                panel.Children.Add(MakeToggleCard(
                    "重启时关机",
                    "当虚拟机内部请求重启时，改为直接关机而不是重新启动",
                    turnOffOnRestart));
                panel.Children.Add(MakeToggleCard(
                    "电池透传",
                    "将宿主电池电量与充电状态同步给虚拟机",
                    battery,
                    span2: true));
                break;

            case Section.Console:
                panel.Children.Add(MakeToggleCard(
                    "启用控制台",
                    "仅第 2 代虚拟机有效。关闭后会移除虚拟显示与键鼠，无法点击“连接”打开画面，适合纯 RDP、SSH 或显卡直通串流场景",
                    console,
                    span2: true));
                panel.Children.Add(MakeToggleCard(
                    "断开时锁屏",
                    "关闭控制台连接窗口时，自动锁定虚拟机内的系统",
                    lockOnDisconnect));
                panel.Children.Add(MakeToggleCard(
                    "开机数字小键盘",
                    "开机时默认开启 NumLock，避免连接控制台时同步关闭宿主小键盘。第 1 代与第 2 代均支持（关机状态可修改）",
                    numLock));
                panel.Children.Add(MakeResolutionCard());
                break;

            case Section.Other:
                break;

            default:
                panel.Children.Add(MakeToggleCard("允许休眠", "向虚拟机开放 ACPI 休眠（S4）能力，允许来宾系统启用休眠或 Windows 快速启动", hibernation));
                panel.Children.Add(MakeToggleCard("重启时关机", "当虚拟机内部请求重启时，改为直接关机而不是重新启动", turnOffOnRestart));
                panel.Children.Add(MakeToggleCard("电池透传", "将宿主电池电量与充电状态同步给虚拟机", battery, span2: true));
                panel.Children.Add(MakeToggleCard("启用控制台", "仅第 2 代虚拟机有效。关闭后会移除虚拟显示与键鼠，无法点击“连接”打开画面，适合纯 RDP、SSH 或显卡直通串流场景", console, span2: true));
                panel.Children.Add(MakeToggleCard("断开时锁屏", "关闭控制台连接窗口时，自动锁定虚拟机内的系统", lockOnDisconnect));
                panel.Children.Add(MakeToggleCard("开机数字小键盘", "开机时默认开启 NumLock，避免连接控制台时同步关闭宿主小键盘。第 1 代与第 2 代均支持（关机状态可修改）", numLock));
                panel.Children.Add(MakeResolutionCard());
                break;
        }

        if (section is Section.All or Section.Other)
        {
            panel.Children.Add(MakeToggleCard(L10n.Get("Pcie_Boot"), L10n.Get("Pcie_BootHelp"), bootVpci, span2: true));
            panel.Children.Add(MakeToggleCard(L10n.Get("Pcie_FullScsi"), L10n.Get("Pcie_FullScsiHelp"), fullScsi, span2: true));
        }

        var content = new StackPanel { Margin = new Thickness(0, 4, 0, 24) };
        content.Children.Add(panel);
        SetBody(content);

        console.Toggled += async (_, _) => await ApplyAsync(() => VmConsoleSupportService.SetConsoleSupportAsync(VmId, console.IsOn));
        numLock.Toggled += async (_, _) => await ApplyAsync(() => VmBootService.SetBootNumLockAsync(VmId, numLock.IsOn));
        lockOnDisconnect.Toggled += async (_, _) => await ApplyAsync(() => VmConsoleSupportService.SetLockOnDisconnectAsync(VmId, lockOnDisconnect.IsOn));
        hibernation.Toggled += async (_, _) => await ApplyAsync(() => VmPowerService.SetHibernationEnabledAsync(VmId, hibernation.IsOn));
        turnOffOnRestart.Toggled += async (_, _) => await ApplyAsync(() => VmPowerService.SetTurnOffOnGuestRestartAsync(VmId, turnOffOnRestart.IsOn));
        bootVpci.Toggled += async (_, _) => await ApplyAsync(() => VmPcieService.SetBootPciExpressAsync(VmId, bootVpci.IsOn));
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

    private VmQueryField[] QueryFieldsForSection() => activeSection switch
    {
        Section.Power =>
        [
            VmPowerBatteryFields.EnableHibernation,
            VmPowerBatteryFields.TurnOffOnGuestRestart,
            VmPowerBatteryFields.BatteryEnabled
        ],
        Section.Console =>
        [
            VmSecurityFields.Shielded,
            VmConsoleFields.BasicEnabled,
            VmConsoleFields.LockOnDisconnect,
            VmBootGlobalFields.NumLock,
            VmDisplayFields.ResolutionType,
            VmDisplayFields.Width,
            VmDisplayFields.Height
        ],
        Section.Other =>
        [
            VmPcieFields.BootPciExpress,
            VmStorageGlobalFields.AllowFullScsiCommandSet
        ],
        _ =>
        [
            VmSecurityFields.Shielded,
            VmConsoleFields.BasicEnabled,
            VmBootGlobalFields.NumLock,
            VmConsoleFields.LockOnDisconnect,
            VmPowerBatteryFields.EnableHibernation,
            VmPowerBatteryFields.TurnOffOnGuestRestart,
            VmPcieFields.BootPciExpress,
            VmStorageGlobalFields.AllowFullScsiCommandSet,
            VmPowerBatteryFields.BatteryEnabled,
            VmDisplayFields.ResolutionType,
            VmDisplayFields.Width,
            VmDisplayFields.Height
        ]
    };

    protected override async Task LoadAsync()
    {
        var contextTask = ReadContextAsync();
        var queryTask = QueryAsync(QueryFieldsForSection());
        await Task.WhenAll(contextTask, queryTask);
        var context = await contextTask;
        var row = await queryTask;
        if (IsStopped) return;
        Updating = true;
        try
        {
            var errors = new List<string>();
            if (activeSection is Section.All or Section.Power)
            {
                var hibernationValue = row.Get(VmPowerBatteryFields.EnableHibernation);
                SetSwitch(hibernation, hibernationValue,
                    VmPowerService.GetHibernationModificationCondition(hibernationValue.HasValue, context.State), errors);
                SetSwitch(turnOffOnRestart, row.Get(VmPowerBatteryFields.TurnOffOnGuestRestart), true, errors);
                SetSwitch(battery, row.Get(VmPowerBatteryFields.BatteryEnabled), true, errors);
            }

            if (activeSection is Section.All or Section.Console)
            {
                var consoleValue = row.Get(VmConsoleFields.BasicEnabled);
                SetSwitch(console, consoleValue,
                    VmConsoleSupportService.GetModificationCondition(context.Generation, context.State), errors);
                var shielding = row.Get(VmSecurityFields.Shielded);
                bool consoleLockedByShield = context.Generation == 2 && (!shielding.HasValue || shielding.Value);
                if (consoleLockedByShield)
                {
                    if (shielding.HasValue) console.IsOn = applied[console] = false;
                    var meta = cardMeta[console];
                    ApplyCardState(meta.Label, meta.Info, console, meta.Description,
                        available: consoleValue.HasValue, editable: false,
                        consoleValue.Error,
                        shielding.HasValue ? "已在「安全」中开启受防护模式并锁定关闭，需先关闭受防护模式才能启用" : shielding.Error);
                }

                SetSwitch(lockOnDisconnect, row.Get(VmConsoleFields.LockOnDisconnect), true, errors);
                SetSwitch(numLock, row.Get(VmBootGlobalFields.NumLock),
                    VmBootService.GetNumLockModificationCondition(context.State), errors);

                var type = row.Get(VmDisplayFields.ResolutionType);
                var width = row.Get(VmDisplayFields.Width);
                var height = row.Get(VmDisplayFields.Height);
                bool hasResolution = type.HasValue && width.HasValue && height.HasValue;
                bool consoleOff = (consoleLockedByShield && shielding.HasValue && shielding.Value)
                    || (consoleValue.HasValue && !consoleValue.Value);
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
                    resolution.PlaceholderText = string.Empty;
                    resolution.Text = appliedResolutionText;
                }
                else
                {
                    appliedResolution = null;
                    appliedResolutionText = string.Empty;
                    resolution.Text = string.Empty;
                    resolution.PlaceholderText = consoleOff ? "未启用控制台" : DefaultResolution;
                }
                bool knownResolution = hasResolution && type.Value is 2 or 3 or 4;
                var resCondition = VmDisplaySettingsService.GetModificationCondition(context.State);
                var resMeta = cardMeta[resolution];
                applyResolution.IsEnabled = knownResolution && resCondition.CanModify;
                string? resUnavailableReason = !hasResolution
                    ? type.State == VmQueryValueState.Failed
                        ? type.Error
                        : consoleLockedByShield && shielding.HasValue && shielding.Value
                            ? "已在「安全」中开启受防护模式并锁定关闭控制台，当前无基本会话显示控制器，需先关闭受防护模式并启用控制台"
                            : consoleValue.HasValue && !consoleValue.Value
                                ? "已关闭「启用控制台」，当前无基本会话显示控制器，开启控制台后才可设置默认分辨率"
                                : "当前虚拟机没有可用的显示分辨率配置。"
                    : "当前分辨率策略尚不支持编辑，已保留原值。";
                ApplyCardState(resMeta.Label, resMeta.Info, resolution, resMeta.Description,
                    available: knownResolution, editable: resCondition.CanModify,
                    resUnavailableReason,
                    resCondition.Reason);
                if (!hasResolution && type.State == VmQueryValueState.Failed)
                    errors.Add("分辨率：" + type.Error);
            }

            if (activeSection is Section.All or Section.Other)
            {
                var bootValue = row.Get(VmPcieFields.BootPciExpress);
                if (context.Generation != 2)
                {
                    bootVpci.IsOn = applied[bootVpci] = false;
                    var bootMeta = cardMeta[bootVpci];
                    ApplyCardState(bootMeta.Label, bootMeta.Info, bootVpci, bootMeta.Description,
                        available: true, editable: false, null,
                        L10n.Get("Pcie_Gen2Only"));
                }
                else
                {
                    SetSwitch(bootVpci, bootValue,
                        VmPcieService.GetModificationCondition(
                            new VmPcieSettings { BootPciExpressAvailable = bootValue.HasValue },
                            nameof(VmPcieSettings.BootPciExpress),
                            context.State),
                        errors);
                }
                SetSwitch(fullScsi, row.Get(VmStorageGlobalFields.AllowFullScsiCommandSet), true, errors);
            }

            if (errors.Count > 0) ShowError(string.Join(Environment.NewLine, errors));
        }
        finally { Updating = false; }
    }

    private void SetSwitch(ToggleSwitch target, VmQueryValue<bool> value, VmModificationCondition condition, List<string> errors)
        => SetSwitch(target, value, condition.CanModify, errors, condition.CanModify ? null : condition.Reason);

    private void SetSwitch(ToggleSwitch target, VmQueryValue<bool> value, bool editable, List<string> errors, string? lockedReason = null)
    {
        if (value.HasValue) { target.IsOn = value.Value; applied[target] = value.Value; }
        var meta = cardMeta[target];
        ApplyCardState(meta.Label, meta.Info, target, meta.Description,
            available: value.HasValue, editable: editable, value.Error, lockedReason);
        if (!value.HasValue && value.State is VmQueryValueState.Failed or VmQueryValueState.Unavailable or VmQueryValueState.Ambiguous)
            errors.Add(meta.Title + "：" + (value.Error ?? "读取失败"));
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
