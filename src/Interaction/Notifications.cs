using System;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace ExHyperV.Interaction
{
    /// <summary>
    /// 全局 UI 通知门面：Snackbar 提示 + 重启提示。
    /// VM 调用本类，内部统一操作 MainWindow 的 SnackbarPresenter（唯一一处碰可视树）。
    /// </summary>
    public static class Notifications
    {
        /// <summary>
        /// 显示 Snackbar 通知。危险/警告类按消息长度动态延时（2~60s），其余固定 2s。
        /// 显示前清空积压队列并关闭当前条，避免堆叠。
        /// </summary>
        public static void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon)
        {
            Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                var presenter = Application.Current.MainWindow?.FindName("SnackbarPresenter") as SnackbarPresenter;
                if (presenter == null) return;

                // 暴力清空积压队列
                try
                {
                    var queueProp = typeof(SnackbarPresenter).GetProperty("Queue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var queueObj = queueProp?.GetValue(presenter);
                    queueObj?.GetType().GetMethod("Clear")?.Invoke(queueObj, null);
                }
                catch { }

                // 安全关闭当前条
                try { await presenter.HideCurrent(); } catch { }

                TimeSpan timeout;
                if (appearance == ControlAppearance.Danger || appearance == ControlAppearance.Caution)
                {
                    int msgLength = message?.Length ?? 0;
                    int calculatedSeconds = Math.Clamp(msgLength / 20, 2, 60); // 每 20 字符 +1 秒
                    timeout = TimeSpan.FromSeconds(calculatedSeconds);
                }
                else
                {
                    timeout = TimeSpan.FromSeconds(2);
                }

                new Snackbar(presenter)
                {
                    Title = title,
                    Content = message,
                    Appearance = appearance,
                    Icon = new SymbolIcon(icon) { FontSize = 24 },   // 统一放大到 24（原默认偏小）
                    Timeout = timeout
                }.Show();
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// 显示带"立即重启"按钮的成功提示（用于需重启生效的操作）。
        /// </summary>
        public static void ShowRestartPrompt(string message)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (Application.Current.MainWindow?.FindName("SnackbarPresenter") is not SnackbarPresenter p) return;

                var grid = new System.Windows.Controls.Grid { VerticalAlignment = VerticalAlignment.Center };
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

                var icon = new SymbolIcon(SymbolRegular.CheckmarkCircle24) { FontSize = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
                var textStack = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                var titleTxt = new Wpf.Ui.Controls.TextBlock { Text = Properties.Resources.Status_Title_Success, FontWeight = FontWeights.Bold, FontSize = 14, Margin = new Thickness(0) };
                var msgTxt = new Wpf.Ui.Controls.TextBlock { Text = message, FontSize = 12, Margin = new Thickness(0, -2, 0, 0) };
                textStack.Children.Add(titleTxt);
                textStack.Children.Add(msgTxt);

                var btn = new Wpf.Ui.Controls.Button { Content = Properties.Resources.Global_Restart, Appearance = ControlAppearance.Primary, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
                // shutdown.exe 启动失败（权限/环境异常）不应让 UI 线程崩溃；失败时用户可手动重启
                btn.Click += (s, e) => { try { System.Diagnostics.Process.Start("shutdown", "-r -t 0"); } catch { } };

                System.Windows.Controls.Grid.SetColumn(icon, 0);
                System.Windows.Controls.Grid.SetColumn(textStack, 1);
                System.Windows.Controls.Grid.SetColumn(btn, 2);
                grid.Children.Add(icon);
                grid.Children.Add(textStack);
                grid.Children.Add(btn);

                new Snackbar(p) { Content = grid, Appearance = ControlAppearance.Success, Timeout = TimeSpan.FromSeconds(15) }.Show();
            });
        }
    }
}
