using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

public sealed class VmMetricCard : Button
{
    private readonly FontIcon icon = new() { FontSize = 26, FontFamily = new FontFamily("Segoe Fluent Icons"), VerticalAlignment = VerticalAlignment.Center };
    private readonly VmMiddleText title = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock subtitle = new() { FontSize = 11, Opacity = .75, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock secondaryReading = new() { FontSize = 11, Opacity = .85, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid secondaryLine = new() { ColumnSpacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock reading = new() { FontSize = 26, VerticalAlignment = VerticalAlignment.Center };
    private string value = "", secondValue = "", unit = "", secondUnit = "";
    private bool selected;
    public VmMetricCard()
    {
        Height = 72; Padding = new Thickness(16, 10, 16, 10); HorizontalContentAlignment = HorizontalAlignment.Stretch;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"];
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        var name = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        secondaryLine.ColumnDefinitions.Add(new()); secondaryLine.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        secondaryLine.Children.Add(subtitle); Grid.SetColumn(secondaryReading, 1); secondaryLine.Children.Add(secondaryReading);
        name.Children.Add(title); name.Children.Add(secondaryLine);
        Grid.SetColumn(name, 1); grid.Children.Add(name); Grid.SetColumn(reading, 2); grid.Children.Add(reading);
        Content = grid;
        ActualThemeChanged += (_, _) => ApplySelection();
        ApplySelection();
    }
    public string Title { get => title.FullText; set { title.FullText = value; AutomationProperties.SetName(this, value); } }
    public string Glyph
    {
        get => icon.Glyph;
        set
        {
            icon.Glyph = value;
            icon.RenderTransformOrigin = new Point(.5, .5);
            icon.RenderTransform = new RotateTransform { Angle = value == "\uF58E" ? 90 : 0 };
        }
    }
    public string Subtitle { get => subtitle.Text; set { subtitle.Text = value; UpdateSecondaryLine(); } }
    public string SecondaryReading { get => secondaryReading.Text; set { secondaryReading.Text = value; UpdateSecondaryLine(); } }
    private void UpdateSecondaryLine() => secondaryLine.Visibility = subtitle.Text.Length + secondaryReading.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Value { get => value; set { this.value = value; UpdateValue(); } }
    public string SecondValue { get => secondValue; set { secondValue = value; UpdateValue(); } }
    public string Unit { get => unit; set { unit = value; UpdateValue(); } }
    public string SecondUnit { get => secondUnit; set { secondUnit = value; UpdateValue(); } }
    public double ValueOpacity { get => reading.Opacity; set => reading.Opacity = value; }
    public bool Selected { get => selected; set { selected = value; ApplySelection(); } }
    private void ApplySelection()
    {
        Background = selected ? (Brush)Application.Current.Resources["VmHeaderCheckedBrush"] : new SolidColorBrush(Windows.UI.Color.FromArgb(32, 128, 128, 128));
        Foreground = (Brush)Application.Current.Resources[selected ? "VmHeaderForegroundBrush" : "TextFillColorPrimaryBrush"];
    }
    private void UpdateValue()
    {
        reading.Inlines.Clear();
        reading.Inlines.Add(new Run { Text = value });
        if (secondValue.Length > 0) reading.Inlines.Add(new Run { Text = " / " + secondValue });
        if (value.Length > 0 || secondValue.Length > 0)
            reading.Inlines.Add(new Run { Text = " " + (secondValue.Length > 0 ? secondUnit : unit), FontSize = 12 });
        AutomationProperties.SetHelpText(this, $"{value} {(secondValue.Length > 0 ? "/ " + secondValue : "")} {unit}");
    }
}

/// <summary>Measure the actual font to keep as much of both ends of a device name as fits.</summary>
public sealed class VmMiddleText : UserControl
{
    private string fullText = "";
    private readonly TextBlock measure = new();
    private readonly TextBlock text = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    public VmMiddleText() { Content = text; SizeChanged += (_, _) => Fit(); Loaded += (_, _) => Fit(); }
    public string FullText { get => fullText; set { fullText = value; text.Text = value; ToolTipService.SetToolTip(this, value); Fit(); } }
    private void Fit()
    {
        if (ActualWidth <= 0) return;
        measure.FontSize = FontSize; measure.FontFamily = FontFamily; measure.FontWeight = FontWeight;
        double WidthOf(string text) { measure.Text = text; measure.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); return measure.DesiredSize.Width; }
        if (WidthOf(fullText) <= ActualWidth) { text.Text = fullText; return; }
        var parts = System.Globalization.StringInfo.ParseCombiningCharacters(fullText);
        string Shorten(int count)
        {
            int left = (count + 1) / 2, right = count / 2;
            return fullText[..(left == 0 ? 0 : parts[left])] + "…" + (right == 0 ? "" : fullText[parts[parts.Length - right]..]);
        }
        int lo = 0, hi = Math.Max(0, parts.Length - 1);
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (WidthOf(Shorten(mid)) <= ActualWidth) lo = mid; else hi = mid - 1; }
        text.Text = Shorten(lo);
    }
}
