
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.App.UI.Shared;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed partial class HostSettingsView : UserControl
{
    private const string DefaultMacMin = "00-15-5D-01-00-00";
    private const string DefaultMacMax = "00-15-5D-01-FF-FF";

    private sealed record SubSectionItem(string Key, string Title, string Glyph);

    private readonly SubSectionItem[] sections =
    [
        new("scheduler", L10n.Get("Host_Set_Sub_Scheduler"), "\uF58E"),
        new("power", L10n.Get("Host_Set_Sub_Power"), "\uE945"),
        new("paths", L10n.Get("Host_Set_Sub_Paths"), "\uE8B7"),
        new("storage", L10n.Get("Host_Set_Sub_Storage"), "\uEDA2"),
        new("mac", L10n.Get("Host_Set_Sub_Mac"), "\uE774"),
        new("compat", L10n.Get("Host_Set_Sub_Compat"), "\uF0B9"),
        new("preferences", L10n.Get("Host_Set_Sub_Preferences"), "\uE756")
    ];

    private readonly Grid rootGrid = new() { Margin = new Thickness(16, 0, 16, 16), ColumnSpacing = 16 };
    private readonly ListView subNav = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ScrollViewer rightScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly ContentControl sectionHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ProgressBar busyBar = new()
    {
        Height = 2,
        VerticalAlignment = VerticalAlignment.Top,
        IsIndeterminate = false,
        Visibility = Visibility.Collapsed,
        IsHitTestVisible = false
    };

    private string activeSub = "scheduler";
    private HostInventorySnapshot? currentInventory;
    private HostPage? feedbackHost;
    private bool updating;
    private bool busy;

    public event Func<Task>? InventoryRefreshRequested;

    public HostSettingsView()
    {
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(156) });
        rootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        subNav.ItemContainerTransitions.Clear();
        foreach (var item in sections)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            var icon = new FontIcon
            {
                Glyph = item.Glyph,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5)
            };
            if (item.Key == "scheduler")
                icon.RenderTransform = new RotateTransform { Angle = 90 };
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = item.Title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
            subNav.Items.Add(new ListViewItem
            {
                Content = row,
                Tag = item.Key,
                MinHeight = 40,
                Padding = new Thickness(12, 6, 8, 6)
            });
        }

        subNav.SelectedIndex = 0;
        subNav.SelectionChanged += (_, _) =>
        {
            if (updating || subNav.SelectedItem is not ListViewItem { Tag: string key }) return;
            activeSub = key;
            RenderSection();
        };

        rightScroll.AttachScrollbarClearance(sectionHost, bottomSpacing: 0);
        rightScroll.Content = sectionHost;

        var rightPane = new Grid();
        rightPane.Children.Add(rightScroll);
        rightPane.Children.Add(busyBar);

        Grid.SetColumn(subNav, 0);
        Grid.SetColumn(rightPane, 1);
        rootGrid.Children.Add(subNav);
        rootGrid.Children.Add(rightPane);

        Content = rootGrid;
        ActualThemeChanged += (_, _) =>
        {
            ApplyThemeBrushes();
            RenderSection();
        };
        sectionHost.SizeChanged += (_, _) =>
        {
            if (activeSub == "scheduler") RenderSection();
        };
        ApplyThemeBrushes();
    }

    internal void BindFeedbackHost(HostPage host) => feedbackHost = host;

    public void SelectSection(string subKey)
    {
        if (!sections.Any(s => s.Key == subKey)) return;
        activeSub = subKey;
        updating = true;
        try
        {
            for (int i = 0; i < subNav.Items.Count; i++)
            {
                if (subNav.Items[i] is ListViewItem item && Equals(item.Tag, subKey))
                {
                    subNav.SelectedIndex = i;
                    break;
                }
            }
        }
        finally { updating = false; }
        RenderSection();
    }

    public void ApplyInventory(HostInventorySnapshot inventory)
    {
        currentInventory = inventory;
        RenderSection();
    }

    public void RefreshTheme()
    {
        ApplyThemeBrushes();
        RenderSection();
    }

    private void ApplyThemeBrushes()
    {
        busyBar.Foreground = ThemeHelper.TryResourceBrush(ActualTheme, "VmAccentBrush", Color.FromArgb(255, 0x4C, 0xC2, 0xFF));
    }

    private void RenderSection()
    {
        if (currentInventory == null) return;
        updating = true;
        try
        {
            sectionHost.Content = activeSub switch
            {
                "scheduler" => BuildSchedulerSection(currentInventory),
                "power" => BuildPowerSection(),
                "paths" => BuildPathsSection(currentInventory),
                "storage" => BuildStorageSection(currentInventory),
                "mac" => BuildMacSection(currentInventory),
                "compat" => BuildCompatSection(currentInventory),
                _ => BuildPreferencesSection(currentInventory)
            };
        }
        finally
        {
            updating = false;
        }
    }

    private UIElement BuildSchedulerSection(HostInventorySnapshot inv)
    {
        var panel = new SettingsFieldPanel { Spacing = 12 };
        HyperVSchedulerType displayedSched = inv.ConfiguredScheduler != HyperVSchedulerType.Unknown
            ? inv.ConfiguredScheduler
            : inv.RunningScheduler;

        var pill = new SettingsSegmentedPill { Width = 300 };
        pill.SetOptions((SettingOption[])[
            new SettingOption(HyperVSchedulerType.Classic, L10n.Get("Host_Sched_Classic")),
            new SettingOption(HyperVSchedulerType.Core, L10n.Get("Host_Sched_Core")),
            new SettingOption(HyperVSchedulerType.Root, L10n.Get("Host_Sched_Root"))
        ]);
        pill.SelectedValue = displayedSched;
        pill.SetControlEnabled(!busy);

        string schedSub = string.Empty;
        if (inv.RunningScheduler != HyperVSchedulerType.Unknown
            && inv.ConfiguredScheduler != HyperVSchedulerType.Unknown
            && inv.RunningScheduler != inv.ConfiguredScheduler)
        {
            schedSub = L10n.Format("Host_Set_Sched_PendingMismatch",
                HostUiHelpers.FormatSchedulerLabel(inv.ConfiguredScheduler, true),
                HostUiHelpers.FormatSchedulerLabel(inv.RunningScheduler, true));
        }

        pill.SelectionChanged += async val =>
        {
            if (updating || busy || val is not HyperVSchedulerType next) return;
            await RunBusyAsync(async () =>
            {
                var res = await HyperVSchedulerService.SetSchedulerTypeAsync(next);
                if (!res.Success)
                {
                    feedbackHost?.ShowSettingsNotice(CoreMessageResolver.ResolveError(res), InfoBarSeverity.Error);
                }
                else
                {
                    feedbackHost?.ShowSettingsNotice(
                        L10n.Format("Host_Set_Sched_ChangedNotice", HostUiHelpers.FormatSchedulerLabel(next, true)),
                        InfoBarSeverity.Warning);
                }
                await RefreshInventoryAsync();
            });
        };

        var schedCard = SettingCard(Row(
            L10n.Get("Host_Set_Sched_Title"),
            L10n.Get("Host_Set_Sched_Desc"),
            pill,
            schedSub));
        SettingsFieldPanel.SetSpan2(schedCard, true);
        panel.Children.Add(schedCard);

        // 3-card Scheduler Guide (Classic / Core / Root with [0][1][2][3] & Auto overlay)
        var guideCard = SettingCard(BuildSchedulerGuide(displayedSched));
        SettingsFieldPanel.SetSpan2(guideCard, true);
        panel.Children.Add(guideCard);

        // NUMA Spanning toggle
        var numaSwitch = CreateToggle(inv.NumaSpanningEnabled == true, inv.NumaSpanningEnabled.HasValue && !busy);
        numaSwitch.Toggled += async (_, _) =>
        {
            if (updating || busy) return;
            bool next = numaSwitch.IsOn;
            await RunBusyAsync(async () =>
            {
                var res = await HyperVNumaService.SetNumaSpanningEnabledAsync(next);
                HandleApiResponse(res);
                await RefreshInventoryAsync();
            });
        };
        var numaCard = SettingCard(Row(L10n.Get("Host_Set_Numa_Title"), L10n.Get("Host_Set_Numa_Desc"), numaSwitch));
        SettingsFieldPanel.SetSpan2(numaCard, true);
        panel.Children.Add(numaCard);

        return panel;
    }

    private UIElement BuildSchedulerGuide(HyperVSchedulerType active)
    {
        bool light = ActualTheme == ElementTheme.Light;
        Color accent = ThemeHelper.ThemeColor(ActualTheme, "VmAccentBrush", Color.FromArgb(255, 0x4C, 0xC2, 0xFF));
        Color cardBg = ThemeHelper.ThemeColor(ActualTheme, "LayerFillColorDefaultBrush",
            light ? Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x28, 0x00, 0x00, 0x00));
        Color borderDefault = ThemeHelper.ThemeColor(ActualTheme, "CardStrokeColorDefaultBrush",
            Color.FromArgb(0x22, 0x80, 0x80, 0x80));
        Color primary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White);
        Color secondary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        Color headerBg = ThemeHelper.ThemeColor(ActualTheme, "VmHeaderCheckedBrush", accent);
        Color headerFg = ThemeHelper.ThemeColor(ActualTheme, "VmHeaderForegroundBrush", Colors.White);

        var guides = new (HyperVSchedulerType Mode, string Title, int[] Selected, bool Auto, string Body)[]
        {
            (HyperVSchedulerType.Classic, L10n.Get("Host_Sched_ClassicFull"), [0, 1, 2, 3], false, L10n.Get("Host_Set_Sched_ClassicBody")),
            (HyperVSchedulerType.Core, L10n.Get("Host_Sched_CoreFull"), [0, 2], false, L10n.Get("Host_Set_Sched_CoreBody")),
            (HyperVSchedulerType.Root, L10n.Get("Host_Sched_RootFull"), [], true, L10n.Get("Host_Set_Sched_RootBody"))
        };

        bool stacked = sectionHost.ActualWidth > 0 && sectionHost.ActualWidth < 740;
        var grid = new Grid { Padding = new Thickness(18, 16, 18, 16), ColumnSpacing = 12, RowSpacing = 12 };
        if (stacked)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            for (int r = 0; r < 3; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        else
        {
            for (int c = 0; c < 3; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int idx = 0; idx < guides.Length; idx++)
        {
            var g = guides[idx];
            bool isActive = g.Mode == active;

            var tileRow = new Grid { ColumnSpacing = 6 };
            for (int t = 0; t < 4; t++)
                tileRow.ColumnDefinitions.Add(new ColumnDefinition());

            for (int core = 0; core < 4; core++)
            {
                bool on = g.Selected.Contains(core);
                var tile = new Border
                {
                    Height = 42,
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    Background = on
                        ? new SolidColorBrush(headerBg)
                        : new SolidColorBrush(light ? Color.FromArgb(0x0A, 0, 0, 0) : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
                    BorderBrush = on ? new SolidColorBrush(headerBg) : new SolidColorBrush(borderDefault),
                    Child = new TextBlock
                    {
                        Text = core.ToString(),
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = new SolidColorBrush(on ? headerFg : primary)
                    }
                };
                Grid.SetColumn(tile, core);
                tileRow.Children.Add(tile);
            }

            var demoWrap = new Grid();
            if (g.Auto) tileRow.Opacity = 0.35;
            demoWrap.Children.Add(tileRow);

            if (g.Auto)
            {
                var autoBadge = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(12, 3, 12, 3),
                    CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(1),
                    Background = new SolidColorBrush(light ? Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xEB, 0x20, 0x20, 0x20)),
                    BorderBrush = new SolidColorBrush(accent),
                    Child = new TextBlock
                    {
                        Text = L10n.Get("Common_Auto"),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(accent)
                    }
                };
                demoWrap.Children.Add(autoBadge);
            }

            var itemStack = new StackPanel { Spacing = 8 };
            itemStack.Children.Add(new TextBlock
            {
                Text = g.Title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primary)
            });
            itemStack.Children.Add(demoWrap);
            itemStack.Children.Add(new TextBlock
            {
                Text = g.Body,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(secondary)
            });

            var itemCard = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 12, 14, 12),
                Background = new SolidColorBrush(isActive ? Color.FromArgb(20, accent.R, accent.G, accent.B) : cardBg),
                BorderBrush = new SolidColorBrush(isActive ? accent : borderDefault),
                Child = itemStack
            };
            Grid.SetRow(itemCard, stacked ? idx : 0);
            Grid.SetColumn(itemCard, stacked ? 0 : idx);
            grid.Children.Add(itemCard);
        }

        return grid;
    }

    private UIElement BuildPowerSection()
    {
        var panel = new SettingsFieldPanel { Spacing = 12 };
        var plans = HostPowerPlanService.GetPowerPlans();
        Guid? activeId = HostPowerPlanService.GetActivePowerPlanId();

        var combo = new ComboBox
        {
            MinWidth = 220,
            IsEnabled = plans.Count > 0 && !busy
        };
        int selectedIdx = -1;
        for (int i = 0; i < plans.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = plans[i].Name, Tag = plans[i].Id });
            if (activeId == plans[i].Id) selectedIdx = i;
        }
        combo.SelectedIndex = selectedIdx;

        combo.SelectionChanged += async (_, _) =>
        {
            if (updating || busy || combo.SelectedItem is not ComboBoxItem { Tag: Guid planId }) return;
            await RunBusyAsync(async () =>
            {
                try
                {
                    await Task.Run(() => HostPowerPlanService.SetActivePowerPlan(planId));
                    feedbackHost?.ShowSuccessToast();
                    await RefreshInventoryAsync();
                }
                catch (Exception ex)
                {
                    feedbackHost?.ShowSettingsNotice(CoreMessageResolver.ResolveException(ex), InfoBarSeverity.Error);
                    RenderSection();
                }
            });
        };

        var card = SettingCard(Row(L10n.Get("Host_Set_Power_Title"), L10n.Get("Host_Set_Power_Desc"), combo));
        SettingsFieldPanel.SetSpan2(card, true);
        panel.Children.Add(card);
        return panel;
    }

    private UIElement BuildPathsSection(HostInventorySnapshot inv)
    {
        var panel = new SettingsFieldPanel { Spacing = 12 };

        panel.Children.Add(CreatePathCard(
            L10n.Get("Host_Set_PathVm_Title"),
            L10n.Get("Host_Set_PathVm_Desc"),
            inv.HostSettings?.DefaultVirtualMachinePath ?? string.Empty,
            path => HyperVHostSettingsService.SetDefaultVirtualMachinePathAsync(path)));

        panel.Children.Add(CreatePathCard(
            L10n.Get("Host_Set_PathVhd_Title"),
            L10n.Get("Host_Set_PathVhd_Desc"),
            inv.HostSettings?.DefaultVirtualHardDiskPath ?? string.Empty,
            path => HostDiskService.SetDefaultVirtualHardDiskPathAsync(path)));

        return panel;
    }

    private Border CreatePathCard(string title, string desc, string currentPath, Func<string, Task<ApiResponse>> setter)
    {
        var box = new TextBox
        {
            Text = currentPath,
            IsReadOnly = true,
            Width = 280,
            VerticalAlignment = VerticalAlignment.Center
        };
        var btn = SettingsActionButton.CreateRect(L10n.Get("Host_Set_Browse"));
        btn.IsEnabled = !busy;
        btn.Click += async (_, _) =>
        {
            if (busy) return;
            await RunBusyAsync(async () =>
            {
                string? folder = await StoragePickers.FolderAsync();
                if (string.IsNullOrWhiteSpace(folder) || !IsLoaded) return;
                var res = await setter(folder);
                HandleApiResponse(res);
                await RefreshInventoryAsync();
            });
        };

        var editor = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        editor.Children.Add(box);
        editor.Children.Add(btn);
        var card = SettingCard(Row(title, desc, editor));
        SettingsFieldPanel.SetSpan2(card, true);
        return card;
    }

    private UIElement BuildStorageSection(HostInventorySnapshot inv)
    {
        var panel = new SettingsFieldPanel { Spacing = 12 };

        var autoMount = CreateToggle(inv.VolumeAutoMountEnabled == true, inv.VolumeAutoMountEnabled.HasValue && !busy);
        autoMount.Toggled += async (_, _) =>
        {
            if (updating || busy) return;
            bool next = autoMount.IsOn;
            await RunBusyAsync(async () =>
            {
                var res = await HostVolumeAutoMountService.SetEnabledAsync(next);
                HandleApiResponse(res);
                await RefreshInventoryAsync();
            });
        };
        panel.Children.Add(SettingCard(Row(L10n.Get("Host_Set_AutoMount_Title"), L10n.Get("Host_Set_AutoMount_Desc"), autoMount)));

        bool nvmeSupported = inv.NativeNvmeSupported;
        var nvmeSwitch = CreateToggle(inv.NativeNvmeEnabled == true, nvmeSupported && inv.NativeNvmeEnabled.HasValue && !busy);
        string? nvmeDisabledReason = nvmeSupported ? null : L10n.Get("Host_Set_NativeNvme_Unsupported");
        nvmeSwitch.Toggled += async (_, _) =>
        {
            if (updating || busy) return;
            bool next = nvmeSwitch.IsOn;
            await RunBusyAsync(async () =>
            {
                var res = await Task.Run(() => HostNvmeService.SetNativeNvmeEnabled(next));
                HandleApiResponse(res, restartNoticeKey: "Host_Set_NativeNvme_Restart");
                await RefreshInventoryAsync();
            });
        };
        panel.Children.Add(SettingCard(Row(L10n.Get("Host_Set_NativeNvme_Title"), L10n.Get("Host_Set_NativeNvme_Desc"), nvmeSwitch, disabledReason: nvmeDisabledReason)));

        return panel;
    }

    private UIElement BuildMacSection(HostInventorySnapshot inv)
    {
        var panel = new SettingsFieldPanel { Spacing = 12 };
        string savedMin = HostUiHelpers.FormatMacWithDashes(inv.HostSettings?.MinimumMacAddress);
        string savedMax = HostUiHelpers.FormatMacWithDashes(inv.HostSettings?.MaximumMacAddress);

        var minBox = new TextBox { Text = savedMin, Width = 148, MaxLength = 17, PlaceholderText = DefaultMacMin };
        var arrow = new TextBlock { Text = "→", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
        var maxBox = new TextBox { Text = savedMax, Width = 148, MaxLength = 17, PlaceholderText = DefaultMacMax };
        var resetBtn = SettingsActionButton.CreateRect(L10n.Get("Host_Set_Mac_Reset"));
        var saveBtn = SettingsActionButton.CreateRect(L10n.Get("Host_Set_Mac_Save"), primary: true);

        void SyncButtons()
        {
            string curMin = HostUiHelpers.FormatMacWithDashes(minBox.Text);
            string curMax = HostUiHelpers.FormatMacWithDashes(maxBox.Text);
            bool dirty = !string.Equals(curMin, savedMin, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(curMax, savedMax, StringComparison.OrdinalIgnoreCase);
            bool isDefault = string.Equals(curMin, DefaultMacMin, StringComparison.OrdinalIgnoreCase)
                && string.Equals(curMax, DefaultMacMax, StringComparison.OrdinalIgnoreCase)
                && string.Equals(savedMin, DefaultMacMin, StringComparison.OrdinalIgnoreCase)
                && string.Equals(savedMax, DefaultMacMax, StringComparison.OrdinalIgnoreCase);

            saveBtn.IsEnabled = dirty && !busy;
            resetBtn.IsEnabled = !isDefault && !busy;
        }

        minBox.TextChanged += (_, _) => SyncButtons();
        maxBox.TextChanged += (_, _) => SyncButtons();
        resetBtn.Click += async (_, _) =>
        {
            if (busy) return;
            await RunBusyAsync(async () =>
            {
                var res = await HostNetworkService.SetDynamicMacAddressRangeAsync(DefaultMacMin, DefaultMacMax);
                HandleApiResponse(res);
                await RefreshInventoryAsync();
            });
        };
        saveBtn.Click += async (_, _) =>
        {
            if (busy) return;
            await RunBusyAsync(async () =>
            {
                var res = await HostNetworkService.SetDynamicMacAddressRangeAsync(minBox.Text, maxBox.Text);
                HandleApiResponse(res);
                await RefreshInventoryAsync();
            });
        };

        SyncButtons();

        var editor = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        editor.Children.Add(minBox);
        editor.Children.Add(arrow);
        editor.Children.Add(maxBox);
        editor.Children.Add(resetBtn);
        editor.Children.Add(saveBtn);

        var card = SettingCard(Row(L10n.Get("Host_Set_Mac_Title"), L10n.Get("Host_Set_Mac_Desc"), editor));
        SettingsFieldPanel.SetSpan2(card, true);
        panel.Children.Add(card);
        return panel;
    }

    private UIElement BuildCompatSection(HostInventorySnapshot inv)
    {
        var panel = new SettingsFieldPanel { Spacing = 12 };

        var gpuSwitch = CreateToggle(inv.GpuRestrictionsRelaxed == true, inv.GpuRestrictionsRelaxed.HasValue && !busy);
        gpuSwitch.Toggled += async (_, _) =>
        {
            if (updating || busy) return;
            bool next = gpuSwitch.IsOn;
            await RunBusyAsync(async () =>
            {
                var res = await Task.Run(() => HostGpuService.SetAssignmentRestrictionsRelaxed(next));
                HandleApiResponse(res);
                await RefreshInventoryAsync();
            });
        };
        panel.Children.Add(SettingCard(Row(L10n.Get("Host_Set_GpuUnlock_Title"), L10n.Get("Host_Set_GpuUnlock_Desc"), gpuSwitch)));

        var openHclSwitch = CreateToggle(inv.OpenHclFromFileEnabled == true, inv.OpenHclFromFileEnabled.HasValue && !busy);
        openHclSwitch.Toggled += async (_, _) =>
        {
            if (updating || busy) return;
            bool next = openHclSwitch.IsOn;
            await RunBusyAsync(async () =>
            {
                var (ok, err) = await Task.Run(() => HostOpenHclService.SetFirmwareLoadFromFileEnabled(next));
                if (ok) feedbackHost?.ShowSuccessToast();
                else feedbackHost?.ShowSettingsNotice(err, InfoBarSeverity.Error);
                await RefreshInventoryAsync();
            });
        };
        panel.Children.Add(SettingCard(Row(L10n.Get("Host_Set_OpenHcl_Title"), L10n.Get("Host_Set_OpenHcl_Desc"), openHclSwitch)));

        // ProductType Server/WinNT toggle
        var pending = inv.PendingProductSwitch;
        bool canSwitch = inv.ProductSwitchReadError == null && HostProductTypeService.CanApplySwitch(pending);
        bool effectiveState = pending is { IsConfirmed: true }
            ? string.Equals(pending.TargetType, "ServerNT", StringComparison.OrdinalIgnoreCase)
            : inv.IsServerSystem;
        var serverSwitch = CreateToggle(effectiveState, canSwitch && !busy);
        string subNote = inv.ProductSwitchReadError ?? (pending switch
        {
            { IsConfirmed: true } => L10n.Format("Host_Env_PendingSwitch", pending.TargetType),
            not null => L10n.Get("Host_ProductUnconfirmed"),
            _ => string.Empty
        });
        if (pending?.RecoveryPaths.Count > 0)
            subNote += Environment.NewLine + string.Join(Environment.NewLine, pending.RecoveryPaths);

        serverSwitch.Toggled += async (_, _) => await UiEvent.RunAsync(async () =>
        {
            if (updating || busy || XamlRoot == null) return;
            bool toServer = serverSwitch.IsOn;
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = L10n.Get("Host_Set_ServerSpoof_ConfirmTitle"),
                Content = new TextBlock
                {
                    Text = L10n.Get(toServer ? "Host_Set_ServerSpoof_ConfirmToServer" : "Host_Set_ServerSpoof_ConfirmToClient"),
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = L10n.Get("Common_Confirm"),
                CloseButtonText = L10n.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                updating = true;
                try { serverSwitch.IsOn = !toServer; }
                finally { updating = false; }
                return;
            }

            await RunBusyAsync(async () =>
            {
                var result = await Task.Run(() => HostProductTypeService.ApplySwitch(toServer));
                if (!result.Success)
                {
                    var details = new List<string> { CoreMessageResolver.ResolveError(result.Response) };
                    if (result.ReplacementSubmitted) details.Add(L10n.Get("Host_ProductSubmitted"));
                    if (result.MountedHiveKey != null) details.Add(result.MountedHiveKey);
                    if (result.RecoveryPaths.Count > 0)
                    {
                        details.Add(L10n.Get("Host_ProductRecovery"));
                        details.AddRange(result.RecoveryPaths);
                    }
                    feedbackHost?.ShowSettingsNotice(string.Join(Environment.NewLine, details), InfoBarSeverity.Error);
                }
                else
                {
                    feedbackHost?.ShowSettingsNotice(
                        L10n.Format("Host_Set_ServerSpoof_Restart", result.TargetType),
                        InfoBarSeverity.Warning);
                }
                await RefreshInventoryAsync();
            });
        }, message => feedbackHost?.ShowSettingsNotice(message));

        var serverCard = SettingCard(Row(
            L10n.Get("Host_Set_ServerSpoof_Title"),
            L10n.Get("Host_Set_ServerSpoof_Desc"),
            serverSwitch,
            subNote,
            canSwitch ? null : subNote));
        SettingsFieldPanel.SetSpan2(serverCard, true);
        panel.Children.Add(serverCard);

        return panel;
    }

    private UIElement BuildPreferencesSection(HostInventorySnapshot inv)
    {
        var panel = new SettingsFieldPanel { Spacing = 12 };
        var appearance = ((App)Application.Current).Appearance;
        var prefs = appearance.Settings;

        // 1. Enhanced Session Mode (Host-side Hyper-V WMI setting)
        var enhSwitch = CreateToggle(inv.HostSettings?.EnhancedSessionModeEnabled == true, inv.HostSettings != null && !busy);
        enhSwitch.Toggled += async (_, _) =>
        {
            if (updating || busy) return;
            bool next = enhSwitch.IsOn;
            await RunBusyAsync(async () =>
            {
                var res = await HostConsoleService.SetEnhancedSessionModeEnabledAsync(next);
                HandleApiResponse(res);
                await RefreshInventoryAsync();
            });
        };
        panel.Children.Add(SettingCard(Row(L10n.Get("Host_Set_EnhancedSession_Title"), L10n.Get("Host_Set_EnhancedSession_Desc"), enhSwitch)));

        // 2. Auto-Open Console on Start (App preference)
        var autoConsole = CreateToggle(prefs.AutoOpenConsole, !busy);
        autoConsole.Toggled += (_, _) =>
        {
            if (updating || busy) return;
            string? err = appearance.Set(appearance.Settings with { AutoOpenConsole = autoConsole.IsOn });
            if (err != null) feedbackHost?.ShowSettingsNotice(err, InfoBarSeverity.Error);
            else feedbackHost?.ShowSuccessToast();
        };
        panel.Children.Add(SettingCard(Row(L10n.Get("Host_Set_AutoConsole_Title"), L10n.Get("Host_Set_AutoConsole_Desc"), autoConsole)));

        // 3. Default Power Button Action (App preference)
        var powerPill = new SettingsSegmentedPill { Width = 220 };
        powerPill.SetOptions((SettingOption[])[
            new SettingOption("Shutdown", L10n.Get("Host_Set_DefaultPower_Shutdown")),
            new SettingOption("Save", L10n.Get("Host_Set_DefaultPower_Save"))
        ]);
        powerPill.SelectedValue = prefs.DefaultPowerAction;
        powerPill.SetControlEnabled(!busy);
        powerPill.SelectionChanged += val =>
        {
            if (updating || busy || val is not string next) return;
            string? err = appearance.Set(appearance.Settings with { DefaultPowerAction = AppearanceSettings.NormalizePowerAction(next) });
            if (err != null) feedbackHost?.ShowSettingsNotice(err, InfoBarSeverity.Error);
            else feedbackHost?.ShowSuccessToast();
        };

        var powerCard = SettingCard(Row(L10n.Get("Host_Set_DefaultPower_Title"), L10n.Get("Host_Set_DefaultPower_Desc"), powerPill));
        SettingsFieldPanel.SetSpan2(powerCard, true);
        panel.Children.Add(powerCard);

        return panel;
    }

    private static ToggleSwitch CreateToggle(bool isOn, bool isEnabled) => new()
    {
        OnContent = string.Empty,
        OffContent = string.Empty,
        MinWidth = 0,
        VerticalAlignment = VerticalAlignment.Center,
        IsOn = isOn,
        IsEnabled = isEnabled
    };

    private Border SettingCard(UIElement child)
    {
        bool light = ActualTheme == ElementTheme.Light;
        return new Border
        {
            Background = ThemeHelper.TryResourceBrush(ActualTheme, "CardBackgroundFillColorDefaultBrush",
                light ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ThemeHelper.TryResourceBrush(ActualTheme, "CardStrokeColorDefaultBrush",
                Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = child
        };
    }

    private Grid Row(string title, string desc, FrameworkElement editor, string subNote = "", string? disabledReason = null)
    {
        var row = new Grid { Margin = new Thickness(24, 16, 24, 16), ColumnSpacing = 24 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var labelGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, ColumnSpacing = 4 };
        labelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        labelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        labelGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleTb = new TextBlock { Text = title, FontSize = 14, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var infoBtn = new SettingInfoButton(title, desc);
        infoBtn.SetDescription(desc, disabledReason);
        Grid.SetColumn(infoBtn, 1);
        labelGrid.Children.Add(titleTb);
        labelGrid.Children.Add(infoBtn);

        if (!string.IsNullOrWhiteSpace(subNote))
        {
            var noteTb = new TextBlock
            {
                Text = subNote,
                FontSize = 12,
                Foreground = ThemeHelper.TryResourceBrush(ActualTheme, "SystemFillColorCautionBrush", Color.FromArgb(255, 0xFC, 0xE1, 0x00)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            Grid.SetRow(noteTb, 1);
            Grid.SetColumnSpan(noteTb, 2);
            labelGrid.Children.Add(noteTb);
        }

        if (editor is Control ctrl && !ctrl.IsEnabled) labelGrid.Opacity = 0.55;

        Grid.SetColumn(editor, 1);
        editor.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(labelGrid);
        row.Children.Add(editor);
        return row;
    }

    private void HandleApiResponse(ApiResponse res, string? restartNoticeKey = null)
    {
        if (!res.Success)
        {
            feedbackHost?.ShowSettingsNotice(CoreMessageResolver.ResolveError(res), InfoBarSeverity.Error);
        }
        else if (!string.IsNullOrWhiteSpace(restartNoticeKey))
        {
            feedbackHost?.ShowSettingsNotice(L10n.Get(restartNoticeKey), InfoBarSeverity.Warning);
        }
        else if (res.RequiresRestart)
        {
            feedbackHost?.ShowSuccessToast();
        }
        else
        {
            feedbackHost?.ShowSuccessToast();
        }
    }

    private async Task RefreshInventoryAsync()
    {
        if (InventoryRefreshRequested != null)
            await InventoryRefreshRequested();
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (busy || feedbackHost == null) return;
        await feedbackHost.RunHostOperationAsync(async () =>
        {
            SetBusy(true);
            try { await action(); }
            finally
            {
                SetBusy(false);
                if (IsLoaded) RenderSection();
            }
        });
    }

    private void SetBusy(bool value)
    {
        busy = value;
        busyBar.IsIndeterminate = value;
        busyBar.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
}
