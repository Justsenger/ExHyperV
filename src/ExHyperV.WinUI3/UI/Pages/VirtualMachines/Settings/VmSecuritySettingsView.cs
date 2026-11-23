using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmSecuritySettingsView : VmSettingsView
{
    private const string OffValue = "off";
    private const string WindowsTemplateId = "1734c6e8-3154-4dda-ba5f-a874cc483422";
    private const string UefiCaTemplateId = "272e7447-90a4-4563-a4b9-8e4ab00526ce";
    private const string OpenSourceShieldedTemplateId = "4292ae2b-ee2c-42b5-a969-dd8f8689f6f3";

    private const string SecureBootDesc =
        "决定开机时允许启动哪些系统：Windows 选 Windows，Linux 或第三方签名系统选 UEFI / CA，PE、macOS 或未签名系统选关闭";
    private const string EncryptionDesc =
        "加密保存状态、检查点内存文件以及跨宿主实时迁移数据，开启时会自动启用 vTPM 并签发宿主密钥";
    private const string ShieldingDesc =
        "企业级隔离模式，会强制锁定安全启动、vTPM 与状态加密，并禁用控制台画面与增强会话，仅能通过 RDP 或 SSH 远程连接，个人单机通常无需开启";

    private readonly SettingsSegmentedPill secureBootPill = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Grid sbHeaderLabel;
    private readonly SettingInfoButton sbHeaderInfo;
    private readonly ToggleSwitch encryption = Switch();
    private readonly Grid encLabel;
    private readonly SettingInfoButton encInfo;
    private readonly ToggleSwitch shielding = Switch();
    private readonly Grid shieldLabel;
    private readonly SettingInfoButton shieldInfo;

    private VmSecurityService.VmSecurityInfo? applied;
    private string appliedSbSelection = OffValue;

    public VmSecuritySettingsView(Guid vmId) : base(vmId)
    {
        sbHeaderLabel = SettingLabel("安全启动", SecureBootDesc, out sbHeaderInfo);
        sbHeaderLabel.Margin = new Thickness(0, 0, 0, 10);

        secureBootPill.SetOptions(
        (SettingOption[])[
            new(OffValue, "关闭"),
            new(WindowsTemplateId, "Windows"),
            new(UefiCaTemplateId, "UEFI / CA"),
            new(OpenSourceShieldedTemplateId, "受防护的开源 VM")
        ]);
        secureBootPill.SetControlEnabled(false);

        var encCard = UniformCard("状态与迁移加密", EncryptionDesc, encryption, out encLabel, out encInfo);
        var shieldCard = UniformCard("受防护模式", ShieldingDesc, shielding, out shieldLabel, out shieldInfo);
        var bottomGrid = new SettingsFieldPanel { Spacing = 12, Margin = new Thickness(0, 14, 0, 0) };
        bottomGrid.Children.Add(encCard);
        bottomGrid.Children.Add(shieldCard);

        var root = new StackPanel { Margin = new Thickness(0, 4, 0, 24) };
        root.Children.Add(sbHeaderLabel);
        root.Children.Add(secureBootPill);
        root.Children.Add(bottomGrid);
        SetBody(root);

        secureBootPill.SelectionChanged += async value =>
        {
            if (Updating || IsBusy || IsStopped || value is not string next) return;
            await ApplyAsync(() => ApplySecureBootChoiceAsync(next));
        };
        encryption.Toggled += async (_, _) => await ApplyAsync(() => VmSecurityService.SetEncryptionAsync(VmId, encryption.IsOn));
        shielding.Toggled += async (_, _) => await ApplyAsync(() => VmSecurityService.SetShieldingAsync(VmId, shielding.IsOn));
    }

    private async Task<ApiResponse> ApplySecureBootChoiceAsync(string next)
    {
        if (next == OffValue)
            return await VmSecurityService.SetSecureBootAsync(VmId, false);

        if (!Guid.TryParse(next, out var templateId))
            return ApiResponse.Fail("安全启动模板标识无效。", kind: ApiErrorKind.InvalidArgument);

        var current = applied;
        if (current is not { SecureBootEnabled: true })
        {
            // 已保留目标模板时仅打开安全启动开关，避免已初始化 vTPM 的虚拟机在重写模板 ID 时失败。
            if (Guid.TryParse(current?.SecureBootTemplateId, out var currentTpl) && currentTpl == templateId)
                return await VmSecurityService.SetSecureBootAsync(VmId, true);

            var enableResult = await VmSecurityService.SetSecureBootAsync(VmId, true);
            if (!enableResult.Success) return enableResult;

            var templateResult = await VmSecurityService.SetSecureBootTemplateAsync(VmId, templateId);
            if (templateResult.Success)
            {
                NotifyChanged();
                return templateResult;
            }

            // 切换其他模板失败时回滚刚才开启的安全启动，防止页面提示失败却保留已开启的 Windows 模板。
            await VmSecurityService.SetSecureBootAsync(VmId, false);
            return WithTpmTemplateHint(templateResult);
        }

        if (Guid.TryParse(current?.SecureBootTemplateId, out var existingTpl) && existingTpl == templateId)
            return ApiResponse.Ok();

        return WithTpmTemplateHint(await VmSecurityService.SetSecureBootTemplateAsync(VmId, templateId));
    }

    private ApiResponse WithTpmTemplateHint(ApiResponse response)
    {
        if (response.Success || applied is not { TpmEnabled: true }) return response;
        string error = CoreMessageResolver.ResolveError(response);
        const string hint = "若该虚拟机已启用并初始化 vTPM，Hyper-V 会锁定安全启动模板，如需更换请先在「设备」的「vTPM」关闭 vTPM";
        if (error.Contains("vTPM", StringComparison.OrdinalIgnoreCase)) return response;
        return ApiResponse.Fail($"{error}\n{hint}", response.Code, response.ErrorSource, response.Exception, response.ErrorKind);
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
            if (result.Success || result.Steps.Any(s => s.State == OperationStepState.Succeeded)) NotifyChanged();
            await ReloadAfterWriteAsync(result);
        });
    }

    private void RestoreApplied()
    {
        if (applied is not { } info || IsStopped) return;
        Updating = true;
        try
        {
            secureBootPill.SelectedValue = appliedSbSelection;
            encryption.IsOn = info.EncryptEnabled;
            shielding.IsOn = info.Shielded;
        }
        finally { Updating = false; }
    }

    private static string ResolveTemplateLabel(Guid id, string? hostName)
    {
        string raw = id.ToString("D").ToLowerInvariant();
        return raw switch
        {
            WindowsTemplateId => "Windows",
            UefiCaTemplateId => "UEFI / CA",
            OpenSourceShieldedTemplateId => "受防护的开源 VM",
            _ => string.IsNullOrWhiteSpace(hostName) ? raw : hostName
        };
    }

    protected override async Task LoadAsync()
    {
        var contextTask = ReadContextAsync();
        var settingsTask = VmSecurityService.GetSecuritySettingsAsync(VmId);
        var templatesTask = VmSecurityService.GetSecureBootTemplatesAsync();
        await Task.WhenAll(contextTask, settingsTask, templatesTask);
        var context = await contextTask;
        var settings = await settingsTask;
        var availableTemplates = await templatesTask;
        if (!settings.HasData) throw new InvalidOperationException(settings.Error);
        if (IsStopped) return;

        var info = settings.Data;
        applied = info;
        Updating = true;
        try
        {
            var sbCondition = VmSecurityService.GetModificationCondition(
                context.Generation, context.State, info, VmSecurityService.Setting.SecureBoot);
            bool templatesOk = availableTemplates.HasData && availableTemplates.Data!.Count > 0;
            bool sbEditable = sbCondition.CanModify && templatesOk;

            var pillOptions = new List<SettingOption> { new(OffValue, "关闭") };
            if (templatesOk)
            {
                var ordered = availableTemplates.Data!
                    .OrderBy(t => t.Id.ToString("D").ToLowerInvariant() switch
                    {
                        WindowsTemplateId => 0,
                        UefiCaTemplateId => 1,
                        OpenSourceShieldedTemplateId => 2,
                        _ => 3
                    })
                    .ToList();
                foreach (var tpl in ordered)
                {
                    string key = tpl.Id.ToString("D").ToLowerInvariant();
                    pillOptions.Add(new(key, ResolveTemplateLabel(tpl.Id, tpl.Name), Enabled: templatesOk));
                }
            }
            else
            {
                pillOptions.Add(new(WindowsTemplateId, "Windows", Enabled: false));
                pillOptions.Add(new(UefiCaTemplateId, "UEFI / CA", Enabled: false));
                pillOptions.Add(new(OpenSourceShieldedTemplateId, "受防护的开源 VM", Enabled: false));
            }
            secureBootPill.SetOptions(pillOptions);

            if (context.Generation != 2 || !info.SecureBootEnabled)
            {
                appliedSbSelection = OffValue;
            }
            else if (Guid.TryParse(info.SecureBootTemplateId, out var activeGuid))
            {
                string norm = activeGuid.ToString("D").ToLowerInvariant();
                if (!pillOptions.Any(o => Equals(o.Value, norm)))
                    pillOptions.Add(new(norm, ResolveTemplateLabel(activeGuid, null)));
                secureBootPill.SetOptions(pillOptions);
                appliedSbSelection = norm;
            }
            else
            {
                appliedSbSelection = WindowsTemplateId;
            }

            secureBootPill.SelectedValue = appliedSbSelection;
            secureBootPill.SetControlEnabled(sbEditable);

            string? sbDisabledReason = context.Generation != 2
                ? "仅第 2 代虚拟机支持安全启动"
                : !sbCondition.CanModify
                    ? (info.Shielded ? "已由受防护模式锁定，需先关闭受防护模式才能更改" : sbCondition.Reason)
                    : !availableTemplates.HasData
                        ? availableTemplates.Error
                        : !templatesOk
                            ? "宿主机未提供可用的安全启动模板。"
                            : null;
            sbHeaderLabel.Opacity = sbEditable ? 1.0 : 0.55;
            sbHeaderInfo.SetDescription(SecureBootDesc, sbDisabledReason);

            encryption.IsOn = context.Generation == 2 && info.EncryptEnabled;
            shielding.IsOn = context.Generation == 2 && info.Shielded;

            var encCond = VmSecurityService.GetModificationCondition(
                context.Generation, context.State, info, VmSecurityService.Setting.Encryption);
            string? encDisabledReason = context.Generation != 2
                ? "仅第 2 代虚拟机支持状态与迁移加密"
                : info.Shielded && !encCond.CanModify && context.State == VmState.PoweredOff
                    ? "已由受防护模式锁定开启"
                    : encCond.CanModify ? null : encCond.Reason;
            ApplyCardState(encLabel, encInfo, encryption, EncryptionDesc,
                available: true, editable: encCond.CanModify, null, encDisabledReason);

            var shieldCond = VmSecurityService.GetModificationCondition(
                context.Generation, context.State, info, VmSecurityService.Setting.Shielding);
            string? shieldDisabledReason = context.Generation != 2
                ? "仅第 2 代虚拟机支持受防护模式"
                : shieldCond.CanModify ? null : shieldCond.Reason;
            ApplyCardState(shieldLabel, shieldInfo, shielding, ShieldingDesc,
                available: true, editable: shieldCond.CanModify, null, shieldDisabledReason);

            if (!availableTemplates.HasData) ShowError(availableTemplates.Error);
            else if (shieldCond is { CanModify: false } restriction) ShowInformation(restriction.Reason);
        }
        finally { Updating = false; }
    }
}
