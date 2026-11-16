using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

/// <summary>任务管理器风格概览左侧栏的 52×46 迷你趋势缩略图。</summary>
internal sealed class VmMiniSparkline : Grid
{
    private const int HistoryLength = 60;
    private const double BoxWidth = 52;
    private const double BoxHeight = 46;
    private readonly bool transfer;
    private readonly Queue<double> samples = new();
    private readonly Border frame;
    private readonly Canvas plot = new();
    private bool inactive;
    private Color lineColor = Color.FromArgb(255, 0x38, 0x98, 0xEC);
    private Color fillColor = Color.FromArgb(0x45, 0x38, 0x98, 0xEC);
    private Color guideColor = Color.FromArgb(0x55, 0x88, 0x88, 0x88);

    public VmMiniSparkline(bool transfer = false)
    {
        this.transfer = transfer;
        Width = BoxWidth;
        Height = BoxHeight;
        frame = new Border
        {
            CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1),
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
        frame.Background = new SolidColorBrush(background);
        frame.BorderBrush = new SolidColorBrush(border);
        lineColor = stroke;
        fillColor = fill;
        guideColor = guide;
        Draw();
    }

    private void Draw()
    {
        double width = plot.ActualWidth > 0 ? plot.ActualWidth : BoxWidth - 2;
        double height = plot.ActualHeight > 0 ? plot.ActualHeight : BoxHeight - 2;
        plot.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
        plot.Children.Clear();

        var data = samples.ToArray();
        if (inactive || data.Length == 0)
            return;

        int offset = HistoryLength - data.Length;
        double ceiling = transfer ? Math.Max(1024, data.Max() * 1.2) : 100;
        var linePoints = new PointCollection();
        for (int i = 0; i < data.Length; i++)
        {
            double x = (offset + i) * width / (HistoryLength - 1);
            double y = height * (1 - data[i] / ceiling);
            linePoints.Add(new Point(x, Math.Clamp(y, 1, height - 1)));
        }

        var fillPoints = new PointCollection { new(offset * width / (HistoryLength - 1), height) };
        foreach (var point in linePoints) fillPoints.Add(point);
        fillPoints.Add(new Point(width, height));

        plot.Children.Add(new Polygon
        {
            Points = fillPoints,
            Fill = new SolidColorBrush(fillColor)
        });

        var strokePoints = new PointCollection();
        foreach (var point in linePoints) strokePoints.Add(point);
        plot.Children.Add(new Polyline
        {
            Points = strokePoints,
            Stroke = new SolidColorBrush(lineColor),
            StrokeThickness = 1.2,
            StrokeLineJoin = PenLineJoin.Round
        });
    }
}
