
using ExHyperV.App.UI.Shared.Controls;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed partial class HostStatusView
{
    private RailEntry CreateRailEntry(
        string key,
        RailCategory category,
        string detailTitle,
        string detailSubtitle,
        string detailGlyph,
        double detailGlyphRotation,
        Color categoryColor,
        VmMiniSparkline sparkline,
        TextBlock titleText,
        TextBlock? subtitleText,
        TextBlock statText,
        string? tooltip)
    {
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        info.Children.Add(titleText);
        if (subtitleText != null) info.Children.Add(subtitleText);
        info.Children.Add(statText);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sparkline.Margin = new Thickness(0, 0, 9, 0);
        Grid.SetColumn(sparkline, 0);
        Grid.SetColumn(info, 1);
        row.Children.Add(sparkline);
        row.Children.Add(info);

        var button = new Button
        {
            Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"],
            Content = row,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1)
        };
        if (!string.IsNullOrWhiteSpace(tooltip))
            ToolTipService.SetToolTip(button, tooltip);

        button.Click += (_, _) => SelectRailKey(key);
        railPanel.Children.Add(button);

        var entry = new RailEntry
        {
            Key = key,
            Category = category,
            DetailTitle = detailTitle,
            DetailSubtitle = detailSubtitle,
            DetailGlyph = detailGlyph,
            DetailGlyphRotation = detailGlyphRotation,
            CategoryColor = categoryColor,
            Button = button,
            Sparkline = sparkline,
            TitleText = titleText,
            SubtitleText = subtitleText,
            StatText = statText
        };
        railEntries.Add(entry);
        return entry;
    }
}
