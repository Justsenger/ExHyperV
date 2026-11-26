
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmSecuritySettingsView : VmSettingsView
{
    private const string OffValue = "off";
    private const string WindowsTemplateId = "1734c6e8-3154-4dda-ba5f-a874cc483422";
    private const string UefiCaTemplateId = "272e7447-90a4-4563-a4b9-8e4ab00526ce";
    private const string OpenSourceShieldedTemplateId = "4292ae2b-ee2c-42b5-a969-dd8f8689f6f3";

    private static string SecureBootDesc => L10n.Get("Security_SecureBootHelp");
    private static string EncryptionDesc => L10n.Get("Security_EncryptionHelp");
    private static string ShieldingDesc => L10n.Get("Security_ShieldingHelp");

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
        sbHeaderLabel = SettingLabel(L10n.Get("Security_SecureBoot"), SecureBootDesc, out sbHeaderInfo);
        sbHeaderLabel.Margin = new Thickness(0, 0, 0, 10);

        secureBootPill.SetOptions(
        (SettingOption[])[
            new(OffValue, L10n.Get("Common_Off")),
            new(WindowsTemplateId, "Windows"),
            new(UefiCaTemplateId, "UEFI / CA"),
            new(OpenSourceShieldedTemplateId, L10n.Get("Security_OpenSourceShielded"))
        ]);
        secureBootPill.SetControlEnabled(false);

        var encCard = UniformCard(L10n.Get("Security_StateEncryption"), EncryptionDesc, encryption, out encLabel, out encInfo);
        var shieldCard = UniformCard(L10n.Get("Security_Shielding"), ShieldingDesc, shielding, out shieldLabel, out shieldInfo);
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
            return ApiResponse.Fail(L10n.Get("Security_InvalidTemplate"), kind: ApiErrorKind.InvalidArgument);

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
        string hint = L10n.Get("Security_TpmLocksTemplate");
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
            OpenSourceShieldedTemplateId => L10n.Get("Security_OpenSourceShielded"),
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

            var pillOptions = new List<SettingOption> { new(OffValue, L10n.Get("Common_Off")) };
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
                pillOptions.Add(new(OpenSourceShieldedTemplateId, L10n.Get("Security_OpenSourceShielded"), Enabled: false));
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
                ? L10n.Get("Security_SecureBootGeneration")
                : !sbCondition.CanModify
                    ? (info.Shielded ? L10n.Get("Security_ShieldedLocked") : sbCondition.Reason)
                    : !availableTemplates.HasData
                        ? availableTemplates.Error
                        : !templatesOk
                            ? L10n.Get("Security_NoTemplates")
                            : null;
            sbHeaderLabel.Opacity = sbEditable ? 1.0 : 0.55;
            sbHeaderInfo.SetDescription(SecureBootDesc, sbDisabledReason);

            encryption.IsOn = context.Generation == 2 && info.EncryptEnabled;
            shielding.IsOn = context.Generation == 2 && info.Shielded;

            var encCond = VmSecurityService.GetModificationCondition(
                context.Generation, context.State, info, VmSecurityService.Setting.Encryption);
            string? encDisabledReason = context.Generation != 2
                ? L10n.Get("Security_EncryptionGeneration")
                : info.Shielded && !encCond.CanModify && context.State == VmState.PoweredOff
                    ? L10n.Get("Security_ShieldedOn")
                    : encCond.CanModify ? null : encCond.Reason;
            ApplyCardState(encLabel, encInfo, encryption, EncryptionDesc,
                available: true, editable: encCond.CanModify, null, encDisabledReason);

            var shieldCond = VmSecurityService.GetModificationCondition(
                context.Generation, context.State, info, VmSecurityService.Setting.Shielding);
            string? shieldDisabledReason = context.Generation != 2
                ? L10n.Get("Security_ShieldingGeneration")
                : shieldCond.CanModify ? null : shieldCond.Reason;
            ApplyCardState(shieldLabel, shieldInfo, shielding, ShieldingDesc,
                available: true, editable: shieldCond.CanModify, null, shieldDisabledReason);

            if (!availableTemplates.HasData) ShowError(availableTemplates.Error);
        }
        finally { Updating = false; }
    }
}
