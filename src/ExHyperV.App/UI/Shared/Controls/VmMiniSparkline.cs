
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace ExHyperV.App.UI.Shared.Controls;

/// <summary>任务管理器风格概览左侧栏的 52×46 迷你趋势缩略图。</summary>
internal sealed partial class VmMiniSparkline : Grid
{
    private const int HistoryLength = 60;
    private const double BoxWidth = 52;
    private const double BoxHeight = 46;
    private readonly bool transfer;
    private readonly Queue<double> samples = new();
    private readonly Border frame;
    private readonly Canvas plot = new();
    private readonly RectangleGeometry plotClip = new();
    private readonly PointCollection fillPoints = new();
    private readonly PointCollection strokePoints = new();
    private readonly Polygon fillShape;
    private readonly Polyline strokeShape;
    private readonly SolidColorBrush bgBrush = new();
    private readonly SolidColorBrush borderBrush = new();
    private readonly SolidColorBrush fillBrush = new();
    private readonly SolidColorBrush strokeBrush = new();
    private double lastClipW = -1, lastClipH = -1;
    private bool inactive;
    private Color lineColor = Color.FromArgb(255, 0x38, 0x98, 0xEC);
    private Color fillColor = Color.FromArgb(0x45, 0x38, 0x98, 0xEC);
    private Color guideColor = Color.FromArgb(0x55, 0x88, 0x88, 0x88);

    public VmMiniSparkline(bool transfer = false)
    {
        this.transfer = transfer;
        Width = BoxWidth;
        Height = BoxHeight;
        fillBrush.Color = fillColor;
        strokeBrush.Color = lineColor;
        fillShape = new Polygon
        {
            Points = fillPoints,
            Fill = fillBrush,
            Visibility = Visibility.Collapsed
        };
        strokeShape = new Polyline
        {
            Points = strokePoints,
            Stroke = strokeBrush,
            StrokeThickness = 1.2,
            StrokeLineJoin = PenLineJoin.Round,
            Visibility = Visibility.Collapsed
        };
        plot.Clip = plotClip;
        plot.Children.Add(fillShape);
        plot.Children.Add(strokeShape);
        frame = new Border
        {
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
            Background = bgBrush,
            BorderBrush = borderBrush,
            Child = plot
        };
        Children.Add(frame);
        plot.SizeChanged += (_, _) => Draw();
    }

    public void Add(double value)
    {
        if (!double.IsFinite(value)) return;
        inactive = false;
        samples.Enqueue(transfer ? Math.Max(0, value) : Math.Clamp(value, 0, 100));
        while (samples.Count > HistoryLength) samples.Dequeue();
        Draw();
    }

    public void SetInactive()
    {
        inactive = true;
        samples.Clear();
        Draw();
    }

    public void Clear()
    {
        inactive = false;
        samples.Clear();
        Draw();
    }

    public void SetPalette(Color background, Color border, Color stroke, Color fill, Color guide)
    {
        if (bgBrush.Color == background && borderBrush.Color == border &&
            lineColor == stroke && fillColor == fill && guideColor == guide)
            return;

        bgBrush.Color = background;
        borderBrush.Color = border;
        lineColor = stroke;
        fillColor = fill;
        guideColor = guide;
        fillBrush.Color = fill;
        strokeBrush.Color = stroke;
    }

    private void Draw()
    {
        double width = plot.ActualWidth > 0 ? plot.ActualWidth : BoxWidth - 2;
        double height = plot.ActualHeight > 0 ? plot.ActualHeight : BoxHeight - 2;
        if (width != lastClipW || height != lastClipH)
        {
            lastClipW = width;
            lastClipH = height;
            plotClip.Rect = new Rect(0, 0, width, height);
        }

        int count = samples.Count;
        if (inactive || count == 0)
        {
            fillShape.Visibility = Visibility.Collapsed;
            strokeShape.Visibility = Visibility.Collapsed;
            fillPoints.Clear();
            strokePoints.Clear();
            return;
        }

        int offset = HistoryLength - count;
        double ceiling = 100;
        if (transfer)
        {
            double maxSample = 0;
            foreach (double sample in samples)
                if (sample > maxSample) maxSample = sample;
            ceiling = Math.Max(1024, maxSample * 1.2);
        }

        fillPoints.Clear();
        strokePoints.Clear();
        fillPoints.Add(new Point(offset * width / (HistoryLength - 1), height));
        int index = 0;
        foreach (double sample in samples)
        {
            double x = (offset + index) * width / (HistoryLength - 1);
            double y = height * (1 - sample / ceiling);
            var pt = new Point(x, Math.Clamp(y, 1, height - 1));
            fillPoints.Add(pt);
            strokePoints.Add(pt);
            index++;
        }
        fillPoints.Add(new Point(width, height));

        fillShape.Visibility = Visibility.Visible;
        strokeShape.Visibility = Visibility.Visible;
    }
}
