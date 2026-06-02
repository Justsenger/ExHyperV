using System;
using System.Windows;
using System.Windows.Controls;

namespace ExHyperV.Tools
{
    public static class AutoFocus
    {
        public static readonly DependencyProperty IsFocusedProperty =
            DependencyProperty.RegisterAttached(
                "IsFocused",
                typeof(bool),
                typeof(AutoFocus),
                new UIPropertyMetadata(false, OnIsFocusedChanged));

        public static bool GetIsFocused(DependencyObject obj) => (bool)obj.GetValue(IsFocusedProperty);
        public static void SetIsFocused(DependencyObject obj, bool value) => obj.SetValue(IsFocusedProperty, value);

        private static void OnIsFocusedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement fe && (bool)e.NewValue)
            {
                fe.Dispatcher.BeginInvoke(new Action(() =>
                {
                    fe.Focus();
                    if (fe is TextBox tb) tb.SelectAll();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }
        }
    }
}