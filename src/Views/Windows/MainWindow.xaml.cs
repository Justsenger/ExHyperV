using System.Windows;
using System.Windows.Interop;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Tools.Controls;
using Wpf.Ui.Controls;

namespace ExHyperV.Views
{
    public partial class MainWindow : FluentWindow
    {
        private TrayIconController? _trayIcon;

        public MainWindow()
        {
            InitializeComponent();

            // 单实例：首个实例在此监听"唤醒"广播（第二个实例启动时会发出）。
            SourceInitialized += (_, _) =>
            {
                // 优先取窗口 HWND 对应的 HwndSource；FromVisual 在极早期可能尚未建立
                // 连接而返回 null，此时静默跳过后唤醒将永远失效，故打印日志便于排查。
                var source = PresentationSource.FromVisual(this) as HwndSource
                             ?? HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                if (source != null)
                    source.AddHook(OnWindowMessage);
                else
                    System.Diagnostics.Debug.WriteLine("[ExHyperV] Single-instance activation hook not attached (no HwndSource).");
            };

            if (App.PerformanceMode)
            {
                // 无 GPU 模式：关 Mica（省 DWM 合成），改不透明底
                WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
                SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
                // 关页面切换过渡（默认 FadeInWithSlide 的下往上滑入，RDP/软渲染下卡）
                RootNavigation.Transition = Wpf.Ui.Animations.Transition.None;
            }
            Loaded += PagePreload;

            //仅按保存偏好上色;系统主题监听延到 PagePreload 后挂,避开 #146 启动渲染竞争
            SettingsService.ApplySavedTheme();

            try
            {
                // 从程序集资源读取，单文件发布也不依赖外部 ICO 文件。
                var icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                    new Uri("pack://application:,,,/Assets/Icon.ico"));
                _trayIcon = new TrayIconController(Application.Current, this, icon,
                    Properties.Resources.Tray_Open, Properties.Resources.Tray_Exit);
            }
            catch (Exception ex)
            {
                // 托盘不可用时仍正常关闭，不能把唯一窗口隐藏后无从恢复。
                System.Diagnostics.Debug.WriteLine($"[ExHyperV] Tray initialization failed: {ex.Message}");
            }
        }

        private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 消息号注册失败时为 0，与 WM_NULL 同值，不能当作唤醒消息处理。
            if (!SingleInstanceGuard.IsActivationAvailable || msg != SingleInstanceGuard.ActivateMessage)
                return IntPtr.Zero;
            handled = true;

            // 与托盘菜单“打开”共用同一还原逻辑：隐藏到托盘时窗口 HWND 仍在，Show() 会触发
            // TrayIconController 的 IsVisibleChanged 自动 Resume 挂起的资源；最小化前的窗口状态
            // 也由控制器记录，避免最大化窗口在唤醒后变成普通大小。
            if (_trayIcon != null)
            {
                _trayIcon.RestoreWindow();
            }
            else
            {
                // 托盘初始化失败时没有控制器可用，退化为直接显示。
                if (!IsVisible) Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            }
            SingleInstanceGuard.BringToFront(new WindowInteropHelper(this).Handle);
            return IntPtr.Zero;
        }

        private void PagePreload(object sender, RoutedEventArgs e)
        {
            Loaded -= PagePreload;
            // 性能模式：不预加载其它页面（省启动内存），只落地首页；其余首次进入时才建
            if (!App.PerformanceMode)
            {
                RootNavigation.Navigate(typeof(PCIePage));
                RootNavigation.Navigate(typeof(HostPage));
                RootNavigation.Navigate(typeof(SwitchPage));
                RootNavigation.Navigate(typeof(VirtualMachinesPage));
                RootNavigation.Navigate(typeof(USBPage));
            }
            RootNavigation.Navigate(typeof(MainPage));

            //预加载/首帧后再挂系统主题监听(跟随模式),与 #146 的 Loaded 渲染竞争错开
            Dispatcher.BeginInvoke(
                new Action(() => SettingsService.EnableSystemThemeWatch(this)),
                System.Windows.Threading.DispatcherPriority.Background);
        }




    }
}
