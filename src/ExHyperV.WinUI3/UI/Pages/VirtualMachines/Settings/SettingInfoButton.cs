using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>说明通过悬停提示或点击浮层显示；编辑器禁用时仍可清晰区分用途说明与不可用原因。</summary>
internal sealed class SettingInfoButton : Button
{
    private readonly TextBlock detail = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
    private readonly Border reasonDivider = new() { Height = 1, Margin = new Thickness(0, 8, 0, 8), Visibility = Visibility.Collapsed };
    private readonly TextBlock reasonText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 340, FontSize = 12.5, Visibility = Visibility.Collapsed };
    private readonly TextBlock tooltipText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
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
        AutomationProperties.SetName(this, L10n.Format("SettingsView_InfoAriaSuffix", name));

        if (Application.Current.Resources.TryGetValue("DividerStrokeColorDefaultBrush", out var div) && div is Brush divBrush)
            reasonDivider.Background = divBrush;
        if (Application.Current.Resources.TryGetValue("SystemFillColorCautionBrush", out var caution) && caution is Brush cautionBrush)
            reasonText.Foreground = cautionBrush;
        else if (Application.Current.Resources.TryGetValue("TextFillColorSecondaryBrush", out var sec) && sec is Brush secBrush)
            reasonText.Foreground = secBrush;

        var contentPanel = new StackPanel();
        contentPanel.Children.Add(detail);
        contentPanel.Children.Add(reasonDivider);
        contentPanel.Children.Add(reasonText);

        explanation = new Flyout { Content = contentPanel, Placement = FlyoutPlacementMode.Bottom };
        Flyout = explanation;
        tooltip.Content = tooltipText;
        ToolTipService.SetToolTip(this, tooltip);
        explanation.Opening += (_, _) => tooltip.IsOpen = false;
        SetDescription(text);
    }

    public void SetDescription(string? text, string? disabledReason = null)
    {
        bool hasText = !string.IsNullOrWhiteSpace(text);
        bool hasReason = !string.IsNullOrWhiteSpace(disabledReason);
        detail.Text = text ?? string.Empty;
        detail.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

        string? formattedReason = hasReason
            ? (hasText && string.Equals(text, disabledReason, StringComparison.Ordinal)
                ? null
                : L10n.Format("Cond_DisabledLine", disabledReason))
            : null;
        bool showReason = !string.IsNullOrWhiteSpace(formattedReason);
        reasonDivider.Visibility = hasText && showReason ? Visibility.Visible : Visibility.Collapsed;
        reasonText.Text = formattedReason ?? string.Empty;
        reasonText.Visibility = showReason ? Visibility.Visible : Visibility.Collapsed;

        string fullText = string.Join("\n\n", new[] { text, formattedReason }.Where(s => !string.IsNullOrWhiteSpace(s)));
        tooltipText.Text = fullText;
        AutomationProperties.SetHelpText(this, fullText);
        Visibility = string.IsNullOrWhiteSpace(fullText) ? Visibility.Collapsed : Visibility.Visible;
    }
}
