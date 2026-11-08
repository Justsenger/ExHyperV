using ExHyperV.Application;
using ExHyperV.UI.Pages.Host;
using ExHyperV.UI.Pages.Overview;
using ExHyperV.UI.Pages.Pcie;
using ExHyperV.UI.Pages.Switches;
using ExHyperV.UI.Pages.VirtualMachines;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace ExHyperV.UI.Windows.Main
{
    public partial class MainWindow : FluentWindow
    {
        private TrayIconController? _trayIcon;
        private readonly MainPageProvider _pages;
        private readonly Queue<Type> _preloadPages = new();
        private DispatcherOperation? _preloadOperation;
        private bool _closed;

        public MainWindow()
        {
            InitializeComponent();
            _pages = new MainPageProvider(RootNavigation.MenuItems
                .OfType<INavigationViewItem>()
                .Concat(RootNavigation.FooterMenuItems.OfType<INavigationViewItem>())
                .Where(item => item.NavigationCacheMode != NavigationCacheMode.Disabled)
                .Select(item => item.TargetPageType)
                .OfType<Type>());
            RootNavigation.SetPageProviderService(_pages);
            if (App.PerformanceMode)
            {
                // 无 GPU 模式：关 Mica（省 DWM 合成），改不透明底
                WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
                SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
                // 关页面切换过渡（默认 FadeInWithSlide 的下往上滑入，RDP/软渲染下卡）
                RootNavigation.Transition = Wpf.Ui.Animations.Transition.None;
            }
            Loaded += OnMainWindowLoaded;
            ContentRendered += OnFirstContentRendered;
            IsVisibleChanged += OnPreloadVisibilityChanged;
            Closed += OnMainWindowClosed;

            // 按保存偏好上色；系统主题监听在首屏后启用，避开启动渲染竞争。
            SettingsService.ApplySavedTheme();

            try
            {
                // 从程序集资源读取，单文件发布也不依赖外部 ICO 文件。
                var icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                    new Uri("pack://application:,,,/Assets/Icon.ico"));
                _trayIcon = new TrayIconController(WpfApplication.Current, this, icon,
                    Properties.Resources.Tray_Open, Properties.Resources.Tray_Exit);
            }
            catch (Exception ex)
            {
                // 托盘不可用时仍正常关闭，不能把唯一窗口隐藏后无从恢复。
                System.Diagnostics.Debug.WriteLine($"[ExHyperV] Tray initialization failed: {ex.Message}");
            }
        }

        private void OnMainWindowLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnMainWindowLoaded;
            RootNavigation.Navigate(typeof(MainPage));
        }

        private void OnFirstContentRendered(object? sender, EventArgs e)
        {
            ContentRendered -= OnFirstContentRendered;
            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    if (!_closed) SettingsService.EnableSystemThemeWatch(this);
                }), DispatcherPriority.Background);

            // 首屏完成后才预热；虚拟机页优先，保留启动后立即查看列表的准备工作。
            if (App.PerformanceMode) return;
            foreach (var page in new[] { typeof(VirtualMachinesPage), typeof(PCIePage),
                         typeof(HostPage), typeof(SwitchPage) })
                _preloadPages.Enqueue(page);
            QueueNextPreload();
        }

        private void QueueNextPreload()
        {
            if (_closed || !IsVisible || _preloadPages.Count == 0 || _preloadOperation != null
                || Dispatcher.HasShutdownStarted) return;

            // 每次只构造一个页面，再交还调度器；输入和渲染优先于预热。
            _preloadOperation = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                _preloadOperation = null;
                if (_closed || !IsVisible || Dispatcher.HasShutdownStarted) return;
                var pageType = _preloadPages.Dequeue();
                try { _pages.GetPage(pageType); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ExHyperV] Page preload failed ({pageType.Name}): {ex}");
                }
                QueueNextPreload();
            }));
        }

        private void OnPreloadVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible) QueueNextPreload();
            else CancelPendingPreload();
        }

        private void CancelPendingPreload()
        {
            _preloadOperation?.Abort();
            _preloadOperation = null;
        }

        private void OnMainWindowClosed(object? sender, EventArgs e)
        {
            _closed = true;
            CancelPendingPreload();
            _preloadPages.Clear();
            ContentRendered -= OnFirstContentRendered;
            IsVisibleChanged -= OnPreloadVisibilityChanged;
        }
    }
}
