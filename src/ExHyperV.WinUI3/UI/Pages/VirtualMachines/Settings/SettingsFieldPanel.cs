using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>设置项按行分成两列；支持 Span2 占满整行，空间不足时自动退回单列。</summary>
internal sealed partial class SettingsFieldPanel : Panel
{
    public double Spacing { get; set; } = 12;
    private const double MinimumColumnWidth = 210;

    public static readonly DependencyProperty Span2Property =
        DependencyProperty.RegisterAttached("Span2", typeof(bool), typeof(SettingsFieldPanel), new PropertyMetadata(false));

    public static bool GetSpan2(DependencyObject obj) => (bool)obj.GetValue(Span2Property);
    public static void SetSpan2(DependencyObject obj, bool value) => obj.SetValue(Span2Property, value);

    private int Columns(double width) => width >= MinimumColumnWidth * 2 + Spacing ? 2 : 1;

    private List<UIElement> VisibleChildren()
    {
        var visible = new List<UIElement>(Children.Count);
        foreach (var child in Children)
        {
            if (child.Visibility != Visibility.Collapsed)
                visible.Add(child);
        }
        return visible;
    }

    private static List<(int Start, int Count, bool FullWidth)> BuildRows(int columns, IReadOnlyList<UIElement> visible)
    {
        var rows = new List<(int, int, bool)>();
        int i = 0;
        while (i < visible.Count)
        {
            if (GetSpan2(visible[i]) || columns == 1)
            {
                rows.Add((i, 1, true));
                i += 1;
            }
            else
            {
                int take = (i + 1 < visible.Count && !GetSpan2(visible[i + 1])) ? 2 : 1;
                rows.Add((i, take, false));
                i += take;
            }
        }
        return rows;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                child.Measure(new Size(0, 0));
        }

        int columns = Columns(availableSize.Width);
        double colWidth = (availableSize.Width - (columns - 1) * Spacing) / columns;
        var visible = VisibleChildren();
        var rows = BuildRows(columns, visible);
        double height = 0;
        for (int r = 0; r < rows.Count; r++)
        {
            var (start, count, fullWidth) = rows[r];
            double w = fullWidth ? availableSize.Width : colWidth;
            double rowHeight = 0;
            for (int offset = 0; offset < count; offset++)
            {
                var child = visible[start + offset];
                child.Measure(new Size(w, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            height += rowHeight + (r == 0 ? 0 : Spacing);
        }
        return new Size(availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = Columns(finalSize.Width);
        double colWidth = (finalSize.Width - (columns - 1) * Spacing) / columns;
        var visible = VisibleChildren();
        var rows = BuildRows(columns, visible);
        double y = 0;
        foreach (var (start, count, fullWidth) in rows)
        {
            double w = fullWidth ? finalSize.Width : colWidth;
            double rowHeight = 0;
            for (int offset = 0; offset < count; offset++)
                rowHeight = Math.Max(rowHeight, visible[start + offset].DesiredSize.Height);
            for (int offset = 0; offset < count; offset++)
                visible[start + offset].Arrange(new Rect(offset * (colWidth + Spacing), y, w, rowHeight));
            y += rowHeight + Spacing;
        }
        return finalSize;
    }
}
