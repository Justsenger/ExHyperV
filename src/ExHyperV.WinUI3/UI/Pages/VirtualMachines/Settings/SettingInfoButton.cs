using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>说明通过悬停提示或点击浮层显示；编辑器禁用时仍可查看原因。</summary>
internal sealed class SettingInfoButton : Button
{
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
    private readonly Flyout explanation;
    private readonly ToolTip tooltip = new();

    public SettingInfoButton(string name, string? text)
    {
        Width = Height = 32;
        MinWidth = MinHeight = 32;
        Padding = new Thickness(0);
        CornerRadius = new CornerRadius(16);
        Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"];
        Content = new FontIcon { Glyph = "\uE946", FontSize = 15 };
        AutomationProperties.SetName(this, name + "说明");
        explanation = new Flyout { Content = detail, Placement = FlyoutPlacementMode.Bottom };
        Flyout = explanation;
        ToolTipService.SetToolTip(this, tooltip);
        explanation.Opening += (_, _) => tooltip.IsOpen = false;
        SetDescription(text);
    }

    public void SetDescription(string? text)
    {
        detail.Text = text ?? string.Empty;
        tooltip.Content = new TextBlock { Text = detail.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
        AutomationProperties.SetHelpText(this, detail.Text);
        Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }
}
