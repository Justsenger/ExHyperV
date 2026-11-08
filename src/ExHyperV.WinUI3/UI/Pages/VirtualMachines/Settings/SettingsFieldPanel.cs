using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>设置项按行分成两列；空间不足时保持输入控件完整并改为单列。</summary>
internal sealed class SettingsFieldPanel : Panel
{
    private const double Gap = 16;
    private const double MinimumColumnWidth = 210;

    private static int Columns(double width) => width >= MinimumColumnWidth * 2 + Gap ? 2 : 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        int columns = Columns(availableSize.Width);
        double width = (availableSize.Width - (columns - 1) * Gap) / columns;
        double height = 0;
        for (int start = 0; start < Children.Count; start += columns)
        {
            double rowHeight = 0;
            for (int offset = 0; offset < columns && start + offset < Children.Count; offset++)
            {
                var child = Children[start + offset];
                child.Measure(new Size(width, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            height += rowHeight + (start == 0 ? 0 : Gap);
        }
        return new Size(availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int columns = Columns(finalSize.Width);
        double width = (finalSize.Width - (columns - 1) * Gap) / columns;
        double y = 0;
        for (int start = 0; start < Children.Count; start += columns)
        {
            double height = 0;
            for (int offset = 0; offset < columns && start + offset < Children.Count; offset++)
                height = Math.Max(height, Children[start + offset].DesiredSize.Height);
            for (int offset = 0; offset < columns && start + offset < Children.Count; offset++)
                Children[start + offset].Arrange(new Rect(offset * (width + Gap), y, width, height));
            y += height + Gap;
        }
        return finalSize;
    }
}
