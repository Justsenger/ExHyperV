using System;
using System.Windows;
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
            // 1. 准备数据：使用我们完善后的构造函数
            _viewModel = new ConsoleViewModel(vmId, vmName);

            // 2. 核心：在加载 UI 前绑定，确保子控件能够通过继承获取 DataContext
            this.DataContext = _viewModel;

            // 3. 初始化组件
            InitializeComponent();

            // 4. 设置窗口标题
            this.Title = vmName;

            // 5. 核心：订阅 CAD 请求事件
            // 当用户点击顶栏的键盘按钮时，ViewModel 会触发此事件
            _viewModel.SendCadRequested += OnSendCadRequested;
        }

        /// <summary>
        /// 响应 ViewModel 发出的发送 CAD 组合键请求
        /// </summary>
        private void OnSendCadRequested(object? sender, EventArgs e)
        {
            // ConsoleHost 是 XAML 中指定的 <components:VmConsoleView x:Name="ConsoleHost" />
            // 调用该组件内部封装好的发送 CAD 方法
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