using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace ExHyperV.Tools
{
    public static class DialogManager
    {
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
                CloseButtonText = Properties.Resources.sure,
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
                PrimaryButtonText = ExHyperV.Properties.Resources.create,
                CloseButtonText = ExHyperV.Properties.Resources.cancel,
                DialogHost = dialogHost,
                VerticalContentAlignment = VerticalAlignment.Top
            };

            var result = await dialog.ShowAsync(CancellationToken.None);

            return result == ContentDialogResult.Primary;
        }
    }
}