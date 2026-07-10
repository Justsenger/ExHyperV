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

            //仅按保存偏好上色;系统主题监听延到 PagePreload 后挂,避开 #146 启动渲染竞争
            SettingsService.ApplySavedTheme();
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

            //预加载/首帧后再挂系统主题监听(跟随模式),与 #146 的 Loaded 渲染竞争错开
            Dispatcher.BeginInvoke(
                new Action(() => SettingsService.EnableSystemThemeWatch(this)),
                System.Windows.Threading.DispatcherPriority.Background);
        }




    }
}