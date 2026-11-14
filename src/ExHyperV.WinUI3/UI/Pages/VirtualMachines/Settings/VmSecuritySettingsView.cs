using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed class VmSecuritySettingsView : VmSettingsView
{
    private readonly ToggleSwitch secureBoot = Switch();
    private readonly ToggleSwitch tpm = Switch();
    private readonly ToggleSwitch encryption = Switch();
    private readonly ToggleSwitch shielding = Switch();
    private readonly ComboBox templates = new() { MinWidth = 260, IsEnabled = false };
    private VmSecurityService.VmSecurityInfo? applied;

    public VmSecuritySettingsView(Guid vmId) : base(vmId)
    {
        var sections = new StackPanel { Margin = new Thickness(24, 4, 24, 24) };
        AddSection(sections, "安全启动", Card(
            Row("启用安全启动", "帮助防止启动时运行未经授权的代码", secureBoot),
            Row("安全模板", null, templates)));
        AddSection(sections, "加密支持", Card(
            Row("启用 TPM", "可为虚拟机提供加密服务的受信任的平台模块", tpm),
            Row("加密状态和虚拟机迁移流量", "加密虚拟机的保存状态与实时迁移流量", encryption)));
        AddSection(sections, "安全策略", Card(
            Row("启用防护", "将虚拟机标记为受防护，会强制启用 TPM 与加密", shielding)));
        SetBody(sections);
        secureBoot.Toggled += async (_, _) => await ApplyAsync(() => VmSecurityService.SetSecureBootAsync(VmId, secureBoot.IsOn));
        tpm.Toggled += async (_, _) => await ApplyAsync(() => VmSecurityService.SetTpmAsync(VmId, tpm.IsOn));
        encryption.Toggled += async (_, _) => await ApplyAsync(() => VmSecurityService.SetEncryptionAsync(VmId, encryption.IsOn));
        shielding.Toggled += async (_, _) => await ApplyAsync(() => VmSecurityService.SetShieldingAsync(VmId, shielding.IsOn));
        templates.SelectionChanged += async (_, _) =>
        {
            if (templates.SelectedItem is ComboBoxItem { Tag: VmSecurityService.SecureBootTemplate template })
                await ApplyAsync(() => VmSecurityService.SetSecureBootTemplateAsync(VmId, template.Id));
        };
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
        if (applied is not { } info || IsStopped) return;
        Updating = true;
        try
        {
            secureBoot.IsOn = info.SecureBootEnabled;
            tpm.IsOn = info.TpmEnabled;
            encryption.IsOn = info.EncryptEnabled;
            shielding.IsOn = info.Shielded;
            templates.SelectedItem = templates.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                item.Tag is VmSecurityService.SecureBootTemplate template &&
                info.SecureBootTemplateId.Contains(template.Id.ToString("D"), StringComparison.OrdinalIgnoreCase));
        }
        finally { Updating = false; }
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
            secureBoot.IsOn = info.SecureBootEnabled;
            tpm.IsOn = info.TpmEnabled;
            encryption.IsOn = info.EncryptEnabled;
            shielding.IsOn = info.Shielded;
            templates.Items.Clear();
            if (availableTemplates.HasData)
                foreach (var template in availableTemplates.Data!)
                {
                    var item = new ComboBoxItem { Content = template.Name, Tag = template };
                    templates.Items.Add(item);
                    if (info.SecureBootTemplateId.Contains(template.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
                        templates.SelectedItem = item;
                }
            VmModificationCondition Condition(VmSecurityService.Setting setting) =>
                VmSecurityService.GetModificationCondition(context.Generation, context.State, info, setting);
            void Apply(Control control, VmSecurityService.Setting setting)
            {
                var condition = Condition(setting);
                control.IsEnabled = condition.CanModify;
                ToolTipService.SetToolTip(control, condition.CanModify ? null : condition.Reason);
            }
            Apply(secureBoot, VmSecurityService.Setting.SecureBoot);
            Apply(tpm, VmSecurityService.Setting.Tpm);
            Apply(encryption, VmSecurityService.Setting.Encryption);
            Apply(shielding, VmSecurityService.Setting.Shielding);
            Apply(templates, VmSecurityService.Setting.SecureBootTemplate);
            templates.IsEnabled &= availableTemplates.HasData && availableTemplates.Data!.Count > 0;
            if (!availableTemplates.HasData) ShowError(availableTemplates.Error);
            else if (Condition(VmSecurityService.Setting.Shielding) is { CanModify: false } restriction) ShowInformation(restriction.Reason);
        }
        finally { Updating = false; }
    }
}
