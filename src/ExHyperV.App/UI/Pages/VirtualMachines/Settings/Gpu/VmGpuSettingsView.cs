using System.Collections.Concurrent;
using System.Numerics;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Gpu;

public sealed partial class VmGpuSettingsView : VmSettingsView
{

    private static readonly ConcurrentDictionary<Guid, string> unconfirmedRemovals = new();

    private readonly VmQueryService query = new();
    private readonly VmGpuService service;
    private readonly StackPanel panel = new() { Spacing = 12, Margin = new Thickness(0, 0, 0, 20) };
    private readonly ContentControl overview = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel assignedList = new() { Spacing = 10 };
    private readonly ContentControl selection = new() { Visibility = Visibility.Collapsed, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel selectionContent = new() { Spacing = 12 };
    private readonly StackPanel hostGpuList = new() { Spacing = 8 };
    private readonly StackPanel deployment = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly ToggleSwitch installDrivers = Switch();
    private readonly Button beginSelectBtn = new() { Content = L10n.Get("Gpu_StartSetup"), IsEnabled = false };
    private readonly SemaphoreSlim stopGate = new(1, 1);

    private bool closing;
    private bool working;
    private Task operation = Task.CompletedTask;
    private IReadOnlyList<VmGpuAssignment> currentAssignments = [];
    private IReadOnlyList<HostGpuInfo> hostGpuCatalog = [];
    private readonly HashSet<string> selectedHostGpuIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expandedCardKeys = new(StringComparer.OrdinalIgnoreCase);

    public VmGpuSettingsView(Guid vmId) : base(vmId)
    {
        service = new VmGpuService(query);
        installDrivers.IsEnabled = true;
        installDrivers.IsOn = true;
        beginSelectBtn.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];

        var backListBtn = new Button { Content = L10n.Get("Pcie_Back") };
        backListBtn.Click += (_, _) => ShowOverview();
        beginSelectBtn.Click += async (_, _) =>
        {
            var chosen = hostGpuCatalog
                .Where(g => !string.IsNullOrWhiteSpace(g.InstanceId) && selectedHostGpuIds.Contains(g.InstanceId))
                .ToList();
            if (chosen.Count == 0) return;
            await RunGpuOperationAsync(() => StartDeploymentAsync(
                chosen,
                installDrivers.IsOn ? GpuDeploymentMode.AddWithDrivers : GpuDeploymentMode.AddWithoutDrivers));
        };

        var driverToggleCard = Card(Row(L10n.Get("Gpu_InstallDrivers"), L10n.Get("Gpu_InstallDriversHelp"), installDrivers));
        driverToggleCard.Margin = new Thickness(0);

        var selectActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        selectActions.Children.Add(backListBtn);
        selectActions.Children.Add(beginSelectBtn);

        selectionContent.Children.Add(hostGpuList);
        selectionContent.Children.Add(driverToggleCard);
        selectionContent.Children.Add(selectActions);
        selection.Content = selectionContent;

        overview.Content = assignedList;

        panel.Children.Add(overview);
        panel.Children.Add(selection);
        panel.Children.Add(BuildDeploymentPanel());

        SetBody(panel);
    }

    protected override async Task LoadAsync()
    {
        EnsureNoUnfinishedDeployment();
        await RefreshAssignmentsAsync();
    }

    private async Task RefreshAssignmentsAsync()
    {
        var result = await Task.Run(() => service.GetVmGpuAssignmentsAsync(VmId));
        if (closing || IsStopped) return;
        if (!result.HasData) throw new InvalidOperationException(CoreMessageResolver.ResolveError(result));
        currentAssignments = result.Data!;

        assignedList.Children.Clear();
        foreach (VmGpuAssignment gpu in currentAssignments)
            assignedList.Children.Add(BuildAssignedGpuCard(gpu));
        assignedList.Children.Add(BuildAddGpuRowCard(isEmpty: currentAssignments.Count == 0));
    }

    private UIElement BuildAssignedGpuCard(VmGpuAssignment gpu)
    {
        string cardKey = string.IsNullOrWhiteSpace(gpu.AdapterId)
            ? (gpu.HostDeviceInstanceId ?? gpu.Name ?? Guid.NewGuid().ToString())
            : gpu.AdapterId;
        bool isExpanded = expandedCardKeys.Contains(cardKey);

        var headerGrid = new Grid { ColumnSpacing = 12 };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBadge = BuildGpuIconBadge(gpu.Manu, gpu.Name, 38);
        Grid.SetColumn(iconBadge, 0);
        headerGrid.Children.Add(iconBadge);

        var title = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(gpu.Name) ? "Unknown Device" : gpu.Name,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextFillColorPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(title, 1);
        headerGrid.Children.Add(title);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };

        UIElement? updateControl = null;
        if (gpu.HasDriverUpdate)
        {
            updateControl = BuildUpdateIconButton(gpu, async () => await RunGpuOperationAsync(() => UpdateDriversAsync(gpu)));
            actions.Children.Add(updateControl);
        }

        var removeBtn = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uF34D",
                FontFamily = AppFonts.FluentSystem,
                FontSize = 15.5
            },
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["RoundIconButtonStyle"],
            IsEnabled = !unconfirmedRemovals.ContainsKey(VmId)
        };
        ToolTipService.SetToolTip(removeBtn, L10n.Get("Gpu_Remove"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(removeBtn, L10n.Get("Gpu_Remove"));
        removeBtn.Tapped += (_, e) => e.Handled = true;
        removeBtn.Click += async (_, _) => await RunGpuOperationAsync(() => RemoveAssignmentAsync(gpu));
        actions.Children.Add(removeBtn);

        var chevronIcon = new FontIcon
        {
            Glyph = isExpanded ? "\uE70E" : "\uE70D",
            FontFamily = AppFonts.SegoeFluent,
            FontSize = 12,
            Foreground = Brush("TextFillColorSecondaryBrush")
        };
        var chevronHost = new Grid
        {
            Width = 26,
            Height = 26,
            VerticalAlignment = VerticalAlignment.Center
        };
        chevronHost.Children.Add(chevronIcon);
        actions.Children.Add(chevronHost);

        Grid.SetColumn(actions, 2);
        headerGrid.Children.Add(actions);

        var detailsGrid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        detailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        string manu = string.IsNullOrWhiteSpace(gpu.Manu) ? "Default" : gpu.Manu;
        string vendor = string.IsNullOrWhiteSpace(gpu.Vendor) ? "Default" : gpu.Vendor;
        string memoryText = gpu.MemoryBytes is ulong bytes ? $"{bytes / 1048576d:F0} MiB" : L10n.Get("Common_Unavailable");
        string? guestDriver = string.IsNullOrWhiteSpace(gpu.GuestDriverVersion) ? null : gpu.GuestDriverVersion;
        string hostDriver = string.IsNullOrWhiteSpace(gpu.DriverVersion) ? "---" : gpu.DriverVersion;
        string driverText = guestDriver != null
            ? (gpu.HasDriverUpdate ? L10n.Format("Gpu_HostVersion", guestDriver, hostDriver) : guestDriver)
            : (hostDriver != "---" ? L10n.Format("Gpu_NotDeployed", hostDriver) : "---");
        string driverCopy = guestDriver ?? (hostDriver != "---" ? hostDriver : string.Empty);

        var c0 = BuildKvCell(L10n.Get("Overview_GpuChipVendor"), manu, manu);
        Grid.SetColumn(c0, 0);
        Grid.SetRow(c0, 0);
        detailsGrid.Children.Add(c0);

        var c1 = BuildKvCell(L10n.Get("Overview_GpuBoardVendor"), vendor, vendor);
        Grid.SetColumn(c1, 1);
        Grid.SetRow(c1, 0);
        detailsGrid.Children.Add(c1);

        var c2 = BuildKvCell(L10n.Get("Gpu_HostDedicatedMemory"), memoryText, gpu.MemoryBytes.HasValue ? memoryText : string.Empty);
        Grid.SetColumn(c2, 0);
        Grid.SetRow(c2, 1);
        detailsGrid.Children.Add(c2);

        var c3 = BuildKvCell(L10n.Get("Overview_GpuDriverVersion"), driverText, driverCopy);
        Grid.SetColumn(c3, 1);
        Grid.SetRow(c3, 1);
        detailsGrid.Children.Add(c3);

        string instanceText = string.IsNullOrWhiteSpace(gpu.HostDeviceInstanceId) ? "---" : gpu.HostDeviceInstanceId;
        var c4 = BuildKvCell(L10n.Get("Overview_GpuDevicePath"), instanceText, gpu.HostDeviceInstanceId);
        Grid.SetColumn(c4, 0);
        Grid.SetRow(c4, 2);
        Grid.SetColumnSpan(c4, 2);
        detailsGrid.Children.Add(c4);

        var transparent = new SolidColorBrush(Colors.Transparent);
        var hoverBrush = ThemeHelper.TryBrush("SubtleFillColorTertiaryBrush", Color.FromArgb(14, 255, 255, 255));
        var headerHost = new Border
        {
            Padding = new Thickness(16, 12, 16, 12),
            Background = transparent,
            CornerRadius = isExpanded ? new CornerRadius(7, 7, 0, 0) : new CornerRadius(7),
            Child = headerGrid
        };
        var divider = new Border
        {
            Height = 1,
            Background = ThemeHelper.TryBrush("DividerStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255)),
            Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed
        };
        var bodyHost = new Border
        {
            Padding = new Thickness(16, 12, 16, 14),
            Visibility = isExpanded ? Visibility.Visible : Visibility.Collapsed,
            Child = detailsGrid
        };

        void ApplyExpandedVisual(bool expanded)
        {
            if (expanded) expandedCardKeys.Add(cardKey);
            else expandedCardKeys.Remove(cardKey);
            chevronIcon.Glyph = expanded ? "\uE70E" : "\uE70D";
            headerHost.CornerRadius = expanded ? new CornerRadius(7, 7, 0, 0) : new CornerRadius(7);
            divider.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            bodyHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        }

        headerHost.PointerEntered += (_, _) => headerHost.Background = hoverBrush;
        headerHost.PointerExited += (_, _) => headerHost.Background = transparent;
        headerHost.PointerCanceled += (_, _) => headerHost.Background = transparent;
        headerHost.Tapped += (_, e) =>
        {
            if (e.Handled
                || IsDescendantOf(e.OriginalSource as DependencyObject, removeBtn)
                || (updateControl != null && IsDescendantOf(e.OriginalSource as DependencyObject, updateControl)))
            {
                return;
            }
            ApplyExpandedVisual(!expandedCardKeys.Contains(cardKey));
        };

        var cardStack = new StackPanel();
        cardStack.Children.Add(headerHost);
        cardStack.Children.Add(divider);
        cardStack.Children.Add(bodyHost);

        return new Border
        {
            Background = ThemeHelper.TryBrush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(13, 255, 255, 255)),
            BorderBrush = ThemeHelper.TryBrush("CardStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = cardStack
        };
    }

    private UIElement BuildUpdateIconButton(VmGpuAssignment gpu, Func<Task> onClick)
    {
        var glyph = new FontIcon
        {
            Glyph = "\uE898",
            FontFamily = AppFonts.SegoeFluent,
            FontSize = 15,
            Foreground = Brush("TextFillColorPrimaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var btn = new Button
        {
            Content = glyph,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["RoundIconButtonStyle"]
        };
        string tip = !string.IsNullOrWhiteSpace(gpu.GuestDriverVersion)
            ? L10n.Format("Gpu_UpdateDriverVersion", gpu.DriverVersion)
            : L10n.Format("Gpu_InstallDriverVersion", gpu.DriverVersion);
        ToolTipService.SetToolTip(btn, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, tip);
        btn.Tapped += (_, e) => e.Handled = true;
        btn.Click += async (_, _) => await UiEvent.RunAsync(() => onClick());

        var dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new SolidColorBrush(Color.FromArgb(255, 255, 59, 48)),
            Stroke = ThemeHelper.TryBrush("SolidBackgroundFillColorTertiaryBrush", Color.FromArgb(255, 42, 42, 42)),
            StrokeThickness = 1.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 1, 0),
            IsHitTestVisible = false
        };

        var host = new Grid { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(btn);
        host.Children.Add(dot);
        return host;
    }

    private Border BuildKvCell(string title, string value, string? copyValue)
    {
        var headRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };

        headRow.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = Brush("TextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        if (!string.IsNullOrWhiteSpace(copyValue))
        {
            var copyIcon = new FontIcon
            {
                Glyph = "\uE8C8",
                FontFamily = AppFonts.SegoeFluent,
                FontSize = 11.5,
                Foreground = Brush("TextFillColorSecondaryBrush")
            };
            var copyBtn = new Button
            {
                Content = copyIcon,
                Width = 22,
                Height = 20,
                MinWidth = 22,
                MinHeight = 20,
                Padding = new Thickness(0),
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["VmSelectorButtonStyle"],
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(copyBtn, L10n.Format("Dev_CopyValueTooltip", copyValue));
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copyBtn, L10n.Format("Dev_CopyLabeledValue", title, copyValue));
            copyBtn.Click += async (_, _) => await UiEvent.RunAsync(async () =>
            {
                if (!CopyTextToClipboard(copyValue, title)) return;
                copyIcon.Glyph = "\uE73E";
                ToolTipService.SetToolTip(copyBtn, L10n.Get("Dev_Copied"));
                await Task.Delay(1600);
                copyIcon.Glyph = "\uE8C8";
                ToolTipService.SetToolTip(copyBtn, L10n.Format("Dev_CopyValueTooltip", copyValue));
            });
            headRow.Children.Add(copyBtn);
        }

        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontWeight = FontWeights.Normal,
            Foreground = Brush("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            IsTextSelectionEnabled = true
        };
        if (!string.IsNullOrWhiteSpace(value) && value != "---")
            ToolTipService.SetToolTip(valueText, value);

        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(headRow);
        stack.Children.Add(valueText);

        return new Border
        {
            Background = Brush("VmKvWellBrush"),
            BorderBrush = Brush("VmKvWellBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            Child = stack
        };
    }

    private Button BuildAddGpuRowCard(bool isEmpty)
    {
        var rowGrid = new Grid { ColumnSpacing = 12 };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(7),
            Background = ThemeHelper.TryBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(16, 255, 255, 255)),
            BorderBrush = ThemeHelper.TryBrush("DividerStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = new FontIcon
            {
                Glyph = "\uE710",
                FontFamily = AppFonts.SegoeFluent,
                FontSize = 16,
                Foreground = Brush("TextFillColorPrimaryBrush")
            }
        };
        Grid.SetColumn(badge, 0);
        rowGrid.Children.Add(badge);

        var title = new TextBlock
        {
            Text = L10n.Get("Gpu_Add"),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = Brush("TextFillColorPrimaryBrush")
        };
        Grid.SetColumn(title, 1);
        rowGrid.Children.Add(title);

        var button = new Button
        {
            Content = rowGrid,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["VmSelectorButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ThemeHelper.TryBrush(isEmpty ? "CardBackgroundFillColorDefaultBrush" : "SubtleFillColorTertiaryBrush", Color.FromArgb(10, 255, 255, 255)),
            BorderBrush = ThemeHelper.TryBrush("CardStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, isEmpty ? 14 : 12, 16, isEmpty ? 14 : 12),
            MinHeight = isEmpty ? 68 : 60,
            IsEnabled = !unconfirmedRemovals.ContainsKey(VmId)
        };
        button.Click += async (_, _) => await RunGpuOperationAsync(ShowSelectionAsync);
        return button;
    }

    private async Task ShowSelectionAsync()
    {
        EnsureNoUnfinishedDeployment();
        var result = await Task.Run(HostGpuService.GetHostGpusAsync);
        if (closing || IsStopped) return;
        if (!result.HasData) throw new InvalidOperationException(CoreMessageResolver.ResolveError(result));

        hostGpuCatalog = result.Data!;
        selectedHostGpuIds.Clear();
        hostGpuList.Children.Clear();

        var existingKeys = currentAssignments
            .Select(a => a.HostDeviceInstanceId)
            .Where(k => !string.IsNullOrEmpty(k))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (hostGpuCatalog.Count == 0)
            Notice(L10n.Get("Gpu_NoHostAdapters"), InfoBarSeverity.Informational);

        foreach (HostGpuInfo gpu in hostGpuCatalog)
        {
            string normalizedKey = gpu.InstanceId;
            bool alreadyAdded = !string.IsNullOrEmpty(normalizedKey) && existingKeys.Contains(normalizedKey);
            bool canSelect = gpu.IsPartitionable && !alreadyAdded;
            hostGpuList.Children.Add(BuildHostGpuPickerRow(gpu, alreadyAdded, canSelect));
        }

        UpdateBeginSelectionState();
        overview.Visibility = deployment.Visibility = Visibility.Collapsed;
        selection.Visibility = Visibility.Visible;
    }

    private UIElement BuildHostGpuPickerRow(HostGpuInfo gpu, bool alreadyAdded, bool canSelect)
    {
        var checkBox = new CheckBox
        {
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = canSelect,
            IsChecked = false
        };

        var accentBar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(0, 2, 2, 0),
            Background = Brush("VmAccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(-16, 2, 0, 2),
            Visibility = Visibility.Collapsed
        };

        var rowGrid = new Grid { ColumnSpacing = 12 };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(checkBox, 0);
        rowGrid.Children.Add(checkBox);

        var icon = BuildGpuIconBadge(gpu.Manu, gpu.Name, 38);
        Grid.SetColumn(icon, 1);
        rowGrid.Children.Add(icon);

        string subText = !gpu.IsPartitionable
            ? L10n.Get("Gpu_PartitionUnsupported")
            : L10n.Format("Gpu_DedicatedMemory", FormatMemoryMb(gpu.MemoryBytes));

        var infoStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        infoStack.Children.Add(new TextBlock
        {
            Text = gpu.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextFillColorPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        infoStack.Children.Add(new TextBlock
        {
            Text = subText,
            FontSize = 12,
            Foreground = Brush("TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        Grid.SetColumn(infoStack, 2);
        rowGrid.Children.Add(infoStack);

        if (!gpu.IsPartitionable || alreadyAdded)
        {
            var tag = new Border
            {
                Background = ThemeHelper.TryBrush("VmBadgeBrush", Color.FromArgb(14, 255, 255, 255)),
                BorderBrush = Brush("VmKvWellBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = !gpu.IsPartitionable ? L10n.Get("Gpu_NoPartitionSupport") : L10n.Get("Gpu_AlreadyAdded"),
                    FontSize = 11,
                    Foreground = Brush("TextFillColorSecondaryBrush")
                }
            };
            Grid.SetColumn(tag, 3);
            rowGrid.Children.Add(tag);
        }

        var containerGrid = new Grid();
        containerGrid.Children.Add(accentBar);
        containerGrid.Children.Add(rowGrid);

        var defaultBg = ThemeHelper.TryBrush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(13, 255, 255, 255));
        var hoverBg = ThemeHelper.TryBrush("SubtleFillColorTertiaryBrush", Color.FromArgb(14, 255, 255, 255));
        var selectedBg = ThemeHelper.SubtleHighlightBrush(ActualTheme, 12, 10);
        var defaultStroke = ThemeHelper.TryBrush("CardStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255));
        var selectedStroke = Brush("VmAccentBrush");

        var rowCard = new Border
        {
            Background = defaultBg,
            BorderBrush = defaultStroke,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12, 16, 12),
            Opacity = canSelect ? 1.0 : 0.48,
            Child = containerGrid
        };

        void SyncVisual(bool selected)
        {
            rowCard.Background = selected ? selectedBg : defaultBg;
            rowCard.BorderBrush = selected ? selectedStroke : defaultStroke;
            accentBar.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }

        void SetSelected(bool selected)
        {
            if (!canSelect || string.IsNullOrWhiteSpace(gpu.InstanceId)) return;
            if (selected) selectedHostGpuIds.Add(gpu.InstanceId);
            else selectedHostGpuIds.Remove(gpu.InstanceId);
            SyncVisual(selected);
            UpdateBeginSelectionState();
        }

        bool suppress = false;
        checkBox.Checked += (_, _) =>
        {
            if (suppress) return;
            SetSelected(true);
        };
        checkBox.Unchecked += (_, _) =>
        {
            if (suppress) return;
            SetSelected(false);
        };
        if (canSelect)
        {
            rowCard.PointerEntered += (_, _) =>
            {
                if (checkBox.IsChecked != true) rowCard.Background = hoverBg;
            };
            rowCard.PointerExited += (_, _) =>
            {
                rowCard.Background = checkBox.IsChecked == true ? selectedBg : defaultBg;
            };
            rowCard.PointerCanceled += (_, _) =>
            {
                rowCard.Background = checkBox.IsChecked == true ? selectedBg : defaultBg;
            };
            rowCard.PointerPressed += (_, e) =>
            {
                if (IsDescendantOf(e.OriginalSource as DependencyObject, checkBox)) return;
                suppress = true;
                bool next = checkBox.IsChecked != true;
                checkBox.IsChecked = next;
                suppress = false;
                SetSelected(next);
            };
        }

        return rowCard;
    }

    private void UpdateBeginSelectionState() => beginSelectBtn.IsEnabled = selectedHostGpuIds.Count > 0;

    private async Task UpdateDriversAsync(VmGpuAssignment assignment)
    {
        EnsureNoUnfinishedDeployment();
        var result = await Task.Run(HostGpuService.GetHostGpusAsync);
        if (closing || IsStopped) return;
        if (!result.HasData) throw new InvalidOperationException(CoreMessageResolver.ResolveError(result));
        var gpu = result.Data!.SingleOrDefault(item =>
            string.Equals(item.InstanceId, assignment.HostDeviceInstanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(L10n.Get("Gpu_HostGpuMissing"));
        await StartDeploymentAsync((HostGpuInfo[])[gpu], GpuDeploymentMode.UpdateDrivers);
    }

    private async Task RemoveAssignmentAsync(VmGpuAssignment assignment)
    {
        if (closing || IsStopped) return;
        ApiResponse result;
        try { result = await Task.Run(() => service.RemoveGpuPartitionAsync(VmId, assignment)); }
        catch (Exception ex) { result = ApiResponse.Fail(ex.Message, exception: ex); }
        bool removalOutcomeUnknown = result.ErrorKind == ApiErrorKind.OutcomeUnknown;
        string resolvedError = result.Success ? string.Empty : CoreMessageResolver.ResolveError(result);
        string detail = removalOutcomeUnknown
            ? L10n.Get("Gpu_RemovalUnknown") + resolvedError
            : resolvedError;
        if (removalOutcomeUnknown)
            unconfirmedRemovals[VmId] = detail;
        NotifyChanged();

        string? refreshError = null;
        try { await RefreshAssignmentsAsync(); }
        catch (Exception ex) { refreshError = L10n.Format("Gpu_RefreshAssignmentsFailed", CoreMessageResolver.ResolveException(ex)); }
        if (!result.Success || removalOutcomeUnknown)
        {
            Notice(string.Join(Environment.NewLine, new[] { detail, refreshError }.Where(text => !string.IsNullOrWhiteSpace(text))), InfoBarSeverity.Error);
            return;
        }
        var state = await Task.Run(() => VmConfigurationState.ReadAsync(VmId));
        string restartRequired = state.HasData && !state.Data!.IsPoweredOff ? L10n.Get("Gpu_EffectiveAfterRestart") : string.Empty;
        Notice(L10n.Get("Gpu_AssignmentRemoved") + restartRequired + (refreshError == null ? string.Empty : "\n" + refreshError),
            refreshError == null ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
    }

    private void ShowOverview()
    {
        if (working || closing) return;
        overview.Visibility = Visibility.Visible;
        selection.Visibility = deployment.Visibility = Visibility.Collapsed;
    }

    private Task RunGpuOperationAsync(Func<Task> action)
    {
        if (working || closing || IsStopped || IsBusy) return Task.CompletedTask;
        ClearOperationFeedback();
        working = true;
        RefreshActions();
        operation = RunOperationCoreAsync(action);
        return operation;
    }

    private async Task RunOperationCoreAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (closing || IsStopped || CancellationToken.IsCancellationRequested)
        {
            // The view is already leaving. Its final snapshot remains available to cleanup.
        }
        catch (Exception ex)
        {
            Notice(CoreMessageResolver.ResolveException(ex), InfoBarSeverity.Error);
            if (workflow != null) ApplySnapshot(workflow, workflow.GetStatus());
        }
        finally { working = false; RefreshActions(); }
    }

    private void Notice(string text, InfoBarSeverity severity)
    {
        if (severity == InfoBarSeverity.Error) ShowError(text);
        else if (!closing && !IsStopped)
        {
            if (severity == InfoBarSeverity.Success) ShowAppliedToast(text);
            else ShowInformation(text, severity);
        }
    }

    private bool CopyTextToClipboard(string value, string label)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(value);
            Clipboard.SetContent(data);
            Clipboard.Flush();
            ShowAppliedToast(L10n.Format("Common_CopiedLabel", label));
            return true;
        }
        catch (Exception ex)
        {
            Notice(L10n.Format("Dev_CopyFailed", CoreMessageResolver.ResolveException(ex)), InfoBarSeverity.Error);
            return false;
        }
    }

    private static FrameworkElement BuildGpuIconBadge(string? manu, string? name, double size)
    {
        var imageSource = GpuIconSource.Get(manu, name, out string? fileName);
        if (imageSource == null)
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(6),
                Background = ThemeHelper.TryBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(14, 255, 255, 255)),
                BorderBrush = ThemeHelper.TryBrush("DividerStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = new FontIcon
                {
                    Glyph = "\uF211",
                    FontFamily = AppFonts.SegoeFluent,
                    FontSize = Math.Max(14, Math.Round(size * 0.45)),
                    Foreground = Brush("TextFillColorPrimaryBrush")
                }
            };
        }

        bool transparentBackdrop = GpuIconSource.IsTransparentBackdrop(fileName);
        var host = new Grid { Width = size, Height = size };
        host.Children.Add(new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255))
        });

        double imgSize = transparentBackdrop ? Math.Max(16, size - 6) : size;
        var img = new Image
        {
            Source = imageSource,
            Width = imgSize,
            Height = imgSize,
            Stretch = transparentBackdrop ? Stretch.Uniform : Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (!transparentBackdrop)
        {
            var clipHost = new Grid
            {
                Width = size,
                Height = size,
                Background = new SolidColorBrush(Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            clipHost.Children.Add(img);
            AttachRoundedClip(clipHost, (float)size, 6f);
            host.Children.Add(clipHost);
        }
        else
        {
            host.Children.Add(img);
        }

        host.Children.Add(new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(6),
            BorderBrush = ThemeHelper.TryBrush("ControlStrokeColorDefaultBrush", Color.FromArgb(28, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        });
        return host;
    }

    private static void AttachRoundedClip(FrameworkElement element, float fallbackSize, float radius)
    {
        void Apply()
        {
            try
            {
                float width = element.ActualWidth > 0 ? (float)element.ActualWidth : fallbackSize;
                float height = element.ActualHeight > 0 ? (float)element.ActualHeight : fallbackSize;
                var visual = ElementCompositionPreview.GetElementVisual(element);
                var compositor = visual.Compositor;
                var rect = compositor.CreateRoundedRectangleGeometry();
                rect.Size = new Vector2(width, height);
                rect.CornerRadius = new Vector2(radius, radius);
                visual.Clip = compositor.CreateGeometricClip(rect);
            }
            catch (Exception error) when (error is System.Runtime.InteropServices.COMException or NotSupportedException)
            {
                // Rounded clipping is optional when the compositor is unavailable.
            }
        }

        Apply();
        element.Loaded += (_, _) => Apply();
        element.SizeChanged += (_, _) => Apply();
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        for (var current = node; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private static string FormatMemoryMb(ulong? memoryBytes)
    {
        if (!memoryBytes.HasValue) return L10n.Get("Host_Sched_Unknown");
        double mb = memoryBytes.Value / (1024.0 * 1024.0);
        return $"{mb:N0} MB";
    }

    private static TextBlock Label(string text, double size = 14, bool secondary = false) => new()
    {
        Text = text,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(secondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush")
    };
}
