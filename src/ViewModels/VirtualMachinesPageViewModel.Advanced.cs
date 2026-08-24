using System.Collections.ObjectModel;
using System.Collections.Concurrent;
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
        private readonly SemaphoreSlim _syntheticBatteryApplyGate = new(1, 1);
        private readonly ConcurrentDictionary<Guid, long> _syntheticBatteryApplyVersions = new();
        private readonly ConcurrentDictionary<Guid, bool> _syntheticBatteryAppliedStates = new();
        private long _advancedSettingsLoadGeneration;

        public string VmAdvancedFullScsiTitle => Properties.Resources.VmAdvanced_FullScsiTitle;
        public string VmAdvancedFullScsiDesc => Properties.Resources.VmAdvanced_FullScsiDesc;
        public string VmAdvancedLockTitle => Properties.Resources.VmAdvanced_LockOnDisconnectTitle;
        public string VmAdvancedLockDesc => Properties.Resources.VmAdvanced_LockOnDisconnectDesc;
        public string VmAdvancedTurnOffTitle => Properties.Resources.VmAdvanced_TurnOffOnGuestRestartTitle;
        public string VmAdvancedTurnOffDesc => Properties.Resources.VmAdvanced_TurnOffOnGuestRestartDesc;
        public string VmAdvancedHibernationTitle => Properties.Resources.VmAdvanced_HibernationTitle;
        public string VmAdvancedHibernationDesc => Properties.Resources.VmAdvanced_HibernationDesc;
        public string VmAdvancedBatteryTitle => Properties.Resources.VmAdvanced_BatteryTitle;
        public string VmAdvancedBatteryDesc => Properties.Resources.VmAdvanced_BatteryDesc;

        [RelayCommand]
        private async Task GoToAdvancedSettingsAsync()
        {
            if (SelectedVm == null) return;
            var vm = SelectedVm;
            string vmName = vm.Name;
            Guid vmId = vm.Id;
            long loadGeneration = Interlocked.Increment(ref _advancedSettingsLoadGeneration);
            CurrentViewType = VmDetailViewType.Advanced;
            IsLoadingSettings = true;
            using (SuppressApply())
            {
                SyntheticBatteryAvailable = false;
                SyntheticBatteryEnabled = false;
                _appliedSyntheticBatteryEnabled = false;
            }

            try
            {
                var (ok, type, w, h) = await VmVideoService.GetResolutionAsync(vmName);
                string videoResolution = (ok && type == 3 && w > 0 && h > 0)
                    ? $"{w} x {h}"
                    : Properties.Resources.VmAdvanced_ResolutionAuto;

                var behaviorResult = await VmAdvancedBehaviorService.GetSettingsAsync(vmName);
                var batteryResult = await GetSyntheticBatteryStateAsync(vmId);
                VmBatteryState batteryState = batteryResult.HasData ? batteryResult.Data : default;
                bool consoleSupportEnabled = await VmConsoleService.IsConsoleSupportEnabledAsync(vmName);
                bool bootNumLockEnabled = await VmBootService.GetBootNumLockAsync(vmName);

                if (!IsCurrentAdvancedSettingsLoad(vmId, loadGeneration))
                    return;

                using (SuppressApply())
                {
                    SelectedVideoResolution = videoResolution;
                    IsConsoleSupportEnabled = consoleSupportEnabled;
                    IsBootNumLockEnabled = bootNumLockEnabled;

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
                    SyntheticBatteryAvailable = batteryState.Available;
                    SyntheticBatteryEnabled = batteryState.Enabled;
                    _appliedSyntheticBatteryEnabled = batteryState.Enabled;

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
                    ShowError($"{Properties.Resources.Error_Common_LoadFail}：{FriendlyError.CleanLines(batteryResult.Error)}");
            }
            finally
            {
                if (loadGeneration == Volatile.Read(ref _advancedSettingsLoadGeneration))
                    IsLoadingSettings = false;
            }
        }

        partial void OnCurrentViewTypeChanged(VmDetailViewType oldValue, VmDetailViewType newValue)
        {
            if (oldValue != VmDetailViewType.Advanced || newValue == VmDetailViewType.Advanced)
                return;

            Interlocked.Increment(ref _advancedSettingsLoadGeneration);
            IsLoadingSettings = false;
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
            var vm = SelectedVm;
            long version = _syntheticBatteryApplyVersions.AddOrUpdate(
                vm.Id,
                1,
                static (_, current) => unchecked(current + 1));
            _ = ApplySyntheticBatteryAsync(vm, value, version);
        }

        private async Task ApplySyntheticBatteryAsync(
            VmInstanceViewModel vm,
            bool enabled,
            long version)
        {
            await _syntheticBatteryApplyGate.WaitAsync();
            try
            {
                if (!IsLatestSyntheticBatteryIntent(vm.Id, version))
                    return;

                bool previous = _syntheticBatteryAppliedStates.TryGetValue(vm.Id, out bool applied)
                    ? applied
                    : _appliedSyntheticBatteryEnabled;
                var result = await VmBatteryService.SetEnabledAsync(vm.Id, enabled);

                // 即使请求已被更新意图取代，也要记录这次已经落地的真实状态；
                // 等待中的最后一次请求会以此为失败回滚基线，并最终收敛到用户最新选择。
                if (result.Success)
                {
                    _syntheticBatteryAppliedStates[vm.Id] = enabled;
                    if (SelectedVm?.Id == vm.Id)
                        _appliedSyntheticBatteryEnabled = enabled;
                }

                if (!IsLatestSyntheticBatteryIntent(vm.Id, version))
                    return;

                // 请求属于已切走的虚拟机或已离开的页面时，只保留后端结果，
                // 不得用旧请求污染当前页状态，也不弹出误导性的成功/失败通知。
                if (SelectedVm?.Id != vm.Id || CurrentViewType != VmDetailViewType.Advanced)
                    return;

                if (!result.Success)
                {
                    using (SuppressApply())
                        SyntheticBatteryEnabled = previous;
                    ShowError($"{VmAdvancedBatteryTitle}：{FriendlyError.CleanLines(result.Error)}");
                    return;
                }

                // 页面重进或加载查询与写入交叠时，以已经落地的写入结果收敛 UI。
                if (SyntheticBatteryEnabled != enabled)
                {
                    using (SuppressApply())
                        SyntheticBatteryEnabled = enabled;
                }

                ShowSuccess($"{VmAdvancedBatteryTitle}：" +
                            (enabled ? Properties.Resources.Button_Enable : Properties.Resources.Common_Disabled));
            }
            finally
            {
                _syntheticBatteryApplyGate.Release();
            }
        }

        private async Task<ApiResponse<VmBatteryState>> GetSyntheticBatteryStateAsync(Guid vmId)
        {
            await _syntheticBatteryApplyGate.WaitAsync();
            try
            {
                var result = await VmBatteryService.GetStateAsync(vmId);
                if (result.HasData)
                    _syntheticBatteryAppliedStates[vmId] = result.Data.Enabled;
                return result;
            }
            finally
            {
                _syntheticBatteryApplyGate.Release();
            }
        }

        private bool IsLatestSyntheticBatteryIntent(Guid vmId, long version)
            => _syntheticBatteryApplyVersions.TryGetValue(vmId, out long latest)
               && latest == version;

        private bool IsCurrentAdvancedSettingsLoad(Guid vmId, long generation)
            => generation == Volatile.Read(ref _advancedSettingsLoadGeneration)
               && SelectedVm?.Id == vmId
               && CurrentViewType == VmDetailViewType.Advanced;

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
