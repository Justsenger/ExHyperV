using System.Windows;
using Wpf.Ui.Controls;

namespace ExHyperV.Views
{
    public partial class ExecutionProgressWindow : FluentWindow
    {
        public ExecutionProgressWindow()
        {
            InitializeComponent();
            // 可以在窗口关闭事件中添加逻辑，例如询问用户是否要中止
        }

        /// <summary>
        /// 向日志窗口追加一行文本。此方法是线程安全的。
        /// </summary>
        /// <param name="text">要追加的文本。</param>
        public void AppendLog(string text)
        {
            // 使用 Dispatcher.Invoke 确保所有UI更新都在主线程上执行
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(text + "\n");
                LogTextBox.ScrollToEnd(); // 自动滚动到底部
            });
        }

        /// <summary>
        /// 更新窗口顶部的状态文本。此方法是线程安全的。
        /// </summary>
        /// <param name="status">新的状态文本。</param>
        public void UpdateStatus(string status)
        {
            Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text = status;
            });
        }

        /// <summary>
        /// 启用关闭按钮，表示流程已完成或已中止。
        /// </summary>
        public void EnableCloseButton()
        {
            Dispatcher.Invoke(() =>
            {
                CloseButton.IsEnabled = true;
            });
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}