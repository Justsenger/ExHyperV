using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

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
}
