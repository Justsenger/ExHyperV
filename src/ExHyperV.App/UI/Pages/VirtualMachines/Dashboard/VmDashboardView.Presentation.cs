
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Pages.VirtualMachines.Console;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Windows.ApplicationModel.DataTransfer;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Dashboard;

public sealed partial class VmDashboardView
{
    private readonly VmUsageChart[] gpuCharts =
    [
        new(L10n.Get("Overview_Gpu3d"), VmUsageChartKind.Gpu),
        new(L10n.Get("Overview_GpuCopy"), VmUsageChartKind.Gpu),
        new(L10n.Get("Overview_GpuEncode"), VmUsageChartKind.Gpu),
        new(L10n.Get("Overview_GpuDecode"), VmUsageChartKind.Gpu)
    ];
    private readonly double?[] gpuValues = new double?[4];
    private readonly Dictionary<string, SolidColorBrush> themedBrushes = new();
    private readonly SolidColorBrush powerRunningBackground = new(Windows.UI.Color.FromArgb(255, 244, 67, 54));
    private readonly SolidColorBrush powerRunningForeground = new(Colors.White);
    private (VmState? State, bool DefaultSave, bool Pending, VmPowerAction? PendingAction, ElementTheme Theme)? lastPowerAppearance;

    private Windows.UI.Color ThemeColor(string key) => ThemeHelper.ThemeColor(ActualTheme, key);

    private Brush Brush(string key)
    {
        if (!themedBrushes.TryGetValue(key, out var brush)) themedBrushes[key] = brush = new SolidColorBrush(ThemeColor(key));
        return brush;
    }

    private void UpdateDeviceTheme()
    {
        foreach (var pair in themedBrushes) pair.Value.Color = ThemeColor(pair.Key);
        lastPowerAppearance = null;
        if (state != null) UpdatePowerAppearance();
    }

    private void LayoutCoreCharts()
    {
        const double gap = 6;
        const double minimumChartWidth = 96;
        const int maximumColumns = 8;
        int count = charts.Count;
        if (count == 0)
        {
            CoreCharts.ColumnDefinitions.Clear();
            CoreCharts.RowDefinitions.Clear();
            memoryChart.Height = 136;
            return;
        }

        double availableWidth = CoreCharts.ActualWidth > 0 ? CoreCharts.ActualWidth
            : CpuDetailPanel.ActualWidth > 0 ? CpuDetailPanel.ActualWidth
            : OverviewDetailScroll.ActualWidth;
        if (availableWidth <= 0) availableWidth = 520;

        int limit = Math.Max(1, Math.Min(Math.Min(maximumColumns, count),
            (int)Math.Floor((availableWidth + gap) / (minimumChartWidth + gap))));
        int rows = (int)Math.Ceiling(count / (double)limit);
        int columns = (int)Math.Ceiling(count / (double)rows);

        memoryChart.Height = 136;
        CoreCharts.ColumnSpacing = CoreCharts.RowSpacing = gap;
        if (CoreCharts.ColumnDefinitions.Count != columns)
        {
            CoreCharts.ColumnDefinitions.Clear();
            for (int i = 0; i < columns; i++) CoreCharts.ColumnDefinitions.Add(new ColumnDefinition());
        }
        if (CoreCharts.RowDefinitions.Count != rows)
        {
            CoreCharts.RowDefinitions.Clear();
            for (int i = 0; i < rows; i++) CoreCharts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        int index = 0;
        foreach (var chart in charts.Values)
        {
            Grid.SetRow(chart, index / columns);
            Grid.SetColumn(chart, index++ % columns);
            chart.Margin = new Thickness(0);
        }
    }

    private void InitializeGpuCharts()
    {
        GpuCharts.ColumnDefinitions.Add(new()); GpuCharts.ColumnDefinitions.Add(new());
        GpuCharts.RowDefinitions.Add(new()); GpuCharts.RowDefinitions.Add(new());
        for (int i = 0; i < 4; i++)
        {
            Grid.SetColumn(gpuCharts[i], i % 2); Grid.SetRow(gpuCharts[i], i / 2);
            GpuCharts.Children.Add(gpuCharts[i]);
        }
    }

    private void UpdateGpuVisibility()
    {
        bool perDevice = gpuEntries.Count == 1;
        bool waitingDriver = state == VmState.Running && gpuDriverBound == false;
        bool showCharts = perDevice && !waitingDriver;
        GpuCharts.Visibility = showCharts ? Visibility.Visible : Visibility.Collapsed;
        GpuMessage.Visibility = showCharts ? Visibility.Collapsed : Visibility.Visible;
        GpuMessage.Text = hasGpu == false ? L10n.Get("Overview_NoGpu")
            : gpuEntries.Count > 1 ? L10n.Get("Overview_GpuMultiWait")
            : waitingDriver ? L10n.Get("Overview_GpuWaitingDriver") : "";
    }

    private void UpdateGpuCharts(VmQueryRow row)
    {
        if (row.Get(VmGpuPerformanceFields.HasGpuEngineInstance) is { HasValue: true } bound)
            gpuDriverBound = bound.Value;
        UpdateGpuVisibility();
        if (gpuEntries.Count != 1) return;

        VmQueryField<double>[] fields =
        [
            VmGpuPerformanceFields.Gpu3d,
            VmGpuPerformanceFields.GpuCopy,
            VmGpuPerformanceFields.GpuEncode,
            VmGpuPerformanceFields.GpuDecode
        ];
        for (int i = 0; i < 4; i++)
        {
            if (row.Get(fields[i]) is { HasValue: true } sample && double.IsFinite(sample.Value))
            {
                double clamped = Math.Clamp(sample.Value, 0, 100);
                gpuCharts[i].Add(clamped, $"{clamped:N1}%");
                gpuValues[i] = clamped;
            }
        }
        if (gpuValues.Any(value => value.HasValue))
        {
            double peak = gpuValues.Where(v => v.HasValue).Max(v => v!.Value);
            gpuReadingText = $"{peak:N1}%";
            gpuEntries[0].ReadingText = gpuReadingText;
            if (overviewRailByKey.TryGetValue("gpu:" + gpuEntries[0].Key, out var rail))
            {
                rail.Sparkline.Add(peak);
                rail.StatText.Text = gpuReadingText;
            }
        }
    }

    private void OnConnectClick(object sender, RoutedEventArgs e)
    {
        if (current == null) return;
        try
        {
            ConsoleWindow.Open(current);
        }
        catch (Exception error)
        {
            Diagnostics.AppDiagnostics.Failure("Console", "OpenFailed", error);
            ShowError(L10n.Format("Dash_ConnectFailed", CoreMessageResolver.ResolveException(error)));
        }
    }

    private void UpdatePowerAppearance()
    {
        bool defaultSave = string.Equals(
            ((App)Application.Current).Appearance?.Settings.DefaultPowerAction,
            "Save",
            StringComparison.OrdinalIgnoreCase);
        bool pending = IsPowerPending;
        var nextState = (state, defaultSave, pending, pending ? activePowerAction : null, ActualTheme);
        if (lastPowerAppearance == nextState) return;
        lastPowerAppearance = nextState;

        bool running = state is VmState.Running or VmState.Starting or VmState.Resuming;
        PowerText.Text = running
            ? L10n.Get(defaultSave ? "Dash_Power_SaveState" : "Dash_Power_ShutDown")
            : state == VmState.Suspended ? L10n.Get("Dash_Power_Resume")
            : L10n.Get("Dash_Power_Start");
        PowerButton.Padding = running ? new Thickness(20, 0, 20, 0) : new Thickness(24, 6, 24, 6);
        PowerButton.Background = running
            ? powerRunningBackground
            : (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        PowerButton.Foreground = running
            ? powerRunningForeground
            : (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
        PowerButton.BorderThickness = new Thickness(0);
        PowerButton.CornerRadius = state == VmState.Running ? new CornerRadius(8, 0, 0, 8) : new CornerRadius(8);
        bool actionable = state is VmState.Running or VmState.PoweredOff or VmState.Saved or VmState.Suspended;
        bool turnOffPending = pending && activePowerAction == VmPowerAction.TurnOff;
        PowerButton.IsEnabled = actionable && !pending;
        PowerMenu.Background = PowerButton.Background;
        PowerMenu.Foreground = PowerButton.Foreground;
        PowerMenu.BorderThickness = new Thickness(0);
        PowerMenu.Visibility = state == VmState.Running ? Visibility.Visible : Visibility.Collapsed;
        PowerMenu.IsEnabled = state == VmState.Running && !turnOffPending;
        PowerMenuRestart.IsEnabled = CanRunPower(VmPowerAction.Restart);
        PowerMenuSuspend.IsEnabled = CanRunPower(VmPowerAction.Suspend);
        PowerMenuSave.IsEnabled = CanRunPower(VmPowerAction.Save);
        PowerMenuTurnOff.IsEnabled = CanRunPower(VmPowerAction.TurnOff);
    }

    private async void OnPowerMenuClick(object sender, RoutedEventArgs e) => await UiEvent.RunAsync(async () =>
    {
        if (current == null || state != VmState.Running || sender is not MenuFlyoutItem { Tag: string name } || !Enum.TryParse<VmPowerAction>(name, out var action) || !CanRunPower(action))
            return;
        PowerWork = ChangePowerAsync(current, state, action);
        await PowerWork;
    }, message => ShowError(message));

    private Flyout? osPickerFlyout;
    private TextBox? osPickerSearch;
    private Grid? osPickerGrid;

    private void OnOpenOsPicker(object sender, RoutedEventArgs e) => OpenOsPicker();

    private void OnOpenOsPicker(object sender, TappedRoutedEventArgs e) => OpenOsPicker();

    private void OpenOsPicker()
    {
        if (current == null) return;
        EnsureOsPickerFlyout();
        if (osPickerSearch != null) osPickerSearch.Text = "";
        RebuildOsPickerGrid("");
        osPickerFlyout!.ShowAt(OsBadgeButton);
        osPickerSearch?.Focus(FocusState.Programmatic);
    }

    private void EnsureOsPickerFlyout()
    {
        if (osPickerFlyout != null) return;

        osPickerSearch = new TextBox
        {
            PlaceholderText = L10n.Get("Dash_SearchOsPlaceholder"),
            Height = 32,
            FontSize = 12.5
        };
        osPickerSearch.TextChanged += (_, _) => RebuildOsPickerGrid(osPickerSearch.Text);

        osPickerGrid = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 6
        };
        for (int i = 0; i < 4; i++)
            osPickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var scroll = new ScrollViewer
        {
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = osPickerGrid
        };

        var root = new StackPanel { Spacing = 10, Width = 420 };
        root.Children.Add(osPickerSearch);
        root.Children.Add(scroll);

        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 460.0));

        osPickerFlyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            FlyoutPresenterStyle = presenterStyle,
            Content = root
        };
    }

    private void RebuildOsPickerGrid(string query)
    {
        if (osPickerGrid == null) return;
        osPickerGrid.Children.Clear();
        osPickerGrid.RowDefinitions.Clear();

        string filter = query.Trim();
        var items = string.IsNullOrEmpty(filter)
            ? OsImages.SupportedTypes
            : OsImages.SupportedTypes.Where(name => name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (items.Count == 0)
        {
            osPickerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var empty = new TextBlock
            {
                Text = L10n.Get("Dash_NoMatchingOs"),
                FontSize = 12.5,
                Foreground = Brush("TextFillColorSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 20, 0, 20)
            };
            Grid.SetColumnSpan(empty, 4);
            osPickerGrid.Children.Add(empty);
            return;
        }

        string selected = OsText.Text;
        int rows = (int)Math.Ceiling(items.Count / 4.0);
        for (int r = 0; r < rows; r++)
            osPickerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < items.Count; i++)
        {
            string osName = items[i];
            bool isSelected = osName.Equals(selected, StringComparison.OrdinalIgnoreCase);

            var content = new StackPanel
            {
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            content.Children.Add(new Image
            {
                Source = OsIconSource.Get(osName),
                Width = 24,
                Height = 24,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            content.Children.Add(new TextBlock
            {
                Text = osName,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            var button = new Button
            {
                Tag = osName,
                Content = content,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 8, 6, 8),
                CornerRadius = new CornerRadius(6),
                Background = isSelected ? Brush("SubtleFillColorSecondaryBrush") : Brush("CardBackgroundFillColorSecondaryBrush"),
                BorderBrush = isSelected ? Brush("AccentFillColorDefaultBrush") : Brush("VmStrokeBrush"),
                BorderThickness = new Thickness(1)
            };
            ToolTipService.SetToolTip(button, osName);
            button.Click += OnPickOperatingSystemClick;

            Grid.SetRow(button, i / 4);
            Grid.SetColumn(button, i % 4);
            osPickerGrid.Children.Add(button);
        }
    }

    private async void OnPickOperatingSystemClick(object sender, RoutedEventArgs e) => await UiEvent.RunAsync(async () =>
    {
        if (current == null || sender is not Button { Tag: string osName }) return;
        osPickerFlyout?.Hide();
        ApplyOperatingSystem(osName);

        var vmId = current.Id;
        var result = await VmMetadataService.SetAsync(vmId, VmMetadataService.OsTypeKey, osName);
        if (!result.Success && current?.Id == vmId)
            ShowError(result.Success ? L10n.Get("Dash_SaveOsFailed") : CoreMessageResolver.ResolveError(result));
    }, message => ShowError(message));

    private void OnCopyGuidClick(object sender, RoutedEventArgs e)
    {
        if (current == null) return;
        try
        {
            var package = new DataPackage();
            package.SetText(current.Id.ToString("D").ToUpperInvariant());
            Clipboard.SetContent(package);
        }
        catch (Exception exception)
        {
            ShowError(L10n.Format("Dash_CopyGuidFailed", exception.Message));
        }
    }
}
