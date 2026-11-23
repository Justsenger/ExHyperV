
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace ExHyperV.App.UI.Shared.Controls;

internal static class ScrollViewerExtensions
{
    internal static void AttachScrollbarClearance(
        this ScrollViewer scroll,
        FrameworkElement? marginTarget = null,
        double bottomSpacing = 0,
        double gutterWidth = 18)
    {
        void Sync()
        {
            double right = scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? gutterWidth : 0;
            var spacing = new Thickness(0, 0, right, bottomSpacing);
            if (marginTarget != null)
            {
                if (marginTarget.Margin != spacing) marginTarget.Margin = spacing;
            }
            else if (scroll.Padding != spacing)
            {
                scroll.Padding = spacing;
            }
        }

        scroll.RegisterPropertyChangedCallback(ScrollViewer.ComputedVerticalScrollBarVisibilityProperty, (_, _) => Sync());
        scroll.Loaded += (_, _) => Sync();
        Sync();
    }
}
