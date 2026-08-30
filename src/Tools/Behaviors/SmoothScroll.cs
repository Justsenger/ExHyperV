using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ExHyperV.Tools;

/// <summary>
/// 为窗口内的像素滚动 ScrollViewer 提供平滑的鼠标滚轮滚动。
/// 在窗口预览阶段选择最靠近鼠标、且当前方向仍可滚动的容器，避免嵌套滚动区抢占外层滚动。
/// </summary>
public static class SmoothScroll
{
    private const double PixelsPerLine = 24;
    private const double MinimumWheelDistance = 48;
    private const double AnimationMilliseconds = 200;

    private static readonly ConditionalWeakTable<ScrollViewer, ScrollAnimation> Animations = new();
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        EventManager.RegisterClassHandler(
            typeof(Window),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel),
            handledEventsToo: true);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled
            || e.Delta == 0
            || !SystemParameters.ClientAreaAnimation
            || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0)
        {
            return;
        }

        int wheelLines = SystemParameters.WheelScrollLines;
        if (wheelLines == 0) return;

        ScrollViewer? viewer = FindScrollableViewer(e.OriginalSource as DependencyObject, e.Delta);
        if (viewer == null) return;

        double distance = wheelLines < 0
            ? Math.Max(MinimumWheelDistance, viewer.ViewportHeight * 0.9)
            : Math.Max(MinimumWheelDistance, wheelLines * PixelsPerLine);
        distance *= e.Delta / 120.0;

        e.Handled = true;
        Animations.GetValue(viewer, static item => new ScrollAnimation(item))
            .AddDelta(-distance);
    }

    private static ScrollViewer? FindScrollableViewer(DependencyObject? source, int wheelDelta)
    {
        for (DependencyObject? current = source; current != null; current = GetParent(current))
        {
            if (current is not ScrollViewer viewer) continue;

            // 由 ItemsControl 驱动的逻辑滚动以“项目”为单位，交给控件原生处理；
            // 页面和卡片区域使用的像素滚动才适合此缓动。
            if (viewer.CanContentScroll) return null;
            if (viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) continue;

            const double epsilon = 0.5;
            bool canScroll = wheelDelta > 0
                ? viewer.VerticalOffset > epsilon
                : viewer.VerticalOffset < viewer.ScrollableHeight - epsilon;
            if (canScroll) return viewer;
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual or Visual3D)
            return VisualTreeHelper.GetParent(child);
        if (child is FrameworkContentElement content)
            return content.Parent;
        return LogicalTreeHelper.GetParent(child);
    }

    private sealed class ScrollAnimation
    {
        private readonly ScrollViewer _viewer;
        private double _startOffset;
        private double _targetOffset;
        private long _startedAt;
        private bool _isRendering;

        public ScrollAnimation(ScrollViewer viewer) => _viewer = viewer;

        public void AddDelta(double delta)
        {
            double baseOffset = _isRendering ? _targetOffset : _viewer.VerticalOffset;
            double target = Math.Clamp(baseOffset + delta, 0, _viewer.ScrollableHeight);
            if (Math.Abs(target - _viewer.VerticalOffset) < 0.1)
            {
                Stop();
                return;
            }

            _startOffset = _viewer.VerticalOffset;
            _targetOffset = target;
            _startedAt = Stopwatch.GetTimestamp();

            if (_isRendering) return;
            _isRendering = true;
            _viewer.Unloaded += OnViewerUnloaded;
            CompositionTarget.Rendering += OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            double progress = Math.Min(
                1,
                Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds / AnimationMilliseconds);
            double remaining = 1 - progress;
            double eased = 1 - remaining * remaining * remaining;
            _viewer.ScrollToVerticalOffset(
                _startOffset + (_targetOffset - _startOffset) * eased);

            if (progress >= 1) Stop();
        }

        private void OnViewerUnloaded(object sender, RoutedEventArgs e) => Stop();

        private void Stop()
        {
            if (!_isRendering) return;
            _isRendering = false;
            CompositionTarget.Rendering -= OnRendering;
            _viewer.Unloaded -= OnViewerUnloaded;
        }
    }
}
