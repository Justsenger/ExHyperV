using ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;
using ExHyperV.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

public sealed partial class VmDashboardView
{
    private const string FluentFont = "ms-appx:///Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";
    private Func<Task>? stopSettings;
    private Action? detachSettingsProgress;
    private readonly List<Task> settingsWork = [];
    private VmSettingsView? sectionedSettings;
    private Task? closingSettings;

    private bool navigatingSettings;
    private bool selectingSettings;
    private readonly string[] settingsKeys = ["processor", "memory", "storage", "gpu", "network", "boot", "security", "advanced", "pcie"];
    private void InitializeNavigation()
    {
        PageTabs.SetItems("状态", "处理器", "内存", "存储", "显卡", "网络", "引导", "安全", "高级", "PCIe");
        PageTabs.SetIcons(new(""), new("\uF58E", Rotation: 90), new("\uEEA0"), new("\uEDA2"),
            new("\uF211"), new("\uE774"), new("\uF2C7"), new("\uEAC9", FluentFont), new("\uE713"), new("\uF0B9"));
        PageTabs.SelectionRequested += async index => await NavigatePageAsync(index);
        DeviceTabs.SetItems("显卡 0", "存储 0", "网络 0");
        DeviceTabs.SetGlyphs("\uF211", "\uEDA2", "\uE774");
        DeviceTabs.SelectionRequested += index =>
        {
            DeviceTabs.Select(index);
            GpuPanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
            StoragePanel.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
            NetworkPanel.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        };
        CpuCard.Selected = true;
        CpuCard.Click += (_, _) => SelectResource(false);
        MemoryCard.Click += (_, _) => SelectResource(true);
    }
    private void SelectResource(bool memory)
    {
        CpuCard.Selected = !memory; MemoryCard.Selected = memory;
        CoreCharts.Visibility = memory ? Visibility.Collapsed : Visibility.Visible;
        MemoryContent.Visibility = memory ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task NavigatePageAsync(int index)
    {
        if (navigatingSettings || current == null || index < 0 || index > settingsKeys.Length || PageTabs.SelectedIndex == index) return;
        navigatingSettings = true;
        PageTabs.IsEnabled = SettingsNavigation.IsEnabled = false;
        var vm = current;
        int version = selectionVersion;
        try
        {
            if (!await TryCloseSettingsAsync() || version != selectionVersion || !ReferenceEquals(vm, current)) return;
            if (index > 0) OpenSettings(settingsKeys[index - 1]);
            PageTabs.Select(index);
        }
        finally { navigatingSettings = false; PageTabs.IsEnabled = SettingsNavigation.IsEnabled = true; }
    }
    private void OnSettingsSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (selectingSettings || navigatingSettings || SettingsNavigation.SelectedItem is not ListViewItem { Tag: int index }) return;
        if (sectionedSettings != null) sectionedSettings.ShowSection(index);
    }
    private void OpenSettings(string key)
    {
        if (current is not { } vm || stopSettings != null) return;
        UserControl view = key switch
        {
            "processor" => new VmProcessorSettingsView(vm.Id),
            "memory" => new VmMemorySettingsView(vm.Id),
            "storage" => new VmStorageSettingsView(vm.Id),
            "gpu" => new VmGpuSettingsView(vm.Id),
            "network" => new VmNetworkSettingsView(vm.Id),
            "boot" => new VmBootSettingsView(vm.Id),
            "security" => new VmSecuritySettingsView(vm.Id),
            "advanced" => new VmAdvancedSettingsView(vm.Id),
            "pcie" => new VmPcieSettingsView(vm.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
        sectionedSettings = view is VmSettingsView grouped && grouped.SectionTitles.Any() ? grouped : null;
        ConfigureSettingsNavigation();
        void Changed(object? sender, EventArgs args)
        {
            cache?.Invalidate(vm.Id);
            if (!IsLoaded || current?.Id != vm.Id) return;
            var refresh = RefreshConfigurationAsync(vm.Id);
            work.RemoveAll(task => task.IsCompleted);
            work.Add(refresh);
        }
        UIElement content = view;
        switch (view)
        {
            case VmSettingsView settings:
                settings.Changed += Changed; stopSettings = settings.StopAsync;
                settings.BusyChanged += SetSettingsBusy;
                detachSettingsProgress = () => settings.BusyChanged -= SetSettingsBusy;
                content = settings.HandlesScrolling ? view
                    : new ScrollViewer { Content = view, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                break;
            case VmPcieSettingsView pcie:
                pcie.Changed += Changed; stopSettings = pcie.StopAsync;
                pcie.BusyChanged += SetSettingsBusy;
                detachSettingsProgress = () => pcie.BusyChanged -= SetSettingsBusy;
                break;
        }
        SettingsHost.Content = content;
        PageTabs.Select(Array.IndexOf(settingsKeys, key) + 1);
        DashboardScroll.Visibility = Visibility.Collapsed;
        SettingsArea.Visibility = Visibility.Visible;
    }

    private void ConfigureSettingsNavigation()
    {
        selectingSettings = true;
        try
        {
            if (sectionedSettings != null)
            {
                SettingsNavigation.Items.Clear();
                int index = 0;
                foreach (string title in sectionedSettings.SectionTitles)
                    SettingsNavigation.Items.Add(new ListViewItem
                    {
                        Content = new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                        Tag = index++, MinHeight = 44, Padding = new Thickness(14, 6, 8, 6)
                    });
                SettingsNavigation.SelectedIndex = 0;
            }
            else SettingsNavigation.Items.Clear();
            bool visible = SettingsNavigation.Items.Count > 0;
            SettingsNavigation.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            SettingsNavigationColumn.Width = new GridLength(visible ? 144 : 0);
            SettingsArea.ColumnSpacing = visible ? 16 : 0;
        }
        finally { selectingSettings = false; }
    }

    private void SetSettingsBusy(bool busy)
    {
        SettingsLoadingBar.IsIndeterminate = busy;
        SettingsLoadingBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private Task CloseSettings()
    {
        if (closingSettings is { IsCompleted: false }) return closingSettings;
        var task = CloseSettingsCoreAsync();
        closingSettings = task;
        settingsWork.RemoveAll(work => work.IsCompleted);
        settingsWork.Add(task);
        return task;
    }

    private async Task CloseSettingsCoreAsync()
    {
        SettingsHost.IsEnabled = false;
        try
        {
            if (stopSettings != null) await stopSettings();
            detachSettingsProgress?.Invoke();
            detachSettingsProgress = null;
            SetSettingsBusy(false);
            stopSettings = null;
            sectionedSettings = null;
            SettingsHost.Content = null;
            SettingsArea.Visibility = Visibility.Collapsed;
            DashboardScroll.Visibility = Visibility.Visible;
        }
        finally { SettingsHost.IsEnabled = true; }
    }

    public async Task<bool> TryCloseSettingsAsync()
    {
        try { await CloseSettings(); return true; }
        catch (Exception exception) { ShowError(exception.Message); return false; }
    }

    private async Task RefreshConfigurationAsync(Guid id)
    {
        var selected = current;
        var source = cancellation;
        var token = source?.Token ?? CancellationToken.None;
        try
        {
            VmQueryRow row;
            if (cache != null) row = await cache.LoadDetailsAsync(id, token);
            else
            {
                await using var query = new VmQueryService();
                var result = await Task.Run(() => query.QueryAsync(new VmQueryRequest(ConfigurationFields, [id]), token), token);
                if (!result.HasData) throw new InvalidOperationException(result.Error ?? "无法刷新虚拟机配置。");
                row = result.Data!.VirtualMachines.FirstOrDefault() ?? throw new InvalidOperationException("此虚拟机已不在宿主清单中。");
            }
            token.ThrowIfCancellationRequested();
            if (IsLoaded && ReferenceEquals(current, selected) && ReferenceEquals(cancellation, source))
                ShowConfiguration(row, preserveLiveIdentity: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsLoaded && ReferenceEquals(current, selected) && ReferenceEquals(cancellation, source))
                ShowError(exception.Message);
        }
    }
}
