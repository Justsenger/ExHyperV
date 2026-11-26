
using System.Globalization;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;

namespace ExHyperV.App.UI.Shared.Appearance;

internal static class ThemeHelper
{
    private static readonly Windows.UI.ViewManagement.AccessibilitySettings Accessibility = new();

    internal static Brush ThemeBrush(FrameworkElement scope, string key, Color? fallback = null)
    {
        var resources = Application.Current.Resources;
        // AppAppearance updates this brush in place when the user changes the accent.
        if (key == "VmAccentBrush" && resources.TryGetValue(key, out var accent) && accent is Brush live)
            return live;

        string theme = ThemeName(scope.ActualTheme);
        var brush = FindThemedBrush(resources, theme, key);
        if (brush != null) return CloneIfSolid(brush);
        return resources.TryGetValue(key, out var value) && value is Brush direct
            ? CloneIfSolid(direct)
            : new SolidColorBrush(fallback ?? FallbackColor(key, theme == "Light"));
    }

    private static string ThemeName(ElementTheme theme) =>
        Accessibility.HighContrast ? "HighContrast" : theme == ElementTheme.Light ? "Light" : "Default";

    private static Brush? FindThemedBrush(ResourceDictionary resources, string theme, string key)
    {
        for (int index = resources.MergedDictionaries.Count - 1; index >= 0; index--)
        {
            var brush = FindThemedBrush(resources.MergedDictionaries[index], theme, key);
            if (brush != null) return brush;
        }

        return ThemeValue(resources, theme, key) switch
        {
            Brush brush => brush,
            Color color => new SolidColorBrush(color),
            _ => null
        };
    }

    private static object? ThemeValue(ResourceDictionary resources, string theme, string key) =>
        resources.ThemeDictionaries.TryGetValue(theme, out var entry) && entry is ResourceDictionary dictionary
            && dictionary.TryGetValue(key, out var value) ? value : null;

    // The host, dashboard and feedback controls read the first explicitly themed brush.
    private static T? FirstMergedBrush<T>(ResourceDictionary resources, string theme, string key) where T : Brush
    {
        foreach (var merged in resources.MergedDictionaries)
            if (ThemeValue(merged, theme, key) is T brush) return brush;
        return null;
    }

    // Feedback controls keep the dictionary brush so changes to a live brush propagate.
    internal static Brush ThemeBrush(ElementTheme theme, string key)
    {
        var resources = Application.Current.Resources;
        string name = ThemeName(theme);
        return FirstMergedBrush<Brush>(resources, name, key)
            ?? ThemeValue(resources, name, key) as Brush
            ?? (resources.TryGetValue(key, out var value) ? value as Brush : null)
            ?? new SolidColorBrush(FallbackColor(key, name == "Light"));
    }

    private static Brush CloneIfSolid(Brush brush) =>
        brush is SolidColorBrush solid ? new SolidColorBrush(solid.Color) { Opacity = solid.Opacity } : brush;

    private static Color FallbackColor(string key, bool light) => key switch
    {
        "TextFillColorPrimaryBrush" => light ? Color.FromArgb(228, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255),
        "TextFillColorSecondaryBrush" => light ? Color.FromArgb(158, 0, 0, 0) : Color.FromArgb(197, 255, 255, 255),
        "CardBackgroundFillColorDefaultBrush" => light ? Color.FromArgb(179, 255, 255, 255) : Color.FromArgb(13, 255, 255, 255),
        "CardStrokeColorDefaultBrush" => light ? Color.FromArgb(15, 0, 0, 0) : Color.FromArgb(25, 255, 255, 255),
        "DividerStrokeColorDefaultBrush" => light ? Color.FromArgb(14, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255),
        "SubtleFillColorSecondaryBrush" => light ? Color.FromArgb(9, 0, 0, 0) : Color.FromArgb(15, 255, 255, 255),
        "SubtleFillColorTertiaryBrush" => light ? Color.FromArgb(6, 0, 0, 0) : Color.FromArgb(10, 255, 255, 255),
        "VmKvWellBrush" => light ? Color.FromArgb(6, 0, 0, 0) : Color.FromArgb(9, 255, 255, 255),
        "VmKvWellBorderBrush" => light ? Color.FromArgb(16, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255),
        "VmBadgeBrush" => light ? Color.FromArgb(5, 0, 0, 0) : Color.FromArgb(8, 255, 255, 255),
        "SystemFillColorCautionBrush" => light ? Color.FromArgb(255, 157, 93, 0) : Color.FromArgb(255, 252, 225, 0),
        _ => Colors.Transparent
    };

    internal static Color MixColor(Color a, Color b, double weightA)
    {
        double w = Math.Clamp(weightA, 0, 1);
        double inv = 1 - w;
        return Color.FromArgb(
            (byte)Math.Round(a.A * w + b.A * inv),
            (byte)Math.Round(a.R * w + b.R * inv),
            (byte)Math.Round(a.G * w + b.G * inv),
            (byte)Math.Round(a.B * w + b.B * inv));
    }

    internal static Color ReadableForeground(Color background)
    {
        double luminance = (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255.0;
        return luminance > 0.52 ? Color.FromArgb(255, 16, 16, 16) : Colors.White;
    }

    internal static Color ThemeColor(ElementTheme theme, string key, Color? fallback = null)
    {
        var resources = Application.Current.Resources;
        if (FirstMergedBrush<SolidColorBrush>(resources, ThemeName(theme), key) is { } themed) return themed.Color;
        return resources.TryGetValue(key, out var value) && value is SolidColorBrush brush
            ? brush.Color : fallback ?? Colors.Gray;
    }

    internal static Brush TryResourceBrush(ElementTheme theme, string key, Color fallback)
        => Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush : new SolidColorBrush(ThemeColor(theme, key, fallback));

    internal static Brush ResourceBrush(string key) => (Brush)Application.Current.Resources[key];

    internal static Color Mix(Color a, Color b, double amount) => Color.FromArgb(255,
        (byte)Math.Round(a.R + (b.R - a.R) * amount), (byte)Math.Round(a.G + (b.G - a.G) * amount),
        (byte)Math.Round(a.B + (b.B - a.B) * amount));

    private static double Luminance(Color color)
    {
        static double Channel(byte value) => value <= 10 ? value / 3294.6 : Math.Pow((value / 255d + .055) / 1.055, 2.4);
        return .2126 * Channel(color.R) + .7152 * Channel(color.G) + .0722 * Channel(color.B);
    }

    internal static double Contrast(Color a, Color b)
    {
        double x = Luminance(a), y = Luminance(b);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }

    internal static Color ContrastingForeground(Color background) =>
        Contrast(background, Colors.Black) >= Contrast(background, Colors.White)
            ? Colors.Black : Colors.White;

    internal static Color StateColor(Color original, Color target, double amount, Color foreground)
    {
        var candidate = Mix(original, target, amount);
        return Contrast(candidate, foreground) >= 4.5 ? candidate : original;
    }

    internal static Brush SubtleHighlightBrush(ElementTheme theme, byte darkAlpha, byte lightAlpha)
        => theme == ElementTheme.Light
            ? new SolidColorBrush(Color.FromArgb(lightAlpha, 0, 0, 0))
            : new SolidColorBrush(Color.FromArgb(darkAlpha, 255, 255, 255));

    internal static Brush TryBrush(string key, Color fallback)
        => Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush
            ? brush
            : new SolidColorBrush(fallback);

    internal static Geometry ParseGeometry(string pathData)
    {
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        if (string.IsNullOrWhiteSpace(pathData)) return geometry;

        int i = 0;
        void SkipSeparators()
        {
            while (i < pathData.Length && (char.IsWhiteSpace(pathData[i]) || pathData[i] == ',')) i++;
        }

        double ReadNumber()
        {
            SkipSeparators();
            int start = i;
            if (i < pathData.Length && (pathData[i] == '+' || pathData[i] == '-')) i++;
            while (i < pathData.Length && char.IsAsciiDigit(pathData[i])) i++;
            if (i < pathData.Length && pathData[i] == '.')
            {
                i++;
                while (i < pathData.Length && char.IsAsciiDigit(pathData[i])) i++;
            }
            if (i < pathData.Length && (pathData[i] == 'e' || pathData[i] == 'E'))
            {
                i++;
                if (i < pathData.Length && (pathData[i] == '+' || pathData[i] == '-')) i++;
                while (i < pathData.Length && char.IsAsciiDigit(pathData[i])) i++;
            }
            if (i == start) throw new FormatException($"Invalid SVG path number at index {start}.");
            return double.Parse(pathData.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        bool ReadFlag()
        {
            SkipSeparators();
            if (i >= pathData.Length || (pathData[i] != '0' && pathData[i] != '1'))
                throw new FormatException($"Invalid SVG arc flag at index {i}.");
            return pathData[i++] == '1';
        }

        SkipSeparators();
        if (i + 1 < pathData.Length && pathData[i] is 'F' or 'f' && pathData[i + 1] is '0' or '1')
        {
            geometry.FillRule = pathData[i + 1] == '0' ? FillRule.EvenOdd : FillRule.Nonzero;
            i += 2;
        }

        Point cur = default, lastCubicCtrl = default, lastQuadCtrl = default;
        char cmd = '\0', prevCmd = '\0';
        PathFigure? figure = null;

        PathFigure EnsureFigure()
        {
            if (figure != null) return figure;
            figure = new PathFigure { StartPoint = cur, IsFilled = true };
            geometry.Figures.Add(figure);
            return figure;
        }

        while (true)
        {
            SkipSeparators();
            if (i >= pathData.Length) break;
            if (char.IsLetter(pathData[i])) cmd = pathData[i++];
            else if (cmd == '\0') break;

            bool rel = char.IsLower(cmd);
            Point Rel(double x, double y) => rel ? new(cur.X + x, cur.Y + y) : new(x, y);

            switch (cmd)
            {
                case 'M':
                case 'm':
                    cur = Rel(ReadNumber(), ReadNumber());
                    figure = new PathFigure { StartPoint = cur, IsFilled = true };
                    geometry.Figures.Add(figure);
                    lastCubicCtrl = lastQuadCtrl = cur;
                    prevCmd = cmd;
                    cmd = rel ? 'l' : 'L';
                    break;
                case 'L':
                case 'l':
                    cur = Rel(ReadNumber(), ReadNumber());
                    EnsureFigure().Segments.Add(new LineSegment { Point = cur });
                    lastCubicCtrl = lastQuadCtrl = cur;
                    prevCmd = cmd;
                    break;
                case 'H':
                case 'h':
                    double hx = ReadNumber();
                    cur = new Point(rel ? cur.X + hx : hx, cur.Y);
                    EnsureFigure().Segments.Add(new LineSegment { Point = cur });
                    lastCubicCtrl = lastQuadCtrl = cur;
                    prevCmd = cmd;
                    break;
                case 'V':
                case 'v':
                    double vy = ReadNumber();
                    cur = new Point(cur.X, rel ? cur.Y + vy : vy);
                    EnsureFigure().Segments.Add(new LineSegment { Point = cur });
                    lastCubicCtrl = lastQuadCtrl = cur;
                    prevCmd = cmd;
                    break;
                case 'C':
                case 'c':
                {
                    var p1 = Rel(ReadNumber(), ReadNumber());
                    var p2 = Rel(ReadNumber(), ReadNumber());
                    var p3 = Rel(ReadNumber(), ReadNumber());
                    EnsureFigure().Segments.Add(new BezierSegment { Point1 = p1, Point2 = p2, Point3 = p3 });
                    lastCubicCtrl = p2;
                    cur = lastQuadCtrl = p3;
                    prevCmd = cmd;
                    break;
                }
                case 'S':
                case 's':
                {
                    var p1 = prevCmd is 'C' or 'c' or 'S' or 's'
                        ? new Point(2 * cur.X - lastCubicCtrl.X, 2 * cur.Y - lastCubicCtrl.Y)
                        : cur;
                    var p2 = Rel(ReadNumber(), ReadNumber());
                    var p3 = Rel(ReadNumber(), ReadNumber());
                    EnsureFigure().Segments.Add(new BezierSegment { Point1 = p1, Point2 = p2, Point3 = p3 });
                    lastCubicCtrl = p2;
                    cur = lastQuadCtrl = p3;
                    prevCmd = cmd;
                    break;
                }
                case 'Q':
                case 'q':
                {
                    var p1 = Rel(ReadNumber(), ReadNumber());
                    var p2 = Rel(ReadNumber(), ReadNumber());
                    EnsureFigure().Segments.Add(new QuadraticBezierSegment { Point1 = p1, Point2 = p2 });
                    lastQuadCtrl = p1;
                    cur = lastCubicCtrl = p2;
                    prevCmd = cmd;
                    break;
                }
                case 'T':
                case 't':
                {
                    var p1 = prevCmd is 'Q' or 'q' or 'T' or 't'
                        ? new Point(2 * cur.X - lastQuadCtrl.X, 2 * cur.Y - lastQuadCtrl.Y)
                        : cur;
                    var p2 = Rel(ReadNumber(), ReadNumber());
                    EnsureFigure().Segments.Add(new QuadraticBezierSegment { Point1 = p1, Point2 = p2 });
                    lastQuadCtrl = p1;
                    cur = lastCubicCtrl = p2;
                    prevCmd = cmd;
                    break;
                }
                case 'A':
                case 'a':
                {
                    double rx = Math.Abs(ReadNumber()), ry = Math.Abs(ReadNumber()), angle = ReadNumber();
                    bool largeArc = ReadFlag(), sweep = ReadFlag();
                    var p = Rel(ReadNumber(), ReadNumber());
                    EnsureFigure().Segments.Add(new ArcSegment
                    {
                        Size = new Size(rx, ry),
                        RotationAngle = angle,
                        IsLargeArc = largeArc,
                        SweepDirection = sweep ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                        Point = p
                    });
                    cur = lastCubicCtrl = lastQuadCtrl = p;
                    prevCmd = cmd;
                    break;
                }
                case 'Z':
                case 'z':
                    if (figure != null)
                    {
                        figure.IsClosed = true;
                        cur = figure.StartPoint;
                        figure = null;
                    }
                    lastCubicCtrl = lastQuadCtrl = cur;
                    prevCmd = cmd;
                    break;
                default:
                    throw new FormatException($"Unsupported SVG path command '{cmd}'.");
            }
        }
        return geometry;
    }
}
