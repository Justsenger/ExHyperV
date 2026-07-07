using System.Windows;
using ExHyperV.Services;
using ExHyperV.Views;
using Wpf.Ui.Controls;

namespace ExHyperV.Views
{
    public partial class MainWindow : FluentWindow
    {
        public MainWindow()
        {
            InitializeComponent();
            Loaded += PagePreload;

            //根据保存的设置初始化主题
            SettingsService.ApplySavedTheme(this);
            
        }

        private void PagePreload(object sender, RoutedEventArgs e)
        {
            //预加载所有子界面
            RootNavigation.Navigate(typeof(PCIePage));
            RootNavigation.Navigate(typeof(HostPage));
            RootNavigation.Navigate(typeof(SwitchPage));
            RootNavigation.Navigate(typeof(VirtualMachinesPage));
            RootNavigation.Navigate(typeof(USBPage));
            RootNavigation.Navigate(typeof(MainPage));

        }




    }
}