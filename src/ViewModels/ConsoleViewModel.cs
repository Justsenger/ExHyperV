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
            "1024 x 768", "1280 x 720", "1600 x 900", "1920 x 1080", "2560 x 1440"
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
    }
}