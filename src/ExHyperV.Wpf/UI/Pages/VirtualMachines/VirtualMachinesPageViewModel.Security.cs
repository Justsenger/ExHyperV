using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.UI.Pages.VirtualMachines
{
    // 安全启动模板（显示名 + WMI 的 SecureBootTemplateId GUID）
    public record SecureBootTemplate(string Name, Guid Id);

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

        // 模板锁在 vTPM 开机初始化后生效，单凭 KP/TpmEnabled 无法判断锁定状态。
        // 按安全启动开关启用编辑；ApplyTemplateAsync 写入被拒绝时显示引擎错误并还原选择。
        // 防护开时一并锁：受防护态安全启动被强制为开，否则模板下拉仍可点、点了必失败(vTPM 已初始化)。
        // VM 状态由父容器 SelectedVm.CanEditSecurity 绑定；子项只绑定安全设置互锁。
        private VmSecurityService.Capabilities SecurityCapabilities => VmSecurityService.GetInterlocks(
            SecureBootEnabled, TpmEnabled, ShieldingEnabled);
        public bool CanEditSecureBootTemplate => SecurityCapabilities.SecureBootTemplate;
        public bool CanEditSecureBoot => SecurityCapabilities.SecureBoot;
        public bool CanEditTpm => SecurityCapabilities.Tpm;
        public bool CanEditEncryption => SecurityCapabilities.Encryption;
        public bool CanEditShielding => SecurityCapabilities.Shielding;

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
            var vm = SelectedVm;
            if (vm == null) return;
            IsLoadingSettings = true;
            try
            {
                // 用 using 包住程序性赋值：即使中途抛异常也保证还原抑制态（旧码把复位写在 try 末尾，异常会永久卡住抑制）。
                using (SuppressApply())
                {
                    // 模板列表向主机实拿(本地化名 + 正确 GUID)，每次进页刷新
                    var templates = (await VmSecurityService.GetSecureBootTemplatesAsync()).RequireData();
                    var info = (await VmSecurityService.GetSecuritySettingsAsync(vm.Id)).RequireData();
                    if (SelectedVm != vm) return;
                    SecureBootTemplates.Clear();
                    foreach (var (name, guid) in templates)
                        SecureBootTemplates.Add(new SecureBootTemplate(name, guid));

                    SecureBootEnabled = info.SecureBootEnabled;
                    TpmEnabled = info.TpmEnabled;
                    EncryptionEnabled = info.EncryptEnabled;
                    ShieldingEnabled = info.Shielded;
                    SelectedSecureBootTemplate =
                        SecureBootTemplates.FirstOrDefault(t => info.SecureBootTemplateId.Contains(t.Id.ToString("D"), StringComparison.OrdinalIgnoreCase))
                        ?? SecureBootTemplates.FirstOrDefault();
                    _appliedTemplate = SelectedSecureBootTemplate;
                }
            }
            catch (Exception ex) { ShowError(FriendlyError.CleanLines(ex.Message)); }
            finally { IsLoadingSettings = false; }
        }

        // 控件先显示用户选择，Core 写入失败后恢复当前虚拟机的显示状态。
        partial void OnSecureBootEnabledChanged(bool value)
        {
            var vm = SelectedVm;
            if (IsApplySuppressed || vm == null) return;
            _ = ApplySecurityAsync(vm, () => VmSecurityService.SetSecureBootAsync(vm.Id, value), () => SecureBootEnabled = !value);
        }

        partial void OnTpmEnabledChanged(bool value)
        {
            var vm = SelectedVm;
            if (IsApplySuppressed || vm == null) return;
            _ = ApplySecurityAsync(vm, () => VmSecurityService.SetTpmAsync(vm.Id, value), () => TpmEnabled = !value, refresh: true);
        }

        partial void OnEncryptionEnabledChanged(bool value)
        {
            var vm = SelectedVm;
            if (IsApplySuppressed || vm == null) return;
            _ = ApplySecurityAsync(vm, () => VmSecurityService.SetEncryptionAsync(vm.Id, value), () => EncryptionEnabled = !value);
        }

        partial void OnShieldingEnabledChanged(bool value)
        {
            var vm = SelectedVm;
            if (IsApplySuppressed || vm == null) return;
            _ = ApplySecurityAsync(vm, () => VmSecurityService.SetShieldingAsync(vm.Id, value), () => ShieldingEnabled = !value, refresh: true);
        }

        partial void OnSelectedSecureBootTemplateChanged(SecureBootTemplate? value)
        {
            var vm = SelectedVm;
            if (IsApplySuppressed || vm == null || value == null) return;
            var previous = _appliedTemplate;
            _ = ApplySecurityAsync(vm, () => VmSecurityService.SetSecureBootTemplateAsync(vm.Id, value.Id),
                () => SelectedSecureBootTemplate = previous, applied: () => _appliedTemplate = value);
        }

        private async Task ApplySecurityAsync(VmInstanceViewModel vm, Func<Task<ApiResponse>> action, Action revert,
            bool refresh = false, Action? applied = null)
        {
            try
            {
                var result = await action();
                result.ThrowIfFailed();
                if (SelectedVm != vm) return;
                applied?.Invoke();
                if (refresh) await LoadSecurityStateAsync();
            }
            catch (Exception ex)
            {
                ShowError($"{Properties.Resources.Xaml_SecurityFeat}：{FriendlyError.CleanLines(ex.Message)}");
                if (SelectedVm == vm)
                {
                    using (SuppressApply()) revert();
                }
            }
        }
    }
}
