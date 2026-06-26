using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public partial class VirtualMachinesPageViewModel
    {
        // ===== 内存设置模块 =====

        // 进内存页时缓存的"原始设置"，失败时据此回弹；仅本模块使用（原误置于核心 .cs）。
        private VmMemorySettings _originalMemorySettingsCache = null!;

        // 导航至内存设置
        [RelayCommand]
        private async Task GoToMemorySettingsAsync()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.MemorySettings;
            IsLoadingSettings = true;

            using (SuppressApply()) // 加载过程中不触发任何 PropertyChanged 逻辑
            {
                try
                {
                    var settings = await VmMemoryService.GetVmMemorySettingsAsync(SelectedVm.Name);
                    if (settings != null)
                    {
                        if (SelectedVm.MemorySettings != null)
                            SelectedVm.MemorySettings.PropertyChanged -= MemorySettings_PropertyChanged;

                        SelectedVm.MemorySettings = settings;
                        _originalMemorySettingsCache = settings.Clone(); // 加载成功时缓存原始状态
                        SelectedVm.MemorySettings.PropertyChanged += MemorySettings_PropertyChanged;
                    }
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
        private async void MemorySettings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (IsApplySuppressed || IsLoadingSettings || SelectedVm?.MemorySettings == null)
                return;

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
                nameof(VmMemorySettings.DynMemOperationAlignment),
                nameof(VmMemorySettings.MaxMemoryBlocksPerNumaNode)
            };

            if (fastTrackProps.Contains(e.PropertyName))
            {
                if (SelectedVm.IsRunning) return;

                // 移除以前错误的 var backup = SelectedVm.MemorySettings.Clone();

                using (SuppressApply())
                {
                    IsLoadingSettings = true;
                    try
                    {
                        var result = await VmMemoryService.SetVmMemorySettingsAsync(SelectedVm.Name, SelectedVm.MemorySettings, false);
                        if (!result.Success)
                        {
                            ShowError($"{Properties.Resources.VmPage_ModifyFail}：{result.Message}");

                            // 核心修复：使用真正纯净的初始缓存进行弹回恢复
                            SelectedVm.MemorySettings.Restore(_originalMemorySettingsCache);
                        }
                        else
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
        // 手动应用内存设置
        [RelayCommand]
        private async Task ApplyMemorySettingsAsync()
        {
            if (SelectedVm?.MemorySettings == null) return;
            IsLoadingSettings = true;
            try
            {
                var result = await VmMemoryService.SetVmMemorySettingsAsync(
                    SelectedVm.Name,
                    SelectedVm.MemorySettings,
                    SelectedVm.IsRunning // 传入当前运行状态
                );

                if (!result.Success)
                {
                    ShowError($"{Properties.Resources.Error_Common_SaveFail}：{FriendlyError.CleanLines(result.Message)}");
                }
                else
                {
                    // 保存成功后更新缓存基准
                    _originalMemorySettingsCache = SelectedVm.MemorySettings.Clone();
                }

                await GoToMemorySettingsAsync();
            }
            catch (Exception ex)
            {
                ShowError(FriendlyError.CleanLines(ex.Message));
            }
            finally { IsLoadingSettings = false; }
        }
        // --- 实验性功能的纯中文数据源 (禁止任何英文) ---

        public List<object> BackingTypeOptions { get; } = new()
{
    new { Value = (byte)0, Name = Properties.Resources.VmPage_BackingTypePhysical },
    new { Value = (byte)1, Name = Properties.Resources.VmPage_BackingTypeVirtual },
    new { Value = (byte)2, Name = Properties.Resources.VmPage_BackingTypeHybrid }
};

        public List<object> MemoryByteGranularityOptions { get; } = new()
{
    new { Value = (byte)0, Name = Properties.Resources.VmPage_MemGranularityAuto },
    new { Value = (byte)1, Name = Properties.Resources.VmPage_MemGranularityStandard },
    new { Value = (byte)2, Name = Properties.Resources.VmPage_MemGranularityLarge },
    new { Value = (byte)3, Name = Properties.Resources.VmPage_MemGranularityHuge }
};
        public List<object> MemoryUintGranularityOptions { get; } = new()
{
    new { Value = (uint)0, Name = Properties.Resources.VmPage_MemGranularityAuto },
    new { Value = (uint)1, Name = Properties.Resources.VmPage_MemGranularityStandard },
    new { Value = (uint)2, Name = Properties.Resources.VmPage_MemGranularityLarge },
    new { Value = (uint)3, Name = Properties.Resources.VmPage_MemGranularityHuge }
};


        public List<object> MemoryTrackingStateOptions { get; } = new()
{
    new { Value = (byte)0, Name = Properties.Resources.VmPage_MemTrackingDisable },
    new { Value = (byte)1, Name = Properties.Resources.VmPage_MemTrackingEnable },
    new { Value = (byte)2, Name = Properties.Resources.VmPage_MemTrackingPerNode }
};

        public List<object> SgxLaunchControlOptions { get; } = new()
{
    new { Value = (uint)0, Name = Properties.Resources.VmPage_SgxLaunchAccessDenied },
    new { Value = (uint)1, Name = Properties.Resources.VmPage_SgxLaunchReadOnly },
    new { Value = (uint)2, Name = Properties.Resources.VmPage_SgxLaunchReadWrite }
};


    }
}
