
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace ExHyperV.App.UI.Shared.Controls;

public enum VmUsageChartKind { Processor, Memory, Gpu, Transfer }

// The three appearances follow the corresponding graphs in the WPF dashboard.
public sealed partial class VmUsageChart : Grid
{
    internal const double ProcessorHeight = 84;
    private const int HistoryLength = 60;
    private readonly Queue<double> samples = new();
    private readonly VmUsageChartKind kind;
    private readonly Grid content = new();
    private readonly RectangleGeometry contentClip = new();
    private readonly Canvas gridLines = new();
    private readonly Canvas plot = new();
    private readonly PointCollection fillPoints = new();
    private readonly PointCollection strokePoints = new();
    private readonly Polygon fillShape;
    private readonly Polyline strokeShape;
    private readonly SolidColorBrush frameBgBrush = Brush(0x08, 255, 255, 255);
    private readonly SolidColorBrush frameBorderBrush = Brush(0x26, 0x88, 0x88, 0x88);
    private readonly SolidColorBrush gridBrush = Brush(0x14, 255, 255, 255);
    private readonly SolidColorBrush customFillBrush = new();
    private readonly SolidColorBrush customStrokeBrush = new();
    private bool? lastDarkTheme;
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
        content.Clip = contentClip;
        bool memory = kind == VmUsageChartKind.Memory;
        fillShape = new Polygon
        {
            Points = fillPoints,
            Visibility = Visibility.Collapsed
        };
        strokeShape = new Polyline
        {
            Points = strokePoints,
            StrokeThickness = memory ? 1.5 : 1.2,
            StrokeLineJoin = PenLineJoin.Round,
            Visibility = Visibility.Collapsed
        };
        plot.Children.Add(fillShape);
        plot.Children.Add(strokeShape);
        frame = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = frameBorderBrush,
            Background = frameBgBrush,
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
        ActualThemeChanged += (_, _) => Draw();
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
        if (lastDarkTheme != dark)
        {
            lastDarkTheme = dark;
            frameBgBrush.Color = dark
                ? Windows.UI.Color.FromArgb(0x08, 255, 255, 255)
                : Windows.UI.Color.FromArgb(0x06, 0, 0, 0);
            gridBrush.Color = dark
                ? Windows.UI.Color.FromArgb(0x14, 255, 255, 255)
                : Windows.UI.Color.FromArgb(0x10, 0, 0, 0);
        }

        double width = content.ActualWidth, height = content.ActualHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        if (width != gridWidth || height != gridHeight)
        {
            gridWidth = width; gridHeight = height;
            // Rebuild the fixed grid only on resize, not on every performance sample.
            contentClip.Rect = new Rect(0, 0, width, height);
            gridLines.Children.Clear();
            for (double x = 0; x < width; x += 10)
                gridLines.Children.Add(new Line { X1 = x, X2 = x, Y2 = height, Stroke = gridBrush, StrokeThickness = .5 });
            for (double y = 0; y < height; y += 10)
                gridLines.Children.Add(new Line { Y1 = y, Y2 = y, X2 = width, Stroke = gridBrush, StrokeThickness = .5 });
        }

        if (samples.Count == 0)
        {
            fillShape.Visibility = Visibility.Collapsed;
            strokeShape.Visibility = Visibility.Collapsed;
            fillPoints.Clear();
            strokePoints.Clear();
            return;
        }

        bool memory = kind == VmUsageChartKind.Memory;
        double plotWidth = width, plotHeight = height;
        int count = samples.Count;
        int offset = HistoryLength - count;
        double ceiling = 100;
        if (kind == VmUsageChartKind.Transfer)
        {
            double maxSample = 0;
            foreach (double sample in samples)
                if (sample > maxSample) maxSample = sample;
            ceiling = Math.Max(1024, maxSample * 1.2);
        }

        fillPoints.Clear();
        strokePoints.Clear();
        fillPoints.Add(new Point(offset * plotWidth / (HistoryLength - 1), plotHeight));
        int index = 0;
        foreach (double sample in samples)
        {
            var pt = new Point((offset + index) * plotWidth / (HistoryLength - 1), plotHeight * (1 - sample / ceiling));
            fillPoints.Add(pt);
            strokePoints.Add(pt);
            index++;
        }
        fillPoints.Add(new Point(plotWidth, plotHeight));

        if (accentColorOverride is { } custom)
        {
            var fillClr = Windows.UI.Color.FromArgb(memory ? (byte)0x38 : (byte)0x40, custom.R, custom.G, custom.B);
            if (customFillBrush.Color != fillClr) customFillBrush.Color = fillClr;
            if (customStrokeBrush.Color != custom) customStrokeBrush.Color = custom;
            if (!ReferenceEquals(fillShape.Fill, customFillBrush)) fillShape.Fill = customFillBrush;
            if (!ReferenceEquals(strokeShape.Stroke, customStrokeBrush)) strokeShape.Stroke = customStrokeBrush;
        }
        else
        {
            var themeFill = (Brush)Application.Current.Resources["VmChartFillBrush"];
            var themeStroke = (Brush)Application.Current.Resources["VmAccentBrush"];
            if (!ReferenceEquals(fillShape.Fill, themeFill)) fillShape.Fill = themeFill;
            if (!ReferenceEquals(strokeShape.Stroke, themeStroke)) strokeShape.Stroke = themeStroke;
        }

        fillShape.Visibility = Visibility.Visible;
        strokeShape.Visibility = Visibility.Visible;
    }
}
