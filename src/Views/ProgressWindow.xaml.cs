using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace ExHyperV.Views
{
    public partial class ExecutionProgressWindow : FluentWindow
    {
        public event EventHandler RetryClicked;
        private bool _autoScroll = true;

        public ExecutionProgressWindow() => InitializeComponent();

        private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_autoScroll)
            {
                LogScrollViewer.ScrollToEnd();
            }
        }

        private void LogScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange == 0)
            {
                _autoScroll = LogScrollViewer.VerticalOffset == LogScrollViewer.ScrollableHeight;
            }
        }

        private void SafeInvoke(Action action)
        {
            try
            {
                Dispatcher.Invoke(action);
            }
            catch
            {S
            }
        }

        public void AppendLog(string text) => SafeInvoke(() =>
        {
            LogTextBox.AppendText(text + "\n");
            LogTextBox.ScrollToEnd();
        });

        public void UpdateStatus(string status) => SafeInvoke(() =>
        {
            StatusTextBlock.Text = status;
        });

        public void ShowSuccessState() => SafeInvoke(() =>
        {
            UpdateStatus("部署完成！");
            RetryButton.Visibility = Visibility.Collapsed;
            CloseButton.IsEnabled = true;
        });

        public void ShowErrorState(string errorMessage) => SafeInvoke(() =>
        {
            UpdateStatus($"发生错误: {errorMessage}");
            RetryButton.Visibility = Visibility.Visible;
            CloseButton.IsEnabled = true;
        });

        public void ResetForRetry() => SafeInvoke(() =>
        {
            LogTextBox.Clear();
            AppendLog("用户选择重试，正在重新执行部署脚本...\n");
            RetryButton.Visibility = Visibility.Collapsed;
            CloseButton.IsEnabled = false;
            _autoScroll = true;
        });

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void RetryButton_Click(object sender, RoutedEventArgs e) => RetryClicked?.Invoke(this, EventArgs.Empty);
    }
}