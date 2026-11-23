
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

        addBtn.Click += async (_, _) => await UiEvent.RunAsync(() => OpenCreateDialogAsync(), message => feedbackHost?.ShowSettingsNotice(message));
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

        var applyBtn = SettingsActionButton.CreateRect(L10n.Get("Host_Switches_Apply"), primary: true);
        var deleteBtn = SettingsActionButton.CreateRect(L10n.Get("Host_Switches_Delete"));
        deleteBtn.IsEnabled = editable && !busy;

        void PopulateUpstreams(SwitchMode selectedMode)
        {
            upstreamCombo.Items.Clear();
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
            var list = pool.ToList();

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
                upstreamCombo.IsEnabled = editable && !busy;
            }
        }

        void SyncDirtyAndAvailability()
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
            applyBtn.IsEnabled = dirty && !busy;
        }

        PopulateUpstreams(sw.SwitchType);
        SyncDirtyAndAvailability();
        updating = false;

        modeCombo.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            var nextMode = (modeCombo.SelectedItem as ComboBoxItem)?.Tag is SwitchMode m ? m : sw.SwitchType;
            updating = true;
            try { PopulateUpstreams(nextMode); }
            finally { updating = false; }
            SyncDirtyAndAvailability();
        };
        upstreamCombo.SelectionChanged += (_, _) => { if (!updating) SyncDirtyAndAvailability(); };
        mgmtToggle.Toggled += (_, _) => { if (!updating) SyncDirtyAndAvailability(); };

        form.Children.Add(LabeledControl(L10n.Get("Host_Switches_ModeLabel"), L10n.Get("Host_Switches_ModeDesc"), modeCombo));
        form.Children.Add(LabeledControl(L10n.Get("Host_Switches_UpstreamLabel"), L10n.Get("Host_Switches_UpstreamDesc"), upstreamCombo));

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

        // Footer actions & warnings
        var footer = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var noteText = isDefault
            ? L10n.Get("Host_Switches_DefaultProtected")
            : (!string.IsNullOrWhiteSpace(sw.StateError) ? sw.StateError : string.Empty);
        if (!string.IsNullOrWhiteSpace(noteText))
        {
            var noteTb = new TextBlock
            {
                Text = noteText,
                FontSize = 12,
                Foreground = ThemeHelper.TryResourceBrush(ActualTheme,
                    !string.IsNullOrWhiteSpace(sw.StateError) ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush",
                    Color.FromArgb(255, 0xA0, 0xA0, 0xA0)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(noteTb, 0);
            footer.Children.Add(noteTb);
        }

        var actionButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actionButtons.Children.Add(deleteBtn);
        actionButtons.Children.Add(applyBtn);
        Grid.SetColumn(actionButtons, 1);
        footer.Children.Add(actionButtons);
        SettingsFieldPanel.SetSpan2(footer, true);
        form.Children.Add(footer);

        applyBtn.Click += async (_, _) =>
        {
            if (busy || !editable || !applyBtn.IsEnabled) return;
            var nextMode = (modeCombo.SelectedItem as ComboBoxItem)?.Tag is SwitchMode m ? m : sw.SwitchType;
            var nextUpstream = (upstreamCombo.SelectedItem as ComboBoxItem)?.Tag as SwitchUpstreamAdapter;
            bool nextMgmt = mgmtToggle.IsOn;

            string? conflict = SwitchConfigurationRules.GetUpdateConflict(sw.Id, nextMode, nextUpstream, inv.Switches);
            if (conflict != null)
            {
                feedbackHost?.ShowSettingsNotice(conflict, InfoBarSeverity.Error);
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
        };

        deleteBtn.Click += async (_, _) =>
        {
            if (busy || !editable || XamlRoot == null) return;
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = L10n.Format("Host_Switches_DeleteTitle", sw.SwitchName),
                Content = new TextBlock { Text = L10n.Get("Host_Switches_DeleteBody"), TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = L10n.Get("Host_Switches_Delete"),
                CloseButtonText = L10n.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            await RunBusyAsync(async () =>
            {
                try
                {
                    if (Guid.TryParse(sw.Id, out var id))
                        await HyperVSwitchService.DeleteSwitchAsync(id);
                    else
                        await HyperVSwitchService.DeleteSwitchAsync(sw.SwitchName);
                    expandedIds.Remove(sw.Id);
                    feedbackHost?.ShowSuccessToast();
                    if (InventoryRefreshRequested != null) await InventoryRefreshRequested();
                }
                catch (Exception ex)
                {
                    feedbackHost?.ShowSettingsNotice(CoreMessageResolver.ResolveException(ex), InfoBarSeverity.Error);
                }
            });
        };

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

    private async Task OpenCreateDialogAsync()
    {
        if (busy || XamlRoot == null || currentInventory == null) return;

        var nameBox = new TextBox
        {
            PlaceholderText = L10n.Get("Host_Switches_NamePlaceholder"),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var modeCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        modeCombo.Items.Add(new ComboBoxItem { Content = L10n.Get("Host_Sw_Mode_BridgeFull"), Tag = SwitchMode.Bridge });
        modeCombo.Items.Add(new ComboBoxItem { Content = L10n.Get("Host_Sw_Mode_Nat"), Tag = SwitchMode.NAT });
        modeCombo.Items.Add(new ComboBoxItem { Content = L10n.Get("Host_Sw_Mode_IsolatedFull"), Tag = SwitchMode.Isolated });
        modeCombo.SelectedIndex = 2; // Default safe Isolated

        var upstreamCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var validationTb = new TextBlock
        {
            FontSize = 12,
            Foreground = ThemeHelper.TryResourceBrush(ActualTheme, "SystemFillColorCriticalBrush", Color.FromArgb(255, 0xFF, 0x6B, 0x6B)),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        void RefreshAdapterChoices()
        {
            upstreamCombo.Items.Clear();
            var mode = (modeCombo.SelectedItem as ComboBoxItem)?.Tag is SwitchMode m ? m : SwitchMode.Isolated;
            if (!SwitchConfigurationRules.RequiresUpstream(mode))
            {
                upstreamCombo.Items.Add(new ComboBoxItem { Content = L10n.Get("Host_Switches_UpstreamNone"), Tag = null });
                upstreamCombo.SelectedIndex = 0;
                upstreamCombo.IsEnabled = false;
                return;
            }
            var available = SwitchConfigurationRules.GetAvailableCreationAdapters(
                mode, currentInventory.Switches, currentInventory.PhysicalAdapters, cachedBridgeable);
            foreach (var a in available)
                upstreamCombo.Items.Add(new ComboBoxItem { Content = a.DisplayName, Tag = a });
            if (available.Count == 0)
            {
                upstreamCombo.Items.Add(new ComboBoxItem { Content = L10n.Get("Host_Switches_UpstreamNone"), Tag = null });
                upstreamCombo.SelectedIndex = 0;
                upstreamCombo.IsEnabled = false;
            }
            else
            {
                upstreamCombo.SelectedIndex = 0;
                upstreamCombo.IsEnabled = true;
            }
        }

        RefreshAdapterChoices();
        modeCombo.SelectionChanged += (_, _) => RefreshAdapterChoices();

        var body = new StackPanel { Spacing = 10, MinWidth = 360 };
        body.Children.Add(nameBox);
        body.Children.Add(new TextBlock { Text = L10n.Get("Host_Switches_ModeLabel"), FontSize = 12.5 });
        body.Children.Add(modeCombo);
        body.Children.Add(new TextBlock { Text = L10n.Get("Host_Switches_UpstreamLabel"), FontSize = 12.5 });
        body.Children.Add(upstreamCombo);
        body.Children.Add(validationTb);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L10n.Get("Host_Switches_DialogTitle"),
            Content = body,
            PrimaryButtonText = L10n.Get("Host_Switches_CreateConfirm"),
            CloseButtonText = L10n.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            var mode = (modeCombo.SelectedItem as ComboBoxItem)?.Tag is SwitchMode m ? m : SwitchMode.Isolated;
            var upstream = (upstreamCombo.SelectedItem as ComboBoxItem)?.Tag as SwitchUpstreamAdapter;
            string? err = SwitchConfigurationRules.GetCreationError(
                nameBox.Text.Trim(), mode, upstream,
                currentInventory.Switches, currentInventory.PhysicalAdapters, cachedBridgeable);
            if (err != null)
            {
                args.Cancel = true;
                validationTb.Text = err;
                validationTb.Visibility = Visibility.Visible;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string newName = nameBox.Text.Trim();
        var chosenMode = (modeCombo.SelectedItem as ComboBoxItem)?.Tag is SwitchMode cm ? cm : SwitchMode.Isolated;
        var chosenUpstream = (upstreamCombo.SelectedItem as ComboBoxItem)?.Tag as SwitchUpstreamAdapter;

        await RunBusyAsync(async () =>
        {
            try
            {
                await HyperVSwitchService.CreateSwitchAsync(newName, chosenMode, chosenUpstream);
                feedbackHost?.ShowSuccessToast();
                if (InventoryRefreshRequested != null) await InventoryRefreshRequested();
            }
            catch (Exception ex)
            {
                feedbackHost?.ShowSettingsNotice(CoreMessageResolver.ResolveException(ex), InfoBarSeverity.Error);
            }
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
