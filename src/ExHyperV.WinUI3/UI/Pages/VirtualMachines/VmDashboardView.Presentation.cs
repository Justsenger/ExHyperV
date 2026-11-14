using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;
public sealed partial class VmDashboardView
{
    private readonly VmUsageChart[] gpuCharts = [new("3D", VmUsageChartKind.Gpu), new("数据复制", VmUsageChartKind.Gpu), new("视频编码", VmUsageChartKind.Gpu), new("视频解码", VmUsageChartKind.Gpu)];
    private readonly double?[] gpuValues = new double?[4];
    private string copiedIp = "";
    private readonly Dictionary<string, SolidColorBrush> themedBrushes = new();
    private Windows.UI.Color ThemeColor(string key)
    {
        string theme = ActualTheme == ElementTheme.Light ? "Light" : "Default";
        foreach (var resources in Application.Current.Resources.MergedDictionaries)
            if (resources.ThemeDictionaries.TryGetValue(theme, out var entry) && entry is ResourceDictionary dictionary
                && dictionary.TryGetValue(key, out var value) && value is SolidColorBrush brush) return brush.Color;
        return ((SolidColorBrush)Application.Current.Resources[key]).Color;
    }
    private Brush Brush(string key)
    {
        if (!themedBrushes.TryGetValue(key, out var brush)) themedBrushes[key] = brush = new SolidColorBrush(ThemeColor(key));
        return brush;
    }
    private void UpdateDeviceTheme()
    {
        foreach (var pair in themedBrushes) pair.Value.Color = ThemeColor(pair.Key);
        if (state != null) UpdatePowerAppearance();
    }
    private void LayoutCoreCharts()
    {
        const double gap = 6;
        const double minimumChartWidth = 104;
        const int maximumColumns = 8;
        int count = charts.Count;
        if (count == 0)
        {
            CoreCharts.ColumnDefinitions.Clear();
            CoreCharts.RowDefinitions.Clear();
            memoryChart.Height = ResourceDetails.Height = VmUsageChart.ProcessorHeight;
            return;
        }

        int limit = Math.Max(1, Math.Min(Math.Min(maximumColumns, count),
            (int)Math.Floor((ResourceDetails.ActualWidth + gap) / (minimumChartWidth + gap))));
        int rows = (int)Math.Ceiling(count / (double)limit);
        // Keep the minimum row count, then balance those rows without stretching the last one.
        int columns = (int)Math.Ceiling(count / (double)rows);
        // The shared detail surface must not change height when switching CPU/memory,
        // including a wrapped CPU grid or resizing while memory is selected.
        memoryChart.Height = ResourceDetails.Height = rows * VmUsageChart.ProcessorHeight + (rows - 1) * gap;
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
        bool perDevice = gpuCards.Count == 1;
        bool showCharts = perDevice && state == VmState.Running && gpuDriverBound == true;
        GpuCharts.Visibility = showCharts ? Visibility.Visible : Visibility.Collapsed;
        GpuMessage.Visibility = showCharts ? Visibility.Collapsed : Visibility.Visible;
        GpuMessage.Text = hasGpu == false ? "未分配显卡" : gpuCards.Count > 1 ? "当前 Core 返回的是虚拟机整体 GPU 监控，尚不能区分这些显卡的读数。" :
            hasGpu == true && state != VmState.Running ? "虚拟机未启动" : hasGpu == true && gpuDriverBound == false ? "等待 GPU 引擎监控数据" : "";
    }
    private void SetGpuReadings(string value)
    {
        if (gpuCards.Count == 1) gpuCards[0].Value = value;
    }
    private void UpdateGpuCharts(VmQueryRow row)
    {
        if (row.Get(VmGpuPerformanceFields.HasGpuEngineInstance) is { HasValue: true } bound) gpuDriverBound = bound.Value;
        UpdateGpuVisibility();
        if (gpuCards.Count != 1) return;
        VmQueryField<double>[] fields = [VmGpuPerformanceFields.Gpu3d, VmGpuPerformanceFields.GpuCopy, VmGpuPerformanceFields.GpuEncode, VmGpuPerformanceFields.GpuDecode];
        for (int i = 0; i < 4; i++) if (row.Get(fields[i]) is { HasValue: true } sample)
        {
            gpuCharts[i].Add(sample.Value, $"{sample.Value:N1}%"); gpuValues[i] = sample.Value;
        }
        if (gpuValues.Any(value => value.HasValue)) SetGpuReadings($"{gpuValues.Max():N1}");
    }    private void UpdatePowerAppearance()
    {
        bool running = state == VmState.Running;
        PowerText.Text = running ? "关机" : state == VmState.Suspended ? "恢复" : "启动";
        PowerButton.Padding = running ? new Thickness(24, 0, 24, 0) : new Thickness(41, 6, 41, 6);
        PowerButton.Background = running ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 67, 54)) : (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        PowerButton.Foreground = running ? new SolidColorBrush(Colors.White) : (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
        PowerButton.BorderThickness = new Thickness(0);
        PowerButton.CornerRadius = running ? new CornerRadius(8, 0, 0, 8) : new CornerRadius(8);
        PowerMenu.Background = PowerButton.Background; PowerMenu.Foreground = PowerButton.Foreground; PowerMenu.BorderThickness = new Thickness(0);
        PowerMenu.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        PowerMenu.IsEnabled = stateVerified && !powerPending;
    }
    private async void OnPowerMenuClick(object sender, RoutedEventArgs e)
    {
        if (current == null || state != VmState.Running || powerPending || sender is not MenuFlyoutItem { Tag: string name } || !Enum.TryParse<VmPowerAction>(name, out var action)) return;
        PowerWork = ChangePowerAsync(current, state, action);
        await PowerWork;
    }
    private void OnCopyIp(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (copiedIp.Length == 0) return;
        try { var package = new DataPackage(); package.SetText(copiedIp); Clipboard.SetContent(package); }
        catch (Exception exception) { ShowError($"无法复制 IP 地址：{exception.Message}"); }
    }
}
