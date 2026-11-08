using ExHyperV.WinUI3.UI.Pages.Host;
using ExHyperV.WinUI3.UI.Pages.Settings;
using ExHyperV.WinUI3.UI.Pages.VirtualMachines;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Microsoft.UI.System;
using ExHyperV.WinUI3.Composition;

namespace ExHyperV.WinUI3.UI.Shell;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> pages = new();
    private bool closeAfterCleanup;
    private bool cleanupInProgress;
    private bool initialSizeApplied;
    private XamlRoot? titleBarRoot;
    private readonly ThemeSettings themeSettings;
    private bool restoringNavigation;
    private int navigationVersion;
    private object? activeNavigationItem;
    private readonly Dictionary<string, ProductPageDefinition> productPages = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = TitleText.Text = ((App)Application.Current).Product.DisplayName;
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        themeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
        themeSettings.Changed += OnSystemThemeChanged;
        Root.RequestedTheme = ((App)Application.Current).Appearance.Settings.Theme;
        ((App)Application.Current).Appearance.Changed += OnAppearanceChanged;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        Root.Loaded += OnRootLoaded;
        Root.ActualThemeChanged += OnThemeChanged;
        AppWindow.Changed += OnAppWindowChanged;
        Activated += OnActivated;
        foreach (var page in ((App)Application.Current).Product.Pages)
        {
            if (string.IsNullOrWhiteSpace(page.Id) || string.IsNullOrWhiteSpace(page.Title) || page.Create is null)
                throw new ArgumentException("产品页面需要有效的标识、名称和创建函数。");
            var route = "product." + page.Id;
            productPages.Add(route, page);
            Navigation.MenuItems.Add(new NavigationViewItem
            {
                Content = page.Title, Tag = route,
                Icon = new FontIcon { Glyph = page.Glyph, FontSize = 18 }
            });
        }
        Navigation.SelectedItem = Navigation.MenuItems[0];
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        ((App)Application.Current).Appearance.UpdatePalette(Root.ActualTheme, themeSettings.HighContrast);
        if (titleBarRoot == null && Root.XamlRoot != null)
        {
            titleBarRoot = Root.XamlRoot;
            titleBarRoot.Changed += OnXamlRootChanged;
        }
        // AppWindow uses physical pixels; the WPF reference window is 1100 × 800 DIPs.
        if (!initialSizeApplied)
        {
            initialSizeApplied = true;
            double scale = Root.XamlRoot?.RasterizationScale ?? 1;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int width = Math.Min((int)Math.Round(1100 * scale), Math.Max(320, area.Width - (int)(32 * scale)));
            int height = Math.Min((int)Math.Round(800 * scale), Math.Max(240, area.Height - (int)(32 * scale)));
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        }
        UpdateTitleBar();
    }

    private void OnThemeChanged(FrameworkElement sender, object args)
    {
        ((App)Application.Current).Appearance.UpdatePalette(Root.ActualTheme, themeSettings.HighContrast);
        UpdateTitleBar();
    }
    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        Root.RequestedTheme = ((App)Application.Current).Appearance.Settings.Theme;
        ((App)Application.Current).Appearance.UpdatePalette(Root.ActualTheme, themeSettings.HighContrast);
    }
    private void OnSystemThemeChanged(ThemeSettings sender, object args) => Root.DispatcherQueue.TryEnqueue(() =>
        ((App)Application.Current).Appearance.UpdatePalette(Root.ActualTheme, themeSettings.HighContrast));
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateTitleBar();
    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args) => UpdateTitleBar();
    private void OnActivated(object sender, WindowActivatedEventArgs args)
        => TitleContent.Opacity = args.WindowActivationState == WindowActivationState.Deactivated ? .55 : 1;

    private void UpdateTitleBar()
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        TitleLeftInset.Width = new GridLength(AppWindow.TitleBar.LeftInset / scale);
        TitleRightInset.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
        if (TitleText.Foreground is SolidColorBrush foreground)
        {
            AppWindow.TitleBar.ButtonForegroundColor = foreground.Color;
            AppWindow.TitleBar.ButtonHoverForegroundColor = foreground.Color;
            AppWindow.TitleBar.ButtonPressedForegroundColor = foreground.Color;
            byte inactive = dark ? (byte)153 : (byte)121;
            AppWindow.TitleBar.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(255, inactive, inactive, inactive);
        }
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, dark ? (byte)55 : (byte)230, dark ? (byte)55 : (byte)230, dark ? (byte)55 : (byte)230);
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, dark ? (byte)45 : (byte)215, dark ? (byte)45 : (byte)215, dark ? (byte)45 : (byte)215);
    }

    private async void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (restoringNavigation) return;
        if ((args.SelectedItem as NavigationViewItem)?.Tag is not string route) return;
        int version = ++navigationVersion;
        if (PageContent.Content is VirtualMachinesPage current && !await current.PrepareToLeaveAsync())
        {
            if (version == navigationVersion)
            {
                restoringNavigation = true;
                Navigation.SelectedItem = activeNavigationItem;
                restoringNavigation = false;
            }
            return;
        }
        if (version != navigationVersion) return;
        if (!pages.TryGetValue(route, out var page))
        {
            page = route switch
            {
                "virtual-machines" => new VirtualMachinesPage(),
                "settings" => new SettingsPage(),
                "host" => new HostPage(),
                _ => productPages.TryGetValue(route, out var definition)
                    ? definition.Create(new ProductPageContext(AppWindow.Id))
                    : throw new InvalidOperationException($"Unknown page: {route}")
            };
            pages.Add(route, page);
        }
        PageContent.Content = page;
        activeNavigationItem = args.SelectedItem;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // Window.Close() can bypass AppWindow.Closing. The native product windows
        // still need releasing; normal title-bar closure already did this above.
        if (!closeAfterCleanup)
        {
            foreach (var page in pages.Values.OfType<IProductPageLifetime>())
            {
                try { page.Dispose(); }
                catch (Exception error) { System.Diagnostics.Trace.TraceError("Product page cleanup failed: {0}", error); }
            }
            try { ((App)Application.Current).Product.DesktopWindows?.CloseAll(); }
            catch (Exception error) { System.Diagnostics.Trace.TraceError("Product desktop cleanup failed after shell closure: {0}", error); }
        }
        ((App)Application.Current).Appearance.Changed -= OnAppearanceChanged;
        themeSettings.Changed -= OnSystemThemeChanged;
        if (titleBarRoot != null) titleBarRoot.Changed -= OnXamlRootChanged;
        Root.Loaded -= OnRootLoaded;
        Root.ActualThemeChanged -= OnThemeChanged;
        AppWindow.Changed -= OnAppWindowChanged;
        Activated -= OnActivated;
        foreach (var page in pages.Values.OfType<VirtualMachinesPage>()) page.ViewModel.Cancel();
        pages.Clear();
    }

    private async void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (closeAfterCleanup) return;
        if (cleanupInProgress) { args.Cancel = true; return; }
        var active = pages.Values.OfType<VirtualMachinesPage>().ToArray();
        args.Cancel = true;
        cleanupInProgress = true;
        Root.IsHitTestVisible = false;
        Navigation.IsEnabled = false;
        try
        {
            await Task.WhenAll(active.Select(page => page.StopAsync()));
            foreach (var page in pages.Values.OfType<IProductPageLifetime>()) await page.StopAsync();
            ((App)Application.Current).Product.DesktopWindows?.CloseAll();
            foreach (var page in pages.Values.OfType<IProductPageLifetime>()) page.Dispose();
            closeAfterCleanup = true;
            Close();
        }
        catch (Exception exception)
        {
            Root.IsHitTestVisible = true;
            Navigation.IsEnabled = true;
            cleanupInProgress = false;
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "尚未安全结束", Content = exception.Message, CloseButtonText = "返回处理" };
            await dialog.ShowAsync();
        }
    }
}
