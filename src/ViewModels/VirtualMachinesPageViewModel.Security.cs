using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;

namespace ExHyperV.ViewModels
{
    // 安全启动模板（显示名 + WMI 的 SecureBootTemplateId GUID）
    public record SecureBootTemplate(string Name, string Guid);

    // ===== 安全子页面（镜像微软 Hyper-V 安全页：安全启动 + 加密支持 + 安全策略）=====
    // 仅第 2 代可用、改动需 VM 关机（可用性由 SelectedVm.CanEditSecurity 控制）。读写走 VmSecurityService。
    public partial class VirtualMachinesPageViewModel
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEditSecureBootTemplate))]
        private bool _secureBootEnabled;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEditEncryption))]
        private bool _tpmEnabled;
        [ObservableProperty] private bool _encryptionEnabled;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEditSecureBoot))]
        [NotifyPropertyChangedFor(nameof(CanEditTpm))]
        [NotifyPropertyChangedFor(nameof(CanEditEncryption))]
        [NotifyPropertyChangedFor(nameof(CanEditSecureBootTemplate))]
        private bool _shieldingEnabled;
        [ObservableProperty] private SecureBootTemplate? _selectedSecureBootTemplate;
        private SecureBootTemplate? _appliedTemplate;

        // 模板能否改"无法预判"——锁只在 vTPM 被开机初始化后才生效(实测: 启了 vTPM 但没开机的 VM 仍能改;
        // KP 存在/TpmEnabled 都不代表锁)。故不预判置灰，只按"安全启动开"启用；真锁住的由 ApplyTemplateAsync 试改失败→弹引擎错+回退兜底。
        // 防护开时一并锁：受防护态安全启动被强制为开，否则模板下拉仍可点、点了必失败(vTPM 已初始化)。
        public bool CanEditSecureBootTemplate => SecureBootEnabled && !ShieldingEnabled;
        // 防护开时锁住 安全启动/TPM/加密 三个开关(微软 UI 行为：受防护态下不许单独改它们)。
        public bool CanEditSecureBoot => !ShieldingEnabled;
        public bool CanEditTpm => !ShieldingEnabled;
        public bool CanEditEncryption => TpmEnabled && !ShieldingEnabled;
        // 防护随时可改(仿微软级联)：开启会自动建/复用 vTPM + 开加密(见 SetShieldingAsync)，不再要求"先开安全启动+TPM"前置。
        public bool CanEditShielding => true;

        // 安全启动模板：进安全页时向主机实拿(VmSecurityService.GetSecureBootTemplatesAsync)，
        // 不再硬编码 GUID——曾把"开源防护"GUID 写错(4292ca59…实为 4292ae2b…)致引擎报参数无效。
        public ObservableCollection<SecureBootTemplate> SecureBootTemplates { get; } = new();

        [RelayCommand]
        private async Task GoToSecuritySettingsAsync()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.Security;
            await LoadSecurityStateAsync();
        }

        private async Task LoadSecurityStateAsync()
        {
            if (SelectedVm == null) return;
            IsLoadingSettings = true;
            try
            {
                // 用 using 包住程序性赋值：即使中途抛异常也保证还原抑制态（旧码把复位写在 try 末尾，异常会永久卡住抑制）。
                using (SuppressApply())
                {
                    // 模板列表向主机实拿(本地化名 + 正确 GUID)，每次进页刷新
                    var templates = await VmSecurityService.GetSecureBootTemplatesAsync();
                    SecureBootTemplates.Clear();
                    foreach (var (name, guid) in templates)
                        SecureBootTemplates.Add(new SecureBootTemplate(name, guid));

                    var info = await VmSecurityService.GetSecuritySettingsAsync(SelectedVm.Name);
                    SecureBootEnabled = info.SecureBootEnabled;
                    TpmEnabled = info.TpmEnabled;
                    EncryptionEnabled = info.EncryptEnabled;
                    ShieldingEnabled = info.Shielded;
                    SelectedSecureBootTemplate =
                        SecureBootTemplates.FirstOrDefault(t => info.SecureBootTemplateId.Contains(t.Guid, StringComparison.OrdinalIgnoreCase))
                        ?? SecureBootTemplates.FirstOrDefault();
                    _appliedTemplate = SelectedSecureBootTemplate;
                }
            }
            finally { IsLoadingSettings = false; }
        }

        // 改动即应用；成功不弹提示(控件本身即反馈)，失败弹引擎原因并回弹。
        partial void OnSecureBootEnabledChanged(bool value)
        {
            if (IsApplySuppressed || SelectedVm == null) return;
            _ = ApplySecurityAsync(() => VmSecurityService.SetSecureBootAsync(SelectedVm.Name, value), () => SecureBootEnabled = !value);
        }

        partial void OnTpmEnabledChanged(bool value)
        {
            if (IsApplySuppressed || SelectedVm == null) return;
            _ = ApplyTpmAsync(value);
        }

        // TPM 改完成功后整页刷新，让互锁(加密可用性等)与最新状态一致。
        private async Task ApplyTpmAsync(bool value)
        {
            var (ok, msg) = await VmSecurityService.SetTpmAsync(SelectedVm.Name, value);
            if (ok) { await LoadSecurityStateAsync(); return; }
            ShowError($"{Properties.Resources.Xaml_SecurityFeat}：{msg}");
            using (SuppressApply()) TpmEnabled = !value;
        }

        partial void OnEncryptionEnabledChanged(bool value)
        {
            if (IsApplySuppressed || SelectedVm == null) return;
            _ = ApplySecurityAsync(() => VmSecurityService.SetEncryptionAsync(SelectedVm.Name, value), () => EncryptionEnabled = !value);
        }

        partial void OnShieldingEnabledChanged(bool value)
        {
            if (IsApplySuppressed || SelectedVm == null) return;
            _ = ApplyShieldingAsync(value);
        }

        // 防护改变会连带 TPM/加密(启用时强制开)，成功后整页刷新让互锁与显示一致。
        private async Task ApplyShieldingAsync(bool value)
        {
            var (ok, msg) = await VmSecurityService.SetShieldingAsync(SelectedVm.Name, value);
            if (ok) { await LoadSecurityStateAsync(); return; }
            ShowError($"{Properties.Resources.Xaml_SecurityFeat}：{msg}");
            using (SuppressApply()) ShieldingEnabled = !value;
        }

        partial void OnSelectedSecureBootTemplateChanged(SecureBootTemplate? value)
        {
            if (IsApplySuppressed || SelectedVm == null || value == null) return;
            _ = ApplyTemplateAsync(value);
        }

        // 模板锁无法预判：直接试改。成功即可；失败(开过机的 vTPM VM 被引擎锁)弹引擎真实错误 + 回退到上次生效值。
        private async Task ApplyTemplateAsync(SecureBootTemplate value)
        {
            var (ok, msg) = await VmSecurityService.SetSecureBootTemplateAsync(SelectedVm.Name, value.Guid);
            if (ok) { _appliedTemplate = value; return; }

            ShowError($"{Properties.Resources.Xaml_SecurityFeat}：{msg}");
            using (SuppressApply()) SelectedSecureBootTemplate = _appliedTemplate;
        }

        private async Task ApplySecurityAsync(Func<Task<(bool Success, string Message)>> action, Action? revert)
        {
            var (ok, msg) = await action();
            if (ok) return;

            ShowError($"{Properties.Resources.Xaml_SecurityFeat}：{msg}");
            if (revert == null) return;
            using (SuppressApply()) revert();
        }
    }
}
