
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Pages.Host;
using ExHyperV.App.UI.Pages.Settings;
using ExHyperV.App.UI.Pages.VirtualMachines;
using ExHyperV.App.UI.Pages.VirtualMachines.Console;
using ExHyperV.App.UI.Shared.Appearance;
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
    private WindowTrayIcon? tray;
    private bool closed;
    private bool initialSizeApplied;
    private bool initialPaneClosed;
    private bool isImmersive;
    private bool paneOpenBeforeImmersive;
    private bool updatingNavigationImmersive;
    private SplitView? rootSplitView;
    private Grid? paneToggleGrid;
    private XamlRoot? titleBarRoot;
    private readonly ThemeSettings themeSettings;

    public MainWindow()
    {
        InitializeComponent();
        Navigation.DisplayModeChanged += OnNavigationDisplayModeChanged;
        Navigation.SizeChanged += OnNavigationSizeChanged;
        UpdateNavigationContentInset();
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        themeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
        themeSettings.Changed += OnSystemThemeChanged;
        Root.RequestedTheme = ((App)Application.Current).Appearance.Settings.Theme;
        SyncNativeTheme();
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
        L10n.LanguageChanged += OnLanguageChanged;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
    }

    private void ApplyNavigationLocalization()
    {
        Root.Language = System.Globalization.CultureInfo.DefaultThreadCurrentUICulture?.Name ?? "en-US";
        NavVirtualMachines.Content = L10n.Get("Nav_VirtualMachines");
        NavHost.Content = L10n.Get("Nav_Host");
        NavSettings.Content = L10n.Get("Nav_Settings");
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        ApplyNavigationLocalization();
        ShellNotice.Hide();
        pages.Clear();
        if (Navigation.SelectedItem is NavigationViewItem { Tag: string route }) ShowPage(route);
    }

    private async Task PrepareLanguageChangeAsync()
    {
        if (cleanupInProgress) throw new OperationCanceledException();
        cleanupInProgress = true;
        Navigation.IsEnabled = false;
        try
        {
            // A pending VM operation can veto disposal. Keep its page reachable in that case.
            foreach (var entry in pages.ToArray())
            {
                if (entry.Value is VirtualMachinesPage vm)
                {
                    await vm.StopAsync();
                    vm.ImmersiveRequested -= SetImmersiveMode;
                    pages.Remove(entry.Key);
                }
                else if (entry.Value is HostPage host)
                {
                    await host.StopAsync();
                    host.ImmersiveRequested -= SetImmersiveMode;
                    pages.Remove(entry.Key);
                }
            }
        }
        finally { Navigation.IsEnabled = true; cleanupInProgress = false; }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs args)
    {
        ((App)Application.Current).Appearance.UpdatePalette(Root.ActualTheme, themeSettings.HighContrast);
        SyncNativeTheme();
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
        SyncNativeTheme();
        UpdateTitleBar();
    }
    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        Root.RequestedTheme = ((App)Application.Current).Appearance.Settings.Theme;
        ((App)Application.Current).Appearance.UpdatePalette(Root.ActualTheme, themeSettings.HighContrast);
        SyncNativeTheme();
    }
    private void OnSystemThemeChanged(ThemeSettings sender, object args) => Root.DispatcherQueue.TryEnqueue(() =>
    {
        ((App)Application.Current).Appearance.UpdatePalette(Root.ActualTheme, themeSettings.HighContrast);
        SyncNativeTheme();
    });

    private void SyncNativeTheme() => NativeTheme.Sync(
        WinRT.Interop.WindowNative.GetWindowHandle(this),
        ((App)Application.Current).Appearance.Settings.Theme,
        themeSettings.HighContrast);
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateTitleBar();
    private bool ContentVisible => AppWindow.IsVisible &&
        AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        UpdateTitleBar();
        if (PageContent.Content is HostPage host) host.SetWindowVisible(ContentVisible);
        else if (PageContent.Content is VirtualMachinesPage vms) vms.SetWindowVisible(ContentVisible);
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
        if (isImmersive)
            EnforceImmersiveNavigation();
        else
            UpdateNavigationContentInset();
    }

    private void OnNavigationSizeChanged(object sender, SizeChangedEventArgs args) => EnforceImmersiveNavigation();

    private void EnsureNavigationParts()
    {
        if (rootSplitView != null && paneToggleGrid != null) return;
        Navigation.ApplyTemplate();
        if (rootSplitView == null)
        {
            rootSplitView = FindDescendantByName<SplitView>(Navigation, "RootSplitView");
            if (rootSplitView != null)
                rootSplitView.RegisterPropertyChangedCallback(SplitView.DisplayModeProperty, (_, _) => EnforceImmersiveNavigation());
        }
        if (paneToggleGrid == null)
        {
            paneToggleGrid = FindDescendantByName<Grid>(Navigation, "PaneToggleButtonGrid");
            if (paneToggleGrid != null)
                paneToggleGrid.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => EnforceImmersiveNavigation());
        }
    }

    private static T? FindDescendantByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T target && string.Equals(target.Name, name, StringComparison.Ordinal))
                return target;
            var nested = FindDescendantByName<T>(child, name);
            if (nested != null) return nested;
        }
        return null;
    }

    private void EnforceImmersiveNavigation()
    {
        if (!isImmersive || updatingNavigationImmersive) return;
        ApplyNavigationImmersiveVisuals(immersive: true);
    }

    private void UpdateNavigationContentInset()
    {
        // Minimal mode puts the toggle above the content instead of reserving a left rail.
        PageContent.Margin = new Thickness(0, !isImmersive && Navigation.IsPaneVisible && Navigation.DisplayMode == NavigationViewDisplayMode.Minimal ? 48 : 0, 0, 0);
    }

    internal void SetImmersiveMode(bool immersive)
    {
        if (isImmersive == immersive) return;
        EnsureNavigationParts();
        if (immersive)
        {
            paneOpenBeforeImmersive = Navigation.IsPaneOpen;
            isImmersive = true;
            ApplyNavigationImmersiveVisuals(immersive: true);
            return;
        }

        isImmersive = false;
        ApplyNavigationImmersiveVisuals(immersive: false);
    }

    private void ApplyNavigationImmersiveVisuals(bool immersive)
    {
        updatingNavigationImmersive = true;
        try
        {
            if (immersive)
            {
                Navigation.IsPaneOpen = false;
                if (paneToggleGrid != null)
                    paneToggleGrid.Visibility = Visibility.Collapsed;
                if (rootSplitView != null)
                {
                    rootSplitView.IsPaneOpen = false;
                    rootSplitView.CompactPaneLength = 0;
                    rootSplitView.DisplayMode = SplitViewDisplayMode.Overlay;
                }
            }
            else
            {
                if (paneToggleGrid != null)
                    paneToggleGrid.ClearValue(UIElement.VisibilityProperty);
                if (rootSplitView != null)
                {
                    rootSplitView.ClearValue(SplitView.CompactPaneLengthProperty);
                    rootSplitView.DisplayMode = Navigation.DisplayMode switch
                    {
                        NavigationViewDisplayMode.Minimal => SplitViewDisplayMode.Overlay,
                        NavigationViewDisplayMode.Compact => SplitViewDisplayMode.CompactOverlay,
                        _ => SplitViewDisplayMode.CompactInline
                    };
                }
                Navigation.IsPaneOpen = paneOpenBeforeImmersive;
            }
            UpdateNavigationContentInset();
        }
        finally
        {
            updatingNavigationImmersive = false;
        }
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

    private async void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args) => await UiEvent.RunAsync(() =>
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string route }) ShowPage(route);
        return Task.CompletedTask;
    });

    private void ShowPage(string route)
    {
        if (!pages.TryGetValue(route, out var page))
        {
            page = route switch
            {
                "virtual-machines" => CreateVirtualMachinesPage(),
                "settings" => new SettingsPage
                {
                    PrepareLanguageChange = PrepareLanguageChangeAsync,
                    ExtensionTarget = () => pages.GetValueOrDefault("virtual-machines") is VirtualMachinesPage { ViewModel.Selected: { } vm }
                        ? (vm.Id, vm.Name) : (Guid.Empty, null)
                },
                "host" => CreateHostPage(),
                _ => throw new InvalidOperationException($"Unknown page: {route}")
            };
            pages.Add(route, page);
        }
        SetImmersiveMode(false);
        if (page is HostPage host) host.SetWindowVisible(ContentVisible);
        else if (page is VirtualMachinesPage vms) vms.SetWindowVisible(ContentVisible);
        Diagnostics.AppDiagnostics.Write("Navigation", "PageSelected", $"page={page.GetType().Name}");
        PageContent.Content = page;
    }

    internal bool TryHideToTray()
    {
        if (tray != null) return true;
        tray = new WindowTrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this),
            () => DispatcherQueue.TryEnqueue(RestoreWindow),
            () => DispatcherQueue.TryEnqueue(async () => await UiEvent.RunAsync(async () =>
            {
                if (closed) return;
                RestoreWindow();
                await CloseAfterCleanupAsync();
            })),
            SyncNativeTheme);
        if (!tray.Show())
        {
            tray.Dispose();
            tray = null;
            ShellNotice.Show(L10n.Get("Settings_TrayUnavailable"));
            return false;
        }
        AppWindow.Hide();
        return true;
    }

    private void RestoreWindow()
    {
        tray?.Dispose();
        tray = null;
        AppWindow.Show();
        Activate();
    }

    internal async void CheckForStartupUpdate()
    {
        try
        {
            var update = await AppUpdates.CheckAsync();
            if (update == null || closed || !((App)Application.Current).Appearance.Settings.AutoCheckUpdate) return;
            var open = new Button { Content = L10n.Get("Settings_ViewRelease") };
            open.Click += async (_, _) => await UiEvent.RunAsync(() => AppUpdates.OpenLinkAsync(AppUpdates.ReleasePage),
                message => ShellNotice.Show(message));
            ShellNotice.Show(L10n.Format("Settings_UpdateFound", update), InfoBarSeverity.Informational, open);
        }
        catch (Exception error) { Diagnostics.AppDiagnostics.Failure("Updates", "StartupCheckFailed", error); }
    }

    private VirtualMachinesPage CreateVirtualMachinesPage()
    {
        var page = new VirtualMachinesPage();
        page.ImmersiveRequested += SetImmersiveMode;
        return page;
    }

    private HostPage CreateHostPage()
    {
        var page = new HostPage();
        page.ImmersiveRequested += SetImmersiveMode;
        return page;
    }

    private async void OnClosed(object sender, WindowEventArgs args) => await UiEvent.RunAsync(async () =>
    {
        closed = true;
        tray?.Dispose();
        tray = null;
        L10n.LanguageChanged -= OnLanguageChanged;
        try
        {
            // Window.Close() can bypass AppWindow.Closing; normal title-bar closure already stopped active pages above.
            if (!closeAfterCleanup)
            {
                await ConsoleWindow.CloseAllAsync();
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
            AppWindow.Closing -= OnClosing;
            Activated -= OnActivated;
            Navigation.DisplayModeChanged -= OnNavigationDisplayModeChanged;
        Navigation.SizeChanged -= OnNavigationSizeChanged;
            foreach (var page in pages.Values.OfType<VirtualMachinesPage>()) page.ViewModel.Cancel();
            PageContent.Content = null;
            pages.Clear();
        }
        finally
        {
            ((App)Application.Current).Shutdown();
        }
    });

    private async void OnClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args) => await UiEvent.RunAsync(async () =>
    {
        if (closed || closeAfterCleanup) return;
        args.Cancel = true;
        if (cleanupInProgress) return;
        if (((App)Application.Current).Appearance.Settings.CloseBehavior == WindowCloseBehavior.Tray)
        {
            TryHideToTray();
            return;
        }
        await CloseAfterCleanupAsync();
    });

    private async Task CloseAfterCleanupAsync()
    {
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
            await ConsoleWindow.CloseAllAsync();
            closeAfterCleanup = true;
            AppWindow.Closing -= OnClosing;
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
    }
}
