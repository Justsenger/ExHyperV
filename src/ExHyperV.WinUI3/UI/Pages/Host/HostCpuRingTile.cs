using ExHyperV.Models;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;

namespace ExHyperV.WinUI3.UI.Pages.Host;

internal enum CpuRingDensity { Cols4, Cols6, Cols8 }

internal sealed record CpuRingSlice(string ConsumerKey, string Label, double Percent, Color Color);

internal sealed partial class AnnularRingCanvas : UserControl
{
    private const double Cx = 29;
    private const double Cy = 29;
    private const double RIn = 12;
    private const double ROut = 23;
    private const double SliceCornerRadius = 2.5;

    private readonly Viewbox viewbox = new() { Stretch = Stretch.Uniform };
    private readonly Canvas canvas = new() { Width = 58, Height = 58 };
    private readonly Ellipse trackRing = new()
    {
        Width = ROut * 2,
        Height = ROut * 2,
        StrokeThickness = ROut - RIn
    };
    private readonly Ellipse fullSliceRing = new()
    {
        Width = ROut * 2,
        Height = ROut * 2,
        StrokeThickness = ROut - RIn,
        Visibility = Visibility.Collapsed
    };
    private readonly List<ShapePath> arcPool = [];

    public AnnularRingCanvas()
    {
        Canvas.SetLeft(trackRing, Cx - ROut);
        Canvas.SetTop(trackRing, Cy - ROut);
        Canvas.SetLeft(fullSliceRing, Cx - ROut);
        Canvas.SetTop(fullSliceRing, Cy - ROut);
        canvas.Children.Add(trackRing);
        canvas.Children.Add(fullSliceRing);
        viewbox.Child = canvas;
        Content = viewbox;
    }

    public void Update(
        Color trackColor,
        IReadOnlyList<CpuRingSlice> keptArcs,
        string? focusedConsumerKey)
    {
        trackRing.Stroke = new SolidColorBrush(trackColor);

        double totalSweep = 0;
        foreach (var s in keptArcs) totalSweep += s.Percent;

        if (keptArcs.Count == 1 && keptArcs[0].Percent >= 99.5)
        {
            var single = keptArcs[0];
            bool dimmed = focusedConsumerKey != null && !string.Equals(single.ConsumerKey, focusedConsumerKey, StringComparison.Ordinal);
            fullSliceRing.Stroke = new SolidColorBrush(single.Color);
            fullSliceRing.Opacity = dimmed ? 0.16 : 1.0;
            fullSliceRing.Visibility = Visibility.Visible;
            for (int i = 0; i < arcPool.Count; i++)
                arcPool[i].Visibility = Visibility.Collapsed;
            return;
        }

        fullSliceRing.Visibility = Visibility.Collapsed;

        double curDeg = -90;
        int used = 0;
        for (int i = 0; i < keptArcs.Count; i++)
        {
            var s = keptArcs[i];
            double spanDeg = (Math.Clamp(s.Percent, 0, 100) / 100.0) * 360.0;
            double gap = spanDeg > 5.0 && (keptArcs.Count > 1 || totalSweep < 98.5) ? 2.4 : 0;
            double start = curDeg + gap / 2.0;
            double end = curDeg + spanDeg - gap / 2.0;
            curDeg += spanDeg;

            var geom = BuildAnnularSector(Cx, Cy, RIn, ROut, start, end, SliceCornerRadius);
            if (geom == null) continue;

            ShapePath path;
            if (used < arcPool.Count)
            {
                path = arcPool[used];
            }
            else
            {
                path = new ShapePath();
                arcPool.Add(path);
                canvas.Children.Add(path);
            }

            bool dimmed = focusedConsumerKey != null && !string.Equals(s.ConsumerKey, focusedConsumerKey, StringComparison.Ordinal);
            path.Data = geom;
            path.Fill = new SolidColorBrush(s.Color);
            path.Opacity = dimmed ? 0.16 : 1.0;
            path.Visibility = Visibility.Visible;
            used++;
        }

        for (int i = used; i < arcPool.Count; i++)
            arcPool[i].Visibility = Visibility.Collapsed;
    }

    internal static Geometry? BuildAnnularSector(
        double cx,
        double cy,
        double rIn,
        double rOut,
        double startDeg,
        double endDeg,
        double cornerR)
    {
        double sweep = endDeg - startDeg;
        if (sweep <= 0.6) return null;
        if (sweep >= 359.2) sweep = 359.2;
        double clampedEnd = startDeg + sweep;

        const double DegToRad = Math.PI / 180.0;
        Point Pt(double r, double deg) => new(cx + r * Math.Cos(deg * DegToRad), cy + r * Math.Sin(deg * DegToRad));

        double maxCr = Math.Min(cornerR, Math.Min((rOut - rIn) * 0.42, (sweep * DegToRad * rIn) * 0.42));
        var figure = new PathFigure { IsClosed = true, IsFilled = true };

        if (maxCr < 0.6)
        {
            Point p1 = Pt(rOut, startDeg);
            Point p2 = Pt(rOut, clampedEnd);
            Point p3 = Pt(rIn, clampedEnd);
            Point p4 = Pt(rIn, startDeg);
            bool large = sweep > 180.0;

            figure.StartPoint = p1;
            figure.Segments.Add(new ArcSegment
            {
                Point = p2,
                Size = new Size(rOut, rOut),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = large
            });
            figure.Segments.Add(new LineSegment { Point = p3 });
            figure.Segments.Add(new ArcSegment
            {
                Point = p4,
                Size = new Size(rIn, rIn),
                SweepDirection = SweepDirection.Counterclockwise,
                IsLargeArc = large
            });
        }
        else
        {
            double dOut = (maxCr / rOut) / DegToRad;
            double dIn = (maxCr / rIn) / DegToRad;
            Point p1 = Pt(rOut, startDeg + dOut);
            Point p2 = Pt(rOut, clampedEnd - dOut);
            Point p3 = Pt(rOut - maxCr, clampedEnd);
            Point p4 = Pt(rIn + maxCr, clampedEnd);
            Point p5 = Pt(rIn, clampedEnd - dIn);
            Point p6 = Pt(rIn, startDeg + dIn);
            Point p7 = Pt(rIn + maxCr, startDeg);
            Point p8 = Pt(rOut - maxCr, startDeg);

            bool largeOut = (clampedEnd - dOut) - (startDeg + dOut) > 180.0;
            bool largeIn = (clampedEnd - dIn) - (startDeg + dIn) > 180.0;
            var cornerSize = new Size(maxCr, maxCr);

            figure.StartPoint = p1;
            figure.Segments.Add(new ArcSegment
            {
                Point = p2,
                Size = new Size(rOut, rOut),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = largeOut
            });
            figure.Segments.Add(new ArcSegment
            {
                Point = p3,
                Size = cornerSize,
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = false
            });
            figure.Segments.Add(new LineSegment { Point = p4 });
            figure.Segments.Add(new ArcSegment
            {
                Point = p5,
                Size = cornerSize,
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = false
            });
            figure.Segments.Add(new ArcSegment
            {
                Point = p6,
                Size = new Size(rIn, rIn),
                SweepDirection = SweepDirection.Counterclockwise,
                IsLargeArc = largeIn
            });
            figure.Segments.Add(new ArcSegment
            {
                Point = p7,
                Size = cornerSize,
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = false
            });
            figure.Segments.Add(new LineSegment { Point = p8 });
            figure.Segments.Add(new ArcSegment
            {
                Point = p1,
                Size = cornerSize,
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = false
            });
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}

internal sealed partial class HostCpuRingTile : UserControl
{
    private const string PerfBoltData = "M9.5 1.5L3.8 8.8h3.7L6.5 14.5l5.7-7.3H8.5l1-5.7z";
    private const string EffLeafData = "M17 8C8 10 5.9 16.17 3.82 21.34l1.89.66.95-2.3c.48.17.98.3 1.34.3C19 20 22 3 22 3c-1 2-8 2.25-13 3.25S2 11.5 2 13.5s1.75 3.75 1.75 3.75C7 8 17 8 17 8z";

    private readonly int lpIndex;
    private readonly CpuCoreType coreType;
    private readonly Border card = new()
    {
        CornerRadius = new CornerRadius(4),
        BorderThickness = new Thickness(1)
    };
    private readonly AnnularRingCanvas ring = new();
    private readonly Grid contentRoot = new();
    private readonly Grid metaPanel = new();
    private readonly StackPanel labelRow = new() { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock lpLabel = new();
    private readonly TextBlock pctLabel = new();
    private readonly ToolTip tip = new();
    private readonly StackPanel tipPanel = new() { Spacing = 4, MinWidth = 170 };
    private readonly StackPanel tipRows = new() { Spacing = 3 };
    private readonly TextBlock tipTitle = new() { FontSize = 12, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock tipCoreType = new() { FontSize = 11 };

    private enum TileFocusState { None, Elevated, Dimmed }

    private CpuRingDensity activeDensity;
    private bool pointerOver;
    private TileFocusState focusState;

    public HostCpuRingTile(int lpIndex, CpuCoreType coreType)
    {
        this.lpIndex = lpIndex;
        this.coreType = coreType;

        lpLabel.Text = L10n.Format("Host_Cpu_Thread", lpIndex);
        lpLabel.TextTrimming = TextTrimming.Clip;
        labelRow.Children.Add(lpLabel);

        if (coreType != CpuCoreType.Unknown)
        {
            bool isEff = coreType == CpuCoreType.Efficient;
            string data = isEff ? EffLeafData : PerfBoltData;
            int vb = isEff ? 24 : 16;
            var path = (ShapePath)XamlReader.Load(
                $"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{data}'/>");
            path.Fill = new SolidColorBrush(isEff
                ? Color.FromArgb(255, 0x6C, 0xCB, 0x5F)
                : Color.FromArgb(255, 0xFA, 0xA1, 0x46));
            var badge = new Viewbox
            {
                Width = 10,
                Height = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Grid { Width = vb, Height = vb, Children = { path } }
            };
            labelRow.Children.Add(badge);
        }

        pctLabel.Text = "—";
        pctLabel.FontWeight = FontWeights.SemiBold;
        Typography.SetNumeralAlignment(pctLabel, FontNumeralAlignment.Tabular);

        card.Child = contentRoot;
        Content = card;

        // Build structured ToolTip (zero "·" separators).
        var tipHeader = new Grid { ColumnSpacing = 12 };
        tipHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tipHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        tipTitle.Text = L10n.Format("Host_Cpu_Thread", lpIndex);
        tipHeader.Children.Add(tipTitle);
        if (coreType != CpuCoreType.Unknown)
        {
            tipCoreType.Text = L10n.Get(coreType == CpuCoreType.Performance ? "Host_Cpu_PerfCore" : "Host_Cpu_EffCore");
            Grid.SetColumn(tipCoreType, 1);
            tipHeader.Children.Add(tipCoreType);
        }
        var divider = new Border { Height = 1, Margin = new Thickness(0, 2, 0, 2) };
        divider.Background = HostUiHelpers.TryResourceBrush(ActualTheme, "DividerStrokeColorDefaultBrush", Color.FromArgb(40, 128, 128, 128));
        tipPanel.Children.Add(tipHeader);
        tipPanel.Children.Add(divider);
        tipPanel.Children.Add(tipRows);
        tip.Content = tipPanel;
        ToolTipService.SetToolTip(this, tip);

        PointerEntered += (_, _) => { pointerOver = true; ApplyBorderBrush(); };
        PointerExited += (_, _) => { pointerOver = false; ApplyBorderBrush(); };
        ActualThemeChanged += (_, _) => ApplyBorderBrush();

        ApplyDensity(CpuRingDensity.Cols4, force: true);
    }

    public void ShowUnavailable()
    {
        pctLabel.Text = "—";
        ring.Update(Colors.Transparent, [], null);
        tipRows.Children.Clear();
    }

    public int LogicalProcessorIndex => lpIndex;

    public void ApplyDensity(CpuRingDensity density, bool force = false)
    {
        if (!force && activeDensity == density) return;
        activeDensity = density;

        contentRoot.Children.Clear();
        contentRoot.RowDefinitions.Clear();
        contentRoot.ColumnDefinitions.Clear();
        metaPanel.Children.Clear();
        metaPanel.RowDefinitions.Clear();
        metaPanel.ColumnDefinitions.Clear();

        switch (density)
        {
            case CpuRingDensity.Cols6:
                card.Padding = new Thickness(7, 7, 7, 7);
                card.MinHeight = 76;
                ring.Width = ring.Height = 38;
                lpLabel.FontSize = 10.5;
                lpLabel.FontWeight = FontWeights.Medium;
                pctLabel.FontSize = 13;
                LayoutHeaderAboveRing(gap: 5);
                break;

            case CpuRingDensity.Cols8:
                card.Padding = new Thickness(4, 5, 4, 5);
                card.MinHeight = 70;
                ring.Width = ring.Height = 32;
                lpLabel.FontSize = 10;
                lpLabel.FontWeight = FontWeights.Medium;
                pctLabel.FontSize = 12;
                LayoutStackedCols8();
                break;

            default: // Cols4 (handles <= 16 threads uniformly)
                card.Padding = new Thickness(11, 8, 11, 8);
                card.MinHeight = 88;
                ring.Width = ring.Height = 44;
                lpLabel.FontSize = 11.5;
                lpLabel.FontWeight = FontWeights.Medium;
                pctLabel.FontSize = 15.5;
                LayoutHeaderAboveRing(gap: 6);
                break;
        }
    }

    private void LayoutHeaderAboveRing(double gap)
    {
        contentRoot.HorizontalAlignment = HorizontalAlignment.Stretch;
        contentRoot.VerticalAlignment = VerticalAlignment.Center;
        contentRoot.RowSpacing = gap;
        contentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        metaPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
        metaPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        labelRow.HorizontalAlignment = HorizontalAlignment.Left;
        pctLabel.HorizontalAlignment = HorizontalAlignment.Right;
        pctLabel.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(labelRow, 0);
        Grid.SetColumn(pctLabel, 1);
        metaPanel.Children.Add(labelRow);
        metaPanel.Children.Add(pctLabel);

        Grid.SetRow(metaPanel, 0);
        contentRoot.Children.Add(metaPanel);

        ring.HorizontalAlignment = HorizontalAlignment.Center;
        ring.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(ring, 1);
        contentRoot.Children.Add(ring);
    }

    private void LayoutStackedCols8()
    {
        contentRoot.HorizontalAlignment = HorizontalAlignment.Stretch;
        contentRoot.VerticalAlignment = VerticalAlignment.Center;
        contentRoot.RowSpacing = 3;
        contentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        metaPanel.HorizontalAlignment = HorizontalAlignment.Center;
        metaPanel.RowSpacing = 0;
        metaPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        metaPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        labelRow.HorizontalAlignment = HorizontalAlignment.Center;
        pctLabel.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetRow(labelRow, 0);
        Grid.SetRow(pctLabel, 1);
        metaPanel.Children.Add(labelRow);
        metaPanel.Children.Add(pctLabel);

        Grid.SetRow(metaPanel, 0);
        contentRoot.Children.Add(metaPanel);

        ring.HorizontalAlignment = HorizontalAlignment.Center;
        ring.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(ring, 1);
        contentRoot.Children.Add(ring);
    }

    public void Update(
        IReadOnlyList<CpuRingSlice> fullSlices,
        IReadOnlyList<CpuRingSlice> keptArcs,
        double totalPercent,
        string? focusedConsumerKey,
        Color? focusedConsumerColor)
    {
        Color primaryText = HostUiHelpers.ThemeColor(ActualTheme, "TextFillColorPrimaryBrush",
            ActualTheme == ElementTheme.Light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White);
        Color secondaryText = HostUiHelpers.ThemeColor(ActualTheme, "TextFillColorSecondaryBrush",
            ActualTheme == ElementTheme.Light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        Color tertiaryText = HostUiHelpers.ThemeColor(ActualTheme, "TextFillColorTertiaryBrush",
            ActualTheme == ElementTheme.Light ? Color.FromArgb(0x72, 0, 0, 0) : Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));

        Color trackColor = Color.FromArgb(16, primaryText.R, primaryText.G, primaryText.B);
        ring.Update(trackColor, keptArcs, focusedConsumerKey);

        CpuRingSlice? match = focusedConsumerKey == null
            ? null
            : fullSlices.FirstOrDefault(s => string.Equals(s.ConsumerKey, focusedConsumerKey, StringComparison.Ordinal) && s.Percent > 0);

        bool hasFocus = focusedConsumerKey != null;
        bool hasFocusedContribution = !hasFocus || match != null;

        int displayPct = hasFocus
            ? (match != null ? (int)Math.Round(match.Percent) : 0)
            : (int)Math.Round(Math.Clamp(totalPercent, 0, 100));

        pctLabel.Text = $"{displayPct}%";
        if (hasFocus)
        {
            // Candidate #3 (Elevated Surface): keep percentages calm neutral primary; ring arc carries color.
            pctLabel.Foreground = new SolidColorBrush(hasFocusedContribution ? primaryText : tertiaryText);
            Opacity = hasFocusedContribution ? 1.0 : 0.28;
            focusState = hasFocusedContribution ? TileFocusState.Elevated : TileFocusState.Dimmed;
        }
        else
        {
            pctLabel.Foreground = new SolidColorBrush(primaryText);
            Opacity = 1.0;
            focusState = TileFocusState.None;
        }

        lpLabel.Foreground = new SolidColorBrush(secondaryText);
        tipCoreType.Foreground = new SolidColorBrush(secondaryText);
        ApplyBorderBrush();

        // Update structured tooltip rows cleanly.
        tipRows.Children.Clear();
        bool anyTip = false;
        foreach (var slice in fullSlices)
        {
            int pct = (int)Math.Round(slice.Percent);
            if (pct <= 0 && slice.Percent < 0.5) continue;
            anyTip = true;
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = new SolidColorBrush(slice.Color),
                VerticalAlignment = VerticalAlignment.Center
            };
            var name = new TextBlock
            {
                Text = slice.Label,
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 190,
                Foreground = new SolidColorBrush(primaryText),
                VerticalAlignment = VerticalAlignment.Center
            };
            var val = new TextBlock
            {
                Text = $"{Math.Max(1, pct)}%",
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(primaryText),
                VerticalAlignment = VerticalAlignment.Center
            };
            Typography.SetNumeralAlignment(val, FontNumeralAlignment.Tabular);

            Grid.SetColumn(dot, 0);
            Grid.SetColumn(name, 1);
            Grid.SetColumn(val, 2);
            row.Children.Add(dot);
            row.Children.Add(name);
            row.Children.Add(val);
            tipRows.Children.Add(row);
        }

        if (!anyTip)
        {
            tipRows.Children.Add(new TextBlock
            {
                Text = $"{L10n.Get("Host_Cpu_HostOwner")}  0%",
                FontSize = 12,
                Foreground = new SolidColorBrush(secondaryText)
            });
        }
    }

    private void ApplyBorderBrush()
    {
        bool light = ActualTheme == ElementTheme.Light;
        Color strokeDefault = HostUiHelpers.ThemeColor(ActualTheme, "ControlStrokeColorDefaultBrush",
            light ? Color.FromArgb(0x14, 0, 0, 0) : Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        Color bgDefault = HostUiHelpers.ThemeColor(ActualTheme, "ControlFillColorDefaultBrush",
            light ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
        Color bgHover = HostUiHelpers.ThemeColor(ActualTheme, "ControlFillColorSecondaryBrush",
            light ? Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));

        if (focusState == TileFocusState.Elevated)
        {
            Color bgElevated = light
                ? Colors.White
                : Color.FromArgb(pointerOver ? (byte)0x20 : (byte)0x18, 0xFF, 0xFF, 0xFF);
            Color borderElevated = light
                ? Color.FromArgb(pointerOver ? (byte)0x38 : (byte)0x29, 0x00, 0x00, 0x00)
                : Color.FromArgb(pointerOver ? (byte)0x48 : (byte)0x38, 0xFF, 0xFF, 0xFF);
            card.Background = new SolidColorBrush(bgElevated);
            card.BorderBrush = new SolidColorBrush(borderElevated);
        }
        else if (focusState == TileFocusState.Dimmed)
        {
            card.Background = new SolidColorBrush(pointerOver ? bgHover : Colors.Transparent);
            card.BorderBrush = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(strokeDefault.A * 0.45),
                strokeDefault.R,
                strokeDefault.G,
                strokeDefault.B));
        }
        else
        {
            card.Background = new SolidColorBrush(pointerOver ? bgHover : bgDefault);
            if (pointerOver)
            {
                Color subtle = HostUiHelpers.ThemeColor(ActualTheme, "ControlStrokeColorSecondaryBrush",
                    Color.FromArgb(0x40, 0x88, 0x88, 0x88));
                card.BorderBrush = new SolidColorBrush(subtle);
            }
            else
            {
                card.BorderBrush = new SolidColorBrush(strokeDefault);
            }
        }
    }
}
