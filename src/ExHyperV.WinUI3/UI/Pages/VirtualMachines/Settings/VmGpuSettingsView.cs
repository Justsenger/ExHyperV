using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmGpuSettingsView : VmSettingsView
{
    private sealed record UnconfirmedRemoval(string Message, string Details);

    private const string FluentSystemFont = "ms-appx:///Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";
    private const string SegoeFluentFont = "Segoe Fluent Icons";
    private const string UpgradeBasePathData = "M8,2.3 L12.6,7 H9.8 V10.2 H6.2 V7 H3.4 Z M3.4,11.9 H12.6 V13.45 H3.4 Z";

    private static readonly ConcurrentDictionary<Guid, UnconfirmedRemoval> unconfirmedRemovals = new();

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
    private readonly Button beginSelectBtn = new() { Content = "开始配置", IsEnabled = false };
    private readonly InfoBar operationNotice = new() { IsClosable = true, IsOpen = false, Visibility = Visibility.Collapsed };
    private readonly TextBox lastOperationDetails = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 220, FontFamily = new FontFamily("Consolas") };
    private readonly Expander lastOperation = new() { Header = "操作详情", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly SemaphoreSlim stopGate = new(1, 1);

    private bool closing;
    private bool working;
    private Task operation = Task.CompletedTask;
    private Task? scriptsLoadTask;
    private IReadOnlyList<VmGpuAssignment> currentAssignments = [];
    private IReadOnlyList<HostGpuInfo> hostGpuCatalog = [];
    private readonly HashSet<string> selectedHostGpuIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expandedCardKeys = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<LinuxGpuDeploymentScript> scripts = [];

    public VmGpuSettingsView(Guid vmId) : base(vmId)
    {
        service = new VmGpuService(query);
        installDrivers.IsEnabled = true;
        installDrivers.IsOn = true;
        beginSelectBtn.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];

        var backListBtn = new Button { Content = "返回" };
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

        var driverToggleCard = Card(Row("安装驱动", "添加显卡后继续识别系统分区并部署驱动", installDrivers));
        driverToggleCard.Margin = new Thickness(0);

        var selectActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        selectActions.Children.Add(backListBtn);
        selectActions.Children.Add(beginSelectBtn);

        selectionContent.Children.Add(hostGpuList);
        selectionContent.Children.Add(driverToggleCard);
        selectionContent.Children.Add(selectActions);
        selection.Content = selectionContent;

        overview.Content = assignedList;

        lastOperation.Content = lastOperationDetails;
        operationNotice.RegisterPropertyChangedCallback(InfoBar.IsOpenProperty, (_, _) =>
            operationNotice.Visibility = operationNotice.IsOpen ? Visibility.Visible : Visibility.Collapsed);
        panel.Children.Add(operationNotice);
        panel.Children.Add(overview);
        panel.Children.Add(selection);
        panel.Children.Add(BuildDeploymentPanel());
        panel.Children.Add(lastOperation);

        SetBody(panel);
        if (unconfirmedRemovals.TryGetValue(vmId, out var previousRemoval))
        {
            lastOperationDetails.Text = previousRemoval.Details;
            lastOperation.Visibility = string.IsNullOrWhiteSpace(previousRemoval.Details) ? Visibility.Collapsed : Visibility.Visible;
            Notice(previousRemoval.Message, InfoBarSeverity.Error);
        }
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
                FontFamily = new FontFamily(FluentSystemFont),
                FontSize = 15.5
            },
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["RoundIconButtonStyle"],
            IsEnabled = !unconfirmedRemovals.ContainsKey(VmId)
        };
        ToolTipService.SetToolTip(removeBtn, "移除显卡");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(removeBtn, "移除显卡");
        removeBtn.Tapped += (_, e) => e.Handled = true;
        removeBtn.Click += async (_, _) => await RunGpuOperationAsync(() => ConfirmAndRemoveAssignmentAsync(gpu));
        actions.Children.Add(removeBtn);

        var chevronIcon = new FontIcon
        {
            Glyph = isExpanded ? "\uE70E" : "\uE70D",
            FontFamily = new FontFamily(SegoeFluentFont),
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
        string memoryText = gpu.MemoryBytes is ulong bytes ? $"{bytes / 1048576d:F0} MiB" : "未取得";
        string? guestDriver = string.IsNullOrWhiteSpace(gpu.GuestDriverVersion) ? null : gpu.GuestDriverVersion;
        string hostDriver = string.IsNullOrWhiteSpace(gpu.DriverVersion) ? "---" : gpu.DriverVersion;
        string driverText = guestDriver != null
            ? (gpu.HasDriverUpdate ? $"{guestDriver}（宿主 {hostDriver}）" : guestDriver)
            : (hostDriver != "---" ? $"未部署（宿主 {hostDriver}）" : "---");
        string driverCopy = guestDriver ?? (hostDriver != "---" ? hostDriver : string.Empty);

        var c0 = BuildKvCell("芯片厂商", manu, manu);
        Grid.SetColumn(c0, 0);
        Grid.SetRow(c0, 0);
        detailsGrid.Children.Add(c0);

        var c1 = BuildKvCell("板卡厂商", vendor, vendor);
        Grid.SetColumn(c1, 1);
        Grid.SetRow(c1, 0);
        detailsGrid.Children.Add(c1);

        var c2 = BuildKvCell("宿主专用显存", memoryText, gpu.MemoryBytes.HasValue ? memoryText : string.Empty);
        Grid.SetColumn(c2, 0);
        Grid.SetRow(c2, 1);
        detailsGrid.Children.Add(c2);

        var c3 = BuildKvCell("驱动版本", driverText, driverCopy);
        Grid.SetColumn(c3, 1);
        Grid.SetRow(c3, 1);
        detailsGrid.Children.Add(c3);

        string instanceText = string.IsNullOrWhiteSpace(gpu.HostDeviceInstanceId) ? "---" : gpu.HostDeviceInstanceId;
        var c4 = BuildKvCell("设备实例路径", instanceText, gpu.HostDeviceInstanceId);
        Grid.SetColumn(c4, 0);
        Grid.SetRow(c4, 2);
        Grid.SetColumnSpan(c4, 2);
        detailsGrid.Children.Add(c4);

        var transparent = new SolidColorBrush(Colors.Transparent);
        var hoverBrush = TryBrush("SubtleFillColorTertiaryBrush", Color.FromArgb(14, 255, 255, 255));
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
            Background = TryBrush("DividerStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255)),
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
            Background = TryBrush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(13, 255, 255, 255)),
            BorderBrush = TryBrush("CardStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = cardStack
        };
    }

    private UIElement BuildUpdateIconButton(VmGpuAssignment gpu, Func<Task> onClick)
    {
        var glyph = new PathIcon
        {
            Data = ParseGeometry(UpgradeBasePathData),
            Width = 15,
            Height = 15,
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
            ? $"更新驱动（宿主新版本 {gpu.DriverVersion}）"
            : $"安装/更新驱动（宿主版本 {gpu.DriverVersion}）";
        ToolTipService.SetToolTip(btn, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(btn, tip);
        btn.Tapped += (_, e) => e.Handled = true;
        btn.Click += async (_, _) => await onClick();

        var dot = new Ellipse
        {
            Width = 9,
            Height = 9,
            Fill = new SolidColorBrush(Color.FromArgb(255, 255, 59, 48)),
            Stroke = TryBrush("SolidBackgroundFillColorTertiaryBrush", Color.FromArgb(255, 42, 42, 42)),
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
                FontFamily = new FontFamily(SegoeFluentFont),
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
            ToolTipService.SetToolTip(copyBtn, $"点击复制：{copyValue}");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copyBtn, $"复制{title}：{copyValue}");
            copyBtn.Click += async (_, _) =>
            {
                if (!CopyTextToClipboard(copyValue, title)) return;
                copyIcon.Glyph = "\uE73E";
                ToolTipService.SetToolTip(copyBtn, "已复制");
                await Task.Delay(1600);
                copyIcon.Glyph = "\uE8C8";
                ToolTipService.SetToolTip(copyBtn, $"点击复制：{copyValue}");
            };
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
            Background = TryBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(16, 255, 255, 255)),
            BorderBrush = TryBrush("DividerStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Child = new FontIcon
            {
                Glyph = "\uE710",
                FontFamily = new FontFamily(SegoeFluentFont),
                FontSize = 16,
                Foreground = Brush("TextFillColorPrimaryBrush")
            }
        };
        Grid.SetColumn(badge, 0);
        rowGrid.Children.Add(badge);

        var textStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = isEmpty ? "未添加显卡" : "添加显卡",
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = Brush("TextFillColorPrimaryBrush")
        });
        if (isEmpty)
        {
            textStack.Children.Add(new TextBlock
            {
                Text = "选择宿主机显卡以启用硬件图形加速",
                FontSize = 12,
                Foreground = Brush("TextFillColorSecondaryBrush")
            });
        }
        Grid.SetColumn(textStack, 1);
        rowGrid.Children.Add(textStack);

        var button = new Button
        {
            Content = rowGrid,
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["VmSelectorButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = TryBrush(isEmpty ? "CardBackgroundFillColorDefaultBrush" : "SubtleFillColorTertiaryBrush", Color.FromArgb(10, 255, 255, 255)),
            BorderBrush = TryBrush("CardStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255)),
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
        operationNotice.IsOpen = false;
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
            Notice("未找到宿主显卡。", InfoBarSeverity.Informational);

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
        _ = EnsureScriptsLoadedAsync(showWarningOnEmpty: false);
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
            ? "不支持 GPU 分区"
            : $"专用显存 {FormatMemoryMb(gpu.MemoryBytes)}";

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
                Background = TryBrush("VmBadgeBrush", Color.FromArgb(14, 255, 255, 255)),
                BorderBrush = Brush("VmKvWellBorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = !gpu.IsPartitionable ? "不支持分区" : "已添加",
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

        var defaultBg = TryBrush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(13, 255, 255, 255));
        var hoverBg = TryBrush("SubtleFillColorTertiaryBrush", Color.FromArgb(14, 255, 255, 255));
        var selectedBg = SubtleHighlightBrush(12, 10);
        var defaultStroke = TryBrush("CardStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255));
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

    private async Task EnsureScriptsLoadedAsync(bool showWarningOnEmpty = false)
    {
        scriptsLoadTask ??= LoadScriptsCoreAsync();
        await scriptsLoadTask;
        // 在线脚本入口沿用现有 Core 合同；空清单不等于确认没有 Linux 方案。
        if (showWarningOnEmpty && scripts.Count == 0 && !closing && !IsStopped)
            Notice("未取得 Linux 部署脚本清单，可能是网络或索引不可用。Windows 驱动部署不受影响。", InfoBarSeverity.Warning);
    }

    private async Task LoadScriptsCoreAsync()
    {
        try
        {
            var fetched = await Task.Run(service.GetAvailableScriptsAsync);
            if (!closing && !IsStopped) scripts = fetched;
        }
        catch
        {
            if (!closing && !IsStopped) scripts = [];
        }
    }

    private async Task UpdateDriversAsync(VmGpuAssignment assignment)
    {
        EnsureNoUnfinishedDeployment();
        var result = await Task.Run(HostGpuService.GetHostGpusAsync);
        if (closing || IsStopped) return;
        if (!result.HasData) throw new InvalidOperationException(CoreMessageResolver.ResolveError(result));
        var gpu = result.Data!.SingleOrDefault(item =>
            string.Equals(item.InstanceId, assignment.HostDeviceInstanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("找不到对应的宿主 GPU，无法确认可同步的驱动版本。");
        await StartDeploymentAsync((HostGpuInfo[])[gpu], GpuDeploymentMode.UpdateDrivers);
    }

    private async Task ConfirmAndRemoveAssignmentAsync(VmGpuAssignment assignment)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "移除显卡分配",
            Content = $"确定要从当前虚拟机移除“{assignment.Name}”？",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || closing || IsStopped) return;
        await RemoveAssignmentAsync(assignment);
    }

    private async Task RemoveAssignmentAsync(VmGpuAssignment assignment)
    {
        ApiResponse result;
        try { result = await Task.Run(() => service.RemoveGpuPartitionAsync(VmId, assignment)); }
        catch (Exception ex) { result = ApiResponse.Fail(ex.Message, exception: ex); }
        bool removalOutcomeUnknown = result.ErrorKind == ApiErrorKind.OutcomeUnknown;
        lastOperationDetails.Text = string.Join(Environment.NewLine, new[]
        {
            $"移除适配器：{assignment.AdapterId}",
            $"成功：{result.Success}",
            $"错误类型：{result.ErrorKind}",
            $"信息：{result.Error}"
        }.Where(text => !string.IsNullOrWhiteSpace(text)));
        lastOperation.Visibility = Visibility.Visible;
        string resolvedError = result.Success ? string.Empty : CoreMessageResolver.ResolveError(result);
        string detail = removalOutcomeUnknown
            ? "移除操作的最终结果尚未确认，不会自动重试，请先核实底层操作。\n" + resolvedError
            : resolvedError;
        if (removalOutcomeUnknown)
            unconfirmedRemovals[VmId] = new UnconfirmedRemoval(detail, lastOperationDetails.Text);
        NotifyChanged();

        string? refreshError = null;
        try { await RefreshAssignmentsAsync(); }
        catch (Exception ex) { refreshError = $"分配清单刷新失败：{CoreMessageResolver.ResolveException(ex)}"; }
        if (!result.Success || removalOutcomeUnknown)
        {
            Notice(string.Join(Environment.NewLine, new[] { detail, refreshError }.Where(text => !string.IsNullOrWhiteSpace(text))), InfoBarSeverity.Error);
            return;
        }
        var state = await Task.Run(() => VmConfigurationState.ReadAsync(VmId));
        string restartRequired = state.HasData && !state.Data!.IsPoweredOff ? "虚拟机重启后生效。" : string.Empty;
        Notice("GPU 分配已移除。" + restartRequired + (refreshError == null ? string.Empty : "\n" + refreshError),
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
        working = true;
        RefreshActions();
        operation = RunOperationCoreAsync(action);
        return operation;
    }

    private async Task RunOperationCoreAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Notice(CoreMessageResolver.ResolveException(ex), InfoBarSeverity.Error);
            if (workflow != null) ApplySnapshot(workflow, workflow.GetStatus());
        }
        finally { working = false; RefreshActions(); }
    }

    private void Notice(string text, InfoBarSeverity severity)
    {
        operationNotice.Title = severity == InfoBarSeverity.Error ? "操作未完成" : string.Empty;
        operationNotice.Message = text;
        operationNotice.Severity = severity;
        operationNotice.IsOpen = true;
    }

    private bool CopyTextToClipboard(string value, string label)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(value);
            Clipboard.SetContent(data);
            Clipboard.Flush();
            ShowAppliedToast($"已复制{label}");
            return true;
        }
        catch (Exception ex)
        {
            Notice($"复制失败：{CoreMessageResolver.ResolveException(ex)}", InfoBarSeverity.Error);
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
                Background = TryBrush("SubtleFillColorSecondaryBrush", Color.FromArgb(14, 255, 255, 255)),
                BorderBrush = TryBrush("DividerStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = new FontIcon
                {
                    Glyph = "\uF211",
                    FontFamily = new FontFamily(SegoeFluentFont),
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
            BorderBrush = TryBrush("ControlStrokeColorDefaultBrush", Color.FromArgb(28, 255, 255, 255)),
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
            catch { }
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

    private Brush SubtleHighlightBrush(byte darkAlpha, byte lightAlpha)
        => ActualTheme == ElementTheme.Light
            ? new SolidColorBrush(Color.FromArgb(lightAlpha, 0, 0, 0))
            : new SolidColorBrush(Color.FromArgb(darkAlpha, 255, 255, 255));

    private static Brush TryBrush(string key, Color fallback)
        => Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            : new SolidColorBrush(fallback);

    private static Geometry ParseGeometry(string pathData)
        => (Geometry)XamlReader.Load($"<Geometry xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">{pathData}</Geometry>");

    private static string FormatMemoryMb(ulong? memoryBytes)
    {
        if (!memoryBytes.HasValue) return "未知";
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
