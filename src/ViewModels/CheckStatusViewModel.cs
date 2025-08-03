using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;

namespace ExHyperV.ViewModels
{
    /// <summary>
    /// 表示单个环境检查项的ViewModel。
    /// </summary>
    public partial class CheckStatusViewModel : ObservableObject
    {
        [ObservableProperty]
        private bool _isChecking = true;

        [ObservableProperty]
        private string _statusText;

        [ObservableProperty]
        private bool? _isSuccess;

        public string IconGlyph => IsSuccess switch
        {
            true => "\uEC61",  // Success icon
            false => "\uEB90", // Failure icon
            _ => ""
        };

        public Brush IconColor => IsSuccess switch
        {
            true => new SolidColorBrush(Color.FromArgb(255, 0, 138, 23)), // Green
            false => new SolidColorBrush(Colors.Red),                     // Red
            _ => Brushes.Transparent
        };

        public CheckStatusViewModel(string initialText)
        {
            _statusText = initialText;
        }

        // 当IsSuccess属性变化时，通知UI更新依赖于它的属性。
        partial void OnIsSuccessChanged(bool? value)
        {
            OnPropertyChanged(nameof(IconGlyph));
            OnPropertyChanged(nameof(IconColor));
        }
    }
}