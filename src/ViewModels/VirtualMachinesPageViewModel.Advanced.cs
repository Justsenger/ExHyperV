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
        // 基本会话默认分辨率：首项=默认(自适应)，其余为固定分辨率
        public ObservableCollection<string> VideoResolutionOptions { get; } = new()
        {
            Properties.Resources.VmAdvanced_ResolutionAuto,
            "3840 x 2160", "2560 x 1600", "2560 x 1440", "1920 x 1200", "1920 x 1080",
            "1680 x 1050", "1600 x 900", "1440 x 900", "1366 x 768", "1280 x 1024",
            "1280 x 720", "1024 x 768", "800 x 600"
        };

        [ObservableProperty] private string _selectedVideoResolution = string.Empty;
        private bool _suppressVideoApply;   // 加载回填时不触发写回

        [RelayCommand]
        private async Task GoToAdvancedSettingsAsync()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.Advanced;
            IsLoadingSettings = true;
            try
            {
                var (ok, type, w, h) = await VmVideoService.GetResolutionAsync(SelectedVm.Name);
                _suppressVideoApply = true;
                SelectedVideoResolution = (ok && type == 3 && w > 0 && h > 0)
                    ? $"{w} x {h}"
                    : Properties.Resources.VmAdvanced_ResolutionAuto;
                _suppressVideoApply = false;
            }
            finally { IsLoadingSettings = false; }
        }

        partial void OnSelectedVideoResolutionChanged(string value)
        {
            if (_suppressVideoApply || SelectedVm == null || string.IsNullOrEmpty(value)) return;
            _ = ApplyVideoResolutionAsync(value);
        }

        private async Task ApplyVideoResolutionAsync(string value)
        {
            int type, w = 0, h = 0;
            if (value == Properties.Resources.VmAdvanced_ResolutionAuto)
            {
                type = 4; // Default(自适应)
            }
            else
            {
                var parts = value.Split('x');
                if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out w) || !int.TryParse(parts[1].Trim(), out h))
                    return;
                type = 3; // Single(固定)
            }

            var (ok, msg) = await VmVideoService.SetResolutionAsync(SelectedVm.Name, type, w, h);
            if (!ok)
                ShowSnackbar(Properties.Resources.VmAdvanced_ResolutionTitle, msg, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
        }
    }
}
