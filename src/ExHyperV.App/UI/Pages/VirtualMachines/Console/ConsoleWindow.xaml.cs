using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Feedback;
using Microsoft.UI.Input;
using Microsoft.UI.System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

public sealed partial class ConsoleWindow : Window
{
    // All registry and selection changes occur on the shell dispatcher.
    private static readonly Dictionary<Guid, ConsoleWindow> WindowsByVm = [];
    private static bool exiting;
    private VmListItem vm;
    private readonly ThemeSettings theme;
    private readonly OverlappedPresenter windowed;
    private readonly DispatcherTimer capsuleTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private Task switchWork = Task.CompletedTask;
    private Task? closeWork;
    private volatile bool closing;
    private bool active, fullscreen, pinned = true, manualWindowSize, verifyFit;
    private double layoutScale;
    private RectInt32 interactiveRect;
    private readonly List<SizeInt32> pendingAutoResizes = [];
    private AppWindowPresenterKind lastPresenterKind = AppWindowPresenterKind.Overlapped;
    private OverlappedPresenterState lastPresenterState = OverlappedPresenterState.Restored;
    private static AppAppearance Appearance => ((App)Application.Current).Appearance;

    private ConsoleWindow(VmListItem target)
    {
        vm = target;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragSpace);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        windowed = (OverlappedPresenter)AppWindow.Presenter;
        AppWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        theme = ThemeSettings.CreateForWindowId(AppWindow.Id);
        theme.Changed += OnSystemTheme;
        Root.ActualThemeChanged += OnActualTheme;
        Appearance.Changed += OnAppearance;
        L10n.LanguageChanged += OnLanguage;
        Root.RequestedTheme = Appearance.Settings.Theme;
        Root.Loaded += OnLoaded;
        TitleStrip.LayoutUpdated += OnTitleLayout;
        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        Activated += OnActivated;
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved), true);
        Surface.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnWheel), true);
        GpuScreen.CompositionScaleChanged += OnGpuScreenScaleChanged;
        capsuleTimer.Tick += (_, _) => { capsuleTimer.Stop(); Capsule.Visibility = Visibility.Collapsed; };
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); Notice.Visibility = Visibility.Collapsed; };
        Localize();
        Render();
    }

    internal static void Open(VmListItem target)
    {
        if (exiting) return;
        if (WindowsByVm.TryGetValue(target.Id, out var existing))
        {
            if (existing.closing || existing.vm.Id != target.Id)
            {
                _ = UiEvent.RunAsync(async () =>
                {
                    await (existing.closeWork ?? existing.switchWork);
                    Open(target);
                });
            }
            else
            {
                if (existing.AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p)
                    p.Restore();
                existing.Activate();
            }
            return;
        }
        var window = new ConsoleWindow(target);
        WindowsByVm.Add(target.Id, window);
        window.Activate();
        window.BeginMonitoring();
    }

    private async Task SwitchAsync(VmListItem target)
    {
        if (closing || !switchWork.IsCompleted) return;
        if (WindowsByVm.ContainsKey(target.Id)) { Open(target); return; }
        ++powerGeneration;
        busyPower = false;
        activePowerAction = null;
        confirmation?.Hide();
        var previous = vm.Id;
        WindowsByVm.Add(target.Id, this);
        vm = target;
        Switcher.IsEnabled = false;
        SetView(View.Connecting);
        await StopMonitoringAsync();
        WindowsByVm.Remove(previous);
        if (closing) return;
        Switcher.IsEnabled = true;
        BeginMonitoring();
    }

    internal static Task CloseAllAsync()
    {
        exiting = true;
        return Task.WhenAll(WindowsByVm.Values.Distinct().ToArray().Select(w => w.RequestClose()));
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        _ = RequestClose();
    }

    private Task RequestClose() => closeWork ??= CloseConsoleAsync();

    private async Task CloseConsoleAsync()
    {
        closing = true;
        ++powerGeneration;
        busyPower = false;
        activePowerAction = null;
        confirmation?.Hide();
        Root.IsHitTestVisible = false;
        pollLifetime?.Cancel();
        await switchWork;
        await StopMonitoringAsync();
        AppWindow.Closing -= OnClosing;
        Close();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        WindowsByVm.Remove(vm.Id);
        Appearance.Changed -= OnAppearance;
        L10n.LanguageChanged -= OnLanguage;
        theme.Changed -= OnSystemTheme;
        AppWindow.Changed -= OnAppWindowChanged;
        TitleStrip.LayoutUpdated -= OnTitleLayout;
        Root.ActualThemeChanged -= OnActualTheme;
        if (Root.XamlRoot is { } root) root.Changed -= OnRootChanged;
        GpuScreen.CompositionScaleChanged -= OnGpuScreenScaleChanged;
        DisposeDisplay();
        capsuleTimer.Stop();
        noticeTimer.Stop();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Root.Loaded -= OnLoaded;
        Root.XamlRoot.Changed += OnRootChanged;
        AppWindow.Changed += OnAppWindowChanged;
        UpdateLayoutMetrics();
        verifyFit = true;
        FitWindowToAspect();
        OnActualTheme(Root, EventArgs.Empty);
    }

    private void OnRootChanged(XamlRoot root, XamlRootChangedEventArgs args) => UpdateLayoutMetrics();
    private void OnTitleLayout(object? sender, object args) => UpdateLayoutMetrics();

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        var kind = sender.Presenter.Kind;
        var state = windowed.State;
        bool stateChanged = kind != lastPresenterKind || state != lastPresenterState;
        lastPresenterKind = kind;
        lastPresenterState = state;
        if (!args.DidSizeChange || closing || fullscreen || state != OverlappedPresenterState.Restored || stateChanged)
        {
            if (args.DidSizeChange) pendingAutoResizes.Clear();
            return;
        }
        int matched = pendingAutoResizes.FindIndex(expected =>
            Math.Abs(sender.Size.Width - expected.Width) <= 2 &&
            Math.Abs(sender.Size.Height - expected.Height) <= 2);
        if (matched >= 0)
        {
            pendingAutoResizes.RemoveRange(0, matched + 1);
            return;
        }
        pendingAutoResizes.Clear();
        manualWindowSize = true;
    }

    private void FitWindowToAspect(int? frameWidth = null, int? frameHeight = null)
    {
        if (closing || fullscreen || (!IsEnhanced && zoom != 0) || windowed.State != OverlappedPresenterState.Restored ||
            Root.XamlRoot is not { } root)
            return;

        (int w, int h) = frameWidth is > 0 && frameHeight is > 0
            ? (frameWidth.Value, frameHeight.Value)
            : liveSize is { } live
                ? live
                : TargetEnhanced ? requestedSize : (1024, 768);
        if (w <= 0 || h <= 0) return;

        double scale = layoutScale > 0 ? layoutScale : root.RasterizationScale;
        double titleDip = TitleStrip.ActualHeight > 0 ? TitleStrip.ActualHeight : 40;

        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var outer = AppWindow.Size;
        int frameW = Viewport.ActualWidth > 0 && outer.Width > 0
            ? Math.Max(0, outer.Width - (int)Math.Round(Viewport.ActualWidth * scale))
            : (int)Math.Round(16 * scale);
        int frameAndTitleH = Viewport.ActualHeight > 0 && outer.Height > 0
            ? Math.Max(0, outer.Height - (int)Math.Round(Viewport.ActualHeight * scale))
            : (int)Math.Round((titleDip + 8) * scale);

        int minOuterW = windowed.PreferredMinimumWidth ?? (int)Math.Ceiling(760 * scale);
        int minOuterH = windowed.PreferredMinimumHeight ?? (int)Math.Ceiling(440 * scale);
        int minViewW = Math.Max(320, minOuterW - frameW);
        int maxViewW = Math.Max(minViewW, work.Width - frameW);
        int minViewH = Math.Max(200, minOuterH - frameAndTitleH);
        int maxViewH = Math.Max(minViewH, work.Height - frameAndTitleH);

        int viewW = Math.Clamp((int)Math.Round(1024 * scale), minViewW, maxViewW);
        int viewH = (int)Math.Round(viewW * (double)h / w);
        if (viewH > maxViewH)
        {
            viewH = maxViewH;
            viewW = Math.Clamp((int)Math.Round(viewH * (double)w / h), minViewW, maxViewW);
            viewH = Math.Clamp((int)Math.Round(viewW * (double)h / w), minViewH, maxViewH);
        }
        else if (viewH < minViewH)
        {
            viewH = minViewH;
            viewW = Math.Clamp((int)Math.Round(viewH * (double)w / h), minViewW, maxViewW);
        }

        int targetOuterW = viewW + frameW;
        int targetOuterH = viewH + frameAndTitleH;
        if (outer.Width == targetOuterW && outer.Height == targetOuterH) return;

        pendingAutoResizes.Add(new SizeInt32(targetOuterW, targetOuterH));
        AppWindow.Resize(new SizeInt32(targetOuterW, targetOuterH));

        var updated = AppWindow.Size;
        var pos = AppWindow.Position;
        int nextX = Math.Clamp(pos.X, work.X, Math.Max(work.X, work.X + work.Width - updated.Width));
        int nextY = Math.Clamp(pos.Y, work.Y, Math.Max(work.Y, work.Y + work.Height - updated.Height));
        if (nextX != pos.X || nextY != pos.Y) AppWindow.Move(new PointInt32(nextX, nextY));
    }

    private void UpdateLayoutMetrics()
    {
        if (Root.XamlRoot is not { } root || closing) return;
        double scale = root.RasterizationScale;
        if (scale != layoutScale)
        {
            layoutScale = scale;
            windowed.PreferredMinimumWidth = (int)Math.Ceiling(760 * scale);
            windowed.PreferredMinimumHeight = (int)Math.Ceiling(440 * scale);
            SizeSurface();
        }
        double inset = Math.Max(0, AppWindow.TitleBar.RightInset / scale);
        if (CaptionInset.Width.Value != inset) CaptionInset.Width = new GridLength(inset);
        double limit = Math.Clamp(Root.ActualWidth - inset - Actions.ActualWidth - 44, 96, 248);
        if (Switcher.MaxWidth != limit) Switcher.MaxWidth = limit;
        var end = Actions.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(Actions.ActualWidth, 0));
        RectInt32 rect = fullscreen ? default : new(0, 0, (int)Math.Ceiling(end.X * scale), (int)Math.Ceiling(TitleStrip.ActualHeight * scale));
        if (!rect.Equals(interactiveRect))
        {
            interactiveRect = rect;
            var nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
            nonClient.SetRegionRects(NonClientRegionKind.Passthrough, rect.Width > 0 && rect.Height > 0 ? [rect] : []);
        }
    }

    private void OnAppearance(object? sender, EventArgs e)
    {
        Root.RequestedTheme = Appearance.Settings.Theme;
        Render();
    }

    private void OnSystemTheme(ThemeSettings sender, object args) =>
        DispatcherQueue.TryEnqueue(() => { if (!closing) OnActualTheme(Root, args); });

    private void OnActualTheme(FrameworkElement sender, object args) =>
        Appearance.UpdatePalette(Root.ActualTheme, theme.HighContrast);

    private void OnLanguage(object? sender, EventArgs e) { Localize(); Render(); }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        active = args.WindowActivationState != WindowActivationState.Deactivated;
        UpdateCursor();
        if (!active) ReleaseInput();
        else
        {
            clipboard?.Activated();
            if (Surface.FocusState != FocusState.Unfocused)
            {
                keyboardCaptured = Input != null;
                SyncLocks();
            }
        }
    }

    private void OnFullscreen(object sender, RoutedEventArgs args) => SetFullscreen(!fullscreen);

    private void SetFullscreen(bool value)
    {
        ReleaseInput();
        fullscreen = value;
        if (value) AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        else AppWindow.SetPresenter(windowed);
        TitleStrip.Visibility = ShowWhen(!value);
        Capsule.Visibility = ShowWhen(value);
        if (!value && (IsEnhanced || zoom == 0) && !manualWindowSize) FitWindowToAspect();
        if (value) Pin.Focus(FocusState.Programmatic);
        else Fullscreen.Focus(FocusState.Programmatic);
        capsuleTimer.Stop();
        if (value && !pinned) capsuleTimer.Start();
        Render();
    }

    private void OnPin(object sender, RoutedEventArgs args)
    {
        pinned = !pinned;
        capsuleTimer.Stop();
        if (!pinned) capsuleTimer.Start();
        Render();
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!fullscreen || pinned) return;
        double y = e.GetCurrentPoint(Root).Position.Y;
        if (y <= (Capsule.Visibility == Visibility.Visible ? 44 : 8))
        {
            capsuleTimer.Stop();
            Capsule.Visibility = Visibility.Visible;
        }
        else if (Capsule.Visibility == Visibility.Visible && !capsuleTimer.IsEnabled) capsuleTimer.Start();
    }

    private void Notify(string text)
    {
        if (closing) return;
        NoticeText.Text = text;
        Notice.Margin = new Thickness(12, fullscreen ? 44 : 12, 12, 0);
        Notice.Visibility = Visibility.Visible;
        noticeTimer.Stop();
        noticeTimer.Start();
    }

    private static Visibility ShowWhen(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
