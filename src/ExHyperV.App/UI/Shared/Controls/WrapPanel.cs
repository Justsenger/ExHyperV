using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ExHyperV.App.UI.Shared.Controls;

// Natural-width badges wrap without stretching the shorter labels into equal cells.
public partial class WrapPanel : Panel
{
    public double Spacing { get; set; } = 8;
    public bool CenterRows { get; set; }
    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, height = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > availableSize.Width) { width = Math.Max(width, x - Spacing); y += height + Spacing; x = 0; height = 0; }
            x += size.Width + Spacing; height = Math.Max(height, size.Height);
        }
        return new Size(Math.Max(width, Math.Max(0, x - Spacing)), y + height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        for (int first = 0; first < Children.Count;)
        {
            double width = Children[first].DesiredSize.Width;
            double height = Children[first].DesiredSize.Height;
            int end = first + 1;
            while (end < Children.Count && width + Spacing + Children[end].DesiredSize.Width <= finalSize.Width)
            {
                width += Spacing + Children[end].DesiredSize.Width;
                height = Math.Max(height, Children[end].DesiredSize.Height);
                end++;
            }
            double x = CenterRows ? Math.Max(0, (finalSize.Width - width) / 2) : 0;
            for (; first < end; first++)
            {
                var child = Children[first];
                var size = child.DesiredSize;
                child.Arrange(new Rect(x, y, size.Width, size.Height));
                x += size.Width + Spacing;
            }
            y += height + Spacing;
        }
        return finalSize;
    }
}
