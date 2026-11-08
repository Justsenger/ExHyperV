using ExHyperV.Models;
using ExHyperV.Services;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

public sealed partial class VmDashboardView : UserControl
{
    private CancellationTokenSource? cancellation;
    private readonly SemaphoreSlim queryGate = new(1, 1);
    private readonly Dictionary<int, VmUsageChart> charts = new();
    private readonly List<Task> work = new();
    private readonly VmUsageChart memoryChart = new("内存使用趋势", VmUsageChartKind.Memory);
    private bool? hasGpu;
    private bool? gpuDriverBound;
    private VmListItem? current;
    private VmState? state;
    private bool powerPending;
    private bool stateVerified;
    private VmInfoCache? cache;
    private VmQueryRow? displayedConfiguration;
    public Task PendingWork { get; private set; } = Task.CompletedTask;
    public Task PowerWork { get; private set; } = Task.CompletedTask;

    internal static readonly VmQueryField[] ConfigurationFields = [
        VmIdentityFields.Name, VmIdentityFields.State, VmIdentityFields.Uptime, VmIdentityFields.Generation,
        VmIdentityFields.ConfigurationVersion, VmIdentityFields.ConfigurationFilePath,
        VmProcessorFields.Count, VmMemoryFields.Startup,
        VmStorageFields.Path, VmStorageFields.DriveType, VmStorageFields.DiskType, VmStorageFields.VirtualDiskType, VmStorageFields.CapacityBytes, VmStorageFields.FileSizeBytes, VmStorageFields.DiskModel,
        VmNetworkFields.Name, VmNetworkFields.SwitchName, VmNetworkFields.MacAddress, VmNetworkFields.IpAddresses, VmNetworkFields.IsConnected,
        VmGpuFields.Name, VmMetadataFields.Key, VmMetadataFields.Value];
    private static readonly VmQueryField[] MetricFields = [
        VmIdentityFields.State, VmIdentityFields.Uptime, VmProcessorFields.UsagePercent,
        VmProcessorMetricFields.Index, VmProcessorMetricFields.UsagePercent,
        VmMemoryFields.AssignedMiB, VmMemoryFields.AvailablePercent,
        VmStorageFields.ReadBytesPerSecond, VmStorageFields.WriteBytesPerSecond,
        VmNetworkFields.SendBytesPerSecond, VmNetworkFields.ReceiveBytesPerSecond, VmNetworkFields.IpAddresses,
        VmGpuPerformanceFields.Gpu3d, VmGpuPerformanceFields.GpuCopy, VmGpuPerformanceFields.GpuEncode, VmGpuPerformanceFields.GpuDecode, VmGpuPerformanceFields.IsDriverBound];

    public VmDashboardView()
    {
        InitializeComponent();
        InitializeUptimeClock();
        InitializeNavigation();
        MemoryContent.Children.Insert(0, memoryChart);
        ResourceDetails.SizeChanged += (_, _) => LayoutCoreCharts();
        foreach (var grid in new[] { GpuSelectors, DiskSelectors, NetworkSelectors })
            grid.SizeChanged += (_, _) => LayoutDeviceSelectors(grid);
        InitializeGpuCharts();
        ActualThemeChanged += (_, _) => UpdateDeviceTheme();
        Unloaded += (_, _) => Stop();
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
    public VmListItem? SelectedVm => current;
    private int selectionVersion;

    public async Task<bool> SelectAsync(VmListItem? vm)
    {
        int version = ++selectionVersion;
        int pageIndex = PageTabs.SelectedIndex;
        int sectionIndex = SettingsNavigation.SelectedIndex;
        if (!await TryCloseSettingsAsync()) return false;
        if (version == selectionVersion)
        {
            Select(vm);
            if (vm != null && pageIndex > 0 && pageIndex <= settingsKeys.Length)
            {
                OpenSettings(settingsKeys[pageIndex - 1]);
                if (sectionedSettings != null && SettingsNavigation.Items.Count > 0)
                {
                    int restored = Math.Clamp(sectionIndex, 0, SettingsNavigation.Items.Count - 1);
                    selectingSettings = true;
                    try { SettingsNavigation.SelectedIndex = restored; }
                    finally { selectingSettings = false; }
                    sectionedSettings.ShowSection(restored);
                }
            }
            else PageTabs.Select(0);
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
        copiedIp = "";
        ClearNetworkAddresses();
        Body.Visibility = vm == null ? Visibility.Collapsed : Visibility.Visible;
        if (vm == null) return;
        VmName.Text = vm.Name;
        StateText.Text = vm.State;
        UptimeText.Text = vm.Uptime;
        ToolTipService.SetToolTip(VmName, vm.Id.ToString());
        ConfigText.Text = "";
        OsText.Text = GpuText.Text = IpText.Text = CpuCard.Value = MemoryCard.Value = "";
        MemoryCard.SecondValue = "";
        hasGpu = gpuDriverBound = null;
        GpuCharts.Visibility = Visibility.Collapsed;
        GpuMessage.Text = "";
        Preview.Source = null;
        PreviewBorder.Visibility = GenerationBadge.Visibility = VersionBadge.Visibility = Visibility.Collapsed;
        OsImage.Source = null;
        GenericIcon.Visibility = Visibility.Visible;
        ShowVmArtwork(vm.IsRunning);
        ErrorBar.IsOpen = false;
        ErrorBar.Visibility = Visibility.Collapsed;
        ResourcesPanel.Visibility = Visibility.Collapsed;
        LoadingBar.Visibility = Visibility.Visible;
        PowerButton.Visibility = Visibility.Collapsed;
        charts.Clear(); CoreCharts.Children.Clear();
        diskRates.Clear(); networkRates.Clear(); Array.Clear(gpuValues);
        foreach (var chart in gpuCharts) chart.Clear();
        ResetDeviceSelection();
        memoryChart.Clear();
        if (cache?.GetSnapshot(vm.Id) is { } cached)
        {
            ShowConfiguration(cached);
            ResourcesPanel.Visibility = Visibility.Visible;
            LoadingBar.Visibility = Visibility.Collapsed;
        }
        else if (cache?.GetBasic(vm.Id) is { } basics) ShowBasicConfiguration(basics);
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
                var response = await Task.Run(() => query.QueryAsync(new VmQueryRequest(ConfigurationFields, [id]), token), token);
                if (!response.HasData) throw new InvalidOperationException(response.Error ?? "无法读取虚拟机信息。");
                row = response.Data!.VirtualMachines.FirstOrDefault() ?? throw new InvalidOperationException("此虚拟机已不在宿主清单中。");
            }
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(displayedConfiguration, row)) ShowConfiguration(row);
            LoadingBar.Visibility = Visibility.Collapsed;
            ResourcesPanel.Visibility = Visibility.Visible;
            long nextPoll = Stopwatch.GetTimestamp();
            long? assigned = null;
            while (!token.IsCancellationRequested)
            {
                long queryStarted = Stopwatch.GetTimestamp();
                var metrics = await Task.Run(() => query.QueryAsync(new VmQueryRequest(MetricFields, [id]), token), token);
                var queryElapsed = Stopwatch.GetElapsedTime(queryStarted);
                token.ThrowIfCancellationRequested();
                if (metrics.HasData && metrics.Data!.VirtualMachines.Count == 0)
                {
                    state = null;
                    PowerButton.Visibility = PowerMenu.Visibility = Visibility.Collapsed;
                    throw new InvalidOperationException("虚拟机已从宿主清单中移除。");
                }
                if (metrics.HasData && metrics.Data!.VirtualMachines.FirstOrDefault() is { } sample)
                {
                    ShowIdentity(sample, live: true, queryElapsed: queryElapsed);
                    if (stateVerified && state == VmState.Running)
                    {
                        CpuCard.Unit = "%"; MemoryCard.Unit = "GB";
                        UpdateGpuCharts(sample);
                        UpdateDeviceRates(sample);
                        UpdateNetworkAddresses(sample);
                        if (sample.Get(VmProcessorFields.UsagePercent) is { HasValue: true } cpu) CpuCard.Value = $"{cpu.Value:N1}";
                        foreach (var core in sample.GetCollection(VmQueryCollection.Processors).Rows)
                        {
                            var index = core.Get(VmProcessorMetricFields.Index);
                            var usage = core.Get(VmProcessorMetricFields.UsagePercent);
                            if (index.HasValue && usage.HasValue && charts.TryGetValue(index.Value, out var chart)) chart.Add(usage.Value, $"{usage.Value:N1}%");
                        }
                        if (sample.Get(VmMemoryFields.AssignedMiB) is { HasValue: true } memory) { assigned = memory.Value; MemoryCard.SecondValue = $"{memory.Value / 1024d:N1}"; }
                        if (sample.Get(VmMemoryFields.AvailablePercent) is { HasValue: true } available)
                        {
                            double used = 100 - available.Value;
                            memoryChart.Add(used, $"{used:N0}%");
                            if (assigned.HasValue) MemoryCard.Value = $"{assigned.Value * used / 100 / 1024:N1}";
                        }
                    }
                    else if (stateVerified && state is VmState.PoweredOff or VmState.Saved or VmState.Suspended)
                    {
                        CpuCard.Value = "0.0"; CpuCard.Unit = "%";
                        foreach (var chart in charts.Values) chart.SetInactive();
                        memoryChart.SetInactive();
                        MemoryCard.Value = MemoryCard.SecondValue = "0.0"; MemoryCard.Unit = "GB";
                        SetGpuReadings("0.0");
                        UpdateGpuVisibility();
                        foreach (var chart in gpuCharts) chart.SetInactive();
                        Array.Clear(gpuValues);
                        assigned = null;
                        ClearNetworkAddresses();
                        foreach (var rate in diskRates.Values.Concat(networkRates.Values)) rate.SetInactive();
                    }
                }
                else if (!metrics.HasData)
                {
                    FreezeUptimeClock();
                    stateVerified = false;
                    PowerButton.IsEnabled = false;
                    ShowError(metrics.Error);
                }
                if (!previewStarted && stateVerified && state == VmState.Running)
                {
                    // The preview has its own clock; its WMI call and JPEG decoding do not delay metrics.
                    previewStarted = true;
                    previewWork = RunPreviewAsync(id, source);
                }
                nextPoll = await WaitForNextPollAsync(nextPoll, MetricInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested) { ShowError(exception.Message); LoadingBar.Visibility = Visibility.Collapsed; }
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

    public void SetCache(VmInfoCache value)
    {
        if (cache != null) cache.BasicReady -= OnBasicReady;
        cache = value;
        cache.BasicReady += OnBasicReady;
    }

    private void OnBasicReady(Guid id) => DispatcherQueue.TryEnqueue(() =>
    {
        if (IsLoaded && current?.Id == id && displayedConfiguration == null && cache?.GetBasic(id) is { } row)
            ShowBasicConfiguration(row);
    });

    private void ShowBasicConfiguration(VmQueryRow row)
    {
        ShowIdentity(row);
        var summary = new List<string>();
        if (row.Get(VmProcessorFields.Count) is { HasValue: true } cores) summary.Add($"{cores.Value} 核");
        if (row.Get(VmMemoryFields.Startup) is { HasValue: true } memory) summary.Add($"{memory.Value / 1024d:0.#} GB");
        ConfigText.Text = string.Join(" / ", summary);
        ShowOperatingSystem(row);
    }

    private void ShowIdentity(VmQueryRow row, bool live = false, TimeSpan queryElapsed = default)
    {
        if (row.Get(VmIdentityFields.Name) is { HasValue: true } name) VmName.Text = name.Value;
        if (row.Get(VmIdentityFields.State) is { HasValue: true } status)
        {
            if (status.Value != state) runtimeVersion++;
            if (status.Value == VmState.Running && state != VmState.Running)
            {
                // A stopped VM's zero values are not samples from its next running session.
                CpuCard.Value = MemoryCard.Value = MemoryCard.SecondValue = "";
                foreach (var chart in charts.Values) chart.Clear();
                memoryChart.Clear();
            }
            state = status.Value;
            ShowVmArtwork(state == VmState.Running);
            if (live) stateVerified = true;
            StateText.Text = VirtualMachinesPageViewModel.StateText(status.Value);
            if (live && current != null) current.State = StateText.Text;
            UpdatePowerAppearance();
            PowerButton.Visibility = state is VmState.Running or VmState.PoweredOff or VmState.Saved or VmState.Suspended ? Visibility.Visible : Visibility.Collapsed;
            PowerButton.IsEnabled = stateVerified && !powerPending;
        }
        else if (live)
        {
            stateVerified = false;
            PowerButton.IsEnabled = false;
        }
        UpdateUptimeClock(row, live, queryElapsed);
    }

    private static string Text<T>(VmQueryRow row, VmQueryField<T> field) => row.Get(field) is { HasValue: true } result ? result.Value?.ToString() ?? "" : "未取得";

    private void ShowConfiguration(VmQueryRow row, bool preserveLiveIdentity = false)
    {
        displayedConfiguration = row;
        if (!preserveLiveIdentity) ShowIdentity(row);
        var count = row.Get(VmProcessorFields.Count);
        var startup = row.Get(VmMemoryFields.Startup);
        var summary = new List<string>();
        if (count.HasValue)
        {
            summary.Add($"{count.Value} 核");
            
            if (charts.Count != count.Value)
            {
                charts.Clear(); CoreCharts.Children.Clear();
                for (int i = 0; i < count.Value; i++) { var chart = new VmUsageChart(i.ToString()); charts.Add(i, chart); CoreCharts.Children.Add(chart); }
            }
            LayoutCoreCharts();
        }
        if (startup.HasValue) summary.Add($"{startup.Value / 1024d:0.#} GB");
        var disks = row.GetCollection(VmQueryCollection.Storage);
        var capacities = disks.Rows.Select(d => d.Get(VmStorageFields.CapacityBytes)).Where(v => v.HasValue).ToArray();
        if (capacities.Length > 0 && capacities.Length == disks.Rows.Count(d => Text(d, VmStorageFields.DiskType) != "Empty")) summary.Add(string.Join(" + ", capacities.OrderByDescending(v => v.Value).Select(v => v.Value >= 1073741824 ? $"{v.Value / 1073741824d:0.#} GB" : $"{v.Value / 1048576d:0} MB")));
        ConfigText.Text = string.Join(" / ", summary);
        ToolTipService.SetToolTip(VmName, $"{current?.Id}\n{Text(row, VmIdentityFields.ConfigurationFilePath)}");
        ShowDevices(row);
        ShowOperatingSystem(row);
    }

    private void ShowOperatingSystem(VmQueryRow row)
    {
        var metadata = row.GetCollection(VmQueryCollection.Metadata);
        var os = metadata.Rows.FirstOrDefault(m => m.Get(VmMetadataFields.Key).Value == VmMetadataService.OsTypeKey)?.Get(VmMetadataFields.Value);
        if (metadata.State == VmQueryValueState.Available)
        {
            OsText.Text = ExHyperV.Tools.OsImages.Canonical(os is { HasValue: true } ? os.Value : null);
            OsImage.Source = OsIconSource.Get(OsText.Text); GenericIcon.Visibility = OsImage.Source == null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowVmArtwork(bool running)
    {
        SystemIconPanel.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        MonitorPreview.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (!running)
        {
            // A later start must wait for a new snapshot, never reuse the previous session's image.
            Preview.Source = null;
            PreviewBorder.Visibility = Visibility.Collapsed;
        }
    }

    private static void AddEmptyRow(StackPanel target, string text)
        => target.Children.Add(new TextBlock { Text = text, FontSize = 14, Opacity = .4, Margin = new Thickness(30), HorizontalAlignment = HorizontalAlignment.Center });

    private static void AddRow(StackPanel target, string title, params string[] lines)
    {
        var panel = new StackPanel { Spacing = 5 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        foreach (var line in lines.Where(s => !string.IsNullOrWhiteSpace(s))) panel.Children.Add(new TextBlock { Text = line, FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        target.Children.Add(panel);
    }

    private async void OnPowerClick(object sender, RoutedEventArgs args)
    {
        if (current == null || powerPending) return;
        PowerWork = ChangePowerAsync(current, state);
        await PowerWork;
    }
    private async Task ChangePowerAsync(VmListItem vm, VmState? originalState, VmPowerAction? requestedAction = null)
    {
        powerPending = true; PowerButton.IsEnabled = false;
        try
        {
            var action = requestedAction ?? (originalState == VmState.Running ? VmPowerAction.Stop : VmPowerAction.Start);
            if (action != VmPowerAction.Start)
            {
                var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = $"{action switch { VmPowerAction.Stop => "关闭", VmPowerAction.Restart => "重启", VmPowerAction.Suspend => "暂停", VmPowerAction.Save => "保存", _ => "强制关闭" }} {vm.Name}？", Content = action == VmPowerAction.TurnOff ? "强制断电会丢失来宾系统中未保存的数据。" : "执行所选电源操作，不会在失败后自动强制断电。", PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            var result = await VmPowerService.ExecuteControlActionAsync(vm.Id, action);
            cache?.Invalidate(vm.Id);
            if (!result.Success && current?.Id == vm.Id) ShowError(result.Error);
            if (result.Success && action is VmPowerAction.Start or VmPowerAction.Restart)
                await RestoreAffinityAfterStartAsync(vm.Id);
        }
        catch (Exception ex) { if (current?.Id == vm.Id) ShowError(ex.Message); }
        finally { powerPending = false; PowerButton.IsEnabled = true; }
    }

    private async Task RestoreAffinityAfterStartAsync(Guid vmId)
    {
        if (Settings.VmCpuAffinitySettingsView.HasUnknownOperation(vmId)) return;
        try
        {
            var result = await Task.Run(() => CpuAffinityService.ApplySavedAffinityAfterStartAsync(vmId));
            if (current?.Id == vmId && result is CpuAffinityService.StartupApplyStatus.Exhausted
                or CpuAffinityService.StartupApplyStatus.InvalidConfiguration or CpuAffinityService.StartupApplyStatus.ReselectionRequired)
                ShowError("虚拟机已启动，但保存的 CPU 亲和性未能恢复，请进入处理器亲和性设置核对。" + Environment.NewLine + result);
        }
        catch (ExHyperV.Tools.CoreOperationException exception)
        {
            if (exception.Result.ErrorKind == ExHyperV.Tools.ApiErrorKind.OutcomeUnknown)
                Settings.VmCpuAffinitySettingsView.MarkUnknownOperation(vmId);
            if (current?.Id == vmId) ShowError("虚拟机已启动，但恢复处理器亲和性未完成：" + exception.Message);
        }
    }
    private void ShowError(string? message)
    {
        ErrorBar.Message = message;
        ErrorBar.Visibility = Visibility.Visible;
        ErrorBar.IsOpen = true;
    }
    private void OnErrorClosed(InfoBar sender, InfoBarClosedEventArgs args) => sender.Visibility = Visibility.Collapsed;
}
