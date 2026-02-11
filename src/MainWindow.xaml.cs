using System.Windows;
using ExHyperV.Views.Pages;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace ExHyperV
{
    public partial class MainWindow : FluentWindow
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += PagePreload;

            if (SystemThemeManager.GetCachedSystemTheme() == SystemTheme.Dark)
            { //根据系统主题自动切换
                ApplicationThemeManager.Apply(ApplicationTheme.Dark);
            }
            else { ApplicationThemeManager.Apply(ApplicationTheme.Light); }

            Loaded += (sender, args) =>  //监听系统切换主题事件
            {
                SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
            };
        }

        private void PagePreload(object sender, RoutedEventArgs e)
        {
            //预加载所有子界面，多线程。
            RootNavigation.Navigate(typeof(DDAPage));
            RootNavigation.Navigate(typeof(StatusPage));


            RootNavigation.Navigate(typeof(MainPage));
            RootNavigation.Navigate(typeof(VirtualMachinesPage)); 

        }




    }
}