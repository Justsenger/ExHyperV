using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

public enum VmUsageChartKind { Processor, Memory, Gpu, Transfer }

// The three appearances follow the corresponding graphs in the WPF dashboard.
public sealed class VmUsageChart : Grid
{
    internal const double ProcessorHeight = 84;
    private const int HistoryLength = 60;
    private readonly Queue<double> samples = new();
    private readonly VmUsageChartKind kind;
    private readonly Grid content = new();
    private readonly Canvas gridLines = new();
    private readonly Canvas plot = new();
    private double gridWidth = -1, gridHeight = -1;
    private Windows.UI.Color? accentColorOverride;
    private readonly Border frame;
    private readonly TextBlock value = new() { FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    public VmUsageChart(string caption, VmUsageChartKind kind = VmUsageChartKind.Processor, double? chartHeight = null)
    {
        this.kind = kind;
        Height = chartHeight ?? kind switch
        {
            VmUsageChartKind.Processor or VmUsageChartKind.Memory => ProcessorHeight,
            VmUsageChartKind.Gpu => 82,
            VmUsageChartKind.Transfer => 136,
            _ => 136
        };
        frame = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(0x26, 0x88, 0x88, 0x88),
            Background = Brush(0x08, 255, 255, 255),
            Child = content
        };
        Children.Add(frame);
        content.Children.Add(gridLines);
        content.Children.Add(plot);
        content.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 10,
            Opacity = .7,
            Margin = new Thickness(5, 3, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });
        content.Children.Add(value);
        ShowCenterValue = true;
        content.SizeChanged += (_, _) => Draw();
        ActualThemeChanged += (_, _) => { gridWidth = -1; Draw(); };
    }
    public bool ShowCenterValue { get => value.Visibility == Visibility.Visible; set => this.value.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public void SetAccentColor(Windows.UI.Color? color)
    {
        if (accentColorOverride == color) return;
        accentColorOverride = color;
        Draw();
    }
    public void Add(double percent, string label)
    {
        if (!double.IsFinite(percent)) return;
        samples.Enqueue(kind == VmUsageChartKind.Transfer ? Math.Max(0, percent) : Math.Clamp(percent, 0, 100));
        while (samples.Count > HistoryLength) samples.Dequeue();
        value.Text = label;
        value.FontSize = 18; value.Opacity = 1;
        Draw();
    }
    public void SetInactive() { samples.Clear(); value.Text = ""; Draw(); }
    public void Clear() { samples.Clear(); value.Text = ""; Draw(); }
    private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue)
        => new(Windows.UI.Color.FromArgb(alpha, red, green, blue));
    private void Draw()
    {
        bool dark = ActualTheme != ElementTheme.Light;
        frame.Background = dark ? Brush(0x08, 255, 255, 255) : Brush(0x06, 0, 0, 0);
        double width = content.ActualWidth, height = content.ActualHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        if (width != gridWidth || height != gridHeight)
        {
            gridWidth = width; gridHeight = height;
            // Rebuild the fixed grid only on resize, not on every performance sample.
            content.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
            gridLines.Children.Clear();
            var gridBrush = dark ? Brush(0x14, 255, 255, 255) : Brush(0x10, 0, 0, 0);
            for (double x = 0; x < width; x += 10)
                gridLines.Children.Add(new Line { X1 = x, X2 = x, Y2 = height, Stroke = gridBrush, StrokeThickness = .5 });
            for (double y = 0; y < height; y += 10)
                gridLines.Children.Add(new Line { Y1 = y, Y2 = y, X2 = width, Stroke = gridBrush, StrokeThickness = .5 });
        }
        plot.Children.Clear();
        if (samples.Count == 0) return;

        bool memory = kind == VmUsageChartKind.Memory;
        double plotWidth = width, plotHeight = height;
        var data = samples.ToArray();
        int offset = HistoryLength - data.Length;
        double ceiling = kind == VmUsageChartKind.Transfer ? Math.Max(1024, data.Max() * 1.2) : 100;
        var linePoints = new PointCollection();
        for (int i = 0; i < data.Length; i++)
            linePoints.Add(new Point((offset + i) * plotWidth / (HistoryLength - 1), plotHeight * (1 - data[i] / ceiling)));
        var points = new PointCollection { new(offset * plotWidth / (HistoryLength - 1), plotHeight) };
        foreach (var point in linePoints) points.Add(point);
        points.Add(new Point(plotWidth, plotHeight));
        Brush fillBrush = accentColorOverride is { } custom
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(memory ? (byte)0x38 : (byte)0x40, custom.R, custom.G, custom.B))
            : (Brush)Application.Current.Resources["VmChartFillBrush"];
        Brush strokeBrush = accentColorOverride is { } customStroke
            ? new SolidColorBrush(customStroke)
            : (Brush)Application.Current.Resources["VmAccentBrush"];
        plot.Children.Add(new Polygon
        {
            Points = points,
            Fill = fillBrush
        });
        // Each WinUI shape owns its mutable point collection.
        var strokePoints = new PointCollection();
        foreach (var point in linePoints) strokePoints.Add(point);
        plot.Children.Add(new Polyline
        {
            Points = strokePoints,
            Stroke = strokeBrush,
            StrokeThickness = memory ? 1.5 : 1.2,
            StrokeLineJoin = PenLineJoin.Round
        });
    }
}
