using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ExHyperV.ViewModels
{
    public partial class ConsoleViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _vmId; // 用于存放 GUID 字符串

        [ObservableProperty]
        private string _vmName; // 用于窗口标题

        [ObservableProperty]
        private bool _isLoading = true; // 控制 XAML 里的加载动画显示

        partial void OnVmIdChanged(string value)
        {
            Debug.WriteLine($"[ConsoleViewModel] 属性赋值成功: {value}");
        }
    }
}