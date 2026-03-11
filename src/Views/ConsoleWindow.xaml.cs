using Wpf.Ui.Controls;
using ExHyperV.ViewModels;
using System.Windows.Input;

namespace ExHyperV.Views
{
    public partial class ConsoleWindow : FluentWindow
    {
        private readonly ConsoleViewModel _viewModel;

        public ConsoleWindow(string vmId, string vmName)
        {
            _viewModel = new ConsoleViewModel(vmId, vmName);
            this.DataContext = _viewModel;
            InitializeComponent();
            this.Title = vmName;
        }

        /// <summary>
        /// 响应 ViewModel 发出的发送 CAD 组合键请求
        /// </summary>
        private void OnSendCadRequested(object? sender, EventArgs e)
        {
            ConsoleHost?.SendCtrlAltDel();
        }

        /// <summary>
        /// 当窗口关闭时，必须销毁 ViewModel 以停止后台状态轮询 Timer
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            // 取消事件订阅，防止内存泄漏
            _viewModel.SendCadRequested -= OnSendCadRequested;

            // 停止 ViewModel 内部的轮询 Timer
            _viewModel.Dispose();
        }
        private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                this.DragMove();
            }
        }
    }
}