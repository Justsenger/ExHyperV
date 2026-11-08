using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;
// Natural-width badges wrap without stretching the shorter labels into equal cells.
public sealed class VmWrapPanel : Panel
{
    public double Spacing { get; set; } = 8;
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
        double x = 0, y = 0, height = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > finalSize.Width) { y += height + Spacing; x = 0; height = 0; }
            child.Arrange(new Rect(x, y, size.Width, size.Height)); x += size.Width + Spacing; height = Math.Max(height, size.Height);
        }
        return finalSize;
    }
}
