// 文件路径: Tools/DialogManager.cs

using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;

// 注意：下面的代码依赖于你的项目能够正确解析 MainWindow 类型。
// 如果编译时提示找不到 MainWindow，请确保在此文件的命名空间之外
// 或在文件顶部有 'using ExHyperV.Views.Windows;'。

namespace ExHyperV.Tools
{
    /// <summary>
    /// 一个静态管理类，用于在应用中的任何地方方便地显示对话框。
    /// 它封装了查找和使用 MainWindow 上的全局 ContentPresenter 的逻辑。
    /// </summary>
    public static class DialogManager
    {
        /// <summary>
        /// 异步显示一个简单的提示对话框。
        /// </summary>
        /// <param name="title">对话框的标题。</param>
        /// <param name="message">对话框显示的消息内容。</param>
        public static async Task ShowAlertAsync(string title, string message)
        {
            if (Application.Current.Dispatcher.CheckAccess())
            {
                await ShowDialogInternal(title, message);
            }
            else
            {
                await Application.Current.Dispatcher.InvokeAsync(() => ShowDialogInternal(title, message));
            }
        }

        private static async Task ShowDialogInternal(string title, string message)
        {
            if (Application.Current.MainWindow is not MainWindow mainWindow)
            {
                return;
            }

            var dialogHost = mainWindow.ContentPresenterForDialogs;
            if (dialogHost == null)
            {
                return;
            }

            var grid = new Grid();

            // 定义列：图标列和文字列
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); // 图标列宽度稍微增加
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // **** 1. 创建图标并设置垂直居中 ****
            var icon = new FontIcon
            {
                FontFamily = Application.Current.FindResource("SegoeFluentIcons") as FontFamily,
                Glyph = "\uE783", // "Warning" 图标
                FontSize = 32,    // 图标稍微大一点
                VerticalAlignment = VerticalAlignment.Center // 在其单元格内垂直居中
            };

            // **** 2. 创建文本块并调整样式 ****
            var contentTextBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                LineHeight = 28, // 拉大行距，以区分主次信息
                VerticalAlignment = VerticalAlignment.Center, // 在其单元格内垂直居中
                Margin = new Thickness(12, 0, 0, 0)
            };

            // 将控件放入Grid
            Grid.SetColumn(icon, 0);
            Grid.SetColumn(contentTextBlock, 1);

            grid.Children.Add(icon);
            grid.Children.Add(contentTextBlock);

            var dialog = new ContentDialog
            {
                Title = title,
                Content = grid,
                CloseButtonText = "确定",
                DialogHost = dialogHost,
                // **** 3. 将标题设置为左对齐 ****
                HorizontalContentAlignment = HorizontalAlignment.Left,
                
            };

            await dialog.ShowAsync(CancellationToken.None);
        }
    }
}