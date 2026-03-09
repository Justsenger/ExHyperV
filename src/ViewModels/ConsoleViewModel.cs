using System.Diagnostics;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input; // 必须引用以支持 RelayCommand
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public partial class ConsoleViewModel : ObservableObject
    {
        [ObservableProperty] private string _vmId;
        [ObservableProperty] private string _vmName;
        [ObservableProperty] private bool _isLoading = true;
        private bool _isConnecting = false;

        // --- 分辨率逻辑 ---
        [ObservableProperty] private string _selectedResolution = "等待连接...";

        [ObservableProperty] private int _currentWidth;
        [ObservableProperty] private int _currentHeight;

        partial void OnCurrentWidthChanged(int value) => UpdateResolutionString();
        partial void OnCurrentHeightChanged(int value) => UpdateResolutionString();

        private void UpdateResolutionString()
        {
            if (CurrentWidth > 0 && CurrentHeight > 0)
                SelectedResolution = $"{CurrentWidth} x {CurrentHeight}";
        }

        public ObservableCollection<string> Resolutions { get; } = new()
{
    "3840 x 2160", // 4K Ultra HD
    "2560 x 1600", // WQXGA (16:10)
    "2560 x 1440", // 2K QHD
    "1920 x 1200", // WUXGA (16:10)
    "1920 x 1080", // Full HD (1080p)
    "1680 x 1050", // WSXGA+ (16:10)
    "1600 x 1200", // UXGA (4:3)
    "1600 x 900",  // HD+
    "1440 x 900",  // WXGA+ (16:10)
    "1366 x 768",  // 笔记本常见分辨率
    "1280 x 1024", // SXGA (5:4)
    "1280 x 800",  // WXGA (16:10)
    "1280 x 720",  // HD (720p)
    "1152 x 864",
    "1024 x 768",  // XGA (推荐)
    "800 x 600"    // SVGA
};

        // --- 会话模式逻辑 ---
        [ObservableProperty] private string _selectedSessionMode = "基本会话";
        [ObservableProperty] private bool _isEnhancedMode = false;
        [ObservableProperty] private SymbolRegular _selectedSessionIcon = SymbolRegular.Broom24;

        // 处理模式切换的命令
        [RelayCommand]
        private void SwitchSessionMode(string mode)
        {
            SelectedSessionMode = mode;
        }

        partial void OnSelectedSessionModeChanged(string value)
        {
            IsEnhancedMode = (value == "增强会话");
            SelectedSessionIcon = IsEnhancedMode ? SymbolRegular.Flash24 : SymbolRegular.Broom24;
            OnPropertyChanged(nameof(CanChangeResolution));
        }

        public bool CanChangeResolution => IsEnhancedMode;

        [ObservableProperty] private int _requestWidth;
        [ObservableProperty] private int _requestHeight;

        [RelayCommand]
        private void ChangeResolution(string resolutionText)
        {
            if (string.IsNullOrEmpty(resolutionText) || !IsEnhancedMode) return;

            // 解析 "1920 x 1080" 字符串
            var parts = resolutionText.Split('x');
            if (parts.Length == 2 &&
                int.TryParse(parts[0].Trim(), out int w) &&
                int.TryParse(parts[1].Trim(), out int h))
            {
                // 关键：我们不需要手动调 RDP 接口
                // 我们只需要更新 CurrentWidth/Height，让 UI 绑定的 Host 控件感知
                // 或者更直接点，这里通过消息或事件通知 View 层去改 Window 的宽和高
                Debug.WriteLine($"[ViewModel] 请求切换分辨率至: {w}x{h}");

                // 触发属性变更，配合下一步的 View 层处理
                CurrentWidth = w;
                CurrentHeight = h;

                // 注意：因为你的属性是 OneWayToSource，我们需要一种方式反向推给 View
                // 建议增加两个专门用于“请求”的属性
                RequestWidth = w;
                RequestHeight = h;
            }
        }


    }


}