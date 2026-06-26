using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;

using ExHyperV.Views;
namespace ExHyperV.Interaction
{
    public static class Dialogs
    {
        /// <summary>
        /// 显示确认对话框，返回用户是否确认
        /// </summary>
        public static async Task<bool> ShowConfirmAsync(string title, string message, string confirmButtonText = null, string cancelButtonText = null, bool isDanger = false, bool showIcon = true, double maxWidth = 0)
        {
            if (Application.Current.MainWindow is not MainWindow mainWindow)
            {
                return false;
            }

            var dialogHost = mainWindow.ContentPresenterForDialogs;
            if (dialogHost == null)
            {
                return false;
            }

            var contentTextBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                LineHeight = 24,
                TextAlignment = TextAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = showIcon ? new Thickness(12, 0, 0, 0) : new Thickness(0)
            };
            if (maxWidth > 0) contentTextBlock.MaxWidth = maxWidth; // 收窄对话框宽度

            // showIcon=false: no icon, text left-aligned and full width (flush with the title)
            object dialogContent;
            if (showIcon)
            {
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var icon = new FontIcon
                {
                    FontFamily = Application.Current.FindResource("SegoeFluentIcons") as FontFamily,
                    Glyph = isDanger ? "\uE814" : "\uE946", // Warning icon for danger, Info icon otherwise
                    FontSize = 28,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = isDanger ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(196, 43, 28)) : null
                };

                Grid.SetColumn(icon, 0);
                Grid.SetColumn(contentTextBlock, 1);
                grid.Children.Add(icon);
                grid.Children.Add(contentTextBlock);
                dialogContent = grid;
            }
            else
            {
                dialogContent = contentTextBlock;
            }

            var dialog = new ContentDialog
            {
                Title = title,
                Content = dialogContent,
                PrimaryButtonText = confirmButtonText ?? Properties.Resources.Btn_Confirm,
                CloseButtonText = cancelButtonText ?? Properties.Resources.Btn_Cancel,
                DialogHost = dialogHost,
                PrimaryButtonAppearance = isDanger ? ControlAppearance.Danger : ControlAppearance.Primary
            };

            var result = await dialog.ShowAsync(CancellationToken.None);
            return result == ContentDialogResult.Primary;
        }

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
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new FontIcon
            {
                FontFamily = Application.Current.FindResource("SegoeFluentIcons") as FontFamily,
                Glyph = "\uE783",
                FontSize = 28,
                VerticalAlignment = VerticalAlignment.Center
            };

            var contentTextBlock = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
                LineHeight = 24,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };

            Grid.SetColumn(icon, 0);
            Grid.SetColumn(contentTextBlock, 1);
            grid.Children.Add(icon);
            grid.Children.Add(contentTextBlock);

            var dialog = new ContentDialog
            {
                Title = title,
                Content = grid,
                CloseButtonText = Properties.Resources.Btn_Confirm,
                DialogHost = dialogHost
            };

            await dialog.ShowAsync(CancellationToken.None);
        }

        public static async Task<bool> ShowContentDialogAsync(string title, UserControl content)
        {
            if (Application.Current.MainWindow is not MainWindow mainWindow)
            {
                return false;
            }

            var dialogHost = mainWindow.ContentPresenterForDialogs;
            if (dialogHost == null)
            {
                return false;
            }

            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                PrimaryButtonText = Properties.Resources.Btn_Create,
                CloseButtonText = Properties.Resources.Btn_Cancel,
                DialogHost = dialogHost,
                VerticalContentAlignment = VerticalAlignment.Top
            };

            var result = await dialog.ShowAsync(CancellationToken.None);

            return result == ContentDialogResult.Primary;
        }

        // ===== 文件系统选择器 =====
        // 封装 Microsoft.Win32 的打开/保存/选目录对话框，VM 不再各自 new 一遍样板。
        // 统一约定：返回选中的路径；用户取消一律返回 null（调用方据此决定是否更新绑定）。

        /// <summary>打开文件选择框。title 传 null 用系统默认标题。</summary>
        public static string? PickOpenFile(string? title, string filter, string? initialDir = null)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter };
            if (!string.IsNullOrWhiteSpace(title)) dlg.Title = title;
            if (!string.IsNullOrWhiteSpace(initialDir)) dlg.InitialDirectory = initialDir;
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        /// <summary>保存文件选择框。</summary>
        public static string? PickSaveFile(string? title, string filter, string? defaultExt = null, string? initialDir = null, string? fileName = null)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = filter };
            if (!string.IsNullOrWhiteSpace(title)) dlg.Title = title;
            if (!string.IsNullOrWhiteSpace(defaultExt)) dlg.DefaultExt = defaultExt;
            if (!string.IsNullOrWhiteSpace(initialDir)) dlg.InitialDirectory = initialDir;
            if (!string.IsNullOrWhiteSpace(fileName)) dlg.FileName = fileName;
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        /// <summary>选择文件夹。</summary>
        public static string? PickFolder(string? title = null, string? initialDir = null)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog();
            if (!string.IsNullOrWhiteSpace(title)) dlg.Title = title;
            if (!string.IsNullOrWhiteSpace(initialDir)) dlg.InitialDirectory = initialDir;
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        }
    }
}