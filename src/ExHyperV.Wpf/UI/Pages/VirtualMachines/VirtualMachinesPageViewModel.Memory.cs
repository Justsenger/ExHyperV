using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.UI.Pages.VirtualMachines
{
    public sealed class MemoryBackingTypeOption
    {
        public byte Value { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool IsEnabled { get; init; } = true;
    }

    public sealed class MemoryTrackingStateOption
    {
        public byte Value { get; init; }
        public string Name { get; init; } = string.Empty;
        public bool IsEnabled { get; init; } = true;
    }

    public partial class VirtualMachinesPageViewModel
    {

        // 进内存页时缓存的"原始设置"，失败时据此回弹；仅本模块使用（原误置于核心 .cs）。
        private VmMemorySettings _originalMemorySettingsCache = null!;
        private VmMmioSettings? _originalMmioSettingsCache;

        [RelayCommand]
        private async Task GoToMemorySettingsAsync()
        {
            if (SelectedVm == null) return;
            var vm = SelectedVm;
            CurrentViewType = VmDetailViewType.MemorySettings;
            IsLoadingSettings = true;

            using (SuppressApply()) // 加载过程中不触发任何 PropertyChanged 逻辑
            {
                try
                {
                    var memoryTask = VmMemoryService.GetVmMemorySettingsAsync(vm.Id);
                    var mmioTask = VmMmioService.GetSettingsAsync(vm.Id);
                    await Task.WhenAll(memoryTask, mmioTask);

                    var settings = (await memoryTask).RequireData();
                    var mmioSettings = (await mmioTask).RequireData();
                    if (SelectedVm != vm) return;
                    if (settings != null)
                    {
                        if (SelectedVm.MemorySettings != null)
                            SelectedVm.MemorySettings.PropertyChanged -= MemorySettings_PropertyChanged;

                        SelectedVm.MemorySettings = settings;
                        _originalMemorySettingsCache = settings.Clone(); // 加载成功时缓存原始状态
                        SelectedVm.MemorySettings.PropertyChanged += MemorySettings_PropertyChanged;
                    }

                    SelectedVm.MmioSettings = mmioSettings;
                    _originalMmioSettingsCache = mmioSettings?.Clone();
                }
                catch (Exception ex)
                {
                    ShowError(ex.Message);
                }
                finally
                {
                    await Task.Delay(100);
                    IsLoadingSettings = false;
                }
            }
        }

        [RelayCommand]
        private async Task ApplyMmioSettingsAsync(string? propertyName)
        {
            if (CurrentViewType != VmDetailViewType.MemorySettings) return;
            if (SelectedVm?.MmioSettings == null || !VmMmioService.CanEdit(SelectedVm.IsRunning) || string.IsNullOrEmpty(propertyName)) return;
            var vm = SelectedVm;
            var settings = vm.MmioSettings;
            var original = _originalMmioSettingsCache?.Clone();

            IsLoadingSettings = true;
            try
            {
                var result = await VmMmioService.SetSettingAsync(
                    vm.Id,
                    settings,
                    propertyName);

                if (!result.Success)
                {
                    ShowError($"{Properties.Resources.Error_Common_SaveFail}：{FriendlyError.CleanLines(result.Error)}");
                    if (original != null)
                    {
                        using (SuppressApply())
                            RestoreMmioProperty(settings, original, propertyName);
                    }
                }
                else if (SelectedVm == vm)
                {
                    _originalMmioSettingsCache ??= SelectedVm.MmioSettings.Clone();
                    RestoreMmioProperty(_originalMmioSettingsCache, SelectedVm.MmioSettings, propertyName);
                }
            }
            catch (Exception ex)
            {
                ShowError(FriendlyError.CleanLines(ex.Message));
                if (original != null)
                {
                    using (SuppressApply())
                        RestoreMmioProperty(settings, original, propertyName);
                }
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        private async void MemorySettings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (IsApplySuppressed || IsLoadingSettings || SelectedVm?.MemorySettings == null)
                return;
            var vm = SelectedVm;
            if (!ReferenceEquals(sender, vm.MemorySettings)) return;
            var settings = vm.MemorySettings;
            var original = _originalMemorySettingsCache?.Clone();

            var fastTrackProps = new[] {
                nameof(VmMemorySettings.BackingPageSize),
                nameof(VmMemorySettings.DynamicMemoryEnabled),
                nameof(VmMemorySettings.MemoryEncryptionPolicy),
                nameof(VmMemorySettings.BackingType),
                nameof(VmMemorySettings.MemoryAccessTrackingState),
                nameof(VmMemorySettings.MemoryAccessTrackingPolicy),
                nameof(VmMemorySettings.EnableColdHint),
                nameof(VmMemorySettings.EnableHotHint),
                nameof(VmMemorySettings.EnableEpf),
                nameof(VmMemorySettings.EnablePrivateCompressionStore),
                nameof(VmMemorySettings.SgxEnabled),
                nameof(VmMemorySettings.CxlEnabled),
                nameof(VmMemorySettings.EnableGpaPinning),
                nameof(VmMemorySettings.DynMemOperationAlignment)
                // MaxMemoryBlocksPerNumaNode 不在此：改数字仅写 model，由"应用"按钮统一下发。
                // 其 NumberBox 的 Value 绑定须带 UpdateSourceTrigger=PropertyChanged，否则吃默认 LostFocus、应用时读到旧值。
            };

            if (fastTrackProps.Contains(e.PropertyName))
            {
                if (!VmMemoryService.GetCapabilities(SelectedVm.IsRunning, SelectedVm.MemorySettings.DynamicMemoryEnabled).OfflineSettings) return;

                using (SuppressApply())
                {
                    VmMemoryService.NormalizeMemoryBackingSettings(SelectedVm.MemorySettings, e.PropertyName);
                    IsLoadingSettings = true;
                    try
                    {
                        var result = await VmMemoryService.SetVmMemorySettingsAsync(
                            vm.Id, settings, false, e.PropertyName);
                        if (!result.Success)
                        {
                            ShowError($"{Properties.Resources.VmPage_ModifyFail}：{result.Error}");

                            // 用纯净的初始缓存弹回恢复
                            if (original != null) settings.Restore(original);
                        }
                        else if (SelectedVm == vm)
                        {
                            // 如果修改成功，需要更新基准缓存为当前状态，否则下次别的选项失败时，会把这次成功的修改也弹回去
                            _originalMemorySettingsCache = SelectedVm.MemorySettings.Clone();
                        }
                    }
                    catch (Exception ex)
                    {
                        // async void 事件处理器：未捕获异常（如缓存为空时 Restore/Clone 抛 NRE）会崩 UI 线程；兜底上报
                        ShowError($"{Properties.Resources.VmPage_ModifyFail}：{ex.Message}");
                    }
                    finally
                    {
                        IsLoadingSettings = false;
                    }
                }
            }
        }
        [RelayCommand]
        private async Task ApplyMemorySettingsAsync()
        {
            // 部分控件在导航离开、卸载时可能触发命令；运行态改内存也会被拒。仅在仍处于内存页时执行。
            if (CurrentViewType != VmDetailViewType.MemorySettings) return;
            if (SelectedVm?.MemorySettings == null) return;
            var vm = SelectedVm;

            using (SuppressApply())
                VmMemoryService.NormalizeMemoryBackingSettings(SelectedVm.MemorySettings, null);

            IsLoadingSettings = true;
            try
            {
                var result = await VmMemoryService.SetVmMemorySettingsAsync(
                    vm.Id,
                    vm.MemorySettings,
                    vm.IsRunning // 传入当前运行状态
                );

                if (!result.Success)
                {
                    ShowError($"{Properties.Resources.Error_Common_SaveFail}：{FriendlyError.CleanLines(result.Error)}");
                }
                else if (SelectedVm == vm)
                {
                    // 保存成功后更新缓存基准
                    _originalMemorySettingsCache = SelectedVm.MemorySettings.Clone();
                }

                if (SelectedVm == vm) await GoToMemorySettingsAsync();
            }
            catch (Exception ex)
            {
                ShowError(FriendlyError.CleanLines(ex.Message));
            }
            finally { IsLoadingSettings = false; }
        }

        private static void RestoreMmioProperty(VmMmioSettings target, VmMmioSettings source, string propertyName)
        {
            switch (propertyName)
            {
                case nameof(VmMmioSettings.LowSizeMiB): target.LowSizeMiB = source.LowSizeMiB; break;
                case nameof(VmMmioSettings.HighSizeMiB): target.HighSizeMiB = source.HighSizeMiB; break;
                case nameof(VmMmioSettings.HighBaseMiB): target.HighBaseMiB = source.HighBaseMiB; break;
            }
        }

        public List<MemoryBackingTypeOption> BackingTypeOptions { get; } = new()
{
    new() { Value = 0, Name = Properties.Resources.VmPage_BackingTypePhysical },
    new() { Value = 1, Name = Properties.Resources.VmPage_BackingTypeVirtual },
    // Hybrid backing requires per-vNUMA-node MemoryBackingType configuration, which the UI
    // does not edit yet. Keep the option visible for discoverability, but do not allow selection.
    new() { Value = 2, Name = Properties.Resources.VmPage_BackingTypeHybrid, IsEnabled = false }
};

        public List<object> MemoryByteGranularityOptions { get; } = new()
{
    new { Value = (byte)0, Name = Properties.Resources.VmPage_MemGranularityAuto },
    new { Value = (byte)1, Name = Properties.Resources.VmPage_MemGranularityStandard },
    new { Value = (byte)2, Name = Properties.Resources.VmPage_MemGranularityLarge },
    new { Value = (byte)3, Name = Properties.Resources.VmPage_MemGranularityHuge }
};
        public List<object> DynamicMemoryAlignmentOptions { get; } = new()
{
    new { Value = (uint)0, Name = Properties.Resources.VmPage_DynMemAlignmentDisabled },
    new { Value = (uint)1, Name = Properties.Resources.VmPage_MemGranularityStandard },
    new { Value = (uint)2, Name = Properties.Resources.VmPage_MemGranularityLarge },
    new { Value = (uint)3, Name = Properties.Resources.VmPage_MemGranularityHuge }
};


        public List<MemoryTrackingStateOption> MemoryTrackingStateOptions { get; } = new()
{
    new() { Value = 0, Name = Properties.Resources.VmPage_MemTrackingDisable },
    new() { Value = 1, Name = Properties.Resources.VmPage_MemTrackingEnable },
    new() { Value = 2, Name = Properties.Resources.VmPage_MemTrackingPerNode, IsEnabled = false }
};

        public List<object> MemoryEncryptionPolicyOptions { get; } = new()
{
    new { Value = (byte)0, Name = Properties.Resources.VmPage_MemEncryptionDisabled },
    new { Value = (byte)1, Name = Properties.Resources.VmPage_MemEncryptionIfSupported },
    new { Value = (byte)2, Name = Properties.Resources.VmPage_MemEncryptionAlways }
};

        public List<object> SgxLaunchControlOptions { get; } = new()
{
    new { Value = (uint)0, Name = Properties.Resources.VmPage_SgxLaunchAccessDenied },
    new { Value = (uint)1, Name = Properties.Resources.VmPage_SgxLaunchReadOnly },
    new { Value = (uint)2, Name = Properties.Resources.VmPage_SgxLaunchReadWrite }
};


    }
}
