using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;
public sealed partial class VmResourceCard : UserControl
{
    public VmResourceCard()
    {
        InitializeComponent();
        UpdateValueVisibility();
        ActualThemeChanged += (_, _) => UpdateGearForeground();
    }
    public event EventHandler? SettingsRequested;
    private bool headerHovered;
    public string Title { get => Heading.Text; set { Heading.Text = value; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(HeaderButton, value); string settingsTip = L10n.Format("Card_SettingsTooltip", value); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SettingsButton, settingsTip); ToolTipService.SetToolTip(SettingsButton, settingsTip); } }
    public string Description { get => DescriptionText.Text; set => DescriptionText.Text = value; }
    public string Glyph
    {
        get => Icon.Glyph;
        set
        {
            Icon.Glyph = value;
            Icon.FontSize = value is "\uF58E" or "\uEEA0" ? 24 : 22;
            Icon.RenderTransformOrigin = new Windows.Foundation.Point(.5, .5);
            Icon.RenderTransform = new Microsoft.UI.Xaml.Media.RotateTransform { Angle = value == "\uF58E" ? 90 : 0 };
            UnitText.Opacity = value == "\uEDA2" ? .6 : .8;
        }
    }
    public string Value { get => ValueText.Text; set { ValueText.Text = value; UpdateValueVisibility(); } }
    public string Unit { get => UnitText.Text; set => UnitText.Text = value; }
    public string SecondValue { get => SecondValueText.Text; set { SecondValueText.Text = value; UpdateValueVisibility(); } }
    public string SecondUnit { get => SecondUnitText.Text; set => SecondUnitText.Text = value; }
    public double ValueOpacity { get => ValuePanel.Opacity; set => ValuePanel.Opacity = value; }
    public UIElement Details { get => (UIElement)DetailHost.Content; set => DetailHost.Content = value; }
    private void UpdateValueVisibility()
    {
        bool hasValue = !string.IsNullOrEmpty(ValueText.Text);
        bool hasSecondValue = !string.IsNullOrEmpty(SecondValueText.Text);
        UnitText.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
        SecondValuePanel.Visibility = hasSecondValue ? Visibility.Visible : Visibility.Collapsed;
        ValueSeparator.Visibility = hasValue && hasSecondValue ? Visibility.Visible : Visibility.Collapsed;
    }
    private void OnToggle(object sender, RoutedEventArgs e)
    {
        bool expanded = HeaderButton.IsChecked == true;
        DescriptionText.Opacity = expanded ? 1 : .6;
        UnitText.Opacity = expanded ? 1 : Glyph == "\uEDA2" ? .6 : .8;
        SecondUnitText.Opacity = expanded ? 1 : .8;
        DetailBorder.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(HeaderButton, L10n.Get(expanded ? "Card_Expanded" : "Card_Collapsed"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(HeaderButton, L10n.Get(expanded ? "Card_CollapseHelp" : "Card_ExpandHelp"));
        var animation = new DoubleAnimation { To = expanded ? 180 : 0, Duration = new Duration(TimeSpan.FromMilliseconds(167)), EnableDependentAnimation = true };
        Storyboard.SetTarget(animation, ChevronRotation); Storyboard.SetTargetProperty(animation, "Angle");
        var storyboard = new Storyboard(); storyboard.Children.Add(animation); storyboard.Begin();
        UpdateGearForeground();
    }
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }
    private void UpdateGearForeground() => SettingsButton.Foreground = (Brush)Application.Current.Resources[
        HeaderButton.IsChecked == true ? "VmHeaderForegroundBrush" : "TextFillColorPrimaryBrush"];
    private void OnHeaderPointerEntered(object sender, PointerRoutedEventArgs e) => headerHovered = true;
    private void OnHeaderPointerExited(object sender, PointerRoutedEventArgs e)
    {
        headerHovered = false;
        VisualStateManager.GoToState(HeaderButton, HeaderButton.IsChecked == true ? "Checked" : "Normal", false);
    }
    private void OnGearPointerEntered(object sender, PointerRoutedEventArgs e) =>
        VisualStateManager.GoToState(HeaderButton, HeaderButton.IsChecked == true ? "CheckedPointerOver" : "PointerOver", false);
    private void OnGearPointerExited(object sender, PointerRoutedEventArgs e) =>
        VisualStateManager.GoToState(HeaderButton, HeaderButton.IsChecked == true
            ? (headerHovered ? "CheckedPointerOver" : "Checked") : (headerHovered ? "PointerOver" : "Normal"), false);
}
