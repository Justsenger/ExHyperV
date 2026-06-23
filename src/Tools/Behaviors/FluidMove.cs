using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ExHyperV.Tools
{
    /// <summary>
    /// 子元素纵向布局位置变化时平滑滑动（替代 Microsoft.Xaml.Behaviors 的 FluidMoveBehavior，
    /// 等价于 AppliesTo=Children + 仅 Y 轴 + CubicEase EaseOut，默认 0.3s）。
    /// 用法：在 Panel 上设 behaviors:FluidMove.IsEnabled="True"。
    /// </summary>
    public static class FluidMove
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled", typeof(bool), typeof(FluidMove),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(DependencyObject o, bool v) => o.SetValue(IsEnabledProperty, v);

        public static readonly DependencyProperty DurationProperty =
            DependencyProperty.RegisterAttached(
                "Duration", typeof(Duration), typeof(FluidMove),
                new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(300))));

        public static Duration GetDuration(DependencyObject o) => (Duration)o.GetValue(DurationProperty);
        public static void SetDuration(DependencyObject o, Duration v) => o.SetValue(DurationProperty, v);

        private static readonly DependencyProperty TrackerProperty =
            DependencyProperty.RegisterAttached("Tracker", typeof(Tracker), typeof(FluidMove));

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not System.Windows.Controls.Panel panel) return;
            if ((bool)e.NewValue)
            {
                if (panel.GetValue(TrackerProperty) is null)
                    panel.SetValue(TrackerProperty, new Tracker(panel));
            }
            else if (panel.GetValue(TrackerProperty) is Tracker t)
            {
                t.Detach();
                panel.SetValue(TrackerProperty, null);
            }
        }

        private sealed class Tracker
        {
            private readonly System.Windows.Controls.Panel _panel;
            private readonly Dictionary<UIElement, double> _lastY = new();

            public Tracker(System.Windows.Controls.Panel panel)
            {
                _panel = panel;
                _panel.LayoutUpdated += OnLayoutUpdated;
            }

            public void Detach() => _panel.LayoutUpdated -= OnLayoutUpdated;

            private void OnLayoutUpdated(object? sender, EventArgs e)
            {
                if (!_panel.IsArrangeValid) return;
                Duration duration = GetDuration(_panel);
                var live = new HashSet<UIElement>();

                foreach (UIElement child in _panel.Children)
                {
                    if (child is not FrameworkElement fe) continue;
                    live.Add(child);

                    double newY = LayoutInformation.GetLayoutSlot(fe).Top;
                    if (double.IsInfinity(newY) || double.IsNaN(newY)) continue;

                    if (_lastY.TryGetValue(child, out double oldY))
                    {
                        double delta = oldY - newY; // 布局上移/下移量；>0 表示元素被往下挪了
                        if (Math.Abs(delta) > 0.5)
                        {
                            TranslateTransform tt = EnsureTransform(child);
                            // 从“看起来还在旧位置”滑回 0，保持当前在途偏移以避免跳变
                            double from = tt.Y + delta;
                            tt.BeginAnimation(TranslateTransform.YProperty,
                                new DoubleAnimation(from, 0.0, duration)
                                {
                                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                                });
                        }
                    }
                    _lastY[child] = newY;
                }

                if (_lastY.Count > live.Count)
                {
                    var dead = new List<UIElement>();
                    foreach (UIElement k in _lastY.Keys)
                        if (!live.Contains(k)) dead.Add(k);
                    foreach (UIElement k in dead) _lastY.Remove(k);
                }
            }

            private static TranslateTransform EnsureTransform(UIElement child)
            {
                if (child.RenderTransform is TranslateTransform existing) return existing;
                var tt = new TranslateTransform();
                child.RenderTransform = tt;
                return tt;
            }
        }
    }
}
