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
                // 标题+描述自绘进正文、紧凑竖排;右侧行动组[立即重启 + 关闭]底对齐到描述底。
                //  - 标题不走模板 Title 槽:那样标题↔描述的间距由模板定、偏宽;自绘能收紧,且让描述底与按钮底齐平。
                //  - 模板自带的关闭 X 在第三列、按整卡居中,与描述不同高;关掉它(IsCloseButtonEnabled=false),把强化方框关闭并入本组。
                var content = new System.Windows.Controls.Grid { HorizontalAlignment = HorizontalAlignment.Stretch };
                content.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                content.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

                var textStack = new System.Windows.Controls.StackPanel { VerticalAlignment = VerticalAlignment.Center };
                var titleText = new System.Windows.Controls.TextBlock { Text = Properties.Resources.Status_Title_Success, FontSize = 16, FontWeight = FontWeights.SemiBold };
                // 描述紧贴标题(顶边距 2),字号 14 与模板正文一致
                var descText = new System.Windows.Controls.TextBlock { Text = message, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                textStack.Children.Add(titleText);
                textStack.Children.Add(descText);

                // 行动组底对齐:整组贴底 + 两按钮各自贴底 → 按钮底 = 描述底
                var actions = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
                // 两个按钮统一高度 34,保证等高
                var restartBtn = new Wpf.Ui.Controls.Button { Content = Properties.Resources.Global_Restart, Appearance = ControlAppearance.Light, VerticalAlignment = VerticalAlignment.Bottom, Height = 34, Padding = new Thickness(16, 0, 16, 0) };
                // shutdown.exe 启动失败(权限/环境异常)不应让 UI 线程崩溃;失败时用户可手动重启
                restartBtn.Click += (s, e) => { try { System.Diagnostics.Process.Start("shutdown", "-r -t 0"); } catch { } };
                // 关闭:与重启等高的小方块(34×34),Dismiss 图标缩小居中。
                // 模板卡片 Padding=12、按钮左 margin=8;负右 margin -4 把 X 拉到距右缘 12-4=8,与左间隙对称。
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
                    // 标题已自绘进 content,不再用模板 Title 槽
                    Content = content,
                    // 内容区显式撑满，ContentPresenter 才会把上面的 Grid 拉到全宽，自动列里的按钮组方能真正贴右
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Appearance = ControlAppearance.Success,
                    Icon = new SymbolIcon(SymbolRegular.CheckmarkCircle24) { FontSize = 28 },   // 比正文的 24 稍大
                    IsCloseButtonEnabled = false,   // 关掉模板右侧那个细边 X,改用正文行动组里自绘的方框关闭
                    Timeout = TimeSpan.FromSeconds(15)
                }.Show();
            });
        }

        // 所有 snackbar 统一经此:同一 Background 优先级 + "清队列 → 关当前 → 再显示"。
        // 两条通道曾用不同优先级(Background/Normal),致先调的进度提示后执行、顶掉后到的重启提示;统一后按调用 FIFO。
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

        // 清空积压队列(SnackbarPresenter.Queue 非公开),避免堆叠。
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
