
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Pages.Host;
using ExHyperV.App.UI.Pages.Settings;
using ExHyperV.App.UI.Pages.VirtualMachines;
using ExHyperV.App.UI.Pages.Web;
using ExHyperV.App.UI.Shared.Feedback;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;

namespace ExHyperV.App.UI.Shell;

public sealed partial class MainWindow : Window
{
    private readonly Dictionary<string, FrameworkElement> pages = new();
    private bool closeAfterCleanup;
    private bool cleanupInProgress;
    private bool initialSizeApplied;
    private bool initialPaneClosed;
    private XamlRoot? titleBarRoot;
    private readonly ThemeSettings themeSettings;
    private bool restoringNavigation;
    private int navigationVersion;
    private object? activeNavigationItem;

    public MainWindow()
    {
        InitializeComponent();
        Navigation.DisplayModeChanged += OnNavigationDisplayModeChanged;
        UpdateNavigationContentInset();
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
        ApplyNavigationLocalization();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
    }

    private void ApplyNavigationLocalization()
    {
        NavVirtualMachines.Content = L10n.Get("Nav_VirtualMachines");
        NavHost.Content = L10n.Get("Nav_Host");
        NavWeb.Content = L10n.Get("Nav_Web");
        NavSettings.Content = L10n.Get("Nav_Settings");
        if (pages.TryGetValue("web", out var webPage) && webPage is WebPage web) web.ApplyLocalization();
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
            int width = Math.Min((int)Math.Round(1140 * scale), Math.Max(320, area.Width - (int)(32 * scale)));
            int height = Math.Min((int)Math.Round(800 * scale), Math.Max(240, area.Height - (int)(32 * scale)));
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
                area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
            Navigation.IsPaneOpen = false;
            Root.DispatcherQueue.TryEnqueue(() => Navigation.IsPaneOpen = false);
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
        ApplyNavigationLocalization();
    }
    private void OnSystemThemeChanged(ThemeSettings sender, object args) => Root.DispatcherQueue.TryEnqueue(() =>
        ((App)Application.Current).Appearance.UpdatePalette(Root.ActualTheme, themeSettings.HighContrast));
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateTitleBar();
    private bool ContentVisible => AppWindow.IsVisible &&
        AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        UpdateTitleBar();
        if (PageContent.Content is HostPage host) host.SetWindowVisible(ContentVisible);
    }
    private void OnActivated(object sender, WindowActivatedEventArgs args)
        => TitleContent.Opacity = args.WindowActivationState == WindowActivationState.Deactivated ? .55 : 1;

    private void OnNavigationDisplayModeChanged(NavigationView sender, NavigationViewDisplayModeChangedEventArgs args)
    {
        if (!initialPaneClosed && args.DisplayMode == NavigationViewDisplayMode.Expanded)
        {
            initialPaneClosed = true;
            sender.IsPaneOpen = false;
        }
        UpdateNavigationContentInset();
    }

    private void UpdateNavigationContentInset()
    {
        // Minimal mode puts the toggle above the content instead of reserving a left rail.
        PageContent.Margin = new Thickness(0, Navigation.IsPaneVisible && Navigation.DisplayMode == NavigationViewDisplayMode.Minimal ? 48 : 0, 0, 0);
    }

    internal void SetImmersiveMode(bool immersive)
    {
        Navigation.IsPaneVisible = !immersive;
        if (immersive) Navigation.IsPaneOpen = false;
        UpdateNavigationContentInset();
    }

    private void UpdateTitleBar()
    {
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        double leftInset = AppWindow.TitleBar.LeftInset / scale;
        double rightInset = AppWindow.TitleBar.RightInset / scale;
        // Minimize/restore can briefly report negative caption insets. Preserve
        // the last valid layout until a subsequent window/DPI change refreshes it.
        // GridLength rejects negative, NaN and infinite values; letting that
        // exception escape AppWindow.Changed terminates the process.
        if (double.IsFinite(scale) && scale > 0 &&
            double.IsFinite(leftInset) && leftInset >= 0 &&
            double.IsFinite(rightInset) && rightInset >= 0)
        {
            TitleLeftInset.Width = new GridLength(leftInset);
            TitleRightInset.Width = new GridLength(rightInset);
        }
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

    private async void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) => await UiEvent.RunAsync(async () =>
    {
        if (restoringNavigation) return;
        if ((args.SelectedItem as NavigationViewItem)?.Tag is not string route) return;
        int version = ++navigationVersion;
        if (PageContent.Content is VirtualMachinesPage current && !await current.PrepareToLeaveAsync())
        {
            if (version == navigationVersion)
            {
                restoringNavigation = true;
                try { Navigation.SelectedItem = activeNavigationItem; }
                finally { restoringNavigation = false; }
            }
            return;
        }
        if (version != navigationVersion) return;
        if (!pages.TryGetValue(route, out var page))
        {
            page = route switch
            {
                "virtual-machines" => CreateVirtualMachinesPage(),
                "settings" => new SettingsPage(),
                "host" => new HostPage(),
                "web" => new WebPage(),
                _ => throw new InvalidOperationException($"Unknown page: {route}")
            };
            pages.Add(route, page);
        }
        if (route != "virtual-machines") SetImmersiveMode(false);
        if (page is HostPage host) host.SetWindowVisible(ContentVisible);
        Diagnostics.AppDiagnostics.Write("Navigation", "PageSelected", $"page={page.GetType().Name}");
        PageContent.Content = page;
        activeNavigationItem = args.SelectedItem;
    });

    private VirtualMachinesPage CreateVirtualMachinesPage()
    {
        var page = new VirtualMachinesPage();
        page.ImmersiveRequested += SetImmersiveMode;
        return page;
    }

    private async void OnClosed(object sender, WindowEventArgs args) => await UiEvent.RunAsync(async () =>
    {
        // Window.Close() can bypass AppWindow.Closing; normal title-bar closure already stopped active pages above.
        if (!closeAfterCleanup)
        {
            foreach (var page in pages.Values.OfType<VirtualMachinesPage>().ToArray())
            {
                try { await page.StopAsync(); }
                catch (Exception error) { System.Diagnostics.Trace.TraceError("VM page cleanup failed: {0}", error); }
            }
            foreach (var page in pages.Values.OfType<HostPage>().ToArray())
            {
                try { await page.StopAsync(); }
                catch (Exception error) { System.Diagnostics.Trace.TraceError("Host page cleanup failed: {0}", error); }
            }
        }
        ((App)Application.Current).Appearance.Changed -= OnAppearanceChanged;
        themeSettings.Changed -= OnSystemThemeChanged;
        if (titleBarRoot != null) titleBarRoot.Changed -= OnXamlRootChanged;
        Root.Loaded -= OnRootLoaded;
        Root.ActualThemeChanged -= OnThemeChanged;
        AppWindow.Changed -= OnAppWindowChanged;
        Activated -= OnActivated;
        Navigation.DisplayModeChanged -= OnNavigationDisplayModeChanged;
        foreach (var page in pages.Values.OfType<VirtualMachinesPage>()) page.ViewModel.Cancel();
        pages.Clear();
    });

    private async void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args) => await UiEvent.RunAsync(async () =>
    {
        if (closeAfterCleanup) return;
        args.Cancel = true;
        if (cleanupInProgress) return;
        var active = pages.Values.OfType<VirtualMachinesPage>().ToArray();
        var hostPages = pages.Values.OfType<HostPage>().ToArray();
        cleanupInProgress = true;
        Root.IsHitTestVisible = false;
        Navigation.IsEnabled = false;
        try
        {
            await Task.WhenAll(active.Select(page => page.StopAsync()));
            await Task.WhenAll(hostPages.Select(page => page.StopAsync()));
            closeAfterCleanup = true;
            if (!Root.DispatcherQueue.TryEnqueue(Close)) Close();
        }
        catch (OperationCanceledException)
        {
            closeAfterCleanup = false;
            Root.IsHitTestVisible = true;
            Navigation.IsEnabled = true;
            cleanupInProgress = false;
        }
        catch (Exception exception)
        {
            closeAfterCleanup = false;
            Root.IsHitTestVisible = true;
            Navigation.IsEnabled = true;
            cleanupInProgress = false;
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = L10n.Get("Main_CloseFailedTitle"), Content = CoreMessageResolver.ResolveException(exception), CloseButtonText = L10n.Get("Main_CloseFailedButton") };
            await dialog.ShowAsync();
        }
    });
}
