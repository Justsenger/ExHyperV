
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace ExHyperV.App.UI.Shared.Controls;

internal static class SettingsActionButton
{
    public static Button Create(string name, string glyph, bool primary = false, double size = 44, double iconSize = 20)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources[primary ? "AccentButtonStyle" : "RoundIconButtonStyle"],
            Width = size, Height = size, MinWidth = size, MinHeight = size,
            CornerRadius = new CornerRadius(size / 2), Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = glyph, FontSize = iconSize, IsTextScaleFactorEnabled = false }
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    public static Button CreateRect(string label, bool primary = false)
    {
        var button = new Button
        {
            Content = label,
            Height = 32,
            Padding = new Thickness(16, 0, 16, 0),
            CornerRadius = new CornerRadius(4),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal
        };
        if (primary) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        return button;
    }
}
