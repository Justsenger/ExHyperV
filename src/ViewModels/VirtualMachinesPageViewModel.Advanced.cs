using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
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
        private bool _suppressConsoleApply;

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

                _suppressConsoleApply = true;
                IsConsoleSupportEnabled = await VmConsoleService.IsConsoleSupportEnabledAsync(SelectedVm.Name);
                _suppressConsoleApply = false;
            }
            finally { IsLoadingSettings = false; }
        }

        partial void OnIsConsoleSupportEnabledChanged(bool value)
        {
            if (_suppressConsoleApply || SelectedVm == null) return;
            _ = ApplyConsoleSupportAsync(value);
        }

        private async Task ApplyConsoleSupportAsync(bool enable)
        {
            var (ok, msg) = await VmConsoleService.SetConsoleSupportAsync(SelectedVm.Name, enable);
            if (ok)
            {
                ShowSnackbar(Properties.Resources.VmAdvanced_ConsoleTitle, Properties.Resources.Common_Success,
                    ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
            }
            else
            {
                ShowSnackbar(Properties.Resources.VmAdvanced_ConsoleTitle, msg,
                    ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                _suppressConsoleApply = true;
                IsConsoleSupportEnabled = !enable;   // 失败回弹开关
                _suppressConsoleApply = false;
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
                    ShowSnackbar(Properties.Resources.VmAdvanced_ResolutionTitle, Properties.Resources.VmAdvanced_ResolutionInvalid,
                        ControlAppearance.Caution, SymbolRegular.Warning24);
                    return;
                }
                w &= ~1; h &= ~1;   // 宽高需为偶数（Set-VMVideo 要求），向下取偶
                type = 3; // Single(固定)
            }

            var (ok, msg) = await VmVideoService.SetResolutionAsync(SelectedVm.Name, type, w, h);
            if (ok)
            {
                if (type == 3) SelectedVideoResolution = $"{w} x {h}";   // 回显取偶后实际应用的值
                ShowSnackbar(Properties.Resources.VmAdvanced_ResolutionTitle, Properties.Resources.Common_Success,
                    ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
            }
            else
                ShowSnackbar(Properties.Resources.VmAdvanced_ResolutionTitle, msg,
                    ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
        }
    }
}
