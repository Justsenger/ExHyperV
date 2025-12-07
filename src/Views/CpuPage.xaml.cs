using System.Windows;
using System.Windows.Controls;
using ExHyperV.ViewModels; // 确保引入了 ViewModel 的命名空间

namespace ExHyperV.Views.Pages
{
    public partial class CpuPage : Page
    {
        public CpuPage()
        {
            InitializeComponent();
            // 订阅 Unloaded 事件
            this.Unloaded += CpuPage_Unloaded;
        }

        private void CpuPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // 当页面卸载时（例如导航离开），获取它的 ViewModel
            if (this.DataContext is IDisposable viewModel)
            {
                // 调用 Dispose 方法，这将停止后台的 MonitorLoop
                viewModel.Dispose();
            }

            // 取消订阅，避免内存泄漏
            this.Unloaded -= CpuPage_Unloaded;
        }
    }
}