using ExHyperV.App.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed partial class HostOverviewView : UserControl
{
    private sealed record OverviewNavItem(HostOverviewSection Section, string ResourceKey, string AutoId);

    private static readonly OverviewNavItem[] Items =
    [
        new(HostOverviewSection.Env, "Host_Tab_Env", "HostOverviewNav_Env"),
        new(HostOverviewSection.Cpu, "Overview_Cpu", "HostOverviewNav_Cpu"),
        new(HostOverviewSection.Memory, "Overview_Memory", "HostOverviewNav_Mem"),
        new(HostOverviewSection.Gpu, "Overview_Gpu", "HostOverviewNav_Gpu"),
        new(HostOverviewSection.Storage, "Host_Overview_Sub_Storage", "HostOverviewNav_Storage"),
        new(HostOverviewSection.Network, "Host_Overview_Sub_Network", "HostOverviewNav_Net")
    ];

    private readonly Grid rootGrid = new() { Margin = new Thickness(16, 0, 16, 16), ColumnSpacing = 16 };
    private readonly ColumnDefinition navColumn = new() { Width = new GridLength(136) };
    private readonly ListView subNav = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly Grid rightHost = new();

    private readonly HostEnvironmentView envView = new();
    private HostCpuThreadMatrixView? cpuView;
    private HostMemoryRingView? memView;
    private HostGpuOverviewView? gpuView;
    private HostStorageOverviewView? storageView;
    private HostNetworkOverviewView? networkView;

    private HostPage? feedbackHost;
    private HostInventorySnapshot? currentInventory;
    private HostLiveSample? lastProcessorSample;
    private HostLiveSample? lastDeviceSample;
    private HostOverviewSection activeSection = HostOverviewSection.Env;
    private bool isFullscreen;

    public event Func<Task>? InventoryRefreshRequested;
    internal event Action<HostOverviewSection>? SectionChanged;
    internal event Action<bool>? FullscreenChanged;

    public HostOverviewView()
    {
        rootGrid.ColumnDefinitions.Add(navColumn);
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        AutomationProperties.SetAutomationId(subNav, "HostOverviewSubNav");
        subNav.ItemContainerTransitions.Clear();
        foreach (var item in Items)
        {
            var label = new TextBlock
            {
                Text = L10n.Get(item.ResourceKey),
                FontSize = 13.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            var lvi = new ListViewItem
            {
                Content = label,
                Tag = item.Section,
                MinHeight = 40,
                Padding = new Thickness(14, 6, 8, 6)
            };
            AutomationProperties.SetAutomationId(lvi, item.AutoId);
            subNav.Items.Add(lvi);
        }

        subNav.SelectedIndex = 0;
        subNav.SelectionChanged += (_, _) =>
        {
            if (subNav.SelectedItem is not ListViewItem { Tag: HostOverviewSection section }) return;
            if (activeSection == section) return;
            activeSection = section;
            if (activeSection != HostOverviewSection.Network)
                networkView?.SetFullscreen(false);
            ShowActiveSection();
            SectionChanged?.Invoke(activeSection);
        };

        Grid.SetColumn(subNav, 0);
        Grid.SetColumn(rightHost, 1);
        rootGrid.Children.Add(subNav);
        rootGrid.Children.Add(rightHost);

        envView.InventoryRefreshRequested += async () =>
        {
            if (InventoryRefreshRequested != null)
                await InventoryRefreshRequested.Invoke();
        };
        envView.Visibility = Visibility.Collapsed;
        rightHost.Children.Add(envView);

        Content = rootGrid;
    }

    public HostOverviewSection ActiveSection => activeSection;
    public bool RequiresTelemetry => activeSection != HostOverviewSection.Env;

    public HostInventorySlices RequiredSlices => activeSection switch
    {
        HostOverviewSection.Env => HostInventorySlices.Environment,
        HostOverviewSection.Gpu => HostInventorySlices.Gpu,
        HostOverviewSection.Storage => HostInventorySlices.Storage,
        HostOverviewSection.Network => HostInventorySlices.Network,
        _ => HostInventorySlices.None
    };

    public HostTelemetryScope GetTelemetryScope() => activeSection switch
    {
        HostOverviewSection.Cpu => new(IncludeProcessor: true, IncludeDevices: false, IncludeVmMemory: false, IncludeVmGpu: false, IncludeVmStorage: false, IncludeVmNetwork: false),
        HostOverviewSection.Memory => new(IncludeProcessor: true, IncludeDevices: false, IncludeVmMemory: true, IncludeVmGpu: false, IncludeVmStorage: false, IncludeVmNetwork: false),
        HostOverviewSection.Gpu => new(IncludeProcessor: false, IncludeDevices: true, IncludeVmMemory: false, IncludeVmGpu: true, IncludeVmStorage: false, IncludeVmNetwork: false),
        HostOverviewSection.Storage => new(IncludeProcessor: false, IncludeDevices: true, IncludeVmMemory: false, IncludeVmGpu: false, IncludeVmStorage: true, IncludeVmNetwork: false),
        HostOverviewSection.Network => new(IncludeProcessor: false, IncludeDevices: true, IncludeVmMemory: false, IncludeVmGpu: false, IncludeVmStorage: false, IncludeVmNetwork: true),
        _ => default
    };

    internal void BindFeedbackHost(HostPage host)
    {
        feedbackHost = host;
        envView.BindFeedbackHost(host);
    }

    public void ExitFullscreen() => networkView?.SetFullscreen(false);

    public void ApplyInventory(HostInventorySnapshot snapshot)
    {
        currentInventory = snapshot;
        var merged = MergeLatestSample();
        if (snapshot.HasSlices(HostInventorySlices.Environment))
            envView.ApplyInventory(snapshot);
        cpuView?.InitializeTopology(snapshot.LogicalProcessorCount);
        memView?.UpdateTelemetry(snapshot, merged);
        if (snapshot.HasSlices(HostInventorySlices.Gpu))
            gpuView?.UpdateTelemetry(snapshot, merged);
        if (snapshot.HasSlices(HostInventorySlices.Storage))
            storageView?.UpdateTelemetry(snapshot, merged);
        if (snapshot.HasSlices(HostInventorySlices.Network))
            networkView?.UpdateTelemetry(snapshot, merged);
        ShowActiveSection();
    }

    public void ApplyProcessorAndMemory(HostLiveSample sample)
    {
        lastProcessorSample = sample;
        int lpCount = currentInventory?.LogicalProcessorCount ?? Math.Max(1, Environment.ProcessorCount);
        cpuView?.UpdateReport(sample.CpuReport, lpCount);
        memView?.UpdateTelemetry(currentInventory, MergeLatestSample());
    }

    public void ApplyDeviceSample(HostLiveSample sample)
    {
        lastDeviceSample = sample;
        var merged = MergeLatestSample();
        if (sample.VmSummary != null)
            memView?.UpdateTelemetry(currentInventory, merged);
        gpuView?.UpdateTelemetry(currentInventory, merged);
        storageView?.UpdateTelemetry(currentInventory, merged);
        networkView?.UpdateTelemetry(currentInventory, merged);
    }

    public void RefreshTheme()
    {
        cpuView?.RefreshTheme();
        memView?.RefreshTheme();
        gpuView?.RefreshTheme();
        storageView?.RefreshTheme();
        networkView?.RefreshTheme();
    }

    private HostLiveSample? MergeLatestSample()
    {
        if (lastDeviceSample == null && lastProcessorSample == null) return null;
        if (lastDeviceSample == null) return lastProcessorSample;
        if (lastProcessorSample == null) return lastDeviceSample;
        return new HostLiveSample
        {
            CpuPercent = lastProcessorSample.CpuPercent,
            CpuFreqGHz = lastDeviceSample.CpuFreqGHz,
            CpuReport = lastProcessorSample.CpuReport,
            Memory = lastProcessorSample.Memory,
            VmSummary = lastDeviceSample.VmSummary,
            Disks = lastDeviceSample.Disks,
            Adapters = lastDeviceSample.Adapters,
            Switches = lastDeviceSample.Switches,
            Gpus = lastDeviceSample.Gpus,
            VmGpus = lastDeviceSample.VmGpus,
            VmStorage = lastDeviceSample.VmStorage,
            VmNetworks = lastDeviceSample.VmNetworks
        };
    }

    private void ShowActiveSection()
    {
        var merged = MergeLatestSample();
        bool hasEnv = currentInventory?.HasSlices(HostInventorySlices.Environment) == true;
        bool hasGpu = currentInventory?.HasSlices(HostInventorySlices.Gpu) == true;
        bool hasStorage = currentInventory?.HasSlices(HostInventorySlices.Storage) == true;
        bool hasNetwork = currentInventory?.HasSlices(HostInventorySlices.Network) == true;

        switch (activeSection)
        {
            case HostOverviewSection.Cpu when cpuView == null:
                cpuView = new HostCpuThreadMatrixView();
                cpuView.InitializeTopology(currentInventory?.LogicalProcessorCount ?? Math.Max(1, Environment.ProcessorCount));
                if (lastProcessorSample != null)
                    cpuView.UpdateReport(lastProcessorSample.CpuReport, currentInventory?.LogicalProcessorCount ?? Math.Max(1, Environment.ProcessorCount));
                rightHost.Children.Add(cpuView);
                break;

            case HostOverviewSection.Memory when memView == null:
                memView = new HostMemoryRingView();
                memView.UpdateTelemetry(currentInventory, merged);
                rightHost.Children.Add(memView);
                break;

            case HostOverviewSection.Gpu when gpuView == null && hasGpu:
                gpuView = new HostGpuOverviewView();
                gpuView.UpdateTelemetry(currentInventory, merged);
                rightHost.Children.Add(gpuView);
                break;

            case HostOverviewSection.Storage when storageView == null && hasStorage:
                storageView = new HostStorageOverviewView();
                storageView.UpdateTelemetry(currentInventory, merged);
                rightHost.Children.Add(storageView);
                break;

            case HostOverviewSection.Network when networkView == null && hasNetwork:
                networkView = new HostNetworkOverviewView();
                networkView.FullscreenChanged += OnNetworkFullscreenChanged;
                networkView.UpdateTelemetry(currentInventory, merged);
                rightHost.Children.Add(networkView);
                break;
        }

        envView.Visibility = activeSection == HostOverviewSection.Env && hasEnv ? Visibility.Visible : Visibility.Collapsed;
        if (cpuView != null) cpuView.Visibility = activeSection == HostOverviewSection.Cpu ? Visibility.Visible : Visibility.Collapsed;
        if (memView != null) memView.Visibility = activeSection == HostOverviewSection.Memory ? Visibility.Visible : Visibility.Collapsed;
        if (gpuView != null) gpuView.Visibility = activeSection == HostOverviewSection.Gpu && hasGpu ? Visibility.Visible : Visibility.Collapsed;
        if (storageView != null) storageView.Visibility = activeSection == HostOverviewSection.Storage && hasStorage ? Visibility.Visible : Visibility.Collapsed;
        if (networkView != null) networkView.Visibility = activeSection == HostOverviewSection.Network && hasNetwork ? Visibility.Visible : Visibility.Collapsed;

        // Re-sync layout & latest telemetry for cached subviews immediately when switched into view.
        if (activeSection == HostOverviewSection.Gpu && hasGpu) gpuView?.UpdateTelemetry(currentInventory, merged);
        else if (activeSection == HostOverviewSection.Storage && hasStorage) storageView?.UpdateTelemetry(currentInventory, merged);
        else if (activeSection == HostOverviewSection.Network && hasNetwork) networkView?.UpdateTelemetry(currentInventory, merged);
        else if (activeSection == HostOverviewSection.Memory) memView?.UpdateTelemetry(currentInventory, merged);
        else if (activeSection == HostOverviewSection.Cpu && lastProcessorSample != null)
            cpuView?.UpdateReport(lastProcessorSample.CpuReport, currentInventory?.LogicalProcessorCount ?? Math.Max(1, Environment.ProcessorCount));
    }

    private void OnNetworkFullscreenChanged(bool fullscreen)
    {
        if (isFullscreen == fullscreen) return;
        isFullscreen = fullscreen;
        subNav.Visibility = fullscreen ? Visibility.Collapsed : Visibility.Visible;
        navColumn.Width = fullscreen ? new GridLength(0) : new GridLength(136);
        rootGrid.ColumnSpacing = fullscreen ? 0 : 16;
        rootGrid.Margin = fullscreen ? new Thickness(0) : new Thickness(16, 0, 16, 16);
        FullscreenChanged?.Invoke(fullscreen);
    }
}
