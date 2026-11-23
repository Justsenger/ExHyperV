
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.System;
using static ExHyperV.App.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings;

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
            ? L10n.Get("Vtpm_GenerationUnsupported")
            : MissingKey
                ? L10n.Get("Vtpm_MissingHostKey")
                : !enabled
                    ? L10n.Get("Common_NotEnabled")
                    : hosts?.Count > 1
                        ? L10n.Format("Vtpm_AuthorizedHosts", hosts.Count)
                        : null;

        var toggle = Switch();
        Updating = true;
        try { toggle.IsOn = enabled; }
        finally { Updating = false; }
        toggle.IsEnabled = CanEdit;
        ToolTipService.SetToolTip(toggle, generation != 2
            ? L10n.Get("Vtpm_RequiresGen2")
            : !Offline
                ? L10n.Get("Common_ModifyWhenOff")
                : info?.Shielded == true
                    ? L10n.Get("Security_ShieldedOn")
                    : enabled ? L10n.Get("Vtpm_TurnOff") : L10n.Get("Vtpm_TurnOn"));
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
            configureTooltip: L10n.Get("Vtpm_Configure"),
            subtitleCaution: MissingKey);

        UIElement? dangerStrip = null;
        if (confirmDisable && CanEdit && enabled)
        {
            var cancel = new Button { Content = L10n.Get("Common_Cancel") };
            cancel.Click += (_, _) => { confirmDisable = false; Render(); };

            var confirm = DangerButton(L10n.Get("Vtpm_ConfirmOff"), () => { });
            confirm.Click += async (_, _) =>
            {
                confirmDisable = false;
                await RunAsync(() => ApplyAsync(VmSecurityService.SetTpmAsync(VmId, false)));
            };

            string warningText = info?.EncryptEnabled == true
                ? L10n.Get("Vtpm_DisableEncryptionWarning")
                : L10n.Get("Vtpm_BitLockerWarning");
            dangerStrip = DangerStrip(this, L10n.Get("Vtpm_DisablePrompt"), warningText, cancel, confirm);
        }

        var bound = hosts?.FirstOrDefault(h => h.IsLocal) ?? hosts?.FirstOrDefault(h => h.IsOwner);
        string binding = generation != 2
            ? L10n.Get("Host_Gpu_Unsupported")
            : info?.KeyReadError ?? (bound == null ? L10n.Get("Vtpm_Unbound") : bound.IsLocal ? L10n.Format("Vtpm_CurrentHostLabel", Environment.MachineName) : bound.Name);

        string? file = generation == 2 ? info?.GuestStateFile : null;
        string stateFileText = generation != 2
            ? L10n.Get("VmState_NotApplicable")
            : file ?? info?.GuestStateDescription ?? L10n.Get("Vtpm_NotGenerated");

        var body = KvGrid(
            (KvTile(
                this,
                L10n.Get("Vtpm_BoundHosts"),
                binding,
                copyValue: generation == 2 ? bound?.SigningThumbprint : null,
                copyLabel: bound?.SigningThumbprint != null ? L10n.Format("Vtpm_CopyFingerprint", bound.SigningThumbprint) : null,
                onError: ShowError), true),
            (KvTile(
                this,
                L10n.Get("Vtpm_StateFile"),
                stateFileText,
                copyValue: file,
                copyLabel: file != null ? L10n.Format("Dev_CopyValueTooltip", file) : null,
                onError: ShowError,
                onReveal: file != null ? () => _ = RunAsync(() => RevealStateFileAsync(file)) : null,
                revealLabel: L10n.Get("Vtpm_OpenStateFolder")), true));

        panel.Children.Add(CardShell(this, header, body, dangerStrip));

        if (generation != 2) return;
        if (!Offline)
        {
            panel.Children.Add(Notice(L10n.Get("Vtpm_RunningLocked"), L10n.Get("Vtpm_OfflineToggle")));
        }
        else if (MissingKey)
        {
            var bar = Notice(L10n.Get("Vtpm_HostCannotUnlock"), L10n.Get("Vtpm_RebindHint"));
            bar.ActionButton = Command(L10n.Get("Vtpm_RebindHost"), ReissueAsync);
            panel.Children.Add(bar);
        }
        else if (info?.Shielded == true)
        {
            panel.Children.Add(Notice(L10n.Get("Vtpm_ShieldingLocksTpm"), L10n.Get("Vtpm_DisableShieldingHint")));
        }
    }

    private void BuildTabs()
    {
        panel.Children.Add(PivotHeader(
            this,
            ((string, string, bool, Action)[])[
                ("\uE72E", L10n.Get("Vtpm_Keys"), !policyTab, () => { if (policyTab) { policyTab = false; Render(); } }),
                ("\uE713", L10n.Get("Vtpm_Policy"), policyTab, () => { if (!policyTab) { policyTab = true; Render(); } })
            ],
            () => { settings = false; Render(); }));
    }

    private void BuildKeys()
    {
        string keyLockTip = generation != 2 ? L10n.Get("Vtpm_OnlyGen2") : L10n.Get("Common_ModifyWhenOff");

        panel.Children.Add(SettingCard(
            this,
            L10n.Get("Vtpm_Reissue"),
            L10n.Get("Vtpm_ReissueHelp"),
            Command(L10n.Get("Vtpm_Issue"), ReissueAsync, CanManageKeys),
            enabled: CanManageKeys,
            disabledTooltip: keyLockTip));

        bool canRestore = CanManageKeys && info?.HasPreviousKey == true;
        panel.Children.Add(SettingCard(
            this,
            L10n.Get("Vtpm_UndoChange"),
            L10n.Get("Vtpm_RestoreHelp"),
            Command(L10n.Get("Vtpm_Restore"), () => ApplyAsync(VmSecurityService.RestorePreviousKeyProtectorAsync(VmId)), canRestore),
            enabled: canRestore,
            disabledTooltip: CanManageKeys ? L10n.Get("Vtpm_NoPreviousKey") : keyLockTip));

        bool canGrant = CanManageKeys && info?.HasLocalKey == true;
        var grantActions = Actions(
            Command(L10n.Get("Vtpm_ExportCertificate"), ExportAsync, generation == 2),
            Command(L10n.Get("Vtpm_AddAuthorization"), ImportAsync, canGrant));

        var hostList = new StackPanel { Spacing = 6 };
        if (info is { HasLocalKey: false, KeyReadError: null, LocalSigningThumbprint: { } localThumb })
        {
            hostList.Children.Add(BuildAuthorizedHostStrip(
                Environment.MachineName,
                L10n.Get("Vtpm_CurrentHost"),
                L10n.Format("Vtpm_UnboundHost", ShortThumbprint(localThumb)),
                actionButton: null));
        }

        foreach (var host in info?.AuthorizedHosts ?? [])
        {
            Button? revoke = null;
            if (!host.IsLocal)
            {
                revoke = Command(
                    L10n.Get("Common_Remove"),
                    () => ApplyAsync(VmSecurityService.RevokeHostAccessAsync(VmId, host.SigningThumbprint, host.EncryptionThumbprint)),
                    canGrant && !host.IsOwner);
                revoke.Padding = new Thickness(10, 3, 10, 4);
                revoke.MinHeight = 26;
                revoke.FontSize = 12;
            }
            hostList.Children.Add(BuildAuthorizedHostStrip(
                host.IsLocal ? Environment.MachineName : host.Name,
                host.IsLocal ? L10n.Get("Vtpm_CurrentHost") : L10n.Get("Vtpm_Authorized"),
                ShortThumbprint(host.SigningThumbprint),
                revoke));
        }

        if (info?.KeyReadError is { } error)
        {
            hostList.Children.Add(new TextBlock
            {
                Text = error,
                FontSize = 12,
                Foreground = ThemeHelper.ThemeBrush(this, "SystemFillColorCautionBrush"),
                TextWrapping = TextWrapping.Wrap
            });
        }

        panel.Children.Add(SettingCard(
            this,
            L10n.Get("Vtpm_AuthorizeOtherHost"),
            L10n.Get("Vtpm_AuthorizationHelp"),
            grantActions,
            below: hostList.Children.Count > 0 ? hostList : null,
            enabled: generation == 2,
            disabledTooltip: L10n.Get("Vtpm_OnlyGen2")));

        if (generation == 2 && !Offline)
            panel.Children.Add(Notice(L10n.Get("Vtpm_RunningPolicyLocked"), L10n.Get("Vtpm_OfflinePolicy")));
    }

    private Border BuildAuthorizedHostStrip(string hostName, string badgeText, string rightDetail, Button? actionButton)
    {
        var left = Actions(
            spacing: 8,
            new TextBlock
            {
                Text = hostName,
                FontSize = 12.5,
                Foreground = ThemeHelper.ThemeBrush(this, "TextFillColorPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            },
            Badge(this, badgeText));

        var rightItems = new List<UIElement>
        {
            new TextBlock
            {
                Text = rightDetail,
                FontFamily = AppFonts.Monospace,
                FontSize = 11.5,
                Foreground = ThemeHelper.ThemeBrush(this, "TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        if (actionButton != null) rightItems.Add(actionButton);

        return new Border
        {
            Background = ThemeHelper.ThemeBrush(this, "SubtleFillColorTertiaryBrush"),
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
        if (string.IsNullOrWhiteSpace(dir)) throw new InvalidOperationException(L10n.Get("Vtpm_OpenFolderFailed"));
        var fallback = await StorageFolder.GetFolderFromPathAsync(dir);
        if (!await Launcher.LaunchFolderAsync(fallback))
            throw new InvalidOperationException(L10n.Get("Vtpm_OpenFolderFailed"));
    }

    private async Task ExportAsync()
    {
        string? path = await StoragePickers.SaveAsync($"{Environment.MachineName}-Guardian.xml", L10n.Get("Vtpm_HostCertificate"), ".xml", ".cer", ".crt");
        if (path != null && !IsStopped) await ApplyAsync(VmSecurityService.ExportLocalGuardianAsync(VmId, path));
    }

    private async Task ImportAsync()
    {
        string? path = await StoragePickers.OpenAsync(".xml", ".cer", ".crt", ".pfx");
        if (path == null || IsStopped) return;
        string? password = null;
        if (string.Equals(System.IO.Path.GetExtension(path), ".pfx", StringComparison.OrdinalIgnoreCase))
        {
            var input = new PasswordBox { Header = L10n.Get("Vtpm_CertificatePassword") };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = L10n.Get("Vtpm_ReadCertificate"),
                Content = input,
                PrimaryButtonText = L10n.Get("Common_Continue"),
                CloseButtonText = L10n.Get("Common_Cancel")
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
                Title = L10n.Get("Vtpm_VerifyCertificate"),
                Content = grant.Error,
                PrimaryButtonText = L10n.Get("Vtpm_SelectHostXml"),
                CloseButtonText = L10n.Get("Common_Cancel")
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
            L10n.Get("Vtpm_BindPhysicalTpm"),
            L10n.Get("Vtpm_BindPhysicalTpmHelp"),
            VtpmPolicy.BindToHostTpm,
            info?.BindToHostTpm);

        var second = PolicyCard(
            L10n.Get("Vtpm_DisableVbs"),
            L10n.Get("Vtpm_DisableVbsHelp"),
            VtpmPolicy.VirtualizationBasedSecurityOptOut,
            info?.VirtualizationBasedSecurityOptOut);

        var third = PolicyCard(
            L10n.Get("Vtpm_DisallowMigration"),
            L10n.Get("Vtpm_DisallowMigrationHelp"),
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
            panel.Children.Add(Notice(L10n.Get("Vtpm_PolicyLocked"), L10n.Get("Vtpm_PolicyRequiresGen2"), InfoBarSeverity.Informational));
        }
        else if (!Offline)
        {
            panel.Children.Add(Notice(L10n.Get("Vtpm_RunningPolicyLocked"), L10n.Get("Vtpm_OfflinePolicy")));
        }
        else if (info?.Shielded == true)
        {
            panel.Children.Add(Notice(L10n.Get("Vtpm_ShieldedPolicyLocked"), L10n.Get("Vtpm_PolicyUnlockHelp")));
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
            ? L10n.Get("Vtpm_OnlyGen2")
            : !Offline
                ? L10n.Get("Common_ModifyWhenOff")
                : info?.Shielded == true
                    ? L10n.Get("Vtpm_ShieldingLock")
                    : value == null
                        ? L10n.Get("Vtpm_UnknownPolicy")
                        : needsTpm
                            ? L10n.Get("Vtpm_EnableFirst")
                            : null;

        ToolTipService.SetToolTip(toggle, disabledTip ?? L10n.Format("Common_ToggleLabel", title));
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
