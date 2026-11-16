using ExHyperV.Services;
using ExHyperV.WinUI3.Localization;
using ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

public sealed partial class VmDashboardView
{
    private const string FluentFont = "ms-appx:///Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";

    private sealed record SubNavItem(string? Key, string Title, string Glyph, string? FontFamily = null, bool IsSeparator = false);

    private static readonly string[] topTabKeys = ["overview", "processor", "memory", "devices", "checkpoints", "advanced"];

    private static SubNavItem[] GetDeviceSubItems() =>
    [
        new("gpu", L10n.Get("Dash_Sub_Gpu"), "\uF211"),
        new("storage", L10n.Get("Dash_Sub_Storage"), "\uEDA2"),
        new("network", L10n.Get("Dash_Sub_Network"), "\uE774"),
        new("serial", L10n.Get("Dash_Sub_Serial"), "\uE950"),
        new(null, "", "", IsSeparator: true),
        new("pcie", L10n.Get("Dash_Sub_Pcie"), "\uF0B9")
    ];

    private static SubNavItem[] GetAdvancedSubItems() =>
    [
        new("boot", L10n.Get("Dash_Sub_Boot"), "\uF2C7"),
        new("security", L10n.Get("Dash_Sub_Security"), "\uEAC9", FluentFont),
        new("console", L10n.Get("Dash_Sub_Console"), "\uE756"),
        new("power", L10n.Get("Dash_Sub_Power"), "\uE7E8"),
        new("storage-policy", L10n.Get("Dash_Sub_Storage"), "\uEDA2")
    ];

    private Func<Task>? stopSettings;
    private Action? detachSettingsProgress;
    private readonly List<Task> settingsWork = [];
    private VmSettingsView? sectionedSettings;
    private Task? closingSettings;
    private bool navigatingSettings;
    private bool selectingSettings;
    private string activeTopKey = "overview";
    private string activeDeviceSubKey = "gpu";
    private string activeAdvancedSubKey = "boot";

    private void InitializeNavigation()
    {
        PageTabs.SetItems(
            L10n.Get("Dash_Tab_Overview"),
            L10n.Get("Dash_Tab_Processor"),
            L10n.Get("Dash_Tab_Memory"),
            L10n.Get("Dash_Tab_Devices"),
            L10n.Get("Dash_Tab_Checkpoints"),
            L10n.Get("Dash_Tab_Advanced"));
        PageTabs.SetIcons(
            new("\uE9D9"),
            new("\uF58E", Rotation: 90),
            new("\uEEA0"),
            new("\uF0E2"),
            new("\uE1DF", FluentFont),
            new("\uEC7A"));
        PageTabs.SelectionRequested += async index => await NavigatePageAsync(index);
    }

    private Task JumpToSettingsAsync(string topKey, string? subKey = null)
    {
        int index = Array.IndexOf(topTabKeys, topKey);
        return index >= 0 ? NavigatePageAsync(index, subKey) : Task.CompletedTask;
    }

    private async Task NavigatePageAsync(int index, string? subKey = null)
    {
        if (navigatingSettings || current == null || index < 0 || index >= topTabKeys.Length) return;
        string targetTopKey = topTabKeys[index];
        if (PageTabs.SelectedIndex == index && subKey == null) return;
        if (PageTabs.SelectedIndex == index && subKey != null)
        {
            string currentSub = targetTopKey == "devices" ? activeDeviceSubKey : activeAdvancedSubKey;
            if (currentSub == subKey) return;
            await SwitchSubPageAsync(subKey);
            return;
        }

        navigatingSettings = true;
        PageTabs.IsEnabled = false;
        var vm = current;
        int version = selectionVersion;
        try
        {
            if (!await TryCloseSettingsAsync(keepSettingsAreaVisible: index > 0 && activeTopKey != "overview")
                || version != selectionVersion || !ReferenceEquals(vm, current))
                return;
            activeTopKey = targetTopKey;
            if (index > 0) OpenTopTab(targetTopKey, subKey);
            else PageTabs.Select(0);
        }
        finally
        {
            navigatingSettings = false;
            PageTabs.IsEnabled = true;
        }
    }

    private async void OnSettingsSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (selectingSettings || navigatingSettings || SettingsNavigation.SelectedItem is not ListViewItem item) return;
        if (item.Tag is int sectionIndex && sectionedSettings != null)
        {
            sectionedSettings.ShowSection(sectionIndex);
            return;
        }
        if (item.Tag is string subKey)
            await SwitchSubPageAsync(subKey);
    }

    private async Task SwitchSubPageAsync(string subKey)
    {
        if (navigatingSettings || current == null) return;
        if (activeTopKey == "devices" && activeDeviceSubKey == subKey) return;
        if (activeTopKey == "advanced" && activeAdvancedSubKey == subKey) return;

        navigatingSettings = true;
        PageTabs.IsEnabled = false;
        var vm = current;
        int version = selectionVersion;
        try
        {
            await CloseSettingsCoreAsync(keepSettingsAreaVisible: true);
            if (version != selectionVersion || !ReferenceEquals(vm, current)) return;
            if (activeTopKey == "devices") activeDeviceSubKey = subKey;
            else if (activeTopKey == "advanced") activeAdvancedSubKey = subKey;
            OpenViewForKey(vm, subKey);
        }
            catch (Exception exception)
        {
            ShowError(CoreMessageResolver.ResolveException(exception));
        }
        finally
        {
            navigatingSettings = false;
            PageTabs.IsEnabled = true;
        }
    }

    private void OpenTopTab(string topKey, string? explicitSubKey = null)
    {
        if (current is not { } vm || stopSettings != null) return;
        activeTopKey = topKey;

        if (topKey == "devices")
        {
            var items = GetDeviceSubItems();
            string subKey = explicitSubKey ?? activeDeviceSubKey;
            if (!items.Any(i => i.Key == subKey)) subKey = "gpu";
            activeDeviceSubKey = subKey;
            ConfigureSubNavigation(items, subKey);
            OpenViewForKey(vm, subKey);
        }
        else if (topKey == "advanced")
        {
            var items = GetAdvancedSubItems();
            string subKey = explicitSubKey ?? activeAdvancedSubKey;
            if (!items.Any(i => i.Key == subKey)) subKey = "boot";
            activeAdvancedSubKey = subKey;
            ConfigureSubNavigation(items, subKey);
            OpenViewForKey(vm, subKey);
        }
        else
        {
            OpenViewForKey(vm, topKey);
        }

        PageTabs.Select(Array.IndexOf(topTabKeys, topKey));
        OverviewArea.Visibility = Visibility.Collapsed;
        SettingsArea.Visibility = Visibility.Visible;
    }

    private void OpenViewForKey(VmListItem vm, string viewKey)
    {
        UserControl view = viewKey switch
        {
            "processor" => new VmProcessorSettingsView(vm.Id),
            "memory" => new VmMemorySettingsView(vm.Id),
            "gpu" => new VmGpuSettingsView(vm.Id),
            "storage" => new VmStorageSettingsView(vm.Id),
            "network" => new VmNetworkSettingsView(vm.Id),
            "serial" => new VmSerialPortSettingsView(vm.Id),
            "pcie" => new VmPcieSettingsView(vm.Id),
            "checkpoints" => new VmSpacetimeSettingsView(vm.Id),
            "boot" => new VmBootSettingsView(vm.Id),
            "security" => new VmSecuritySettingsView(vm.Id),
            "console" => new VmAdvancedSettingsView(vm.Id, VmAdvancedSettingsView.Section.Console),
            "power" => new VmAdvancedSettingsView(vm.Id, VmAdvancedSettingsView.Section.Power),
            "storage-policy" => new VmAdvancedSettingsView(vm.Id, VmAdvancedSettingsView.Section.StoragePolicy),
            _ => throw new ArgumentOutOfRangeException(nameof(viewKey))
        };

        sectionedSettings = view is VmSettingsView grouped && grouped.SectionTitles.Any() ? grouped : null;
        if (viewKey is "processor" or "memory")
            ConfigureSectionNavigation();
        else if (viewKey == "checkpoints")
            ClearSettingsNavigation();

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
                settings.BindFeedbackHost(this);
                settings.Changed += Changed;
                stopSettings = settings.StopAsync;
                settings.BusyChanged += SetSettingsBusy;
                detachSettingsProgress = () => settings.BusyChanged -= SetSettingsBusy;
                content = settings.HandlesScrolling ? view
                    : new ScrollViewer { Content = view, Padding = new Thickness(0, 0, 0, 72), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                break;
            case VmPcieSettingsView pcie:
                pcie.BindFeedbackHost(this);
                pcie.Changed += Changed;
                stopSettings = pcie.StopAsync;
                pcie.BusyChanged += SetSettingsBusy;
                detachSettingsProgress = () => pcie.BusyChanged -= SetSettingsBusy;
                break;
            case VmSpacetimeSettingsView spacetime:
                spacetime.BindFeedbackHost(this);
                spacetime.Changed += Changed;
                stopSettings = spacetime.StopAsync;
                spacetime.BusyChanged += SetSettingsBusy;
                detachSettingsProgress = () => spacetime.BusyChanged -= SetSettingsBusy;
                break;
        }
        SettingsHost.Content = content;
    }

    private void ConfigureSectionNavigation()
    {
        selectingSettings = true;
        try
        {
            var titles = sectionedSettings?.SectionTitles.ToArray() ?? [];
            bool sameItems = titles.Length > 0 && SettingsNavigation.Items.Count == titles.Length
                && SettingsNavigation.Items.Select((obj, idx) => obj is ListViewItem item
                    && item.Tag is int tag && tag == idx
                    && item.Content is TextBlock tb && tb.Text == titles[idx]).All(x => x);

            if (!sameItems)
            {
                SettingsNavigation.Items.Clear();
                for (int i = 0; i < titles.Length; i++)
                {
                    SettingsNavigation.Items.Add(new ListViewItem
                    {
                        Content = new TextBlock { Text = titles[i], FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center },
                        Tag = i,
                        MinHeight = 44,
                        Padding = new Thickness(14, 6, 8, 6)
                    });
                }
            }
            if (titles.Length > 0 && SettingsNavigation.SelectedIndex != 0)
                SettingsNavigation.SelectedIndex = 0;
            SetSettingsNavigationVisible(titles.Length > 0, width: 144);
        }
        finally { selectingSettings = false; }
    }

    private void ConfigureSubNavigation(SubNavItem[] items, string selectedKey)
    {
        selectingSettings = true;
        try
        {
            SettingsNavigation.Items.Clear();
            ListViewItem? selectedItem = null;
            foreach (var entry in items)
            {
                if (entry.IsSeparator)
                {
                    SettingsNavigation.Items.Add(new ListViewItem
                    {
                        IsEnabled = false,
                        IsHitTestVisible = false,
                        MinHeight = 9,
                        Height = 9,
                        Padding = new Thickness(10, 4, 10, 4),
                        Content = new Border
                        {
                            Height = 1,
                            Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
                        }
                    });
                    continue;
                }

                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
                var icon = new FontIcon { Glyph = entry.Glyph, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
                if (!string.IsNullOrEmpty(entry.FontFamily)) icon.FontFamily = new FontFamily(entry.FontFamily);
                row.Children.Add(icon);
                row.Children.Add(new TextBlock { Text = entry.Title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });

                var item = new ListViewItem
                {
                    Content = row,
                    Tag = entry.Key,
                    MinHeight = 40,
                    Padding = new Thickness(12, 6, 8, 6)
                };
                SettingsNavigation.Items.Add(item);
                if (entry.Key == selectedKey) selectedItem = item;
            }
            if (selectedItem != null) SettingsNavigation.SelectedItem = selectedItem;
            SetSettingsNavigationVisible(true, width: 156);
        }
        finally { selectingSettings = false; }
    }

    private void ClearSettingsNavigation()
    {
        selectingSettings = true;
        try
        {
            SettingsNavigation.Items.Clear();
            SetSettingsNavigationVisible(false, width: 0);
        }
        finally { selectingSettings = false; }
    }

    private void SetSettingsNavigationVisible(bool visible, double width)
    {
        SettingsNavigation.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SettingsNavigationColumn.Width = new GridLength(visible ? width : 0);
        SettingsArea.ColumnSpacing = visible ? 16 : 0;
    }

    private void SetSettingsBusy(bool busy)
    {
        SettingsLoadingBar.IsIndeterminate = busy;
        SettingsLoadingBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private Task CloseSettings(bool keepSettingsAreaVisible = false)
    {
        if (closingSettings is { IsCompleted: false }) return closingSettings;
        var task = CloseSettingsCoreAsync(keepSettingsAreaVisible);
        closingSettings = task;
        settingsWork.RemoveAll(work => work.IsCompleted);
        settingsWork.Add(task);
        return task;
    }

    private async Task CloseSettingsCoreAsync(bool keepSettingsAreaVisible)
    {
        SettingsHost.IsEnabled = false;
        try
        {
            if (stopSettings != null) await stopSettings();
            HideSuccessToast();
            ClearSettingsNotice();
            detachSettingsProgress?.Invoke();
            detachSettingsProgress = null;
            SetSettingsBusy(false);
            stopSettings = null;
            sectionedSettings = null;
            SettingsHost.Content = null;
            if (!keepSettingsAreaVisible)
            {
                SettingsArea.Visibility = Visibility.Collapsed;
                OverviewArea.Visibility = Visibility.Visible;
                activeTopKey = "overview";
            }
        }
        finally { SettingsHost.IsEnabled = true; }
    }

    public async Task<bool> TryCloseSettingsAsync(bool keepSettingsAreaVisible = false)
    {
        try { await CloseSettings(keepSettingsAreaVisible); return true; }
        catch (Exception exception)
        {
            if (!DashboardNoticeBanner.IsOpen)
                ShowError(CoreMessageResolver.ResolveException(exception));
            return false;
        }
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
                if (!result.HasData) throw new InvalidOperationException(
                    result.Success ? L10n.Get("Dash_RefreshConfigError") : CoreMessageResolver.ResolveError(result));
                row = result.Data!.VirtualMachines.FirstOrDefault() ?? throw new InvalidOperationException(L10n.Get("Dash_VmNotInHost"));
            }
            token.ThrowIfCancellationRequested();
            if (IsLoaded && ReferenceEquals(current, selected) && ReferenceEquals(cancellation, source))
                ShowConfiguration(row, preserveLiveIdentity: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsLoaded && ReferenceEquals(current, selected) && ReferenceEquals(cancellation, source))
                ShowError(CoreMessageResolver.ResolveException(exception));
        }
    }
}
