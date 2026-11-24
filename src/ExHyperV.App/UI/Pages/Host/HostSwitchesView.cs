
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed partial class HostSwitchesView : UserControl
{
    private readonly Grid root = new() { Margin = new Thickness(16, 0, 16, 16), RowSpacing = 12 };
    private readonly Button addBtn = SettingsActionButton.CreateRect(L10n.Get("Host_Switches_New"), primary: true);
    private readonly Button refreshBtn = SettingsActionButton.CreateRect(L10n.Get("Host_Switches_Refresh"));
    private readonly ProgressRing busyRing = new() { Width = 16, Height = 16, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly ScrollViewer listScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly StackPanel cardsPanel = new() { Spacing = 8 };
    private readonly TextBlock emptyState = new()
    {
        Text = L10n.Get("Host_Switches_Empty"),
        FontSize = 13.5,
        Opacity = 0.65,
        Margin = new Thickness(4, 12, 4, 0),
        Visibility = Visibility.Collapsed
    };

    private HostInventorySnapshot? currentInventory;
    private List<SwitchUpstreamAdapter> cachedBridgeable = [];
    private HostPage? feedbackHost;
    private readonly HashSet<string> expandedIds = new(StringComparer.OrdinalIgnoreCase);
    private bool busy;

    public event Func<Task>? InventoryRefreshRequested;

    public HostSwitchesView()
    {
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        toolbar.Children.Add(addBtn);
        toolbar.Children.Add(refreshBtn);
        toolbar.Children.Add(busyRing);

        addBtn.Click += async (_, _) => await UiEvent.RunAsync(CreateSwitchAsync, message => feedbackHost?.ShowSettingsNotice(message));
        refreshBtn.Click += async (_, _) =>
        {
            if (busy || InventoryRefreshRequested == null) return;
            await RunBusyAsync(InventoryRefreshRequested);
        };

        var bodyStack = new StackPanel { Spacing = 8 };
        bodyStack.Children.Add(emptyState);
        bodyStack.Children.Add(cardsPanel);
        listScroll.AttachScrollbarClearance(bodyStack, bottomSpacing: 0);
        listScroll.Content = bodyStack;

        Grid.SetRow(toolbar, 0);
        Grid.SetRow(listScroll, 1);
        root.Children.Add(toolbar);
        root.Children.Add(listScroll);
        Content = root;

        ActualThemeChanged += (_, _) =>
        {
            if (currentInventory != null) ApplyInventory(currentInventory);
        };
    }

    internal void BindFeedbackHost(HostPage host) => feedbackHost = host;

    public void ApplyInventory(HostInventorySnapshot inventory)
    {
        currentInventory = inventory;
        cachedBridgeable = inventory.PhysicalAdapters.Where(a => a.CanBridge).ToList();
        RebuildCards();
    }

    private void RebuildCards()
    {
        if (currentInventory == null) return;
        double savedScroll = listScroll.VerticalOffset;
        cardsPanel.Children.Clear();

        var switches = currentInventory.Switches;
        emptyState.Visibility = switches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var sw in switches)
            cardsPanel.Children.Add(BuildSwitchExpander(sw, currentInventory));

        listScroll.ChangeView(null, savedScroll, null, disableAnimation: true);
    }

    private Expander BuildSwitchExpander(SwitchInfo sw, HostInventorySnapshot inv)
    {
        bool light = ActualTheme == ElementTheme.Light;
        bool isDefault = SwitchConfigurationRules.IsDefaultSwitch(sw.Id);
        var endpoints = inv.SwitchEndpoints.TryGetValue(sw.SwitchName, out var ep) ? ep : [];

        // Header
        var headerGrid = new Grid { ColumnSpacing = 10 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var ico = new FontIcon { Glyph = "\uE774", FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        var nameTb = new TextBlock
        {
            Text = sw.SwitchName,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        var modeBadge = new Border
        {
            Background = ThemeHelper.TryResourceBrush(ActualTheme, "VmBadgeBrush",
                light ? Color.FromArgb(0x08, 0, 0, 0) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ThemeHelper.TryResourceBrush(ActualTheme, "VmStrokeBrush", Color.FromArgb(0x20, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = string.IsNullOrWhiteSpace(sw.StateError) ? HostUiHelpers.FormatSwitchMode(sw.SwitchType) : L10n.Get("Common_Unavailable"), FontSize = 12 }
        };
        var countTb = new TextBlock
        {
            Text = L10n.Format("Host_Switches_EndpointsCount", endpoints.Count),
            FontSize = 12,
            Opacity = 0.68,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(ico, 0);
        Grid.SetColumn(nameTb, 1);
        Grid.SetColumn(modeBadge, 2);
        Grid.SetColumn(countTb, 3);
        headerGrid.Children.Add(ico);
        headerGrid.Children.Add(nameTb);
        headerGrid.Children.Add(modeBadge);
        headerGrid.Children.Add(countTb);

        if (!string.IsNullOrWhiteSpace(sw.SubnetPrefix))
        {
            var subTb = new TextBlock
            {
                Text = L10n.Format("Host_Switches_Subnet", sw.SubnetPrefix),
                FontSize = 12,
                Opacity = 0.68,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(subTb, 4);
            headerGrid.Children.Add(subTb);
        }

        // Expanded content
        var form = new SettingsFieldPanel { Spacing = 12 };
        bool editable = !isDefault && string.IsNullOrWhiteSpace(sw.StateError);
        bool updating = true;

        var modeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = editable && !busy };
        modeCombo.Items.Add(new ComboBoxItem { Content = L10n.Get("Host_Sw_Mode_BridgeFull"), Tag = SwitchMode.Bridge });
        modeCombo.Items.Add(new ComboBoxItem { Content = L10n.Get("Host_Sw_Mode_Nat"), Tag = SwitchMode.NAT });
        modeCombo.Items.Add(new ComboBoxItem { Content = L10n.Get("Host_Sw_Mode_IsolatedFull"), Tag = SwitchMode.Isolated });
        modeCombo.SelectedIndex = !string.IsNullOrWhiteSpace(sw.StateError) ? -1 : sw.SwitchType switch
        {
            SwitchMode.Bridge => 0,
            SwitchMode.NAT => 1,
            _ => 2
        };

        var upstreamCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var mgmtToggle = new ToggleSwitch
        {
            OnContent = L10n.Get("Host_Switches_Allow"),
            OffContent = L10n.Get("Host_Switches_Deny"),
            IsOn = sw.AllowManagementOS,
            VerticalAlignment = VerticalAlignment.Center
        };

        var upstreamValue = new TextBox { IsReadOnly = true };
        var upstreamField = new Grid();
        upstreamField.Children.Add(upstreamValue);
        upstreamField.Children.Add(upstreamCombo);

        void PopulateUpstreams(SwitchMode selectedMode)
        {
            upstreamCombo.Items.Clear();
            upstreamCombo.Visibility = Visibility.Collapsed;
            upstreamValue.Text = L10n.Get("Host_Switches_UpstreamNone");
            if (selectedMode == SwitchMode.Isolated)
            {
                upstreamCombo.Items.Add(new ComboBoxItem { Content = L10n.Get("Host_Switches_UpstreamNone"), Tag = null });
                upstreamCombo.SelectedIndex = 0;
                upstreamCombo.IsEnabled = false;
                return;
            }

            IReadOnlyList<SwitchUpstreamAdapter> pool = selectedMode == SwitchMode.Bridge
                ? cachedBridgeable
                : inv.PhysicalAdapters;

            int selIdx = -1;
            var list = pool.Where(a => !inv.Switches.Any(other => other.Id != sw.Id && other.Upstream?.ConnectionId == a.ConnectionId)).ToList();

            for (int i = 0; i < list.Count; i++)
            {
                var adapter = list[i];
                upstreamCombo.Items.Add(new ComboBoxItem { Content = adapter.DisplayName, Tag = adapter });
                if (sw.Upstream?.ConnectionId == adapter.ConnectionId)
                    selIdx = i;
            }

            if (list.Count == 0)
            {
                upstreamCombo.Items.Add(new ComboBoxItem { Content = L10n.Get("Host_Switches_UpstreamNone"), Tag = null });
                upstreamCombo.SelectedIndex = 0;
                upstreamCombo.IsEnabled = false;
            }
            else
            {
                upstreamCombo.SelectedIndex = selIdx;
                if (selIdx >= 0) upstreamValue.Text = list[selIdx].DisplayName;
                else if (editable && selectedMode != sw.SwitchType) upstreamCombo.Visibility = Visibility.Visible;
                upstreamCombo.IsEnabled = editable && !busy;
            }
        }

        async Task ApplySelectionAsync()
        {
            var curMode = (modeCombo.SelectedItem as ComboBoxItem)?.Tag is SwitchMode m ? m : sw.SwitchType;
            var curUpstream = (upstreamCombo.SelectedItem as ComboBoxItem)?.Tag as SwitchUpstreamAdapter;

            if (curMode == SwitchMode.NAT)
            {
                mgmtToggle.IsEnabled = false;
                if (curMode != sw.SwitchType && !mgmtToggle.IsOn)
                {
                    bool prev = updating;
                    updating = true;
                    mgmtToggle.IsOn = true;
                    updating = prev;
                }
            }
            else
            {
                mgmtToggle.IsEnabled = editable && !busy;
            }

            bool dirty = editable && SwitchConfigurationRules.IsConfigurationComplete(curMode, curUpstream) && (
                curMode != sw.SwitchType
                || curUpstream?.ConnectionId != sw.Upstream?.ConnectionId
                || mgmtToggle.IsOn != sw.AllowManagementOS);
            if (curMode != sw.SwitchType && SwitchConfigurationRules.RequiresUpstream(curMode) && !upstreamCombo.IsEnabled && curUpstream == null)
            {
                feedbackHost?.ShowSettingsNotice(L10n.Get("Host_Switches_NoUpstream"), InfoBarSeverity.Error);
                RebuildCards();
                return;
            }
            if (dirty && !busy) await ApplyChangeAsync(curMode, curUpstream, mgmtToggle.IsOn);
        }

        PopulateUpstreams(sw.SwitchType);
        mgmtToggle.IsEnabled = editable && !busy && sw.SwitchType != SwitchMode.NAT;
        updating = false;

        modeCombo.SelectionChanged += async (_, _) =>
        {
            if (updating) return;
            var nextMode = (modeCombo.SelectedItem as ComboBoxItem)?.Tag is SwitchMode m ? m : sw.SwitchType;
            updating = true;
            try { PopulateUpstreams(nextMode); }
            finally { updating = false; }
            await ApplySelectionAsync();
        };
        upstreamCombo.SelectionChanged += async (_, _) => { if (!updating) await ApplySelectionAsync(); };
        mgmtToggle.Toggled += async (_, _) => { if (!updating) await ApplySelectionAsync(); };

        form.Children.Add(LabeledControl(L10n.Get("Host_Switches_ModeLabel"), L10n.Get("Host_Switches_ModeDesc"), modeCombo));
        form.Children.Add(LabeledControl(L10n.Get("Host_Switches_UpstreamLabel"), L10n.Get("Host_Switches_UpstreamDesc"), upstreamField));

        var mgmtRow = new Grid { ColumnSpacing = 12 };
        mgmtRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        mgmtRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mgmtLabel = BuildLabel(L10n.Get("Host_Switches_MgmtOsLabel"), L10n.Get("Host_Switches_MgmtOsDesc"));
        Grid.SetColumn(mgmtLabel, 0);
        Grid.SetColumn(mgmtToggle, 1);
        mgmtRow.Children.Add(mgmtLabel);
        mgmtRow.Children.Add(mgmtToggle);
        form.Children.Add(mgmtRow);

        // Connected VM endpoints
        var epTags = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
        if (endpoints.Count == 0)
        {
            epTags.Children.Add(new TextBlock { Text = L10n.Get("Host_Switches_NoEndpoints"), FontSize = 12.5, Opacity = 0.65 });
        }
        else
        {
            foreach (var e in endpoints)
            {
                var pill = new Border
                {
                    Background = ThemeHelper.TryResourceBrush(ActualTheme, "VmBadgeBrush",
                        light ? Color.FromArgb(0x08, 0, 0, 0) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
                    BorderBrush = ThemeHelper.TryResourceBrush(ActualTheme, "VmStrokeBrush", Color.FromArgb(0x20, 0x80, 0x80, 0x80)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 3, 8, 3),
                    Child = new TextBlock { Text = e.Name, FontSize = 12 }
                };
                string tipText = string.Join("\n", new[] { e.MacAddress, e.IpAddress }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (!string.IsNullOrWhiteSpace(tipText))
                    ToolTipService.SetToolTip(pill, tipText);
                epTags.Children.Add(pill);
            }
        }
        form.Children.Add(LabeledControl(L10n.Get("Host_Switches_EndpointsLabel"), L10n.Get("Host_Switches_EndpointsDesc"), epTags));

        async Task ApplyChangeAsync(SwitchMode nextMode, SwitchUpstreamAdapter? nextUpstream, bool nextMgmt)
        {
            string? conflict = SwitchConfigurationRules.GetUpdateConflict(sw.Id, nextMode, nextUpstream, inv.Switches);
            if (conflict != null)
            {
                feedbackHost?.ShowSettingsNotice(conflict, InfoBarSeverity.Error);
                RebuildCards();
                return;
            }

            await RunBusyAsync(async () =>
            {
                try
                {
                    if (Guid.TryParse(sw.Id, out var id))
                        await HyperVSwitchService.UpdateSwitchConfigurationAsync(id, nextMode, nextUpstream, nextMgmt);
                    else
                        await HyperVSwitchService.UpdateSwitchConfigurationAsync(sw.SwitchName, nextMode, nextUpstream, nextMgmt);
                    feedbackHost?.ShowSuccessToast();
                    if (InventoryRefreshRequested != null) await InventoryRefreshRequested();
                }
                catch (Exception ex)
                {
                    feedbackHost?.ShowSettingsNotice(CoreMessageResolver.ResolveException(ex), InfoBarSeverity.Error);
                }
            });
        }

        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
            Header = headerGrid,
            Content = form,
            IsExpanded = expandedIds.Contains(sw.Id)
        };
        expander.Expanding += (_, _) => expandedIds.Add(sw.Id);
        expander.Collapsed += (_, _) => expandedIds.Remove(sw.Id);
        return expander;
    }

    private async Task CreateSwitchAsync()
    {
        if (busy || currentInventory == null) return;
        var used = currentInventory.Switches.Select(sw => sw.SwitchName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int index = used.Count + 1;
        string name;
        do { name = $"Custom Switch {index++}"; } while (used.Contains(name));

        await RunBusyAsync(async () =>
        {
            await HyperVSwitchService.CreateSwitchAsync(name, SwitchMode.Isolated, null);
            if (InventoryRefreshRequested != null) await InventoryRefreshRequested();
            var created = currentInventory.Switches.FirstOrDefault(sw => sw.SwitchName == name);
            if (created != null) expandedIds.Add(created.Id);
            feedbackHost?.ShowSuccessToast();
        });
    }

    private static StackPanel LabeledControl(string title, string desc, UIElement editor)
    {
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(BuildLabel(title, desc));
        stack.Children.Add(editor);
        return stack;
    }

    private static Grid BuildLabel(string title, string desc)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, ColumnSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        var infoBtn = new SettingInfoButton(title, desc);
        Grid.SetColumn(infoBtn, 1);
        grid.Children.Add(infoBtn);
        return grid;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (busy || feedbackHost == null) return;
        await feedbackHost.RunHostOperationAsync(() => RunActionAsync(action));
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        busy = true;
        addBtn.IsEnabled = refreshBtn.IsEnabled = false;
        busyRing.IsActive = true;
        busyRing.Visibility = Visibility.Visible;
        try { await action(); }
        finally
        {
            busy = false;
            addBtn.IsEnabled = refreshBtn.IsEnabled = true;
            busyRing.IsActive = false;
            busyRing.Visibility = Visibility.Collapsed;
            if (IsLoaded) RebuildCards();
        }
    }
}
