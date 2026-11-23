using System.Diagnostics;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Pages.VirtualMachines.Settings.Network;
using ExHyperV.App.UI.Pages.VirtualMachines.Settings.Pcie;
using ExHyperV.App.UI.Pages.VirtualMachines.Settings.Processor;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Dashboard;

public sealed partial class VmDashboardView : UserControl, IDashboardFeedbackHost
{
    private enum NoticeOrigin { Dashboard, Settings }
    private CancellationTokenSource? cancellation;
    private readonly SemaphoreSlim queryGate = new(1, 1);
    private readonly Dictionary<int, VmUsageChart> charts = new();
    private readonly List<Task> work = new();
    private readonly VmUsageChart memoryChart = new(L10n.Get("Overview_MemoryUsageTrend"), VmUsageChartKind.Memory, chartHeight: 148);
    private bool? hasGpu;
    private bool? gpuDriverBound;
    private VmListItem? current;
    private VmState? state;
    private bool powerPending;
    private bool stateVerified;
    private bool transientServiceBanner;
    private NoticeOrigin activeNoticeOrigin;
    private readonly DispatcherTimer successToastTimer = new() { Interval = TimeSpan.FromMilliseconds(2600) };
    private VmInfoCache? cache;
    private VmQueryRow? displayedConfiguration;
    public Task PendingWork { get; private set; } = Task.CompletedTask;
    public Task PowerWork { get; private set; } = Task.CompletedTask;

    internal static readonly VmQueryField[] ConfigurationFields = [
        VmIdentityFields.Name, VmIdentityFields.State, VmIdentityFields.Uptime, VmIdentityFields.Generation,
        VmIdentityFields.ConfigurationVersion, VmIdentityFields.ConfigurationFilePath,
        VmProcessorFields.Count, VmProcessorFields.ExposeVirtualizationExtensions, VmProcessorFields.SmtMode,
        VmMemoryFields.Startup, VmMemoryFields.DynamicMemoryEnabled, VmMemoryFields.Minimum, VmMemoryFields.Maximum,
        VmStorageFields.Path, VmStorageFields.DriveType, VmStorageFields.DiskType, VmStorageFields.VirtualDiskType,
        VmStorageFields.CapacityBytes, VmStorageFields.FileSizeBytes, VmStorageFields.DiskModel,
        VmStorageFields.ControllerType, VmStorageFields.ControllerNumber, VmStorageFields.ControllerLocation, VmStorageFields.DiskNumber,
        VmNetworkFields.Name, VmNetworkFields.SwitchName, VmNetworkFields.MacAddress, VmNetworkFields.IpAddresses, VmNetworkFields.IsConnected,
        VmGpuFields.Name, VmGpuFields.Manu, VmGpuFields.Vendor, VmGpuFields.DriverVersion, VmGpuFields.MemoryBytes, VmGpuFields.HostDeviceInstanceId,
        VmMetadataFields.Key, VmMetadataFields.Value];

    private static readonly VmQueryField[] MetricFields = [
        VmIdentityFields.State, VmIdentityFields.Uptime, VmProcessorFields.UsagePercent,
        VmProcessorMetricFields.Index, VmProcessorMetricFields.UsagePercent,
        VmMemoryFields.AssignedMiB, VmMemoryFields.AvailablePercent, VmMemoryFields.PressurePercent, VmMemoryFields.DemandMiB, VmMemoryFields.SmartPagingMiB,
        VmStorageFields.ReadBytesPerSecond, VmStorageFields.WriteBytesPerSecond,
        VmNetworkFields.SendBytesPerSecond, VmNetworkFields.ReceiveBytesPerSecond, VmNetworkFields.IpAddresses,
        VmGpuPerformanceFields.Gpu3d, VmGpuPerformanceFields.GpuCopy, VmGpuPerformanceFields.GpuEncode, VmGpuPerformanceFields.GpuDecode, VmGpuPerformanceFields.HasGpuEngineInstance];

    public VmDashboardView()
    {
        InitializeComponent();
        OverviewRailScroll.AttachScrollbarClearance(bottomSpacing: 0);
        OverviewDetailScroll.AttachScrollbarClearance(bottomSpacing: 0);
        ApplyLocalization();
        InitializeUptimeClock();
        InitializeNavigation();
        InitializeOverview();
        InitializeGpuCharts();
        successToastTimer.Tick += (_, _) => HideSuccessToast();
        DashboardNoticeBanner.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => SyncNoticeBannerClearance());
        DashboardNoticeBanner.SizeChanged += (_, _) => SyncNoticeBannerClearance();
        Loaded += (_, _) =>
        {
            AttachAppearanceEvents();
            UpdateOverviewTheme();
        };
        ActualThemeChanged += (_, _) =>
        {
            UpdateDeviceTheme();
            UpdateOverviewTheme();
        };
        Unloaded += (_, _) =>
        {
            DetachAppearanceEvents();
            Stop();
        };
    }

    public void Stop()
    {
        FreezeUptimeClock();
        cancellation?.Cancel();
    }

    public async Task StopAsync()
    {
        await CloseSettings();
        Stop();
        await Task.WhenAll(work.Append(PowerWork));
    }

    internal event Action<bool>? ImmersiveRequested;
    private bool settingsImmersive;

    public VmListItem? SelectedVm => current;
    private int selectionVersion;

    public async Task<bool> SelectAsync(VmListItem? vm)
    {
        int version = ++selectionVersion;
        int pageIndex = PageTabs.SelectedIndex;
        int sectionIndex = SettingsNavigation.SelectedIndex;
        string deviceSub = activeDeviceSubKey;
        string advSub = activeAdvancedSubKey;
        if (!await TryCloseSettingsAsync()) return false;
        if (version == selectionVersion)
        {
            Select(vm);
            if (vm != null && pageIndex > 0 && pageIndex < topTabKeys.Length)
            {
                string key = topTabKeys[pageIndex];
                OpenTopTab(key, key == "devices" ? deviceSub : key == "advanced" ? advSub : null);
                if (sectionedSettings != null && SettingsNavigation.Items.Count > 0)
                {
                    int restored = Math.Clamp(sectionIndex, 0, SettingsNavigation.Items.Count - 1);
                    selectingSettings = true;
                    try { SettingsNavigation.SelectedIndex = restored; }
                    finally { selectingSettings = false; }
                    sectionedSettings.ShowSection(restored);
                }
            }
            else
            {
                activeTopKey = "overview";
                PageTabs.Select(0);
            }
        }
        return true;
    }

    private void Select(VmListItem? vm)
    {
        Stop();
        if (current != null) current.PropertyChanged -= OnSelectedVmChanged;
        current = vm;
        if (current != null) current.PropertyChanged += OnSelectedVmChanged;
        state = null;
        stateVerified = false;
        displayedConfiguration = null;
        PowerMenu.Visibility = Visibility.Collapsed;
        PowerMenu.IsEnabled = false;
        ClearNetworkAddresses();
        Body.Visibility = vm == null ? Visibility.Collapsed : Visibility.Visible;
        if (vm == null) return;

        VmName.Text = vm.Name;
        StateText.Text = vm.State;
        UptimeText.Text = vm.Uptime;
        ToolTipService.SetToolTip(VmName, vm.Id.ToString());
        OsText.Text = VersionText.Text = "";
        GuidText.Text = vm.Id.ToString("D").ToUpperInvariant();
        GenerationPanel.Visibility = Visibility.Collapsed;
        VersionPanel.Visibility = Visibility.Collapsed;
        hasGpu = gpuDriverBound = null;
        GpuCharts.Visibility = Visibility.Collapsed;
        GpuMessage.Text = "";
        Preview.Source = null;
        PreviewBorder.Visibility = Visibility.Collapsed;
        OsImage.Source = null;
        GenericIcon.Visibility = Visibility.Visible;
        ShowVmArtwork(vm.IsRunning);
        transientServiceBanner = false;
        HideSuccessToast();
        SetPageTabsTrailingAction(null);
        DashboardNoticeBanner.Hide();
        OverviewPane.Visibility = Visibility.Collapsed;
        LoadingBar.Visibility = Visibility.Visible;
        PowerButton.Visibility = Visibility.Collapsed;

        charts.Clear();
        CoreCharts.Children.Clear();
        diskRates.Clear();
        networkRates.Clear();
        Array.Clear(gpuValues);
        foreach (var chart in gpuCharts) chart.Clear();
        ResetDeviceSelection();
        ResetOverviewTelemetry();
        memoryChart.Clear();

        if (cache?.GetSnapshot(vm.Id) is { } cached)
        {
            ShowConfiguration(cached);
            OverviewPane.Visibility = Visibility.Visible;
            LoadingBar.Visibility = Visibility.Collapsed;
        }

        var source = new CancellationTokenSource();
        cancellation = source;
        PendingWork = RunAsync(vm.Id, source);
        work.RemoveAll(task => task.IsCompleted);
        work.Add(PendingWork);
    }

    private void OnSelectedVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(VmListItem.Name) && current != null)
            VmName.Text = current.Name;
    }

    private async Task RunAsync(Guid id, CancellationTokenSource source)
    {
        var token = source.Token;
        bool acquired = false;
        Task previewWork = Task.CompletedTask;
        bool previewStarted = false;
        try
        {
            await queryGate.WaitAsync(token);
            acquired = true;
            await using var query = new VmQueryService();
            VmQueryRow row;
            if (cache != null) row = await cache.LoadDetailsAsync(id, token);
            else
            {
                var response = await Task.Run(() => query.QueryAsync(new VmQueryRequest(ConfigurationFields, (Guid[])[id]), token), token);
                if (!response.HasData) throw new InvalidOperationException(
                    response.Success ? L10n.Get("Dash_ReadVmError") : CoreMessageResolver.ResolveError(response));
                row = response.Data!.VirtualMachines.FirstOrDefault() ?? throw new InvalidOperationException(L10n.Get("Dash_VmNotInHost"));
            }
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(displayedConfiguration, row)) ShowConfiguration(row);
            LoadingBar.Visibility = Visibility.Collapsed;
            OverviewPane.Visibility = Visibility.Visible;
            long nextPoll = Stopwatch.GetTimestamp();
            int consecutiveServiceMisses = 0;
            while (!token.IsCancellationRequested)
            {
                long queryStarted = Stopwatch.GetTimestamp();
                var metrics = await Task.Run(() => query.QueryAsync(new VmQueryRequest(MetricFields, (Guid[])[id]), token), token);
                var queryElapsed = Stopwatch.GetElapsedTime(queryStarted);
                token.ThrowIfCancellationRequested();
                if (metrics.HasData && metrics.Data!.VirtualMachines.Count == 0)
                {
                    state = null;
                    PowerButton.Visibility = PowerMenu.Visibility = Visibility.Collapsed;
                    throw new InvalidOperationException(L10n.Get("Dash_VmRemoved"));
                }
                if (metrics.HasData && metrics.Data!.VirtualMachines.FirstOrDefault() is { } sample)
                {
                    consecutiveServiceMisses = 0;
                    if (transientServiceBanner)
                    {
                        transientServiceBanner = false;
                        if (DashboardNoticeBanner.IsOpen)
                            DashboardNoticeBanner.Hide();
                    }
                    ShowIdentity(sample, live: true, queryElapsed: queryElapsed);
                    if (stateVerified && state == VmState.Running)
                    {
                        UpdateOverviewLiveMetrics(sample);
                    }
                    else if (stateVerified && state is VmState.PoweredOff or VmState.Saved or VmState.Suspended)
                    {
                        SetOverviewInactive();
                    }
                }
                else if (!metrics.HasData)
                {
                    bool serviceUnavailable = CoreErrorClassifier.IsServiceUnavailable(metrics.Code, metrics.ErrorSource, metrics.Exception);
                    if (serviceUnavailable && ++consecutiveServiceMisses < 2)
                    {
                        // 单次瞬断先静默重连一次。
                    }
                    else
                    {
                        FreezeUptimeClock();
                        stateVerified = false;
                        PowerButton.IsEnabled = false;
                        transientServiceBanner = serviceUnavailable;
                        ShowError(CoreMessageResolver.ResolveError(metrics),
                            serviceUnavailable ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
                    }
                }
                if (!previewStarted && stateVerified && state == VmState.Running)
                {
                    previewStarted = true;
                    previewWork = RunPreviewAsync(id, source);
                }
                nextPoll = await WaitForNextPollAsync(nextPoll, MetricInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Stopping the dashboard cancels the pending poll.
        }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested)
            {
                ShowError(CoreMessageResolver.ResolveException(exception));
                LoadingBar.Visibility = Visibility.Collapsed;
            }
        }
        finally
        {
            source.Cancel();
            try { await previewWork; }
            finally
            {
                if (acquired) queryGate.Release();
                if (ReferenceEquals(cancellation, source))
                {
                    FreezeUptimeClock();
                    cancellation = null;
                }
                source.Dispose();
            }
        }
    }

    public void SetCache(VmInfoCache value) => cache = value;

    private void ShowIdentity(VmQueryRow row, bool live = false, TimeSpan queryElapsed = default)
    {
        if (row.Get(VmIdentityFields.Name) is { HasValue: true } name) VmName.Text = name.Value;
        if (row.Get(VmIdentityFields.Generation) is { HasValue: true } generation)
        {
            GenerationText.Text = L10n.Format("Dash_GenFormat", generation.Value);
            GenerationPanel.Visibility = Visibility.Visible;
        }
        if (row.Get(VmIdentityFields.ConfigurationVersion) is { HasValue: true } version && !string.IsNullOrWhiteSpace(version.Value))
        {
            VersionText.Text = L10n.Format("Dash_VersionFormat", version.Value);
            VersionPanel.Visibility = Visibility.Visible;
        }
        if (row.Get(VmIdentityFields.State) is { HasValue: true } status)
        {
            if (status.Value != state) runtimeVersion++;
            if (status.Value == VmState.Running && state != VmState.Running)
                ClearOverviewMetricsOnStart();
            state = status.Value;
            ShowVmArtwork(state == VmState.Running);
            if (live) stateVerified = true;
            StateText.Text = VirtualMachinesPageViewModel.StateText(status.Value);
            if (live && current != null) current.PowerState = status.Value;
            UpdatePowerAppearance();
            PowerButton.Visibility = state != null ? Visibility.Visible : Visibility.Collapsed;
            bool actionable = state is VmState.Running or VmState.PoweredOff or VmState.Saved or VmState.Suspended;
            PowerButton.IsEnabled = actionable && !powerPending;
            PowerMenu.IsEnabled = actionable && !powerPending;
        }
        else if (live)
        {
            stateVerified = false;
            PowerButton.IsEnabled = false;
            PowerMenu.IsEnabled = false;
        }
        if (live && SettingsHost.Content is VmPcieSettingsView pcie)
            pcie.UpdatePowerState(stateVerified && state.HasValue ? state.Value : VmState.Unknown);
        if (live && SettingsHost.Content is VmNetworkSettingsView network)
            network.UpdatePowerState(stateVerified && state.HasValue ? state.Value : VmState.Unknown);
        UpdateUptimeClock(row, live, queryElapsed);
    }

    private static string MissingText => L10n.Get("Common_Unavailable");
    private static bool IsMissingText(string? value) => string.IsNullOrWhiteSpace(value) || string.Equals(value, MissingText, StringComparison.Ordinal);

    private static string Text<T>(VmQueryRow row, VmQueryField<T> field)
        => row.Get(field) is { HasValue: true } result ? result.Value?.ToString() ?? "" : MissingText;

    private void ShowConfiguration(VmQueryRow row, bool preserveLiveIdentity = false)
    {
        displayedConfiguration = row;
        if (!preserveLiveIdentity) ShowIdentity(row);
        var count = row.Get(VmProcessorFields.Count);
        if (count.HasValue)
        {
            if (charts.Count != count.Value)
            {
                charts.Clear();
                CoreCharts.Children.Clear();
                for (int i = 0; i < count.Value; i++)
                {
                    var chart = new VmUsageChart(i.ToString(), VmUsageChartKind.Processor, VmUsageChart.ProcessorHeight);
                    charts.Add(i, chart);
                    CoreCharts.Children.Add(chart);
                }
            }
            LayoutCoreCharts();
        }
        ToolTipService.SetToolTip(VmName, $"{current?.Id}\n{Text(row, VmIdentityFields.ConfigurationFilePath)}");
        UpdateOverviewConfiguration(row);
        ShowDevices(row);
        ShowOperatingSystem(row);
    }

    private void ShowOperatingSystem(VmQueryRow row)
    {
        var metadata = row.GetCollection(VmQueryCollection.Metadata);
        var os = metadata.Rows.FirstOrDefault(m => m.Get(VmMetadataFields.Key).Value == VmMetadataService.OsTypeKey)?.Get(VmMetadataFields.Value);
        if (metadata.State == VmQueryValueState.Available)
            ApplyOperatingSystem(os is { HasValue: true } ? os.Value : null);
    }

    private void ApplyOperatingSystem(string? osType)
    {
        var name = OsIconSource.GetResourceName(osType);
        var image = OsIconSource.Get(name);
        OsText.Text = name;
        OsImage.Source = image;
        GenericIcon.Visibility = image == null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowVmArtwork(bool running)
    {
        SystemIconPanel.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        MonitorPreview.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (!running)
        {
            Preview.Source = null;
            PreviewBorder.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnPowerClick(object sender, RoutedEventArgs args) => await UiEvent.RunAsync(async () =>
    {
        if (current == null || powerPending) return;
        PowerWork = ChangePowerAsync(current, state);
        await PowerWork;
    }, message => ShowError(message));

    private async Task ChangePowerAsync(VmListItem vm, VmState? originalState, VmPowerAction? requestedAction = null)
    {
        powerPending = true;
        PowerButton.IsEnabled = false;
        try
        {
            bool defaultSave = string.Equals(
                ((App)Application.Current).Appearance?.Settings.DefaultPowerAction,
                "Save",
                StringComparison.OrdinalIgnoreCase);
            var action = requestedAction ?? (originalState == VmState.Running
                ? (defaultSave ? VmPowerAction.Save : VmPowerAction.Stop)
                : VmPowerAction.Start);
            if (action != VmPowerAction.Start)
            {
                string verb = action switch
                {
                    VmPowerAction.Stop => L10n.Get("Dash_PowerAction_Stop"),
                    VmPowerAction.Restart => L10n.Get("Dash_PowerAction_Restart"),
                    VmPowerAction.Suspend => L10n.Get("Dash_PowerAction_Suspend"),
                    VmPowerAction.Save => L10n.Get("Dash_PowerAction_Save"),
                    _ => L10n.Get("Dash_PowerAction_TurnOff")
                };
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = L10n.Format("Dash_PowerConfirmTitle", verb, vm.Name),
                    Content = L10n.Get(action == VmPowerAction.TurnOff ? "Dash_PowerConfirm_TurnOffBody" : "Dash_PowerConfirm_NormalBody"),
                    PrimaryButtonText = L10n.Get("Common_Confirm"),
                    CloseButtonText = L10n.Get("Common_Cancel"),
                    DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            var session = new VmPowerOperationSession(vm.Id, action, vm.Name);
            await session.AdvanceAsync();
            while (session.Stage == VmPowerOperationStage.AwaitingConfirmation && session.Confirmation is { } repair)
            {
                if (current?.Id != vm.Id || XamlRoot == null)
                {
                    await session.SubmitDecisionAsync(repair.RequestId, approve: false);
                    break;
                }
                var (approved, gpuSelections) = await PromptPowerRepairAsync(repair);
                await session.SubmitDecisionAsync(repair.RequestId, approved, gpuSelections);
                if (session.Stage == VmPowerOperationStage.ReadyToRetry)
                    await session.AdvanceAsync();
            }

            cache?.Invalidate(vm.Id);
            if (session.Stage == VmPowerOperationStage.Completed)
            {
                if (!string.IsNullOrWhiteSpace(session.RepairNotice) && current?.Id == vm.Id)
                {
                    ShowError(session.RepairNotice, InfoBarSeverity.Informational);
                    _ = RefreshConfigurationAsync(vm.Id);
                }
                if (action is VmPowerAction.Start or VmPowerAction.Restart)
                    await RestoreAffinityAfterStartAsync(vm.Id);
                if (action == VmPowerAction.Start
                    && originalState == VmState.PoweredOff
                    && (((App)Application.Current).Appearance?.Settings.AutoOpenConsole ?? false)
                    && current?.Id == vm.Id)
                {
                    OnConnectClick(this, new RoutedEventArgs());
                }
            }
            else if (session.Stage == VmPowerOperationStage.Failed && current?.Id == vm.Id)
            {
                string message = session.Failure != null
                    ? CoreMessageResolver.ResolveError(session.Failure)
                    : session.Error;
                ShowError(message);
            }
        }
        catch (Exception ex) { if (current?.Id == vm.Id) ShowError(CoreMessageResolver.ResolveException(ex)); }
        finally { powerPending = false; PowerButton.IsEnabled = true; }
    }

    private async Task<(bool Approved, Dictionary<Guid, string>? GpuSelections)> PromptPowerRepairAsync(VmPowerRepairConfirmation repair)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = repair.Message, TextWrapping = TextWrapping.Wrap });

        var pickers = new List<(Guid InstanceId, ComboBox Box)>();
        foreach (var target in repair.GpuTargets.Where(t => t.RequiresSelection))
        {
            if (!Guid.TryParse(target.Instance.Trim('{', '}', '_', ' '), out var instanceGuid)) continue;
            var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var candidate in target.Candidates)
            {
                box.Items.Add(new ComboBoxItem
                {
                    Content = candidate.DeviceInstanceId,
                    Tag = candidate.Id
                });
            }
            if (box.Items.Count > 0) box.SelectedIndex = 0;
            panel.Children.Add(new TextBlock
            {
                Text = L10n.Format("Dash_PowerRepair_SelectGpu", target.VdevNumber),
                FontSize = 12.5
            });
            panel.Children.Add(box);
            pickers.Add((instanceGuid, box));
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = repair.Title,
            Content = panel,
            PrimaryButtonText = repair.ConfirmText,
            CloseButtonText = L10n.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return (false, null);

        if (pickers.Count == 0)
            return (true, null);

        var selections = new Dictionary<Guid, string>();
        foreach (var (instanceId, box) in pickers)
        {
            if (box.SelectedItem is ComboBoxItem { Tag: string id })
                selections[instanceId] = id;
        }
        return (true, selections);
    }

    private async Task RestoreAffinityAfterStartAsync(Guid vmId)
    {
        if (VmCpuAffinitySettingsView.HasUnknownOperation(vmId)) return;
        try
        {
            var result = await Task.Run(() => CpuAffinityService.ApplySavedAffinityAfterStartAsync(vmId));
            if (current?.Id == vmId && result is CpuAffinityService.StartupApplyStatus.Exhausted
                or CpuAffinityService.StartupApplyStatus.InvalidConfiguration or CpuAffinityService.StartupApplyStatus.ReselectionRequired)
                ShowError(L10n.Format("Dash_AffinityRestoreFailed", result));
        }
        catch (ExHyperV.Tools.CoreOperationException exception)
        {
            if (exception.Result.ErrorKind == ExHyperV.Tools.ApiErrorKind.OutcomeUnknown)
                VmCpuAffinitySettingsView.MarkUnknownOperation(vmId);
            if (current?.Id == vmId) ShowError(L10n.Format("Dash_AffinityRestoreError", CoreMessageResolver.ResolveError(exception.Result)));
        }
    }

    private void ApplyLocalization()
    {
        ToolTipService.SetToolTip(SystemIconPanel, L10n.Get("Dash_IconPickerTooltip"));
        ToolTipService.SetToolTip(OsBadgeButton, L10n.Get("Dash_IconPickerTooltip"));
        ToolTipService.SetToolTip(GuidBadgeButton, L10n.Get("Dash_CopyGuidTooltip"));
        AutomationProperties.SetName(PowerMenu, L10n.Get("Dash_MorePowerActions"));
        PowerMenuRestart.Text = L10n.Get("Dash_Power_Restart");
        PowerMenuSuspend.Text = L10n.Get("Dash_Power_Pause");
        PowerMenuSave.Text = L10n.Get("Dash_Power_SaveState");
        PowerMenuTurnOff.Text = L10n.Get("Dash_Power_ForceTurnOff");
        ConnectButton.Content = L10n.Get("Dash_Connect");
        string gearTip = L10n.Get("Dash_OpenGearTooltip");
        ToolTipService.SetToolTip(OverviewDetailGear, gearTip);
        AutomationProperties.SetName(OverviewDetailGear, gearTip);
        AutomationProperties.SetName(SettingsNavigation, L10n.Get("Dash_SettingsNavAria"));
        AutomationProperties.SetName(SettingsLoadingBar, L10n.Get("Dash_LoadingSettingsAria"));
    }

    bool IDashboardFeedbackHost.HasSettingsError =>
        DashboardNoticeBanner.IsOpen
        && DashboardNoticeBanner.Severity == InfoBarSeverity.Error
        && activeNoticeOrigin == NoticeOrigin.Settings;

    void IDashboardFeedbackHost.ShowSuccessToast(string? text) => ShowSuccessToast(text);
    void IDashboardFeedbackHost.HideSuccessToast() => HideSuccessToast();
    void IDashboardFeedbackHost.ShowSettingsNotice(string? text, InfoBarSeverity severity, Button? actionButton)
    {
        HideSuccessToast();
        transientServiceBanner = false;
        activeNoticeOrigin = NoticeOrigin.Settings;
        DashboardNoticeBanner.Show(text, severity, actionButton);
    }

    void IDashboardFeedbackHost.ClearSettingsNotice() => ClearSettingsNotice();
    void IDashboardFeedbackHost.SetPageTabsTrailingAction(UIElement? content) => SetPageTabsTrailingAction(content);

    private void SetPageTabsTrailingAction(UIElement? content)
    {
        PageTabsTrailingHost.Content = content;
        PageTabsTrailingHost.Visibility = content != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ClearSettingsNotice()
    {
        if (activeNoticeOrigin == NoticeOrigin.Settings && DashboardNoticeBanner.IsOpen)
            DashboardNoticeBanner.Hide();
    }

    private void ShowSuccessToast(string? text)
    {
        if (DashboardNoticeBanner.IsOpen)
        {
            if (DashboardNoticeBanner.Severity == InfoBarSeverity.Error) return;
            transientServiceBanner = false;
            DashboardNoticeBanner.Hide();
        }
        successToastTimer.Stop();
        OverlaySuccessToast.Show(text, elevated: true);
        successToastTimer.Start();
    }

    private void HideSuccessToast()
    {
        successToastTimer.Stop();
        OverlaySuccessToast.Hide();
    }

    private void ShowError(string? message, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        HideSuccessToast();
        activeNoticeOrigin = NoticeOrigin.Dashboard;
        if (severity != InfoBarSeverity.Warning) transientServiceBanner = false;
        DashboardNoticeBanner.Show(message, severity);
    }

    private void SyncNoticeBannerClearance()
    {
        double bottom = DashboardNoticeBanner.Visibility == Visibility.Visible && DashboardNoticeBanner.ActualHeight > 0
            ? Math.Ceiling(DashboardNoticeBanner.ActualHeight) + 12
            : 0;
        var margin = new Thickness(0, 0, 0, bottom);
        if (SettingsHost.Margin != margin) SettingsHost.Margin = margin;
        if (OverviewPane.Margin != margin) OverviewPane.Margin = margin;
    }

    private void OnErrorClosed(object? sender, EventArgs args)
    {
        transientServiceBanner = false;
    }
}
