using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    // ===== 高级模块 =====
    public partial class VirtualMachinesPageViewModel
    {
        // 基本会话默认分辨率：下拉为预设，可编辑框可手动输入自定义 "宽 x 高"
        public ObservableCollection<string> VideoResolutionOptions { get; } = new()
        {
            Properties.Resources.VmAdvanced_ResolutionAuto,
            "3840 x 2160", "2560 x 1440", "1920 x 1200", "1920 x 1080",
            "1600 x 900", "1366 x 768", "1280 x 1024", "1280 x 720", "1024 x 768", "800 x 600"
        };

        [ObservableProperty] private string _selectedVideoResolution = string.Empty;

        // 控制台支持开关（增删合成显示控制器）
        [ObservableProperty] private bool _isConsoleSupportEnabled = true;

        // 启动时 NumLock（BIOSNumLock 固件设置；仅关机可改，UI 按 IsRunning 置灰、失败回弹）
        [ObservableProperty] private bool _isBootNumLockEnabled;

        [ObservableProperty] private bool _allowFullScsiCommandSetAvailable;
        [ObservableProperty] private bool _allowFullScsiCommandSet;
        [ObservableProperty] private bool _lockOnDisconnectAvailable;
        [ObservableProperty] private bool _lockOnDisconnect;
        [ObservableProperty] private bool _turnOffOnGuestRestartAvailable;
        [ObservableProperty] private bool _turnOffOnGuestRestart;
        [ObservableProperty] private bool _enableHibernationAvailable;
        [ObservableProperty] private bool _enableHibernation;
        [ObservableProperty] private bool _syntheticBatteryAvailable;
        [ObservableProperty] private bool _syntheticBatteryEnabled;

        private bool _appliedAllowFullScsiCommandSet;
        private bool _appliedLockOnDisconnect;
        private bool _appliedTurnOffOnGuestRestart;
        private bool _appliedEnableHibernation;
        private bool _appliedSyntheticBatteryEnabled;

        public string VmAdvancedFullScsiTitle => AdvancedText("VmAdvanced_FullScsiTitle");
        public string VmAdvancedFullScsiDesc => AdvancedText("VmAdvanced_FullScsiDesc");
        public string VmAdvancedLockTitle => AdvancedText("VmAdvanced_LockOnDisconnectTitle");
        public string VmAdvancedLockDesc => AdvancedText("VmAdvanced_LockOnDisconnectDesc");
        public string VmAdvancedTurnOffTitle => AdvancedText("VmAdvanced_TurnOffOnGuestRestartTitle");
        public string VmAdvancedTurnOffDesc => AdvancedText("VmAdvanced_TurnOffOnGuestRestartDesc");
        public string VmAdvancedHibernationTitle => AdvancedText("VmAdvanced_HibernationTitle");
        public string VmAdvancedHibernationDesc => AdvancedText("VmAdvanced_HibernationDesc");
        public string VmAdvancedBatteryTitle => AdvancedText("VmAdvanced_BatteryTitle");
        public string VmAdvancedBatteryDesc => AdvancedText("VmAdvanced_BatteryDesc");

        [RelayCommand]
        private async Task GoToAdvancedSettingsAsync()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.Advanced;
            IsLoadingSettings = true;
            try
            {
                var (ok, type, w, h) = await VmVideoService.GetResolutionAsync(SelectedVm.Name);
                SelectedVideoResolution = (ok && type == 3 && w > 0 && h > 0)
                    ? $"{w} x {h}"
                    : Properties.Resources.VmAdvanced_ResolutionAuto;

                var behaviorResult = await VmAdvancedBehaviorService.GetSettingsAsync(SelectedVm.Name);
                var batteryResult = await VmBatteryService.GetStateAsync(SelectedVm.Name);

                using (SuppressApply())
                {
                    IsConsoleSupportEnabled = await VmConsoleService.IsConsoleSupportEnabledAsync(SelectedVm.Name);
                    IsBootNumLockEnabled = await VmBootService.GetBootNumLockAsync(SelectedVm.Name);

                    // 先清除上一台虚拟机的状态；查询失败或属性缺失时，设置仍显示但保持置灰。
                    AllowFullScsiCommandSetAvailable = false;
                    AllowFullScsiCommandSet = false;
                    LockOnDisconnectAvailable = false;
                    LockOnDisconnect = false;
                    TurnOffOnGuestRestartAvailable = false;
                    TurnOffOnGuestRestart = false;
                    EnableHibernationAvailable = false;
                    EnableHibernation = false;
                    _appliedAllowFullScsiCommandSet = false;
                    _appliedLockOnDisconnect = false;
                    _appliedTurnOffOnGuestRestart = false;
                    _appliedEnableHibernation = false;
                    SyntheticBatteryAvailable = batteryResult.Available;
                    SyntheticBatteryEnabled = batteryResult.Enabled;
                    _appliedSyntheticBatteryEnabled = batteryResult.Enabled;

                    if (behaviorResult.HasData)
                    {
                        var settings = behaviorResult.Data!;
                        AllowFullScsiCommandSetAvailable = settings.AllowFullScsiCommandSetAvailable;
                        AllowFullScsiCommandSet = settings.AllowFullScsiCommandSet;
                        LockOnDisconnectAvailable = settings.LockOnDisconnectAvailable;
                        LockOnDisconnect = settings.LockOnDisconnect;
                        TurnOffOnGuestRestartAvailable = settings.TurnOffOnGuestRestartAvailable;
                        TurnOffOnGuestRestart = settings.TurnOffOnGuestRestart;
                        EnableHibernationAvailable = settings.EnableHibernationAvailable;
                        EnableHibernation = settings.EnableHibernation;

                        _appliedAllowFullScsiCommandSet = settings.AllowFullScsiCommandSet;
                        _appliedLockOnDisconnect = settings.LockOnDisconnect;
                        _appliedTurnOffOnGuestRestart = settings.TurnOffOnGuestRestart;
                        _appliedEnableHibernation = settings.EnableHibernation;
                    }
                }

                if (!behaviorResult.Success)
                    ShowError($"{Properties.Resources.Error_Common_LoadFail}：{FriendlyError.CleanLines(behaviorResult.Error)}");
                if (!batteryResult.Success)
                    ShowError($"{Properties.Resources.Error_Common_LoadFail}：{FriendlyError.CleanLines(batteryResult.Message)}");
            }
            finally { IsLoadingSettings = false; }
        }

        partial void OnAllowFullScsiCommandSetChanged(bool value)
        {
            if (!CanApplyAdvancedBehavior(AllowFullScsiCommandSetAvailable)) return;
            _ = ApplyAdvancedBehaviorAsync(VmAdvancedBehavior.AllowFullScsiCommandSet, value);
        }

        partial void OnLockOnDisconnectChanged(bool value)
        {
            if (!CanApplyAdvancedBehavior(LockOnDisconnectAvailable)) return;
            _ = ApplyAdvancedBehaviorAsync(VmAdvancedBehavior.LockOnDisconnect, value);
        }

        partial void OnTurnOffOnGuestRestartChanged(bool value)
        {
            if (!CanApplyAdvancedBehavior(TurnOffOnGuestRestartAvailable)) return;
            _ = ApplyAdvancedBehaviorAsync(VmAdvancedBehavior.TurnOffOnGuestRestart, value);
        }

        partial void OnEnableHibernationChanged(bool value)
        {
            if (!CanApplyAdvancedBehavior(EnableHibernationAvailable) || SelectedVm?.IsRunning == true) return;
            _ = ApplyAdvancedBehaviorAsync(VmAdvancedBehavior.EnableHibernation, value);
        }

        partial void OnSyntheticBatteryEnabledChanged(bool value)
        {
            if (IsApplySuppressed || CurrentViewType != VmDetailViewType.Advanced
                || SelectedVm == null || !SyntheticBatteryAvailable)
                return;
            _ = ApplySyntheticBatteryAsync(value);
        }

        private async Task ApplySyntheticBatteryAsync(bool enabled)
        {
            if (SelectedVm == null) return;
            var vm = SelectedVm;
            bool previous = _appliedSyntheticBatteryEnabled;
            var (success, message) = await VmBatteryService.SetEnabledAsync(vm.Name, enabled);
            if (!success)
            {
                if (SelectedVm == vm)
                {
                    using (SuppressApply())
                        SyntheticBatteryEnabled = previous;
                }
                ShowError($"{VmAdvancedBatteryTitle}：{FriendlyError.CleanLines(message)}");
                return;
            }

            _appliedSyntheticBatteryEnabled = enabled;
            ShowSuccess($"{VmAdvancedBatteryTitle}：" +
                        (enabled ? Properties.Resources.Button_Enable : Properties.Resources.Common_Disabled));
        }

        private bool CanApplyAdvancedBehavior(bool available)
            => !IsApplySuppressed
               && CurrentViewType == VmDetailViewType.Advanced
               && SelectedVm != null
               && available;

        private async Task ApplyAdvancedBehaviorAsync(VmAdvancedBehavior behavior, bool value)
        {
            if (SelectedVm == null) return;
            var vm = SelectedVm;
            bool previous = GetAppliedAdvancedBehavior(behavior);

            var result = await VmAdvancedBehaviorService.SetSettingAsync(vm.Name, behavior, value);
            if (!result.Success)
            {
                RestoreAdvancedBehavior(vm, behavior, previous);
                ShowError(FriendlyError.CleanLines(result.Error));
                return;
            }

            SetAppliedAdvancedBehavior(behavior, value);
            ShowSuccess($"{GetAdvancedBehaviorTitle(behavior)}：" +
                        (value ? Properties.Resources.Button_Enable : Properties.Resources.Common_Disabled));
        }

        private bool GetAppliedAdvancedBehavior(VmAdvancedBehavior behavior) => behavior switch
        {
            VmAdvancedBehavior.AllowFullScsiCommandSet => _appliedAllowFullScsiCommandSet,
            VmAdvancedBehavior.LockOnDisconnect => _appliedLockOnDisconnect,
            VmAdvancedBehavior.TurnOffOnGuestRestart => _appliedTurnOffOnGuestRestart,
            VmAdvancedBehavior.EnableHibernation => _appliedEnableHibernation,
            _ => false,
        };

        private void SetAppliedAdvancedBehavior(VmAdvancedBehavior behavior, bool value)
        {
            switch (behavior)
            {
                case VmAdvancedBehavior.AllowFullScsiCommandSet:
                    _appliedAllowFullScsiCommandSet = value;
                    break;
                case VmAdvancedBehavior.LockOnDisconnect:
                    _appliedLockOnDisconnect = value;
                    break;
                case VmAdvancedBehavior.TurnOffOnGuestRestart:
                    _appliedTurnOffOnGuestRestart = value;
                    break;
                case VmAdvancedBehavior.EnableHibernation:
                    _appliedEnableHibernation = value;
                    break;
            }
        }

        private void RestoreAdvancedBehavior(
            VmInstanceViewModel vm, VmAdvancedBehavior behavior, bool value)
        {
            if (SelectedVm != vm) return;
            using (SuppressApply())
            {
                switch (behavior)
                {
                    case VmAdvancedBehavior.AllowFullScsiCommandSet:
                        AllowFullScsiCommandSet = value;
                        break;
                    case VmAdvancedBehavior.LockOnDisconnect:
                        LockOnDisconnect = value;
                        break;
                    case VmAdvancedBehavior.TurnOffOnGuestRestart:
                        TurnOffOnGuestRestart = value;
                        break;
                    case VmAdvancedBehavior.EnableHibernation:
                        EnableHibernation = value;
                        break;
                }
            }
        }

        private string GetAdvancedBehaviorTitle(VmAdvancedBehavior behavior) => behavior switch
        {
            VmAdvancedBehavior.AllowFullScsiCommandSet => VmAdvancedFullScsiTitle,
            VmAdvancedBehavior.LockOnDisconnect => VmAdvancedLockTitle,
            VmAdvancedBehavior.TurnOffOnGuestRestart => VmAdvancedTurnOffTitle,
            VmAdvancedBehavior.EnableHibernation => VmAdvancedHibernationTitle,
            _ => string.Empty,
        };

        private static string AdvancedText(string key)
            => Properties.Resources.ResourceManager.GetString(key) ?? key;

        partial void OnIsConsoleSupportEnabledChanged(bool value)
        {
            if (IsApplySuppressed || SelectedVm == null) return;
            _ = ApplyConsoleSupportAsync(value);
        }

        private async Task ApplyConsoleSupportAsync(bool enable)
        {
            var (ok, msg) = await VmConsoleService.SetConsoleSupportAsync(SelectedVm.Name, enable);
            if (ok)
            {
                // 正文带上开/关结果，否则只显示功能名("控制台支持")看不出实际状态
                ShowSuccess($"{Properties.Resources.VmAdvanced_ConsoleTitle}：{(enable ? Properties.Resources.Button_Enable : Properties.Resources.Common_Disabled)}");
            }
            else
            {
                ShowError($"{Properties.Resources.VmAdvanced_ConsoleTitle}：{msg}");
                using (SuppressApply())
                    IsConsoleSupportEnabled = !enable;   // 失败回弹开关
            }
        }

        partial void OnIsBootNumLockEnabledChanged(bool value)
        {
            if (IsApplySuppressed || SelectedVm == null) return;
            _ = ApplyBootNumLockAsync(value);
        }

        private async Task ApplyBootNumLockAsync(bool enable)
        {
            var (ok, msg) = await VmBootService.SetBootNumLockAsync(SelectedVm.Name, enable);
            if (ok)
                ShowSuccess($"{Properties.Resources.VmAdvanced_NumLockTitle}：{(enable ? Properties.Resources.Button_Enable : Properties.Resources.Common_Disabled)}");
            else
            {
                ShowError($"{Properties.Resources.VmAdvanced_NumLockTitle}：{msg}");
                using (SuppressApply())
                    IsBootNumLockEnabled = !enable;   // 失败回弹
            }
        }

        // 应用：可填预设或自定义 "宽x高"（x/×/空格/* 等分隔符均接受）；空或"自适应"=Default(自适应)
        [RelayCommand]
        private async Task ApplyVideoResolutionAsync()
        {
            if (SelectedVm == null) return;
            string text = (SelectedVideoResolution ?? string.Empty).Trim();
            int type, w = 0, h = 0;

            if (text.Length == 0 || text == Properties.Resources.VmAdvanced_ResolutionAuto)
            {
                type = 4; // Default(自适应)
            }
            else
            {
                var parts = text.Split(new[] { 'x', 'X', '×', '*', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[0], out w) || !int.TryParse(parts[1], out h)
                    || w < 200 || w > 7680 || h < 200 || h > 4320)
                {
                    ShowTip(Properties.Resources.VmAdvanced_ResolutionInvalid);
                    return;
                }
                w &= ~1; h &= ~1;   // 宽高需为偶数（Set-VMVideo 要求），向下取偶
                type = 3; // Single(固定)
            }

            var (ok, msg) = await VmVideoService.SetResolutionAsync(SelectedVm.Name, type, w, h);
            if (ok)
            {
                if (type == 3) SelectedVideoResolution = $"{w} x {h}";   // 回显取偶后实际应用的值
                // 正文带上实际生效的值，否则只显示功能名("基本会话默认分辨率")看不出实际值
                ShowSuccess($"{Properties.Resources.VmAdvanced_ResolutionTitle}：{(type == 3 ? $"{w} x {h}" : Properties.Resources.VmAdvanced_ResolutionAuto)}");
            }
            else
                ShowError($"{Properties.Resources.VmAdvanced_ResolutionTitle}：{msg}");
        }
    }
}
