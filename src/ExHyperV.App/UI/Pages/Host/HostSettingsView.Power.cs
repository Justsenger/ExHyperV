using System.Globalization;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed partial class HostSettingsView
{
    private static readonly Guid CpuSubgroupGuid = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid PcieSubgroupGuid = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    private static readonly Guid DiskSubgroupGuid = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    private static readonly Guid SleepSubgroupGuid = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid DisplaySubgroupGuid = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid UsbSubgroupGuid = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid ButtonsSubgroupGuid = new("4f971e89-eebd-4455-a8de-9e59040e7347");
    private static readonly Guid BatterySubgroupGuid = new("e73a048d-bf27-4f12-9731-8b2076e8891f");
    private static readonly Guid WirelessSubgroupGuid = new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
    private static readonly Guid MediaSubgroupGuid = new("9596fb26-9850-41fd-ac3e-f7c3c00afd4b");
    private static readonly Guid DesktopSubgroupGuid = new("0d7dbae2-4294-402a-ba8e-26777e8488cd");
    private static readonly Guid BrowserSubgroupGuid = new("02f815b5-a5cf-4c84-bf20-649d1f75d3d8");
    private static readonly Guid GraphicsSubgroupGuid = new("5fb4938d-1ee8-4b0f-9a3c-5036b0ab995c");
    private static readonly Guid EnergySaverSubgroupGuid = new("de830923-a562-41af-a086-e3a2c6bad2da");
    private static readonly Guid PresenceSubgroupGuid = new("8619b916-e004-4dd8-9b66-dae86f806698");

    private sealed record FilteredPowerSubgroup(HostPowerSubgroup Subgroup, IReadOnlyList<HostPowerSetting> Settings, bool ForcedOpen);

    private bool powerShowHidden;
    private string powerSearch = string.Empty;
    private readonly HashSet<Guid> powerExpandedGroups = [CpuSubgroupGuid, PcieSubgroupGuid, DiskSubgroupGuid, SleepSubgroupGuid];
    private Guid? powerLoadedPlanId;
    private bool powerLoadedIncludeHidden;
    private IReadOnlyList<HostPowerSubgroup>? powerLoadedSubgroups;
    private bool powerHasBattery;
    private long powerLoadSeq;

    private StackPanel? powerGroupsPanel;
    private TextBlock? powerCountPillText;
    private Button? powerExpandAllBtn;

    private UIElement BuildPowerSection()
    {
        var panel = new SettingsFieldPanel { Spacing = 12 };
        var plans = HostPowerPlanService.GetPowerPlans();
        Guid? activeId = HostPowerPlanService.GetActivePowerPlanId();
        Guid targetPlanId = activeId ?? (plans.Count > 0 ? plans[0].Id : Guid.Empty);
        powerHasBattery = HostPowerPlanService.HasBattery();

        var combo = new ComboBox
        {
            MinWidth = 220,
            IsEnabled = plans.Count > 0 && !busy
        };
        int selectedIdx = -1;
        for (int i = 0; i < plans.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = plans[i].Name, Tag = plans[i].Id });
            if (targetPlanId == plans[i].Id) selectedIdx = i;
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
                    powerLoadedPlanId = null;
                    powerLoadedSubgroups = null;
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

        var planCard = SettingCard(Row(L10n.Get("Host_Set_Power_Title"), L10n.Get("Host_Set_Power_Desc"), combo));
        SettingsFieldPanel.SetSpan2(planCard, true);
        panel.Children.Add(planCard);

        if (targetPlanId == Guid.Empty)
            return panel;

        var toolbarCard = SettingCard(BuildPowerToolbar(targetPlanId));
        SettingsFieldPanel.SetSpan2(toolbarCard, true);
        panel.Children.Add(toolbarCard);

        powerGroupsPanel = new StackPanel { Spacing = 10 };
        SettingsFieldPanel.SetSpan2(powerGroupsPanel, true);
        panel.Children.Add(powerGroupsPanel);

        if (powerLoadedPlanId == targetPlanId
            && powerLoadedIncludeHidden == powerShowHidden
            && powerLoadedSubgroups != null)
        {
            RebuildPowerGroups(targetPlanId);
        }
        else
        {
            ShowPowerLoadingState();
            _ = LoadPowerSubgroupsAsync(targetPlanId, powerShowHidden);
        }

        return panel;
    }

    private UIElement BuildPowerToolbar(Guid targetPlanId)
    {
        var grid = new Grid
        {
            Margin = new Thickness(18, 12, 18, 12),
            ColumnSpacing = 12,
            RowSpacing = 10
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var searchBox = new TextBox
        {
            PlaceholderText = L10n.Get("Host_Set_Power_SearchPlaceholder"),
            Text = powerSearch,
            MinWidth = 160,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        searchBox.TextChanged += (_, _) =>
        {
            powerSearch = searchBox.Text ?? string.Empty;
            if (powerLoadedPlanId == targetPlanId && powerLoadedSubgroups != null)
                RebuildPowerGroups(targetPlanId);
        };
        Grid.SetColumn(searchBox, 0);
        grid.Children.Add(searchBox);

        var rightBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };

        powerExpandAllBtn = new Button
        {
            Content = L10n.Get("Host_Set_Power_ExpandAll"),
            VerticalAlignment = VerticalAlignment.Center
        };
        powerExpandAllBtn.Click += (_, _) =>
        {
            var filtered = FilterPowerSubgroups();
            if (filtered.Count == 0) return;
            bool allExpanded = filtered.All(g => g.ForcedOpen || powerExpandedGroups.Contains(g.Subgroup.Id));
            if (allExpanded)
                powerExpandedGroups.Clear();
            else
            {
                foreach (var item in filtered)
                    powerExpandedGroups.Add(item.Subgroup.Id);
            }
            RebuildPowerGroups(targetPlanId);
        };
        rightBar.Children.Add(powerExpandAllBtn);

        var hiddenLabel = new TextBlock
        {
            Text = L10n.Get("Host_Set_Power_ShowHidden"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(hiddenLabel, L10n.Get("Host_Set_Power_ShowHiddenDesc"));
        rightBar.Children.Add(hiddenLabel);

        powerCountPillText = new TextBlock
        {
            FontSize = 11.5,
            Text = "0 / 0",
            Foreground = TrySecondaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };
        var countPill = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            Background = ThemeHelper.TryResourceBrush(ActualTheme, "SubtleFillColorSecondaryBrush",
                Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = powerCountPillText
        };
        rightBar.Children.Add(countPill);

        var showHiddenToggle = CreateToggle(powerShowHidden, !busy);
        showHiddenToggle.Toggled += async (_, _) =>
        {
            if (updating || busy) return;
            powerShowHidden = showHiddenToggle.IsOn;
            ShowPowerLoadingState();
            await LoadPowerSubgroupsAsync(targetPlanId, powerShowHidden);
        };
        rightBar.Children.Add(showHiddenToggle);

        Grid.SetColumn(rightBar, 1);
        grid.Children.Add(rightBar);
        return grid;
    }

    private void ShowPowerLoadingState()
    {
        if (powerGroupsPanel == null) return;
        powerGroupsPanel.Children.Clear();
        powerGroupsPanel.Children.Add(SettingCard(new TextBlock
        {
            Text = L10n.Get("Host_Set_Power_Loading"),
            Margin = new Thickness(20, 22, 20, 22),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = TrySecondaryBrush()
        }));
    }

    private async Task LoadPowerSubgroupsAsync(Guid planId, bool includeHidden)
    {
        long seq = ++powerLoadSeq;
        try
        {
            var groups = await Task.Run(() => HostPowerPlanService.GetPowerPlanSubgroups(planId, includeHidden));
            if (seq != powerLoadSeq || !IsLoaded || activeSub != "power") return;

            powerLoadedPlanId = planId;
            powerLoadedIncludeHidden = includeHidden;
            powerLoadedSubgroups = groups;
            RebuildPowerGroups(planId);
        }
        catch (Exception ex)
        {
            if (seq != powerLoadSeq || !IsLoaded || activeSub != "power") return;
            feedbackHost?.ShowSettingsNotice(CoreMessageResolver.ResolveException(ex), InfoBarSeverity.Error);
        }
    }

    private List<FilteredPowerSubgroup> FilterPowerSubgroups()
    {
        if (powerLoadedSubgroups == null)
            return [];

        string query = powerSearch.Trim();
        var result = new List<FilteredPowerSubgroup>();

        foreach (var group in powerLoadedSubgroups)
        {
            string groupTitle = ResolveSubgroupTitle(group);
            bool groupMatches = query.Length > 0
                && groupTitle.Contains(query, StringComparison.OrdinalIgnoreCase);

            var matchedSettings = query.Length == 0 || groupMatches
                ? group.Settings
                : group.Settings.Where(s =>
                    s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || s.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || s.Unit.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matchedSettings.Count == 0)
                continue;

            result.Add(new FilteredPowerSubgroup(group, matchedSettings, query.Length > 0));
        }

        return result;
    }

    private void RebuildPowerGroups(Guid planId)
    {
        if (powerGroupsPanel == null || powerLoadedSubgroups == null) return;

        int totalCount = powerLoadedSubgroups.Sum(g => g.Settings.Count);
        var filtered = FilterPowerSubgroups();
        int visibleCount = filtered.Sum(g => g.Settings.Count);

        if (powerCountPillText != null)
            powerCountPillText.Text = L10n.Format("Host_Set_Power_CountRatio", visibleCount, totalCount);

        if (powerExpandAllBtn != null)
        {
            bool allExpanded = filtered.Count > 0 && filtered.All(g => g.ForcedOpen || powerExpandedGroups.Contains(g.Subgroup.Id));
            powerExpandAllBtn.Content = L10n.Get(allExpanded ? "Host_Set_Power_CollapseAll" : "Host_Set_Power_ExpandAll");
            powerExpandAllBtn.IsEnabled = filtered.Count > 0;
        }

        powerGroupsPanel.Children.Clear();
        if (filtered.Count == 0)
        {
            powerGroupsPanel.Children.Add(SettingCard(new TextBlock
            {
                Text = L10n.Get("Host_Set_Power_Empty"),
                Margin = new Thickness(20, 26, 20, 26),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Foreground = TrySecondaryBrush()
            }));
            return;
        }

        foreach (var item in filtered)
            powerGroupsPanel.Children.Add(BuildPowerSubgroupExpander(planId, item));
    }

    private Expander BuildPowerSubgroupExpander(Guid planId, FilteredPowerSubgroup item)
    {
        var group = item.Subgroup;
        string groupTitle = ResolveSubgroupTitle(group);

        var headerGrid = new Grid { ColumnSpacing = 10 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = GetSubgroupGlyph(group.Id),
            FontSize = 15,
            Foreground = ThemeHelper.TryResourceBrush(ActualTheme, "VmAccentBrush", Color.FromArgb(255, 0x4C, 0xC2, 0xFF)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        headerGrid.Children.Add(icon);

        var titleTb = new TextBlock
        {
            Text = groupTitle,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleTb, 1);
        headerGrid.Children.Add(titleTb);

        var badge = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 2, 8, 2),
            Background = ThemeHelper.TryResourceBrush(ActualTheme, "SubtleFillColorSecondaryBrush",
                Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = L10n.Format("Host_Set_Power_Count", item.Settings.Count),
                FontSize = 11.5,
                Foreground = TrySecondaryBrush()
            }
        };
        Grid.SetColumn(badge, 2);
        headerGrid.Children.Add(badge);

        var body = new StackPanel();
        if (powerHasBattery)
        {
            body.Children.Add(BuildPowerColumnHeaderRow());
            body.Children.Add(BuildHairlineDivider());
        }

        for (int i = 0; i < item.Settings.Count; i++)
        {
            if (i > 0)
                body.Children.Add(BuildHairlineDivider());
            body.Children.Add(BuildPowerSettingRow(planId, item.Settings[i]));
        }

        var expander = new Expander
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(0),
            Header = headerGrid,
            Content = body,
            IsExpanded = item.ForcedOpen || powerExpandedGroups.Contains(group.Id)
        };
        expander.Expanding += (_, _) =>
        {
            if (!item.ForcedOpen)
                powerExpandedGroups.Add(group.Id);
        };
        expander.Collapsed += (_, _) =>
        {
            if (!item.ForcedOpen)
                powerExpandedGroups.Remove(group.Id);
        };
        return expander;
    }

    private Grid BuildPowerColumnHeaderRow()
    {
        var row = new Grid
        {
            Padding = new Thickness(20, 7, 18, 7),
            ColumnSpacing = 16,
            Background = new SolidColorBrush(ActualTheme == ElementTheme.Light
                ? Color.FromArgb(0x08, 0, 0, 0)
                : Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF))
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(184) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(184) });

        var leftLabel = new TextBlock
        {
            Text = L10n.Get("Host_Set_Power_ColSetting"),
            FontSize = 11.5,
            Foreground = TrySecondaryBrush(),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(leftLabel, 0);
        row.Children.Add(leftLabel);

        var acHeader = BuildSourceColumnHeader("\uE83E", L10n.Get("Host_Set_Power_ColAc"));
        Grid.SetColumn(acHeader, 1);
        row.Children.Add(acHeader);

        var dcHeader = BuildSourceColumnHeader("\uE83F", L10n.Get("Host_Set_Power_ColDc"));
        Grid.SetColumn(dcHeader, 2);
        row.Children.Add(dcHeader);

        return row;
    }

    private StackPanel BuildSourceColumnHeader(string glyph, string text)
    {
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, Foreground = TrySecondaryBrush() });
        stack.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = TrySecondaryBrush() });
        return stack;
    }

    private Border BuildHairlineDivider() => new()
    {
        Height = 1,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Background = ThemeHelper.TryResourceBrush(ActualTheme, "CardStrokeColorDefaultBrush",
            Color.FromArgb(0x18, 0x80, 0x80, 0x80))
    };

    private Grid BuildPowerSettingRow(Guid planId, HostPowerSetting setting)
    {
        var row = new Grid
        {
            Padding = new Thickness(20, 12, 18, 12),
            ColumnSpacing = 16
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        var titleGrid = new Grid { ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Left };
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleTb = new TextBlock
        {
            Text = setting.Name,
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleTb, 0);
        titleGrid.Children.Add(titleTb);

        if (setting.IsHidden)
        {
            var hiddenBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Background = ThemeHelper.TryResourceBrush(ActualTheme, "SubtleFillColorSecondaryBrush",
                    Color.FromArgb(0x20, 0x80, 0x80, 0x80)),
                BorderBrush = ThemeHelper.TryResourceBrush(ActualTheme, "CardStrokeColorDefaultBrush",
                    Color.FromArgb(0x22, 0x80, 0x80, 0x80)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = L10n.Get("Host_Set_Power_HiddenTag"),
                    FontSize = 11,
                    Foreground = TrySecondaryBrush()
                }
            };
            Grid.SetColumn(hiddenBadge, 1);
            titleGrid.Children.Add(hiddenBadge);
        }

        string rangeHint = FormatRangeHint(setting);
        string tooltipText = string.Join(" ", new[] { setting.Description, rangeHint }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (!string.IsNullOrWhiteSpace(tooltipText))
        {
            var infoBtn = new SettingInfoButton(setting.Name, tooltipText);
            Grid.SetColumn(infoBtn, 2);
            titleGrid.Children.Add(infoBtn);
        }

        labelStack.Children.Add(titleGrid);

        if (!string.IsNullOrWhiteSpace(rangeHint))
        {
            labelStack.Children.Add(new TextBlock
            {
                Text = rangeHint,
                FontSize = 11.5,
                Foreground = TrySecondaryBrush()
            });
        }

        Grid.SetColumn(labelStack, 0);
        row.Children.Add(labelStack);

        if (powerHasBattery)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(184) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(184) });

            var acEditor = BuildPowerSettingEditor(planId, setting, applyToDc: false);
            Grid.SetColumn(acEditor, 1);
            row.Children.Add(acEditor);

            var dcEditor = BuildPowerSettingEditor(planId, setting, applyToDc: true);
            Grid.SetColumn(dcEditor, 2);
            row.Children.Add(dcEditor);
        }
        else
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(224) });

            var acEditor = BuildPowerSettingEditor(planId, setting, applyToDc: false);
            Grid.SetColumn(acEditor, 1);
            row.Children.Add(acEditor);
        }

        return row;
    }

    private FrameworkElement BuildPowerSettingEditor(Guid planId, HostPowerSetting setting, bool applyToDc)
    {
        return setting.ValueKind == HostPowerSettingValueKind.Choice
            ? BuildPowerChoiceCombo(planId, setting, applyToDc)
            : BuildPowerNumericStepper(planId, setting, applyToDc);
    }

    private ComboBox BuildPowerChoiceCombo(Guid planId, HostPowerSetting setting, bool applyToDc)
    {
        uint currentValue = applyToDc ? setting.DcValue : setting.AcValue;
        var combo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0,
            VerticalAlignment = VerticalAlignment.Center
        };

        int selectedIdx = -1;
        for (int i = 0; i < setting.Options.Count; i++)
        {
            var opt = setting.Options[i];
            combo.Items.Add(new ComboBoxItem { Content = opt.Name, Tag = opt.Value });
            if (opt.Value == currentValue) selectedIdx = i;
        }

        if (selectedIdx < 0)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = L10n.Format("Host_Set_Power_CustomOption", currentValue),
                Tag = currentValue
            });
            selectedIdx = combo.Items.Count - 1;
        }

        int committedIndex = selectedIdx;
        combo.SelectedIndex = selectedIdx;

        combo.SelectionChanged += async (_, _) =>
        {
            if (updating || busy || combo.SelectedIndex == committedIndex) return;
            if (combo.SelectedItem is not ComboBoxItem { Tag: uint nextValue }) return;

            bool ok = await CommitPowerValueAsync(planId, setting, nextValue, applyToDc);
            if (ok)
            {
                committedIndex = combo.SelectedIndex;
            }
            else
            {
                updating = true;
                try { combo.SelectedIndex = committedIndex; }
                finally { updating = false; }
            }
        };

        return combo;
    }

    private FrameworkElement BuildPowerNumericStepper(Guid planId, HostPowerSetting setting, bool applyToDc)
    {
        uint committedValue = applyToDc ? setting.DcValue : setting.AcValue;
        uint min = setting.Minimum;
        uint max = Math.Max(min, setting.Maximum);
        uint step = Math.Max(1u, setting.Increment);

        var transparent = new SolidColorBrush(Colors.Transparent);
        var box = new TextBox
        {
            Text = committedValue.ToString(CultureInfo.InvariantCulture),
            BorderThickness = new Thickness(0),
            Background = transparent,
            Padding = new Thickness(2, 0, 2, 0),
            MinWidth = 0,
            MinHeight = 0,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 13
        };
        box.Resources["TextControlBackground"] = transparent;
        box.Resources["TextControlBackgroundPointerOver"] = transparent;
        box.Resources["TextControlBackgroundFocused"] = transparent;
        box.Resources["TextControlBackgroundDisabled"] = transparent;
        box.Resources["TextControlBorderBrushPointerOver"] = transparent;
        box.Resources["TextControlBorderBrushFocused"] = transparent;
        box.Resources["TextControlBorderBrushDisabled"] = transparent;
        box.Loaded += (_, _) =>
        {
            if (VisualTreeHelper.GetChildrenCount(box) == 0) return;
            var root = VisualTreeHelper.GetChild(box, 0);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                if (VisualTreeHelper.GetChild(root, i) is not FrameworkElement child) continue;
                if (child.Name is "ContentElement" or "PlaceholderTextContentPresenter")
                    child.VerticalAlignment = VerticalAlignment.Center;
                else if (child.Name == "DeleteButton")
                {
                    child.Width = 0;
                    child.Height = 0;
                    child.Opacity = 0;
                    child.IsHitTestVisible = false;
                }
            }
        };

        var decBtn = CreatePowerStepperButton("\uE738");
        var incBtn = CreatePowerStepperButton("\uE710");

        void SyncStepperButtons(uint value)
        {
            decBtn.IsEnabled = value > min;
            incBtn.IsEnabled = value < max;
        }
        SyncStepperButtons(committedValue);

        async Task SubmitNumericAsync(uint candidate)
        {
            uint clamped = Math.Clamp(candidate, min, max);
            if (clamped == committedValue)
            {
                box.Text = committedValue.ToString(CultureInfo.InvariantCulture);
                SyncStepperButtons(committedValue);
                return;
            }

            bool ok = await CommitPowerValueAsync(planId, setting, clamped, applyToDc);
            if (ok)
                committedValue = clamped;
            box.Text = committedValue.ToString(CultureInfo.InvariantCulture);
            SyncStepperButtons(committedValue);
        }

        decBtn.Click += async (_, _) =>
        {
            if (busy || committedValue <= min) return;
            uint next = committedValue - min >= step ? committedValue - step : min;
            await SubmitNumericAsync(next);
        };

        incBtn.Click += async (_, _) =>
        {
            if (busy || committedValue >= max) return;
            uint next = max - committedValue >= step ? committedValue + step : max;
            await SubmitNumericAsync(next);
        };

        box.KeyDown += async (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            await TryCommitTextAsync();
        };

        box.LostFocus += async (_, _) => await TryCommitTextAsync();

        async Task TryCommitTextAsync()
        {
            string raw = (box.Text ?? string.Empty).Trim();
            string currentText = committedValue.ToString(CultureInfo.InvariantCulture);
            if (raw == currentText) return;
            if (!uint.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint parsed)
                || parsed < min || parsed > max)
            {
                box.Text = currentText;
                feedbackHost?.ShowSettingsNotice(L10n.Format("Host_Set_Power_InvalidNumber", min, max), InfoBarSeverity.Error);
                return;
            }
            await SubmitNumericAsync(parsed);
        }

        var inner = new Grid { VerticalAlignment = VerticalAlignment.Stretch };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

        Grid.SetColumn(decBtn, 0);
        Grid.SetColumn(box, 1);
        inner.Children.Add(decBtn);
        inner.Children.Add(box);

        if (!string.IsNullOrWhiteSpace(setting.Unit))
        {
            var unitTb = new TextBlock
            {
                Text = setting.Unit,
                FontSize = 11,
                Foreground = TrySecondaryBrush(),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                MaxWidth = 56,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(unitTb, 2);
            inner.Children.Add(unitTb);
        }

        Grid.SetColumn(incBtn, 3);
        inner.Children.Add(incBtn);

        var border = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = inner
        };
        void ApplyBorderTheme()
        {
            bool light = ActualTheme == ElementTheme.Light;
            border.Background = ThemeHelper.TryResourceBrush(ActualTheme, "ControlFillColorDefaultBrush",
                light ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));
            border.BorderBrush = ThemeHelper.TryResourceBrush(ActualTheme, "ControlStrokeColorDefaultBrush",
                Color.FromArgb(0x22, 0x80, 0x80, 0x80));
        }
        ApplyBorderTheme();
        border.ActualThemeChanged += (_, _) => ApplyBorderTheme();
        return border;
    }

    private static Button CreatePowerStepperButton(string glyph)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var btn = new Button
        {
            Width = 28,
            Height = 30,
            MinWidth = 28,
            MinHeight = 30,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = transparent,
            Content = new FontIcon { Glyph = glyph, FontSize = 11 },
            VerticalAlignment = VerticalAlignment.Center
        };
        btn.Resources["ButtonBackground"] = transparent;
        btn.Resources["ButtonBackgroundDisabled"] = transparent;
        btn.Resources["ButtonBorderBrush"] = transparent;
        return btn;
    }

    private async Task<bool> CommitPowerValueAsync(Guid planId, HostPowerSetting target, uint nextValue, bool applyToDc)
    {
        if (busy || feedbackHost == null) return false;
        bool succeeded = false;
        await feedbackHost.RunHostOperationAsync(async () =>
        {
            SetBusy(true);
            try
            {
                await Task.Run(() => HostPowerPlanService.SetPowerSettingValue(
                    planId, target.SubgroupId, target.Id, nextValue, applyToDc));
                UpdateCachedPowerSettingValue(target.SubgroupId, target.Id, nextValue, applyToDc);
                feedbackHost.ShowSuccessToast();
                succeeded = true;
            }
            catch (Exception ex)
            {
                feedbackHost.ShowSettingsNotice(CoreMessageResolver.ResolveException(ex), InfoBarSeverity.Error);
            }
            finally
            {
                SetBusy(false);
            }
        });
        return succeeded;
    }

    private void UpdateCachedPowerSettingValue(Guid subgroupId, Guid settingId, uint nextValue, bool applyToDc)
    {
        if (powerLoadedSubgroups == null) return;
        var updatedGroups = powerLoadedSubgroups.ToArray();
        for (int g = 0; g < updatedGroups.Length; g++)
        {
            if (updatedGroups[g].Id != subgroupId) continue;
            var settings = updatedGroups[g].Settings.ToArray();
            for (int s = 0; s < settings.Length; s++)
            {
                if (settings[s].Id != settingId) continue;
                settings[s] = applyToDc
                    ? settings[s] with { DcValue = nextValue }
                    : settings[s] with { AcValue = nextValue };
                updatedGroups[g] = updatedGroups[g] with { Settings = settings };
                powerLoadedSubgroups = updatedGroups;
                return;
            }
        }
    }

    private static string ResolveSubgroupTitle(HostPowerSubgroup group) =>
        group.Id == HostPowerPlanService.NoSubgroupId || string.IsNullOrWhiteSpace(group.Name)
            ? L10n.Get("Host_Set_Power_NoSubgroup")
            : group.Name;

    private static string FormatRangeHint(HostPowerSetting setting)
    {
        if (setting.ValueKind == HostPowerSettingValueKind.Choice)
            return string.Empty;

        string baseHint = string.IsNullOrWhiteSpace(setting.Unit)
            ? L10n.Format("Host_Set_Power_RangeHint", setting.Minimum, setting.Maximum)
            : L10n.Format("Host_Set_Power_RangeUnitHint", setting.Minimum, setting.Maximum, setting.Unit);

        return setting.Unit == "s" || L10n.MatchesSystemLabel(setting.Unit, "Host_Set_Power_SecondsUnit")
            ? L10n.Format("Host_Set_Power_ZeroNeverHint", baseHint)
            : baseHint;
    }

    private static string GetSubgroupGlyph(Guid subgroupId)
    {
        if (subgroupId == CpuSubgroupGuid) return "\uE950";
        if (subgroupId == PcieSubgroupGuid) return "\uE964";
        if (subgroupId == DiskSubgroupGuid) return "\uEDA2";
        if (subgroupId == SleepSubgroupGuid) return "\uE708";
        if (subgroupId == DisplaySubgroupGuid || subgroupId == GraphicsSubgroupGuid) return "\uE7F4";
        if (subgroupId == UsbSubgroupGuid) return "\uECF0";
        if (subgroupId == ButtonsSubgroupGuid) return "\uE7E8";
        if (subgroupId == BatterySubgroupGuid) return "\uE83F";
        if (subgroupId == WirelessSubgroupGuid) return "\uE701";
        if (subgroupId == MediaSubgroupGuid) return "\uE714";
        if (subgroupId == DesktopSubgroupGuid) return "\uE771";
        if (subgroupId == BrowserSubgroupGuid) return "\uE774";
        if (subgroupId == EnergySaverSubgroupGuid) return "\uEC0A";
        if (subgroupId == PresenceSubgroupGuid) return "\uE823";
        return "\uE713";
    }

    private Brush TrySecondaryBrush() =>
        ThemeHelper.TryResourceBrush(ActualTheme, "TextFillColorSecondaryBrush",
            ActualTheme == ElementTheme.Light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
}
