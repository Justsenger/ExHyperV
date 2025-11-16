using System; // 确保引用 System 命名空间
using System.Windows;
using System.Windows.Controls;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.Views
{
    public partial class ExecutionProgressWindow : FluentWindow
    {
        // 1. 【新增】定义一个公共事件，用于通知调用者“重试”按钮被点击了
        public event EventHandler RetryClicked;
        private bool _autoScroll = true;

        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 只有在 _autoScroll 为 true 的情况下，才滚动到底部
            if (_autoScroll)
            {
                LogScrollViewer.ScrollToEnd();
            }
        }

        // 当 ScrollViewer 的滚动位置发生变化时触发
        private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // e.ExtentHeightChange > 0 表示内容增加了（即添加了新日志）
            // e.ExtentHeightChange == 0 表示只是用户在滚动，内容本身没有增加
            if (e.ExtentHeightChange == 0)
            {
                // 如果是用户在滚动，判断当前是否在底部
                // VerticalOffset 是当前滚动条顶部的位置
                // ScrollableHeight 是可滚动的总高度
                if (LogScrollViewer.VerticalOffset == LogScrollViewer.ScrollableHeight)
                {
                    // 用户滚动到了最底部，恢复自动滚动
                    _autoScroll = true;
                }
                else
                {
                    // 用户向上滚动了，停止自动滚动
                    _autoScroll = false;
                }
            }
        }

        public ExecutionProgressWindow()
        {
            InitializeComponent();

            // 2. 【新增】增加窗口关闭时的逻辑，防止在部署过程中被意外关闭
            this.Closing += (s, e) => {
                // 如果关闭按钮仍是禁用状态，说明任务还没执行完
                if (!CloseButton.IsEnabled)
                {
                    e.Cancel = true; // 取消关闭操作
                    Utils.Show("部署正在进行中，请勿关闭窗口。");
                }
            };
        }

        public void AppendLog(string text)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText(text + "\n");
                LogTextBox.ScrollToEnd();
            });
        }

        public void UpdateStatus(string status)
        {
            Dispatcher.Invoke(() =>
            {
                StatusTextBlock.Text = status;
            });
        }

        // 3. 【修改】用下面三个方法替换原来的 EnableCloseButton()，以实现更清晰的状态管理

        /// <summary>
        /// 当部署成功时调用此方法。
        /// </summary>
        public void ShowSuccessState()
        {
            Dispatcher.Invoke(() => {
                UpdateStatus("部署完成！");
                RetryButton.Visibility = Visibility.Collapsed; // 隐藏重试按钮
                CloseButton.IsEnabled = true; // 启用关闭按钮
            });
        }

        /// <summary>
        /// 当部署失败时调用此方法。
        /// </summary>
        public void ShowErrorState(string errorMessage)
        {
            Dispatcher.Invoke(() => {
                UpdateStatus($"发生错误: {errorMessage}");
                RetryButton.Visibility = Visibility.Visible; // 显示重试按钮
                CloseButton.IsEnabled = true; // 启用关闭按钮
            });
        }

        /// <summary>
        /// 当用户点击重试后，调用此方法重置窗口状态。
        /// </summary>
        public void ResetForRetry()
        {
            Dispatcher.Invoke(() => {
                LogTextBox.Clear(); // 清空旧日志
                AppendLog("用户选择重试，正在重新执行部署脚本...\n");
                RetryButton.Visibility = Visibility.Collapsed; // 隐藏重试按钮
                CloseButton.IsEnabled = false; // 禁用关闭按钮
            });
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        // 4. 【新增】为重试按钮添加点击事件的处理逻辑
        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            // 触发 RetryClicked 事件，通知后台逻辑
            RetryClicked?.Invoke(this, EventArgs.Empty);
        }


    }
}