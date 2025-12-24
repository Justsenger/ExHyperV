using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ExHyperV.Tools
{
    public static class ScrollHelper
    {
        public static readonly DependencyProperty LockParentScrollProperty =
            DependencyProperty.RegisterAttached(
                "LockParentScroll",
                typeof(bool),
                typeof(ScrollHelper),
                new PropertyMetadata(false, OnLockParentScrollChanged));

        public static void SetLockParentScroll(DependencyObject element, bool value) => element.SetValue(LockParentScrollProperty, value);
        public static bool GetLockParentScroll(DependencyObject element) => (bool)element.GetValue(LockParentScrollProperty);
        private static readonly DependencyProperty CachedScrollerProperty =
            DependencyProperty.RegisterAttached("CachedScroller", typeof(ScrollViewer), typeof(ScrollHelper), new PropertyMetadata(null));

        private static readonly DependencyProperty OriginalVisibilityProperty =
            DependencyProperty.RegisterAttached("OriginalVisibility", typeof(ScrollBarVisibility), typeof(ScrollHelper), new PropertyMetadata(ScrollBarVisibility.Auto));

        private static void OnLockParentScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is Page page)
            {
                if ((bool)e.NewValue)
                {
                    page.Loaded += Page_Loaded;
                    page.Unloaded += Page_Unloaded;
                }
                else
                {
                    page.Loaded -= Page_Loaded;
                    page.Unloaded -= Page_Unloaded;
                }
            }
        }

        private static void Page_Loaded(object sender, RoutedEventArgs e)
        {
            var page = sender as Page;
            if (page == null) return;
            var scroller = FindParent<ScrollViewer>(page);
            if (scroller != null)
            {
                page.SetValue(CachedScrollerProperty, scroller);
                page.SetValue(OriginalVisibilityProperty, scroller.VerticalScrollBarVisibility);
                scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            }
        }

        private static void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            var page = sender as Page;
            if (page == null) return;
            var scroller = (ScrollViewer)page.GetValue(CachedScrollerProperty);
            var originalValue = (ScrollBarVisibility)page.GetValue(OriginalVisibilityProperty);

            if (scroller != null)
            {
                scroller.VerticalScrollBarVisibility = originalValue;
                page.SetValue(CachedScrollerProperty, null);
            }
        }

        private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent) return parent;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }
    }
}