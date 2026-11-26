using System.Globalization;
using ExHyperV.App.Localization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace ExHyperV.App.UI.Shared.Controls;

internal enum EnergySliderKind { Count, Reservation, Limit, RelativeWeight, Custom }

internal sealed partial class SettingsEnergySlider : UserControl
{
    private const double ThumbWidth = 12;

    private readonly double trackHeight;
    private readonly double thumbHeight;
    private readonly EnergySliderKind kind;
    private readonly Grid root = new() { Background = new SolidColorBrush(Colors.Transparent) };
    private readonly Border track = new()
    {
        CornerRadius = new CornerRadius(3),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Border fill = new()
    {
        CornerRadius = new CornerRadius(3),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Stretch,
        Width = 0
    };
    private readonly Canvas bubbleCanvas = new() { IsHitTestVisible = false };
    private readonly Border thumb = new()
    {
        Width = ThumbWidth,
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Popup tooltipPopup = new()
    {
        IsLightDismissEnabled = false,
        ShouldConstrainToRootBounds = true
    };
    private readonly Border tooltipBorder = new()
    {
        Padding = new Thickness(9, 4, 9, 4),
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        IsHitTestVisible = false
    };
    private readonly TextBlock tooltipText = new()
    {
        FontSize = 11.5,
        FontWeight = FontWeights.Normal
    };

    private readonly List<BubbleParticle> bubbles = [];
    private readonly RectangleGeometry fillClip = new();
    private bool isRendering;
    private long lastRenderTicks;
    private string lastMeasuredHint = string.Empty;
    private double cachedTipWidth;
    private double cachedTipHeight;

    private int min = 0;
    private int max = 100;
    private int? coupledMin;
    private int? coupledMax;
    private string? coupledReason;
    private int currentValue = 0;
    private double displayRatio = 0;
    private double targetRatio = 0;
    private bool hasInitialLayout;
    private int contextCount = 1;
    private int hostThreads = Math.Max(1, Environment.ProcessorCount);
    private bool isDragging;
    private bool hittingConstraint;
    private double grabOffsetX;
    private uint? capturedPointerId;
    private readonly Border constraintMarker = new()
    {
        Width = 2,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Stretch,
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed
    };

    private int EffectiveMin => Math.Clamp(coupledMin ?? min, min, max);
    private int EffectiveMax => Math.Clamp(Math.Max(EffectiveMin, coupledMax ?? max), min, max);

    public Func<int, string>? CustomHintFormatter { get; set; }
    public Func<int, int>? SnapValue { get; set; }
    public event Action<int>? ValueChanged;

    private sealed class BubbleParticle
    {
        public required Ellipse Shape { get; init; }
        public required ScaleTransform Scale { get; init; }
        public double XRatio { get; init; }
        public double Phase { get; set; }
        public double Speed { get; init; }
        public double Size { get; init; }
    }

    public SettingsEnergySlider(EnergySliderKind kind, bool compact = false)
    {
        this.kind = kind;
        trackHeight = compact ? 14 : 24;
        thumbHeight = compact ? 22 : 34;
        root.Height = thumbHeight;
        track.Height = trackHeight;
        thumb.Height = thumbHeight;
        if (kind == EnergySliderKind.Count) { min = 1; max = hostThreads; }
        else if (kind == EnergySliderKind.RelativeWeight) { min = 1; max = 10000; }

        fill.Clip = fillClip;
        fill.Child = bubbleCanvas;

        var trackGrid = new Grid();
        trackGrid.Children.Add(fill);
        trackGrid.Children.Add(constraintMarker);
        track.Child = trackGrid;

        root.Children.Add(track);
        root.Children.Add(thumb);
        tooltipBorder.Child = tooltipText;
        tooltipPopup.Child = tooltipBorder;
        root.Children.Add(tooltipPopup);
        Content = root;

        InitBubbles();
        ApplyThemeBrushes();
        ActualThemeChanged += (_, _) => ApplyThemeBrushes();
        SizeChanged += (_, _) => UpdateVisuals();
        Unloaded += (_, _) => StopBubblesAndTooltip(forceStopRendering: true);

        IsTabStop = true;
        UseSystemFocusVisuals = true;
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += OnPointerReleased;
        PointerCaptureLost += (_, _) => StopBubblesAndTooltip();
        KeyDown += OnSliderKeyDown;
    }

    public int Value
    {
        get => currentValue;
        set
        {
            int clamped = Math.Clamp(value, min, max);
            if (clamped == currentValue) { UpdateVisuals(); return; }
            currentValue = clamped;
            UpdateVisuals();
        }
    }

    public void SetRange(int minimum, int maximum)
    {
        int nextMax = Math.Max(minimum, maximum);
        if (min == minimum && max == nextMax) return;
        min = minimum;
        max = nextMax;
        currentValue = Math.Clamp(currentValue, min, max);
        UpdateVisuals();
    }

    public void SetContext(int vmCoreCount, int hostLogicalCount)
    {
        int nextCores = Math.Max(1, vmCoreCount);
        int nextHost = hostLogicalCount > 0 ? hostLogicalCount : hostThreads;
        if (contextCount == nextCores && hostThreads == nextHost) return;
        contextCount = nextCores;
        hostThreads = nextHost;
        if (isDragging) UpdateTooltipTextAndPosition();
    }

    public void SetCoupledBounds(int? minimumAllowed, int? maximumAllowed, string? reason = null)
    {
        if (coupledMin == minimumAllowed && coupledMax == maximumAllowed && coupledReason == reason)
            return;
        coupledMin = minimumAllowed;
        coupledMax = maximumAllowed;
        coupledReason = reason;
        if (currentValue > EffectiveMin && currentValue < EffectiveMax)
            hittingConstraint = false;
        UpdateVisuals();
    }

    private void InitBubbles()
    {
        (double x, double size, double phase, double speed)[] specs =
        [
            (0.10, 4.0, 0.00, 1.18),
            (0.24, 6.0, 0.25, 0.95),
            (0.39, 3.0, 0.12, 1.32),
            (0.54, 5.0, 0.42, 1.05),
            (0.68, 4.0, 0.18, 1.14),
            (0.81, 6.0, 0.55, 0.91),
            (0.91, 3.5, 0.33, 1.25),
        ];

        var bubbleBrush = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255));
        foreach (var (x, size, phase, speed) in specs)
        {
            var scale = new ScaleTransform { CenterX = size / 2, CenterY = size / 2 };
            var el = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = bubbleBrush,
                RenderTransform = scale,
                Visibility = Visibility.Collapsed
            };
            bubbleCanvas.Children.Add(el);
            bubbles.Add(new BubbleParticle { Shape = el, Scale = scale, XRatio = x, Phase = phase, Speed = speed, Size = size });
        }
    }

    private void StartRendering()
    {
        if (isRendering) return;
        isRendering = true;
        lastRenderTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnCompositionRendering;
    }

    private void StopRendering()
    {
        if (!isRendering) return;
        isRendering = false;
        CompositionTarget.Rendering -= OnCompositionRendering;
    }

    private void OnCompositionRendering(object? sender, object e)
    {
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = Math.Clamp((double)(nowTicks - lastRenderTicks) / System.Diagnostics.Stopwatch.Frequency, 0.001, 0.05);
        lastRenderTicks = nowTicks;

        double diff = targetRatio - displayRatio;
        if (Math.Abs(diff) > 0.0003)
        {
            double alpha = 1.0 - Math.Exp(-38.0 * dt);
            displayRatio += diff * alpha;
            ApplyDisplayRatio();
        }
        else if (displayRatio != targetRatio)
        {
            displayRatio = targetRatio;
            ApplyDisplayRatio();
        }
        else if (!isDragging)
        {
            StopRendering();
            return;
        }

        if (!isDragging) return;

        double fillW = Math.Max(1, fill.Width);
        foreach (var b in bubbles)
        {
            b.Phase = (b.Phase + dt * b.Speed) % 1.0;
            double p = b.Phase;
            double left = b.XRatio * Math.Max(0, fillW - b.Size) + p * 4.0;
            double bottom = -2 + p * (trackHeight + 4);
            double top = trackHeight - bottom - b.Size;
            double opacity = p < 0.2 ? (p / 0.2) * 0.85 : p > 0.8 ? ((1.0 - p) / 0.2) * 0.55 : 0.85 - (p - 0.2) * 0.5;
            double scale = 0.6 + p * 0.55;

            Canvas.SetLeft(b.Shape, left);
            Canvas.SetTop(b.Shape, top);
            b.Shape.Opacity = Math.Clamp(opacity, 0, 1);
            b.Scale.ScaleX = scale;
            b.Scale.ScaleY = scale;
        }
    }

    private void StartBubblesAndTooltip()
    {
        foreach (var b in bubbles) b.Shape.Visibility = Visibility.Visible;
        StartRendering();
        if (XamlRoot != null)
        {
            tooltipPopup.IsOpen = true;
            UpdateTooltipTextAndPosition();
        }
    }

    private void StopBubblesAndTooltip(bool forceStopRendering = false)
    {
        isDragging = false;
        hittingConstraint = false;
        capturedPointerId = null;
        foreach (var b in bubbles) b.Shape.Visibility = Visibility.Collapsed;
        tooltipPopup.IsOpen = false;
        if (forceStopRendering || Math.Abs(targetRatio - displayRatio) <= 0.0003)
        {
            displayRatio = targetRatio;
            ApplyDisplayRatio();
            StopRendering();
        }
    }

    private void ApplyThemeBrushes()
    {
        var res = Application.Current.Resources;
        var primaryText = (Brush)res["TextFillColorPrimaryBrush"];
        var trackColor = primaryText is SolidColorBrush scb
            ? Color.FromArgb(20, scb.Color.R, scb.Color.G, scb.Color.B)
            : Color.FromArgb(20, 128, 128, 128);
        var markerColor = primaryText is SolidColorBrush m
            ? Color.FromArgb(115, m.Color.R, m.Color.G, m.Color.B)
            : Color.FromArgb(115, 255, 255, 255);
        bool isLight = ActualTheme == ElementTheme.Light;
        track.Background = new SolidColorBrush(trackColor);
        constraintMarker.Background = new SolidColorBrush(markerColor);
        fill.Background = (Brush)res["VmHeaderCheckedBrush"];
        thumb.Background = new SolidColorBrush(isLight ? Color.FromArgb(255, 245, 245, 245) : Color.FromArgb(255, 44, 44, 44));
        thumb.BorderBrush = isLight ? new SolidColorBrush(Color.FromArgb(64, 0, 0, 0)) : (Brush)res["ControlStrokeColorDefaultBrush"];
        tooltipBorder.Background = (Brush)res["SolidBackgroundFillColorQuarternaryBrush"];
        tooltipBorder.BorderBrush = (Brush)res["ControlStrokeColorDefaultBrush"];
        tooltipText.Foreground = primaryText;
    }

    private double ValueToRatio(int val)
    {
        if (kind == EnergySliderKind.RelativeWeight)
        {
            double clamped = Math.Clamp(val, 1, 10000);
            return Math.Clamp(Math.Log10(clamped) / 4.0, 0, 1);
        }
        return max > min ? Math.Clamp((double)(val - min) / (max - min), 0, 1) : 0;
    }

    private int RatioToValue(double ratio)
    {
        double r = Math.Clamp(ratio, 0, 1);
        if (kind != EnergySliderKind.RelativeWeight)
        {
            int raw = (int)Math.Round(min + r * (max - min));
            return SnapValue != null ? Math.Clamp(SnapValue(raw), min, max) : raw;
        }

        int v = (int)Math.Round(Math.Pow(10, r * 4.0));
        if (v <= 20) return Math.Max(1, v);
        if (v <= 100) return (int)Math.Round(v / 5.0) * 5;
        if (v <= 500) return (int)Math.Round(v / 10.0) * 10;
        if (v <= 2000) return (int)Math.Round(v / 50.0) * 50;
        return Math.Min(10000, (int)Math.Round(v / 200.0) * 200);
    }

    private string FormatHint()
    {
        string baseHint;
        if (kind == EnergySliderKind.Custom)
        {
            baseHint = CustomHintFormatter?.Invoke(currentValue) ?? currentValue.ToString();
        }
        else if (kind == EnergySliderKind.Count)
        {
            int pct = (int)Math.Round(currentValue * 100.0 / Math.Max(1, hostThreads));
            baseHint = L10n.Format("Energy_HostPercent", pct);
        }
        else if (kind == EnergySliderKind.Reservation)
        {
            double eff = contextCount * currentValue / 100.0;
            baseHint = L10n.Format("Energy_ReserveCores", FormatCores(eff));
        }
        else if (kind == EnergySliderKind.Limit)
        {
            double eff = contextCount * currentValue / 100.0;
            baseHint = L10n.Format("Energy_LimitCores", FormatCores(eff));
        }
        else if (currentValue == 100)
        {
            baseHint = L10n.Get("Energy_WeightBalanced");
        }
        else
        {
            double mult = currentValue / 100.0;
            string multStr = mult < 1 ? mult.ToString("0.##") : mult.ToString("0.#");
            baseHint = currentValue < 100
                ? L10n.Format("Energy_WeightLow", multStr)
                : L10n.Format("Energy_WeightHigh", multStr);
        }

        bool showConstraint = !string.IsNullOrWhiteSpace(coupledReason) &&
            (hittingConstraint ||
             (coupledMin is { } cMin && cMin > min && currentValue <= EffectiveMin) ||
             (coupledMax is { } cMax && cMax < max && currentValue >= EffectiveMax));
        string sep = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "，" : ", ";
        return showConstraint ? $"{baseHint}{sep}{coupledReason}" : baseHint;
    }

    private static string FormatCores(double val)
    {
        double rounded = Math.Round(val, 1);
        return Math.Abs(rounded - Math.Round(rounded)) < 0.05
            ? ((int)Math.Round(rounded)).ToString()
            : rounded.ToString("0.0");
    }

    private void UpdateVisuals()
    {
        targetRatio = ValueToRatio(currentValue);
        double w = root.ActualWidth;
        if (w <= 0)
        {
            displayRatio = targetRatio;
            return;
        }
        if (!hasInitialLayout)
        {
            hasInitialLayout = true;
            displayRatio = targetRatio;
            ApplyDisplayRatio();
            return;
        }

        if (Math.Abs(targetRatio - displayRatio) > 0.0003)
        {
            StartRendering();
            UpdateConstraintMarker(w);
            if (isDragging) UpdateTooltipTextAndPosition();
        }
        else
        {
            displayRatio = targetRatio;
            ApplyDisplayRatio();
        }
    }

    private void UpdateConstraintMarker(double w)
    {
        double usable = Math.Max(1, w - ThumbWidth);
        int? markerVal = coupledMin is { } cMin && cMin > min ? cMin
            : coupledMax is { } cMax && cMax < max ? cMax
            : null;
        if (markerVal is { } mv)
        {
            double markerX = Math.Clamp(ValueToRatio(mv) * usable + ThumbWidth / 2.0 - 1, 0, Math.Max(0, w - 2));
            constraintMarker.Margin = new Thickness(markerX, 0, 0, 0);
            constraintMarker.Visibility = Visibility.Visible;
        }
        else
        {
            constraintMarker.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyDisplayRatio()
    {
        double w = root.ActualWidth;
        if (w <= 0) return;
        double ratio = Math.Clamp(displayRatio, 0, 1);
        double usable = Math.Max(1, w - ThumbWidth);
        double left = ratio * usable;
        bool hasFill = ratio > 0.0001;
        double fillW = hasFill ? Math.Clamp(left + ThumbWidth / 2.0, 0, w) : 0;
        thumb.Margin = new Thickness(left, 0, 0, 0);
        fill.Visibility = hasFill ? Visibility.Visible : Visibility.Collapsed;
        fill.Width = fillW;
        fillClip.Rect = new Rect(0, 0, fillW, trackHeight);

        UpdateConstraintMarker(w);

        if (isDragging)
        {
            UpdateTooltipTextAndPosition();
        }
    }

    private void UpdateTooltipTextAndPosition()
    {
        if (!tooltipPopup.IsOpen || root.ActualWidth <= 0) return;
        string hint = FormatHint();
        if (!string.Equals(hint, lastMeasuredHint, StringComparison.Ordinal) || cachedTipWidth <= 0)
        {
            lastMeasuredHint = hint;
            tooltipText.Text = hint;
            tooltipBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            cachedTipWidth = tooltipBorder.DesiredSize.Width;
            cachedTipHeight = tooltipBorder.DesiredSize.Height;
        }
        double ratio = Math.Clamp(displayRatio, 0, 1);
        double desiredX = ratio * root.ActualWidth - ratio * cachedTipWidth;
        double clampedX = Math.Clamp(desiredX, 0, Math.Max(0, root.ActualWidth - cachedTipWidth));
        tooltipPopup.HorizontalOffset = clampedX;
        tooltipPopup.VerticalOffset = -(cachedTipHeight + 6);
    }

    private void UpdateFromPointerX(double pointerX)
    {
        double w = root.ActualWidth;
        if (w <= 0) return;
        double usable = Math.Max(1, w - ThumbWidth);
        double rawRatio = Math.Clamp((pointerX - grabOffsetX) / usable, 0, 1);
        int rawValue = RatioToValue(rawRatio);
        int effMin = EffectiveMin;
        int effMax = EffectiveMax;
        int next = Math.Clamp(rawValue, effMin, effMax);
        bool nextHitting = (coupledMin.HasValue && effMin > min && rawValue <= effMin)
            || (coupledMax.HasValue && effMax < max && rawValue >= effMax);
        bool hittingChanged = nextHitting != hittingConstraint;
        hittingConstraint = nextHitting;
        if (next != currentValue)
        {
            currentValue = next;
            UpdateVisuals();
            ValueChanged?.Invoke(currentValue);
        }
        else if (hittingChanged)
        {
            UpdateTooltipTextAndPosition();
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsEnabled) return;
        var pt = e.GetCurrentPoint(root);
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse && !pt.Properties.IsLeftButtonPressed)
            return;

        e.Handled = true;
        Focus(FocusState.Pointer);
        if (!root.CapturePointer(e.Pointer)) return;

        isDragging = true;
        capturedPointerId = e.Pointer.PointerId;
        double thumbLeft = thumb.Margin.Left;
        if (pt.Position.X >= thumbLeft && pt.Position.X <= thumbLeft + ThumbWidth)
            grabOffsetX = pt.Position.X - thumbLeft;
        else
        {
            grabOffsetX = ThumbWidth / 2.0;
            UpdateFromPointerX(pt.Position.X);
        }
        StartBubblesAndTooltip();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!isDragging || capturedPointerId != e.Pointer.PointerId) return;
        e.Handled = true;
        UpdateFromPointerX(e.GetCurrentPoint(root).Position.X);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (capturedPointerId != e.Pointer.PointerId) return;
        e.Handled = true;
        try { root.ReleasePointerCapture(e.Pointer); }
        catch (System.Runtime.InteropServices.COMException error) when (error.HResult == unchecked((int)0x80000013))
        {
            // The XAML input source was closed and no longer owns the capture.
        }
        StopBubblesAndTooltip();
    }

    private void OnSliderKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsEnabled) return;
        int step = kind == EnergySliderKind.RelativeWeight ? 25 : kind == EnergySliderKind.Count ? 1 : 5;
        int delta = e.Key switch
        {
            VirtualKey.Left or VirtualKey.Down => -step,
            VirtualKey.Right or VirtualKey.Up => step,
            _ => 0
        };
        if (delta == 0) return;
        e.Handled = true;
        int rawNext = currentValue + delta;
        int effMin = EffectiveMin;
        int effMax = EffectiveMax;
        int next = Math.Clamp(rawNext, effMin, effMax);
        hittingConstraint = (coupledMin.HasValue && effMin > min && rawNext <= effMin)
            || (coupledMax.HasValue && effMax < max && rawNext >= effMax);
        if (next != currentValue)
        {
            currentValue = next;
            UpdateVisuals();
            ValueChanged?.Invoke(currentValue);
        }
    }
}
