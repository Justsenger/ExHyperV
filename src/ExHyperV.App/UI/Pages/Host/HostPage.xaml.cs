using System.Diagnostics;
using System.Runtime.InteropServices;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.Host;

public sealed partial class HostPage : Page, IDashboardFeedbackHost
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private readonly HostTelemetrySampler sampler = new();
    private readonly HostStatusView statusView = new();
    private readonly HostEnvironmentView environmentView = new();
    private readonly HostSettingsView settingsView = new();
    private readonly HostSwitchesView switchesView = new();

    private readonly DispatcherQueueTimer uptimeTimer;
    private readonly DispatcherTimer successToastTimer = new() { Interval = TimeSpan.FromMilliseconds(2600) };

    private HostInventorySnapshot? inventory;
    private CancellationTokenSource? telemetryCts;
    private CancellationTokenSource? inventoryCts;
    private Task telemetryTask = Task.CompletedTask;
    private Task? inventoryTask;
    private bool pageActive;
    private bool windowVisible = true;

    internal void SetWindowVisible(bool visible)
    {
        if (windowVisible == visible) return;
        windowVisible = visible;
        EnsureTelemetryState();
    }
    private bool stopping;
    private bool needsInventory;
    private readonly HashSet<Task> pendingOperations = [];
    private (string? Text, InfoBarSeverity Severity)? deferredNotice;

    internal async Task RunHostOperationAsync(Func<Task> action)
    {
        if (!pageActive || stopping) return;
        var task = action();
        pendingOperations.Add(task);
        try { await task; }
        catch (Exception ex) { ShowSettingsNotice(CoreMessageResolver.ResolveException(ex)); }
        finally { pendingOperations.Remove(task); }
    }
    private HostVmSummary? lastVmSummary;
    private int activeTabIndex;
    private bool appearanceHooked;

    public HostPage()
    {
        InitializeComponent();

        HostNameText.Text = Environment.MachineName;

        PageTabs.SetItems(
            L10n.Get("Host_Tab_Status"),
            L10n.Get("Host_Tab_Env"),
            L10n.Get("Host_Tab_Settings"),
            L10n.Get("Host_Tab_Switches"));
        PageTabs.SetGlyphs("\uE9D9", "\uE9D5", "\uE713", "\uE774");
        PageTabs.Select(0);
        PageTabs.SelectionRequested += OnTabSelectionRequested;

        statusView.BindFeedbackHost(this);
        environmentView.BindFeedbackHost(this);
        settingsView.BindFeedbackHost(this);
        switchesView.BindFeedbackHost(this);

        environmentView.InventoryRefreshRequested += ReloadInventoryAsync;
        settingsView.InventoryRefreshRequested += ReloadInventoryAsync;
        switchesView.InventoryRefreshRequested += ReloadInventoryAsync;

        TabBodyHost.Children.Add(statusView);
        TabBodyHost.Children.Add(environmentView);
        TabBodyHost.Children.Add(settingsView);
        TabBodyHost.Children.Add(switchesView);
        UpdateVisibleTab();

        uptimeTimer = DispatcherQueue.CreateTimer();
        uptimeTimer.Interval = TimeSpan.FromSeconds(1);
        uptimeTimer.Tick += (_, _) => UpdateUptimeDisplay();
        UpdateUptimeDisplay();

        successToastTimer.Tick += (_, _) => HideSuccessToast();
        HostNoticeBanner.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => SyncNoticeBannerClearance());
        HostNoticeBanner.SizeChanged += (_, _) => SyncNoticeBannerClearance();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) =>
        {
            if (inventory != null) ApplyTopSummary(inventory);
            statusView.RefreshTheme();
            settingsView.RefreshTheme();
        };
    }

    public async Task StopAsync()
    {
        uptimeTimer.Stop();
        HideSuccessToast();
        DetachAppearance();
        stopping = true;
        pageActive = false;
        inventoryCts?.Cancel();
        telemetryCts?.Cancel();
        await Task.WhenAll(pendingOperations.Append(telemetryTask).Append(inventoryTask ?? Task.CompletedTask).ToArray());
    }

    private async void OnLoaded(object sender, RoutedEventArgs args) => await UiEvent.RunAsync(async () =>
    {
        stopping = false;
        pageActive = true;
        AttachAppearance();
        uptimeTimer.Start();
        UpdateUptimeDisplay();

        if (inventoryTask is { } previous) await previous;
        if (!pageActive || stopping) return;
        if (deferredNotice is { } notice)
        {
            deferredNotice = null;
            ShowSettingsNotice(notice.Text, notice.Severity);
        }
        if (inventory == null || needsInventory)
            await ReloadInventoryAsync();
        else
            EnsureTelemetryState();
    }, message => ShowSettingsNotice(message));

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        uptimeTimer.Stop();
        DetachAppearance();
        pageActive = false;
        if (inventoryCts != null) needsInventory = true;
        inventoryCts?.Cancel();
        telemetryCts?.Cancel();
    }

    private void OnTabSelectionRequested(int index)
    {
        if (index < 0 || index > 3 || activeTabIndex == index) return;
        activeTabIndex = index;
        PageTabs.Select(index);
        UpdateVisibleTab();
        EnsureTelemetryState();
    }

    private void OnHyperVStatusButtonClick(object sender, RoutedEventArgs args)
    {
        OnTabSelectionRequested(1); // Environment readiness tab
    }

    private async void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => await UiEvent.RunAsync(async () =>
    {
        args.Handled = true;
        await ReloadInventoryAsync();
    }, message => ShowSettingsNotice(message));

    private void UpdateVisibleTab()
    {
        statusView.Visibility = activeTabIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        environmentView.Visibility = activeTabIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        settingsView.Visibility = activeTabIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        switchesView.Visibility = activeTabIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ReloadInventoryAsync()
    {
        if (!pageActive || stopping) { needsInventory = true; return; }
        if (inventoryTask is { IsCompleted: false })
        {
            await inventoryTask;
            return;
        }

        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        inventoryTask = tcs.Task;
        try
        {
            if (inventory == null)
            {
                LoadingBar.Visibility = Visibility.Visible;
                TabBodyHost.Visibility = Visibility.Collapsed;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            inventoryCts = cts;
            var snapshot = await sampler.LoadInventoryAsync(cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!pageActive || stopping) return;
            needsInventory = false;
            inventory = snapshot;

            lastVmSummary = snapshot.VmSummary ?? lastVmSummary;
            ApplyTopSummary(snapshot);
            statusView.ApplyInventory(snapshot);
            environmentView.ApplyInventory(snapshot);
            settingsView.ApplyInventory(snapshot);
            switchesView.ApplyInventory(snapshot);

            LoadingBar.Visibility = Visibility.Collapsed;
            TabBodyHost.Visibility = Visibility.Visible;
            EnsureTelemetryState();
        }
        catch (OperationCanceledException) when (inventoryCts == null || inventoryCts.IsCancellationRequested)
        {
            if (!pageActive || stopping) return;
            LoadingBar.Visibility = Visibility.Collapsed;
            TabBodyHost.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            if (!pageActive || stopping) return;
            LoadingBar.Visibility = Visibility.Collapsed;
            TabBodyHost.Visibility = Visibility.Visible;
            ShowSettingsNotice(CoreMessageResolver.ResolveException(ex), InfoBarSeverity.Error);
        }
        finally
        {
            inventoryCts = null;
            tcs.TrySetResult();
            inventoryTask = null;
        }
    }

    private void EnsureTelemetryState()
    {
        if (!pageActive || !windowVisible || stopping || activeTabIndex != 0 || inventory == null)
        {
            telemetryCts?.Cancel();
            return;
        }
        if (telemetryCts is { IsCancellationRequested: false }) return;
        var cts = new CancellationTokenSource();
        telemetryCts = cts;
        telemetryTask = RunTelemetryLoopAsync(telemetryTask, cts);
    }

    private async Task RunTelemetryLoopAsync(Task previous, CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            await previous;
            token.ThrowIfCancellationRequested();
            Diagnostics.AppDiagnostics.Write("HostMonitor", "Started");
            await using var cpu = await Task.Run(() => new HostCpuThreadMonitorService(), token);
            using var devices = new HostDeviceTelemetryService();
            using var network = new HostNetworkTelemetryService();
            int tick = 0;
            long nextDeadline = Stopwatch.GetTimestamp();

            while (!token.IsCancellationRequested)
            {
                var currentInv = inventory;
                if (currentInv != null)
                {
                    var sample = await Task.Run(() => HostTelemetrySampler.SampleLiveAsync(currentInv, cpu, devices, network, tick++ % 2 == 0, token), token);
                    token.ThrowIfCancellationRequested();

                    if (pageActive && windowVisible && !stopping && activeTabIndex == 0 && ReferenceEquals(inventory, currentInv))
                    {
                        Diagnostics.AppDiagnostics.Write("HostMonitor", "Sample", level: TraceEventType.Verbose);
                        statusView.ApplySample(sample);
                        ShowVmSummary(sample.VmSummary);
                    }
                }

                long period = (long)(SampleInterval.TotalSeconds * Stopwatch.Frequency);
                long now = Stopwatch.GetTimestamp();
                nextDeadline += period;
                if (nextDeadline <= now)
                    nextDeadline += ((now - nextDeadline) / period + 1) * period;
                await Task.Delay(Stopwatch.GetElapsedTime(now, nextDeadline), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Leaving the host page ends this sampling loop.
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Host telemetry loop stopped: {0}", ex);
        }
        finally
        {
            Diagnostics.AppDiagnostics.Write("HostMonitor", "Stopped");
            if (ReferenceEquals(telemetryCts, cts)) telemetryCts = null;
            cts.Dispose();
        }
    }

    private void ShowVmSummary(HostVmSummary? value)
    {
        lastVmSummary = value ?? lastVmSummary;
        VmCountBadgeText.Text = lastVmSummary is { } summary
            ? L10n.Format("Host_VmRunningSummary", summary.Running, summary.Total) : L10n.Get("Common_Unavailable");
    }

    private void ApplyTopSummary(HostInventorySnapshot inv)
    {
        bool light = ActualTheme == ElementTheme.Light;
        Color okColor = light ? Color.FromArgb(255, 0x00, 0x8A, 0x17) : Color.FromArgb(255, 0x6C, 0xCB, 0x5F);
        Color cautionColor = light ? Color.FromArgb(255, 0x9D, 0x5D, 0x00) : Color.FromArgb(255, 0xFC, 0xE1, 0x00);

        HostNameText.Text = inv.HostName;
        OsCaptionText.Text = string.IsNullOrWhiteSpace(inv.OsCaption) ? Environment.OSVersion.VersionString : inv.OsCaption;

        string archName = inv.Arch.ToString().ToUpperInvariant();
        ArchBadgeText.Text = archName;
        ShowVmSummary(lastVmSummary ?? inv.VmSummary);

        string virtKind = inv.Arch == Architecture.Arm64 ? "EL2" : L10n.Get("Host_Env_CpuVirt");
        VirtBadgeIcon.Glyph = inv.CpuVirtOk && inv.GpuPvSupported ? "\uE73E" : "\uE7BA";
        VirtBadgeIcon.Foreground = new SolidColorBrush(inv.CpuVirtOk && inv.GpuPvSupported ? okColor : cautionColor);
        VirtBadgeText.Text = L10n.Format("Host_Badge_VirtGpuPv", virtKind);
        ToolTipService.SetToolTip(VirtBadge, L10n.Get("Host_Badge_VirtTooltip"));
        VirtBadge.Visibility = Visibility.Visible;

        string iommuName = inv.Arch == Architecture.Arm64 ? "SMMU" : "IOMMU";
        IommuBadgeIcon.Foreground = new SolidColorBrush(inv.IommuOk ? okColor : cautionColor);
        IommuBadgeText.Text = L10n.Format(inv.IommuOk ? "Host_Badge_IommuReady" : "Host_Badge_IommuOff", iommuName);
        ToolTipService.SetToolTip(IommuBadge, L10n.Format("Host_Badge_IommuTooltip", iommuName));
        IommuBadge.Visibility = Visibility.Visible;

        var schedShown = inv.RunningScheduler;
        string schedLabel = HostUiHelpers.FormatSchedulerLabel(schedShown, full: true);
        string powerLabel = string.IsNullOrWhiteSpace(inv.ActivePowerPlanName) ? "---" : inv.ActivePowerPlanName;
        SchedulerBadgeText.Text = $"{schedLabel} / {powerLabel}";
        ToolTipService.SetToolTip(SchedulerBadge, L10n.Get("Host_Badge_SchedPowerTooltip"));
        SchedulerBadge.Visibility = Visibility.Visible;

        bool hvReady = inv.Diagnostics?.IsReady == true;
        HyperVStatusIcon.Glyph = hvReady ? "\uE73E" : "\uE7BA";
        HyperVStatusIcon.Foreground = new SolidColorBrush(hvReady ? okColor : cautionColor);
        HyperVStatusText.Text = L10n.Get(hvReady ? "Host_HyperVReady" : "Host_HyperVNotReady");
        ToolTipService.SetToolTip(HyperVStatusButton, L10n.Get("Host_HyperVCheckTooltip"));
    }

    private void UpdateUptimeDisplay()
    {
        var up = HostTelemetrySampler.Uptime;
        string formatted = up.ToString(up.Days > 0 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss");
        UptimeSubtitleText.Text = L10n.Format("Host_UptimeContinuous", formatted);
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
            if (inventory != null) ApplyTopSummary(inventory);
            statusView.RefreshTheme();
            settingsView.RefreshTheme();
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
