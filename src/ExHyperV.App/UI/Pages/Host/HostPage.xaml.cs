using System.Diagnostics;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ExHyperV.App.UI.Pages.Host;

public sealed partial class HostPage : Page, IDashboardFeedbackHost
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private readonly HostTelemetrySampler sampler = new();
    private readonly HostOverviewView overviewView = new();
    private readonly HostStatusView statusView = new();
    private HostSettingsView? settingsView;
    private HostSwitchesView? switchesView;

    private readonly DispatcherTimer successToastTimer = new() { Interval = TimeSpan.FromMilliseconds(2600) };

    private HostInventorySnapshot inventory = HostTelemetrySampler.CreateInitialSnapshot();
    private CancellationTokenSource? processorCts;
    private CancellationTokenSource? deviceCts;
    private CancellationTokenSource? inventoryCts;
    private Task processorTask = Task.CompletedTask;
    private Task deviceTask = Task.CompletedTask;
    private Task inventoryTask = Task.CompletedTask;
    private bool pageActive;
    private bool windowVisible = true;
    private bool stopping;
    private int forceNextVmRefresh;
    private readonly HashSet<Task> pendingOperations = [];
    private (string? Text, InfoBarSeverity Severity)? deferredNotice;
    private int activeTabIndex;
    private bool appearanceHooked;
    private bool isImmersive;

    internal event Action<bool>? ImmersiveRequested;

    internal void SetWindowVisible(bool visible)
    {
        if (windowVisible == visible) return;
        windowVisible = visible;
        EnsureTelemetryState();
    }

    internal async Task RunHostOperationAsync(Func<Task> action)
    {
        if (!pageActive || stopping) return;
        var task = action();
        pendingOperations.Add(task);
        try { await task; }
        catch (Exception ex) { ShowSettingsNotice(CoreMessageResolver.ResolveException(ex)); }
        finally { pendingOperations.Remove(task); }
    }

    public HostPage()
    {
        InitializeComponent();

        PageTabs.SetItems(
            L10n.Get("Host_Tab_Overview"),
            L10n.Get("Host_Tab_Perf"),
            L10n.Get("Host_Tab_Settings"),
            L10n.Get("Host_Tab_Switches"));
        PageTabs.SetGlyphs("\uE80F", "\uE9D9", "\uE713", "\uE774");
        PageTabs.Select(0);
        PageTabs.SelectionRequested += OnTabSelectionRequested;

        overviewView.BindFeedbackHost(this);
        overviewView.InventoryRefreshRequested += RefreshActiveSlicesAsync;
        overviewView.SectionChanged += OnOverviewSectionChanged;
        overviewView.FullscreenChanged += SetImmersive;

        statusView.BindFeedbackHost(this);

        TabBodyHost.Children.Add(overviewView);
        TabBodyHost.Children.Add(statusView);
        overviewView.ApplyInventory(inventory);
        UpdateVisibleTab();

        successToastTimer.Tick += (_, _) => HideSuccessToast();
        HostNoticeBanner.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => SyncNoticeBannerClearance());
        HostNoticeBanner.SizeChanged += (_, _) => SyncNoticeBannerClearance();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => RefreshChildThemes();
    }

    public async Task StopAsync()
    {
        SetImmersive(false);
        overviewView.ExitFullscreen();
        HideSuccessToast();
        DetachAppearance();
        stopping = true;
        pageActive = false;
        inventoryCts?.Cancel();
        processorCts?.Cancel();
        deviceCts?.Cancel();
        await Task.WhenAll(pendingOperations.Append(processorTask).Append(deviceTask).Append(inventoryTask).ToArray());
    }

    private async void OnLoaded(object sender, RoutedEventArgs args) => await UiEvent.RunAsync(async () =>
    {
        stopping = false;
        pageActive = true;
        AttachAppearance();

        await inventoryTask;
        if (!pageActive || stopping) return;
        if (deferredNotice is { } notice)
        {
            deferredNotice = null;
            ShowSettingsNotice(notice.Text, notice.Severity);
        }
        await EnsureRequiredSlicesAsync();
    }, message => ShowSettingsNotice(message));

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        overviewView.ExitFullscreen();
        SetImmersive(false);
        DetachAppearance();
        pageActive = false;
        inventoryCts?.Cancel();
        processorCts?.Cancel();
        deviceCts?.Cancel();
    }

    private async void OnTabSelectionRequested(int index) => await UiEvent.RunAsync(async () =>
    {
        if (index < 0 || index > 3 || activeTabIndex == index) return;
        if (activeTabIndex == 0 && index != 0)
            overviewView.ExitFullscreen();
        activeTabIndex = index;
        PageTabs.Select(index);
        if (index == 1)
            Interlocked.Exchange(ref forceNextVmRefresh, 1);
        UpdateVisibleTab();
        EnsureTelemetryState();
        await EnsureRequiredSlicesAsync();
    }, message => ShowSettingsNotice(message));

    private async void OnOverviewSectionChanged(HostOverviewSection section) => await UiEvent.RunAsync(async () =>
    {
        if (section is HostOverviewSection.Memory or HostOverviewSection.Gpu or HostOverviewSection.Storage or HostOverviewSection.Network)
            Interlocked.Exchange(ref forceNextVmRefresh, 1);
        UpdateVisibleTab();
        EnsureTelemetryState();
        await EnsureRequiredSlicesAsync();
    }, message => ShowSettingsNotice(message));

    private void SetImmersive(bool immersive)
    {
        if (isImmersive == immersive) return;
        isImmersive = immersive;
        PageTabs.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        OuterGrid.Margin = new Thickness(immersive ? 0 : 12);
        OuterFrame.BorderThickness = new Thickness(immersive ? 0 : 1);
        OuterFrame.CornerRadius = new CornerRadius(immersive ? 0 : 8);
        ImmersiveRequested?.Invoke(immersive);
    }

    private async void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => await UiEvent.RunAsync(async () =>
    {
        args.Handled = true;
        await RefreshActiveSlicesAsync();
    }, message => ShowSettingsNotice(message));

    private HostInventorySlices RequiredSlicesForActiveView() => activeTabIndex switch
    {
        0 => overviewView.RequiredSlices,
        1 => HostInventorySlices.Environment | HostInventorySlices.Compute | HostInventorySlices.Gpu | HostInventorySlices.Storage | HostInventorySlices.Network | HostInventorySlices.Settings,
        2 => HostInventorySlices.Environment | HostInventorySlices.Settings,
        3 => HostInventorySlices.Network,
        _ => HostInventorySlices.None
    };

    private HostInventorySlices PerfSlices =>
        HostInventorySlices.Environment | HostInventorySlices.Compute | HostInventorySlices.Gpu | HostInventorySlices.Storage | HostInventorySlices.Network | HostInventorySlices.Settings;

    private void UpdateVisibleTab()
    {
        overviewView.Visibility = activeTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool hasPerf = inventory.HasSlices(PerfSlices);
        statusView.Visibility = activeTabIndex == 1 && hasPerf ? Visibility.Visible : Visibility.Collapsed;

        bool hasSettings = inventory.HasSlices(HostInventorySlices.Settings);
        if (activeTabIndex == 2 && hasSettings && settingsView == null)
        {
            settingsView = new HostSettingsView();
            settingsView.BindFeedbackHost(this);
            settingsView.InventoryRefreshRequested += RefreshActiveSlicesAsync;
            settingsView.ApplyInventory(inventory);
            TabBodyHost.Children.Add(settingsView);
        }
        if (settingsView != null) settingsView.Visibility = activeTabIndex == 2 && hasSettings ? Visibility.Visible : Visibility.Collapsed;

        bool hasNetwork = inventory.HasSlices(HostInventorySlices.Network);
        if (activeTabIndex == 3 && hasNetwork && switchesView == null)
        {
            switchesView = new HostSwitchesView();
            switchesView.BindFeedbackHost(this);
            switchesView.InventoryRefreshRequested += RefreshActiveSlicesAsync;
            switchesView.ApplyInventory(inventory);
            TabBodyHost.Children.Add(switchesView);
        }
        if (switchesView != null) switchesView.Visibility = activeTabIndex == 3 && hasNetwork ? Visibility.Visible : Visibility.Collapsed;

        bool bodyReady = activeTabIndex switch
        {
            0 => true,
            1 => hasPerf,
            2 => hasSettings,
            3 => hasNetwork,
            _ => false
        };
        TabBodyHost.Visibility = bodyReady ? Visibility.Visible : Visibility.Collapsed;
    }

    private Task RefreshActiveSlicesAsync()
    {
        var activeSlices = RequiredSlicesForActiveView();
        if (activeSlices == HostInventorySlices.None)
            activeSlices = HostInventorySlices.Environment | HostInventorySlices.Compute;

        inventory = new HostInventorySnapshot
        {
            LoadedSlices = HostInventorySlices.None,
            HostName = inventory.HostName,
            OsCaption = inventory.OsCaption,
            CpuModel = inventory.CpuModel,
            Arch = inventory.Arch,
            LogicalProcessorCount = inventory.LogicalProcessorCount,
            TotalMemoryGiB = inventory.TotalMemoryGiB
        };
        return EnsureRequiredSlicesAsync(activeSlices);
    }

    private Task EnsureRequiredSlicesAsync(HostInventorySlices? explicitSlices = null)
    {
        if (!pageActive || stopping) return Task.CompletedTask;
        var required = explicitSlices ?? RequiredSlicesForActiveView();
        var missing = explicitSlices ?? (required & ~inventory.LoadedSlices);
        if (missing == HostInventorySlices.None)
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            UpdateVisibleTab();
            EnsureTelemetryState();
            return Task.CompletedTask;
        }

        inventoryCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        inventoryCts = cts;
        var nextTask = RunSliceLoadAsync(inventoryTask, cts, missing);
        inventoryTask = nextTask;
        return nextTask;
    }

    private async Task RunSliceLoadAsync(Task previous, CancellationTokenSource cts, HostInventorySlices slicesToLoad)
    {
        var token = cts.Token;
        try
        {
            try { await previous; }
            catch (OperationCanceledException) { }
            token.ThrowIfCancellationRequested();
            if (!pageActive || stopping) return;

            var remaining = slicesToLoad & ~inventory.LoadedSlices;
            if (remaining != HostInventorySlices.None)
            {
                LoadingBar.Visibility = Visibility.Visible;
                var baseSnapshot = inventory;
                var updated = await Task.Run(() => sampler.LoadSlicesAsync(baseSnapshot, remaining, token), token);
                token.ThrowIfCancellationRequested();
                if (!pageActive || stopping) return;
                inventory = updated;

                overviewView.ApplyInventory(updated);
                if (updated.HasSlices(PerfSlices) && (activeTabIndex == 1 || (remaining & PerfSlices) != 0))
                    statusView.ApplyInventory(updated);
                if (updated.HasSlices(HostInventorySlices.Settings))
                    settingsView?.ApplyInventory(updated);
                if (updated.HasSlices(HostInventorySlices.Network))
                    switchesView?.ApplyInventory(updated);

                if (activeTabIndex == 1 || (activeTabIndex == 0 && (remaining & (HostInventorySlices.Gpu | HostInventorySlices.Storage | HostInventorySlices.Network)) != 0))
                    Interlocked.Exchange(ref forceNextVmRefresh, 1);
            }

            if ((RequiredSlicesForActiveView() & ~inventory.LoadedSlices) == HostInventorySlices.None)
                LoadingBar.Visibility = Visibility.Collapsed;
            UpdateVisibleTab();
            EnsureTelemetryState();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (ReferenceEquals(inventoryCts, cts) && pageActive && !stopping)
            {
                LoadingBar.Visibility = Visibility.Collapsed;
                UpdateVisibleTab();
            }
        }
        catch (Exception ex)
        {
            if (!pageActive || stopping) return;
            if (ReferenceEquals(inventoryCts, cts))
            {
                LoadingBar.Visibility = Visibility.Collapsed;
                UpdateVisibleTab();
                ShowSettingsNotice(CoreMessageResolver.ResolveException(ex), InfoBarSeverity.Error);
            }
        }
        finally
        {
            if (ReferenceEquals(inventoryCts, cts)) inventoryCts = null;
            cts.Dispose();
        }
    }

    private HostTelemetryScope GetActiveTelemetryScope()
    {
        if (!pageActive || !windowVisible || stopping) return default;
        if (activeTabIndex == 1)
        {
            if (!inventory.HasSlices(PerfSlices)) return default;
            return new HostTelemetryScope(IncludeProcessor: true, IncludeDevices: true, IncludeVmMemory: true, IncludeVmGpu: false, IncludeVmStorage: false, IncludeVmNetwork: false);
        }
        if (activeTabIndex == 0)
        {
            var scope = overviewView.GetTelemetryScope();
            var neededSlice = overviewView.RequiredSlices;
            if (neededSlice != HostInventorySlices.None && !inventory.HasSlices(neededSlice))
                return default;
            return scope;
        }
        return default;
    }

    private void EnsureTelemetryState()
    {
        var scope = GetActiveTelemetryScope();
        bool wantProcessor = scope.IncludeProcessor;
        bool wantDevices = scope.IncludeDevices || scope.IncludeVmMemory;

        if (!wantProcessor)
        {
            processorCts?.Cancel();
        }
        else if (processorCts is not { IsCancellationRequested: false })
        {
            var cts = new CancellationTokenSource();
            processorCts = cts;
            processorTask = RunProcessorLoopAsync(processorTask, cts);
        }

        if (!wantDevices)
        {
            deviceCts?.Cancel();
        }
        else if (deviceCts is not { IsCancellationRequested: false })
        {
            var cts = new CancellationTokenSource();
            deviceCts = cts;
            deviceTask = RunDeviceLoopAsync(deviceTask, cts);
        }
    }

    private async Task RunProcessorLoopAsync(Task previous, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            await previous;
            token.ThrowIfCancellationRequested();
            Diagnostics.AppDiagnostics.Write("HostMonitor", "ProcessorLoopStarted");
            await SampleProcessorLoopAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Leaving CPU/Memory telemetry ends this sampling loop.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Host processor loop stopped: {0}", ex);
        }
        finally
        {
            Diagnostics.AppDiagnostics.Write("HostMonitor", "ProcessorLoopStopped");
            if (ReferenceEquals(processorCts, cts)) processorCts = null;
            cts.Dispose();
        }
    }

    private async Task RunDeviceLoopAsync(Task previous, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            await previous;
            token.ThrowIfCancellationRequested();
            Diagnostics.AppDiagnostics.Write("HostMonitor", "DeviceLoopStarted");
            await SampleDeviceLoopAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Leaving device/overview telemetry ends this sampling loop.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Host device loop stopped: {0}", ex);
        }
        finally
        {
            Diagnostics.AppDiagnostics.Write("HostMonitor", "DeviceLoopStopped");
            if (ReferenceEquals(deviceCts, cts)) deviceCts = null;
            cts.Dispose();
        }
    }

    private async Task SampleProcessorLoopAsync(CancellationToken token)
    {
        await using var cpu = await Task.Run(() => new HostCpuThreadMonitorService(), token);
        var warmup = Stopwatch.StartNew();
        using var timer = new PeriodicTimer(SampleInterval);
        do
        {
            var sample = await Task.Run(() => HostTelemetrySampler.SampleProcessorAndMemory(cpu), token);
            token.ThrowIfCancellationRequested();
            overviewView.ApplyProcessorAndMemory(sample);
            statusView.ApplyProcessorAndMemory(sample);
            if (sample.CpuReport?.Sample.State == ExHyperV.Models.PerformanceSampleState.WarmingUp &&
                warmup.Elapsed < SampleInterval)
            {
                await Task.Delay(150, token);
                continue;
            }
            await timer.WaitForNextTickAsync(token);
        } while (!token.IsCancellationRequested);
    }

    private async Task SampleDeviceLoopAsync(CancellationToken token)
    {
        using var devices = new HostDeviceTelemetryService();
        using var network = new HostNetworkTelemetryService();
        using var timer = new PeriodicTimer(SampleInterval);
        int tick = 0;
        do
        {
            if (inventory is { } currentInv)
            {
                var scope = GetActiveTelemetryScope();
                bool forced = Interlocked.Exchange(ref forceNextVmRefresh, 0) != 0;
                bool refreshVms = forced || (tick++ % 2 == 0);
                var sample = await Task.Run(() => HostTelemetrySampler.SampleDevicesAsync(currentInv, devices, network, scope, refreshVms, token), token);
                token.ThrowIfCancellationRequested();
                if (ReferenceEquals(currentInv, inventory))
                {
                    Diagnostics.AppDiagnostics.Write("HostMonitor", "Sample", level: TraceEventType.Verbose);
                    overviewView.ApplyDeviceSample(sample);
                    statusView.ApplyDeviceSample(sample);
                }
            }
        } while (await timer.WaitForNextTickAsync(token));
    }

    private void RefreshChildThemes()
    {
        overviewView.RefreshTheme();
        statusView.RefreshTheme();
        settingsView?.RefreshTheme();
    }

    private void AttachAppearance()
    {
        if (appearanceHooked) return;
        if (Application.Current is App { Appearance: { } appearance })
        {
            appearance.Changed += OnAppearanceChanged;
            appearanceHooked = true;
        }
    }

    private void DetachAppearance()
    {
        if (!appearanceHooked) return;
        if (Application.Current is App { Appearance: { } appearance })
            appearance.Changed -= OnAppearanceChanged;
        appearanceHooked = false;
    }

    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!pageActive || stopping) return;
            RefreshChildThemes();
        });
    }

    // IDashboardFeedbackHost implementation
    public bool HasSettingsError => HostNoticeBanner.IsOpen && HostNoticeBanner.Severity == InfoBarSeverity.Error;

    public void ShowSuccessToast(string? text = null)
    {
        if (!pageActive || stopping || HasSettingsError) return;
        ClearSettingsNotice();
        successToastTimer.Stop();
        OverlaySuccessToast.Show(text, elevated: true);
        successToastTimer.Start();
    }

    public void HideSuccessToast()
    {
        successToastTimer.Stop();
        OverlaySuccessToast.Hide();
    }

    public void ShowSettingsNotice(string? text, InfoBarSeverity severity = InfoBarSeverity.Error, Button? actionButton = null)
    {
        if (!pageActive || stopping) { deferredNotice = (text, severity); return; }
        HideSuccessToast();
        HostNoticeBanner.Show(text, severity, actionButton);
    }

    public void ClearSettingsNotice()
    {
        if (HostNoticeBanner.IsOpen) HostNoticeBanner.Hide();
    }

    private void SyncNoticeBannerClearance()
    {
        double bottom = HostNoticeBanner.Visibility == Visibility.Visible && HostNoticeBanner.ActualHeight > 0
            ? Math.Ceiling(HostNoticeBanner.ActualHeight) + 12
            : 0;
        var margin = new Thickness(0, 0, 0, bottom);
        if (TabBodyHost.Margin != margin) TabBodyHost.Margin = margin;
    }
}
