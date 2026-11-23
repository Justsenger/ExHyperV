using ExHyperV.Models;
using ExHyperV.WinUI3.Localization;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.WinUI3.UI.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.System;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>虚拟 TPM 概览、解锁密钥管理与即时策略设置。</summary>
public sealed partial class VmVtpmSettingsView : VmSettingsView
{
    private readonly StackPanel panel = new() { Margin = new(0, 0, 0, 20), Spacing = 12 };
    private VmVtpmInfo? info;
    private int generation;
    private VmState power;
    private bool hasLoadedData;
    private bool settings, policyTab, confirmDisable;

    private bool Offline => power == VmState.PoweredOff;
    private bool CanManageKeys => generation == 2 && Offline;
    private bool CanEdit => CanManageKeys && info?.Shielded == false;
    private bool MissingKey => info is { TpmEnabled: true, HasLocalKey: false } &&
        (info.KeyProtectorConfigured == false || info.KeyReadError == null);

    public VmVtpmSettingsView(Guid vmId) : base(vmId)
    {
        SetBody(panel);
        ActualThemeChanged += (_, _) =>
        {
            if (hasLoadedData && !IsStopped) Render();
        };
    }

    protected override async Task LoadAsync()
    {
        var context = await ReadContextAsync();
        var result = context.Generation == 2 ? await VmSecurityService.GetVtpmStateAsync(VmId) : null;
        if (result is { HasData: false }) throw new InvalidOperationException(result.Error);
        if (IsStopped) return;
        generation = context.Generation;
        power = context.State;
        info = result?.Data;
        if (!CanEdit || info?.TpmEnabled != true) confirmDisable = false;
        hasLoadedData = true;
        Render();
    }

    private void Render()
    {
        panel.Children.Clear();
        if (settings)
        {
            BuildTabs();
            if (policyTab) BuildPolicy();
            else BuildKeys();
        }
        else
        {
            BuildOverview();
        }
    }

    private Button Command(string label, Func<Task> action, bool enabled = true)
    {
        var button = new Button { Content = label, IsEnabled = enabled };
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    private async Task ApplyAsync(Task<ApiResponse> write) => await FinishWriteAsync(await write);

    private async Task FinishWriteAsync(ApiResponse result)
    {
        if (result.Success || result.Steps.Any(s => s.State == OperationStepState.Succeeded)) NotifyChanged();
        await ReloadAfterWriteAsync(result);
    }

    private ContentDialog? activeDialog;

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        activeDialog = dialog;
        try { return await dialog.ShowAsync(); }
        finally { activeDialog = null; }
    }

    public override Task StopAsync()
    {
        activeDialog?.Hide();
        return base.StopAsync();
    }

    private async Task ReissueAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = L10n.Get("Vtpm_ReissueTitle"),
            Content = L10n.Format("Vtpm_ReissueBody", VmId),
            PrimaryButtonText = L10n.Get("Common_Confirm"), CloseButtonText = L10n.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary && !IsStopped)
            await ApplyAsync(VmSecurityService.ReissueLocalKeyProtectorAsync(VmId));
    }

    private void BuildOverview()
    {
        bool enabled = info?.TpmEnabled == true;
        var hosts = info?.AuthorizedHosts;
        string? subtitle = generation != 2
            ? "第 1 代虚拟机不支持"
            : MissingKey
                ? "缺少当前宿主密钥"
                : !enabled
                    ? "未启用"
                    : hosts?.Count > 1
                        ? $"已授权 {hosts.Count} 个宿主"
                        : null;

        var toggle = Switch();
        Updating = true;
        try { toggle.IsOn = enabled; }
        finally { Updating = false; }
        toggle.IsEnabled = CanEdit;
        ToolTipService.SetToolTip(toggle, generation != 2
            ? "仅第 2 代虚拟机支持 vTPM"
            : !Offline
                ? "关闭虚拟机后可修改"
                : info?.Shielded == true
                    ? "已由受防护模式锁定开启"
                    : enabled ? "关闭 vTPM" : "启用 vTPM");
        toggle.Toggled += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped) return;
            if (enabled)
            {
                confirmDisable = !confirmDisable;
                Render();
                return;
            }
            confirmDisable = false;
            await RunAsync(() => ApplyAsync(VmSecurityService.SetTpmAsync(VmId, true)));
        };

        var header = Header(
            scope: this,
            fluentGlyph: "\uEAD1",
            title: "vTPM",
            subtitle: subtitle,
            active: enabled,
            badge: null,
            toggle: toggle,
            configure: () => { settings = true; policyTab = false; confirmDisable = false; Render(); },
            configureTooltip: "配置 vTPM 密钥与高级安全设置",
            subtitleCaution: MissingKey);

        UIElement? dangerStrip = null;
        if (confirmDisable && CanEdit && enabled)
        {
            var cancel = new Button { Content = "取消" };
            cancel.Click += (_, _) => { confirmDisable = false; Render(); };

            var confirm = DangerButton("确认关闭", () => { });
            confirm.Click += async (_, _) =>
            {
                confirmDisable = false;
                await RunAsync(() => ApplyAsync(VmSecurityService.SetTpmAsync(VmId, false)));
            };

            string warningText = info?.EncryptEnabled == true
                ? "关闭将同时关闭「状态与迁移加密」，且系统内 BitLocker 可能要求恢复密钥。"
                : "关闭后系统内依赖 vTPM 的 BitLocker 或安全解锁功能将受影响。";
            dangerStrip = DangerStrip(this, "确认关闭 vTPM？", warningText, cancel, confirm);
        }

        var bound = hosts?.FirstOrDefault(h => h.IsLocal) ?? hosts?.FirstOrDefault(h => h.IsOwner);
        string binding = generation != 2
            ? "不支持"
            : info?.KeyReadError ?? (bound == null ? "未绑定" : bound.IsLocal ? $"{Environment.MachineName}（当前宿主）" : bound.Name);

        string? file = generation == 2 ? info?.GuestStateFile : null;
        string stateFileText = generation != 2
            ? "不适用"
            : file ?? info?.GuestStateDescription ?? "未生成";

        var body = KvGrid(
            (KvTile(
                this,
                "绑定宿主",
                binding,
                copyValue: generation == 2 ? bound?.SigningThumbprint : null,
                copyLabel: bound?.SigningThumbprint != null ? $"点击复制签名指纹：{bound.SigningThumbprint}" : null,
                onError: ShowError), true),
            (KvTile(
                this,
                "状态文件",
                stateFileText,
                copyValue: file,
                copyLabel: file != null ? $"点击复制：{file}" : null,
                onError: ShowError,
                onReveal: file != null ? () => _ = RunAsync(() => RevealStateFileAsync(file)) : null,
                revealLabel: "打开状态文件所在文件夹"), true));

        panel.Children.Add(CardShell(this, header, body, dangerStrip));

        if (generation != 2) return;
        if (!Offline)
        {
            panel.Children.Add(Notice("虚拟机运行中，vTPM 开关已锁定", "关闭虚拟机后才可开启或关闭 vTPM"));
        }
        else if (MissingKey)
        {
            var bar = Notice("当前宿主无法解开此 vTPM", "虚拟机可能从其他宿主拷贝而来，需重新绑定后才可启动");
            bar.ActionButton = Command("重新绑定当前宿主", ReissueAsync);
            panel.Children.Add(bar);
        }
        else if (info?.Shielded == true)
        {
            panel.Children.Add(Notice("已启用受防护模式，vTPM 已锁定开启", "如需关闭请先前往「高级 → 策略」关闭受防护模式"));
        }
    }

    private void BuildTabs()
    {
        panel.Children.Add(PivotHeader(
            this,
            ((string, string, bool, Action)[])[
                ("\uE72E", "密钥", !policyTab, () => { if (policyTab) { policyTab = false; Render(); } }),
                ("\uE713", "策略", policyTab, () => { if (!policyTab) { policyTab = true; Render(); } })
            ],
            () => { settings = false; Render(); }));
    }

    private void BuildKeys()
    {
        string keyLockTip = generation != 2 ? "仅第 2 代虚拟机支持" : "关闭虚拟机后可修改";

        panel.Children.Add(SettingCard(
            this,
            "重新签发",
            "使用当前宿主证书重新生成 vTPM 解锁密钥，适用于从其他宿主迁移后无法开机时修复。若系统内启用了 BitLocker，下次开机需要输入恢复密钥",
            Command("签发", ReissueAsync, CanManageKeys),
            enabled: CanManageKeys,
            disabledTooltip: keyLockTip));

        bool canRestore = CanManageKeys && info?.HasPreviousKey == true;
        panel.Children.Add(SettingCard(
            this,
            "撤销上次修改",
            "恢复到上一次修改或重签之前还能正常启动的解锁密钥",
            Command("还原", () => ApplyAsync(VmSecurityService.RestorePreviousKeyProtectorAsync(VmId)), canRestore),
            enabled: canRestore,
            disabledTooltip: CanManageKeys ? "本次会话暂无可还原的历史密钥" : keyLockTip));

        bool canGrant = CanManageKeys && info?.HasLocalKey == true;
        var grantActions = Actions(
            Command("导出宿主证书", ExportAsync, generation == 2),
            Command("添加授权", ImportAsync, canGrant));

        var hostList = new StackPanel { Spacing = 6 };
        if (info is { HasLocalKey: false, KeyReadError: null, LocalSigningThumbprint: { } localThumb })
        {
            hostList.Children.Add(BuildAuthorizedHostStrip(
                Environment.MachineName,
                "当前宿主",
                $"尚未绑定  {ShortThumbprint(localThumb)}",
                actionButton: null));
        }

        foreach (var host in info?.AuthorizedHosts ?? [])
        {
            Button? revoke = null;
            if (!host.IsLocal)
            {
                revoke = Command(
                    "移除",
                    () => ApplyAsync(VmSecurityService.RevokeHostAccessAsync(VmId, host.SigningThumbprint, host.EncryptionThumbprint)),
                    canGrant && !host.IsOwner);
                revoke.Padding = new Thickness(10, 3, 10, 4);
                revoke.MinHeight = 26;
                revoke.FontSize = 12;
            }
            hostList.Children.Add(BuildAuthorizedHostStrip(
                host.IsLocal ? Environment.MachineName : host.Name,
                host.IsLocal ? "当前宿主" : "已授权",
                ShortThumbprint(host.SigningThumbprint),
                revoke));
        }

        if (info?.KeyReadError is { } error)
        {
            hostList.Children.Add(new TextBlock
            {
                Text = error,
                FontSize = 12,
                Foreground = ThemeBrush(this, "SystemFillColorCautionBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }

        panel.Children.Add(SettingCard(
            this,
            "授权其他宿主启动",
            "导出当前宿主证书供其他宿主导入，或选择其他宿主导出的证书文件以允许其直接启动此虚拟机",
            grantActions,
            below: hostList.Children.Count > 0 ? hostList : null,
            enabled: generation == 2,
            disabledTooltip: "仅第 2 代虚拟机支持"));

        if (generation == 2 && !Offline)
            panel.Children.Add(Notice("虚拟机运行中，密钥操作与安全策略已锁定", "关闭虚拟机后才可修改密钥授权与安全隔离策略"));
    }

    private Border BuildAuthorizedHostStrip(string hostName, string badgeText, string rightDetail, Button? actionButton)
    {
        var left = Actions(
            spacing: 8,
            new TextBlock
            {
                Text = hostName,
                FontSize = 12.5,
                Foreground = ThemeBrush(this, "TextFillColorPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            },
            Badge(this, badgeText));

        var rightItems = new List<UIElement>
        {
            new TextBlock
            {
                Text = rightDetail,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
                Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        if (actionButton != null) rightItems.Add(actionButton);

        return new Border
        {
            Background = ThemeBrush(this, "SubtleFillColorTertiaryBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 10, 7),
            Child = Split(left, Actions(spacing: 8, rightItems.ToArray()))
        };
    }

    private async Task RevealStateFileAsync(string path)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        if (!fullPath.Contains('"') && System.IO.File.Exists(fullPath))
        {
            try
            {
                // RunAsAdministrator 或 VM 运行中锁定 .vmgs 时，explorer.exe /select 比 WinRT StorageFile 更稳定地定位并选中文件。
                using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{fullPath}\"",
                    UseShellExecute = true
                });
                return;
            }
            catch
            {
                // 回退到 WinRT 文件夹定位。
            }

            try
            {
                var file = await StorageFile.GetFileFromPathAsync(fullPath);
                var folder = await file.GetParentAsync();
                if (folder != null)
                {
                    var options = new FolderLauncherOptions();
                    options.ItemsToSelect.Add(file);
                    if (await Launcher.LaunchFolderAsync(folder, options)) return;
                }
            }
            catch
            {
                // 回退到直接打开所在目录。
            }
        }

        string? dir = System.IO.Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(dir)) throw new InvalidOperationException("无法打开状态文件所在文件夹。");
        var fallback = await StorageFolder.GetFolderFromPathAsync(dir);
        if (!await Launcher.LaunchFolderAsync(fallback))
            throw new InvalidOperationException("无法打开状态文件所在文件夹。");
    }

    private async Task ExportAsync()
    {
        string? path = await StoragePickers.SaveAsync($"{Environment.MachineName}-Guardian.xml", "宿主证书", ".xml", ".cer", ".crt");
        if (path != null && !IsStopped) await ApplyAsync(VmSecurityService.ExportLocalGuardianAsync(VmId, path));
    }

    private async Task ImportAsync()
    {
        string? path = await StoragePickers.OpenAsync(".xml", ".cer", ".crt", ".pfx");
        if (path == null || IsStopped) return;
        string? password = null;
        if (string.Equals(System.IO.Path.GetExtension(path), ".pfx", StringComparison.OrdinalIgnoreCase))
        {
            var input = new PasswordBox { Header = "证书密码（无密码时留空）" };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "读取宿主证书",
                Content = input,
                PrimaryButtonText = "继续",
                CloseButtonText = "取消"
            };
            if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || IsStopped) return;
            password = input.Password;
        }
        var grant = await VmSecurityService.GrantHostAccessAsync(VmId, path, password);
        if (grant.DiagnosticCode == CoreErrorCode.GuardianMetadataRequired && !IsStopped)
        {
            var explain = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "核对宿主证书",
                Content = grant.Error,
                PrimaryButtonText = "选择宿主 XML",
                CloseButtonText = "取消"
            };
            if (await ShowDialogAsync(explain) != ContentDialogResult.Primary || IsStopped) return;
            var metadata = await StoragePickers.OpenAsync(".xml");
            if (metadata == null || IsStopped) return;
            grant = await VmSecurityService.GrantHostAccessAsync(VmId, path, password, metadata);
        }
        await FinishWriteAsync(grant);
    }

    private void BuildPolicy()
    {
        var first = PolicyCard(
            "绑定宿主物理 TPM",
            "将解锁密钥绑定到当前宿主主板的物理 TPM 芯片，整机拷贝到其他宿主将无法启动，更换主板后也无法解锁",
            VtpmPolicy.BindToHostTpm,
            info?.BindToHostTpm);

        var second = PolicyCard(
            "禁用 VBS",
            "关闭虚拟机内部的基于虚拟化的安全（VBS，如内核隔离与内存完整性），仅在兼容排障时开启",
            VtpmPolicy.VirtualizationBasedSecurityOptOut,
            info?.VirtualizationBasedSecurityOptOut);

        var third = PolicyCard(
            "禁止跨宿主迁移",
            "限制此虚拟机禁止在多台 Hyper-V 宿主之间进行实时迁移或导出受保护状态",
            VtpmPolicy.DisableMigration,
            info?.MigrationDisabled);

        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        grid.Children.Add(first);
        Grid.SetColumn(second, 1);
        grid.Children.Add(second);
        Grid.SetRow(third, 1);
        Grid.SetColumnSpan(third, 2);
        grid.Children.Add(third);
        panel.Children.Add(grid);

        if (generation != 2)
        {
            panel.Children.Add(Notice("策略已锁定", "仅第 2 代虚拟机支持 vTPM 策略。", InfoBarSeverity.Informational));
        }
        else if (!Offline)
        {
            panel.Children.Add(Notice("虚拟机运行中，密钥操作与安全策略已锁定", "关闭虚拟机后才可修改密钥授权与安全隔离策略"));
        }
        else if (info?.Shielded == true)
        {
            panel.Children.Add(Notice("已启用受防护模式，安全策略已锁定", "如需修改请先前往「高级 → 策略」关闭受防护模式，密钥修复不受影响"));
        }
    }

    private Border PolicyCard(string title, string help, VtpmPolicy policy, bool? value)
    {
        var toggle = Switch();
        bool needsTpm = policy == VtpmPolicy.BindToHostTpm && info?.TpmEnabled != true;
        bool isEnabled = CanEdit && value.HasValue && !needsTpm;

        Updating = true;
        try { toggle.IsOn = value == true && (policy != VtpmPolicy.BindToHostTpm || info?.TpmEnabled == true); }
        finally { Updating = false; }
        toggle.IsEnabled = isEnabled;

        string? disabledTip = generation != 2
            ? "仅第 2 代虚拟机支持"
            : !Offline
                ? "关闭虚拟机后可修改"
                : info?.Shielded == true
                    ? "已由受防护模式锁定"
                    : value == null
                        ? "当前宿主未提供可识别的策略值"
                        : needsTpm
                            ? "需先开启 vTPM 后才可修改"
                            : null;

        ToolTipService.SetToolTip(toggle, disabledTip ?? $"切换{title}");
        toggle.Toggled += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped) return;
            await RunAsync(() => ApplyAsync(VmSecurityService.SetVtpmPolicyAsync(VmId, policy, toggle.IsOn)));
        };

        return SettingCard(
            this,
            title,
            help,
            toggle,
            enabled: isEnabled,
            disabledTooltip: disabledTip);
    }

    private static string ShortThumbprint(string value)
        => string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(12, value.Length)];
}
