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
        /// </summary>
        public static void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon)
        {
            TimeSpan timeout = (appearance == ControlAppearance.Danger || appearance == ControlAppearance.Caution)
                ? TimeSpan.FromSeconds(Math.Clamp((message?.Length ?? 0) / 20, 2, 60))   // 每 20 字符 +1 秒
                : TimeSpan.FromSeconds(2);

            Dispatch(presenter => new Snackbar(presenter)
            {
                Title = title,
                Content = message,
                Appearance = appearance,
                Icon = new SymbolIcon(icon) { FontSize = 24 },   // 统一放大到 24（原默认偏小）
                Timeout = timeout
            }.Show());
        }

        /// <summary>
        /// 显示带"立即重启"按钮的成功提示（用于需重启生效的操作）。
        /// </summary>
        public static void ShowRestartPrompt(string message)
        {
            Dispatch(presenter =>
            {
                // 自绘标题和操作区，以绕过 Snackbar 模板固定的标题间距与关闭按钮布局。
                var content = new System.Windows.Controls.Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
                content.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                content.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

                var textStack = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var titleText = new System.Windows.Controls.TextBlock { Text = Properties.Resources.Status_Title_Success, FontSize = 16, FontWeight = FontWeights.SemiBold };
                var descText = new System.Windows.Controls.TextBlock { Text = message, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                textStack.Children.Add(titleText);
                textStack.Children.Add(descText);

                var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
                var restartBtn = new Wpf.Ui.Controls.Button { Content = Properties.Resources.Global_Restart, Appearance = ControlAppearance.Light, VerticalAlignment = VerticalAlignment.Bottom, Height = 34, Padding = new Thickness(16, 0, 16, 0) };
                restartBtn.Click += (s, e) => { try { System.Diagnostics.Process.Start("shutdown", "-r -t 0"); } catch { } };
                var closeBtn = new Wpf.Ui.Controls.Button { Icon = new SymbolIcon(SymbolRegular.Dismiss24) { FontSize = 16 }, Appearance = ControlAppearance.Secondary, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(8, 0, -4, 0), Width = 34, Height = 34, Padding = new Thickness(0) };
                closeBtn.Click += async (s, e) => { try { await presenter.HideCurrent(); } catch { } };
                actions.Children.Add(restartBtn);
                actions.Children.Add(closeBtn);

                System.Windows.Controls.Grid.SetColumn(textStack, 0);
                System.Windows.Controls.Grid.SetColumn(actions, 1);
                content.Children.Add(textStack);
                content.Children.Add(actions);

                new Snackbar(presenter)
                {
                    Content = content,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Appearance = ControlAppearance.Success,
                    Icon = new SymbolIcon(SymbolRegular.CheckmarkCircle24) { FontSize = 28 },
                    IsCloseButtonEnabled = false,
                    Timeout = TimeSpan.FromSeconds(15)
                }.Show();
            });
        }

        // 所有 snackbar 统一经此:同一 Background 优先级 + "清队列 → 关当前 → 再显示"。
        // 使用同一调度优先级以保持通知调用顺序。
        private static void Dispatch(Action<SnackbarPresenter> show)
        {
            Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                if (Application.Current.MainWindow?.FindName("SnackbarPresenter") is not SnackbarPresenter presenter) return;
                ClearQueue(presenter);
                try { await presenter.HideCurrent(); } catch { }   // 安全关闭当前条
                show(presenter);
            }, DispatcherPriority.Background);
        }

        // SnackbarPresenter 没有公开清空队列的接口，因此通过反射移除积压通知。
        private static void ClearQueue(SnackbarPresenter presenter)
        {
            try
            {
                var queueProp = typeof(SnackbarPresenter).GetProperty("Queue", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var queueObj = queueProp?.GetValue(presenter);
                queueObj?.GetType().GetMethod("Clear")?.Invoke(queueObj, null);
            }
            catch { }
        }
    }
}
