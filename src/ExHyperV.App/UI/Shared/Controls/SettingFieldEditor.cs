using System.Globalization;
using System.Reflection;
using ExHyperV.App.Localization;
using ExHyperV.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExHyperV.App.UI.Shared.Controls;

internal enum SettingEditorKind { Text, Toggle, Choice, Energy, SingleRowSlider, HeaderSlider }
internal sealed record SettingOption(object Value, string Label, bool Enabled = true);

internal sealed record CustomSliderAdapter(
    int SliderMin,
    int SliderMax,
    Func<object?, int> ToSliderPos,
    Func<int, object?> FromSliderPos,
    Func<int, string> FormatTooltip,
    string UnitText = "",
    string Placeholder = "",
    int MaxTypedValue = 0,
    bool AllowAutoZero = true,
    Func<int, int>? SnapValue = null);

/// <summary>设置值与 WinUI 控件之间的显示适配，不包含宿主业务规则。</summary>
internal sealed partial class SettingFieldEditor : UserControl
{
    private readonly Grid layout = new() { ColumnSpacing = 8 };
    private readonly decimal scale;
    private readonly TextBox? text;
    private readonly SettingsEnergySlider? energySlider;
    private readonly CustomSliderAdapter? customSlider;
    private readonly TextBlock? customUnitText;
    private readonly ToggleSwitch? toggle;
    private readonly SettingsSegmentedPill? pillChoice;
    private readonly Button? apply;
    private readonly bool deferred;
    private object? loadedValue;
    private object? currentCustomValue;
    private Button? decrease;
    private Button? increase;
    private Border? stepperBox;
    private decimal minimum;
    private decimal? maximum;
    private int effectiveSliderMin;
    private int effectiveSliderMax;
    private string loadedText = "";
    private bool loading;
    public PropertyInfo Property { get; }
    public bool IsFieldEnabled { get; private set; }
    public FrameworkElement? HeaderValueControl { get; }
    public event EventHandler? SubmitRequested;
    public event Action<string?>? AvailabilityChanged;
    public event Action? ValueEdited;

    public bool HasPendingEdit => customSlider != null ? !Equals(NormalizeCompare(currentCustomValue), NormalizeCompare(loadedValue))
        : text != null ? text.Text != loadedText
        : toggle != null ? toggle.IsOn != (loadedValue is true)
        : pillChoice != null && !Equals(pillChoice.SelectedValue, loadedValue);

    public string TextPlaceholder
    {
        get => text?.PlaceholderText ?? "";
        set { if (text != null) text.PlaceholderText = value; }
    }

    private object? NormalizeCompare(object? v)
    {
        if (customSlider is { AllowAutoZero: true } && v is uint u && u == 0) return null;
        return v;
    }

    private static object ConvertSliderTypedValue(long value, Type propertyType)
    {
        var underlying = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        if (underlying == typeof(int)) return checked((int)value);
        if (underlying == typeof(uint)) return checked((uint)Math.Max(0, value));
        if (underlying == typeof(long)) return value;
        if (underlying == typeof(ulong)) return checked((ulong)Math.Max(0, value));
        if (underlying == typeof(double)) return (double)value;
        return Convert.ChangeType(value, underlying, CultureInfo.InvariantCulture);
    }

    private static string FormatCustomSliderValue(object? value, bool allowAutoZero)
    {
        if (value == null) return "";
        if (allowAutoZero && value is uint u && u == 0) return "";
        if (value is double d) return Math.Round(d).ToString("0", CultureInfo.InvariantCulture);
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    public SettingFieldEditor(
        PropertyInfo property,
        SettingEditorKind kind,
        SettingOption[]? options = null,
        decimal scale = 1,
        bool deferred = false,
        EnergySliderKind? energyKind = null,
        CustomSliderAdapter? customSlider = null)
    {
        Property = property;
        this.scale = scale;
        this.deferred = deferred;
        this.customSlider = customSlider;
        effectiveSliderMin = customSlider?.SliderMin ?? 0;
        effectiveSliderMax = customSlider?.SliderMax ?? 0;
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Content = layout;
        IsEnabled = false;

        if (kind == SettingEditorKind.Energy && energyKind is { } ek)
        {
            text = new TextBox
            {
                FontSize = 20,
                Padding = new Thickness(4, 0, 4, 0),
                MinWidth = 28,
                MinHeight = 30,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            MakeBorderlessTransparent(text);
            var valueStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center
            };
            valueStack.Children.Add(text);
            if (scale == 1000)
            {
                valueStack.Children.Add(new TextBlock
                {
                    Text = "%",
                    FontSize = 13,
                    Opacity = 0.72,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            HeaderValueControl = valueStack;

            energySlider = new SettingsEnergySlider(ek) { HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumnSpan(energySlider, 2);
            layout.Children.Add(energySlider);

            energySlider.ValueChanged += val =>
            {
                if (loading) return;
                loading = true;
                try { text.Text = val.ToString(CultureInfo.InvariantCulture); }
                finally { loading = false; }
                ValueEdited?.Invoke();
            };
            text.TextChanged += (_, _) =>
            {
                if (loading) return;
                if (decimal.TryParse(text.Text.Trim(), NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out decimal parsed) && parsed is >= 0 and <= int.MaxValue)
                {
                    loading = true;
                    try { energySlider.Value = (int)Math.Round(parsed); }
                    finally { loading = false; }
                }
                ValueEdited?.Invoke();
            };
        }
        else if (kind == SettingEditorKind.SingleRowSlider && customSlider != null)
        {
            energySlider = new SettingsEnergySlider(EnergySliderKind.Custom, compact: false)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CustomHintFormatter = customSlider.FormatTooltip
            };
            energySlider.SetRange(effectiveSliderMin, effectiveSliderMax);
            Grid.SetColumnSpan(energySlider, 2);
            layout.Children.Add(energySlider);

            energySlider.ValueChanged += pos =>
            {
                if (loading) return;
                currentCustomValue = customSlider.FromSliderPos(pos);
                ValueEdited?.Invoke();
            };
        }
        else if (kind == SettingEditorKind.HeaderSlider && customSlider != null)
        {
            text = new TextBox
            {
                FontSize = 20,
                Padding = new Thickness(4, 0, 4, 0),
                MinWidth = 42,
                MinHeight = 30,
                TextAlignment = TextAlignment.Right,
                PlaceholderText = customSlider.Placeholder,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            MakeBorderlessTransparent(text);
            customUnitText = new TextBlock
            {
                Text = customSlider.UnitText,
                FontSize = 13,
                Opacity = 0.72,
                VerticalAlignment = VerticalAlignment.Center
            };
            var valueStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            valueStack.Children.Add(text);
            valueStack.Children.Add(customUnitText);
            HeaderValueControl = valueStack;

            energySlider = new SettingsEnergySlider(EnergySliderKind.Custom)
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                CustomHintFormatter = customSlider.FormatTooltip,
                SnapValue = customSlider.SnapValue
            };
            energySlider.SetRange(effectiveSliderMin, effectiveSliderMax);
            Grid.SetColumnSpan(energySlider, 2);
            layout.Children.Add(energySlider);

            energySlider.ValueChanged += pos =>
            {
                if (loading) return;
                currentCustomValue = customSlider.FromSliderPos(pos);
                loading = true;
                try
                {
                    text.Text = FormatCustomSliderValue(currentCustomValue, customSlider.AllowAutoZero);
                    if (customUnitText != null)
                        customUnitText.Visibility = string.IsNullOrEmpty(text.Text) ? Visibility.Collapsed : Visibility.Visible;
                }
                finally { loading = false; }
                ValueEdited?.Invoke();
            };
            text.TextChanged += (_, _) =>
            {
                if (loading) return;
                string raw = text.Text.Trim();
                if (string.IsNullOrEmpty(raw) && customSlider.AllowAutoZero)
                {
                    currentCustomValue = ConvertSliderTypedValue(0, Property.PropertyType);
                    loading = true;
                    try { energySlider.Value = effectiveSliderMin; }
                    finally { loading = false; }
                    if (customUnitText != null) customUnitText.Visibility = Visibility.Collapsed;
                    ValueEdited?.Invoke();
                    return;
                }
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
                {
                    if (customSlider.MaxTypedValue > 0 && parsed > customSlider.MaxTypedValue)
                        parsed = customSlider.MaxTypedValue;
                    if (!customSlider.AllowAutoZero && parsed < 0)
                        parsed = 0;
                    currentCustomValue = ConvertSliderTypedValue(parsed, Property.PropertyType);
                    loading = true;
                    try
                    {
                        int pos = customSlider.ToSliderPos(currentCustomValue);
                        energySlider.Value = pos;
                    }
                    finally { loading = false; }
                    if (customUnitText != null) customUnitText.Visibility = Visibility.Visible;
                    ValueEdited?.Invoke();
                }
            };
        }
        else if (kind == SettingEditorKind.Toggle)
        {
            toggle = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0 };
            toggle.Toggled += (_, _) => EditedSelection();
            layout.Children.Add(toggle);
        }
        else if (kind == SettingEditorKind.Choice)
        {
            pillChoice = new SettingsSegmentedPill { HorizontalAlignment = HorizontalAlignment.Stretch };
            pillChoice.SetOptions(options ?? []);
            pillChoice.SelectionChanged += _ => EditedSelection();
            Grid.SetColumnSpan(pillChoice, 2);
            layout.Children.Add(pillChoice);
        }
        else
        {
            text = new TextBox { Width = property.PropertyType == typeof(string) ? 310 : 160 };
            apply = new Button { Content = L10n.Get("Common_Apply"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], IsEnabled = false };
            text.TextChanged += (_, _) =>
            {
                if (loading) return;
                apply.IsEnabled = HasPendingEdit;
                UpdateStepper();
                ValueEdited?.Invoke();
            };
            apply.Click += (_, _) => SubmitRequested?.Invoke(this, EventArgs.Empty);
            layout.Children.Add(text);
            Grid.SetColumn(apply, 1);
            layout.Children.Add(apply);
        }
    }

    private void EditedSelection()
    {
        if (loading) return;
        ValueEdited?.Invoke();
        if (!deferred) SubmitRequested?.Invoke(this, EventArgs.Empty);
    }

    public object? CaptureDraft() => customSlider != null ? currentCustomValue
        : text != null ? text.Text
        : toggle != null ? toggle.IsOn
        : pillChoice?.SelectedValue;

    public void RestoreDraft(object? value)
    {
        loading = true;
        try
        {
            if (customSlider != null)
            {
                currentCustomValue = value;
                if (energySlider != null)
                {
                    int pos = customSlider.ToSliderPos(value);
                    energySlider.Value = pos;
                }
                if (text != null)
                {
                    text.Text = FormatCustomSliderValue(value, customSlider.AllowAutoZero);
                    if (customUnitText != null)
                        customUnitText.Visibility = string.IsNullOrEmpty(text.Text) ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            else if (text != null)
            {
                text.Text = value is string s ? s
                    : value == null ? ""
                    : scale == 1 ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
                    : (Convert.ToDecimal(value) / scale).ToString(CultureInfo.InvariantCulture);
                if (energySlider != null && decimal.TryParse(text.Text, NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out decimal sliderVal) && sliderVal is >= 0 and <= int.MaxValue)
                    energySlider.Value = (int)Math.Round(sliderVal);
            }
            if (toggle != null) toggle.IsOn = value is true;
            if (pillChoice != null) pillChoice.SelectedValue = value;
        }
        finally { loading = false; }
        if (apply != null) apply.IsEnabled = HasPendingEdit;
        UpdateStepper();
        ValueEdited?.Invoke();
    }

    public void Revert() => RestoreDraft(customSlider != null ? loadedValue : text != null ? loadedText : loadedValue);

    private static void MakeBorderlessTransparent(TextBox box, bool clearInteractive = false)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        box.BorderThickness = new Thickness(0);
        box.Background = transparent;
        box.Resources["TextControlBackground"] = transparent;
        box.Resources["TextControlBackgroundDisabled"] = transparent;
        box.Resources["TextControlBorderBrushDisabled"] = transparent;
        if (clearInteractive)
        {
            box.Resources["TextControlBackgroundPointerOver"] = transparent;
            box.Resources["TextControlBackgroundFocused"] = transparent;
            box.Resources["TextControlBorderBrushPointerOver"] = transparent;
            box.Resources["TextControlBorderBrushFocused"] = transparent;
        }
    }

    private static Button CreateStepperButton(string tooltip, string glyph)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        var btn = new Button
        {
            Width = 28,
            Height = 34,
            MinWidth = 28,
            MinHeight = 34,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = transparent,
            CornerRadius = new CornerRadius(5),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = new FontIcon { Glyph = glyph, FontSize = 11 }
        };
        btn.Resources["ButtonBackground"] = transparent;
        btn.Resources["ButtonBackgroundDisabled"] = transparent;
        btn.Resources["ButtonBorderBrush"] = transparent;
        btn.Resources["ButtonBorderBrushPointerOver"] = transparent;
        btn.Resources["ButtonBorderBrushPressed"] = transparent;
        btn.Resources["ButtonBorderBrushDisabled"] = transparent;
        ToolTipService.SetToolTip(btn, tooltip);
        return btn;
    }

    public void UseNumericStepper(string title)
    {
        if (text == null || Property.PropertyType == typeof(string)) return;
        minimum = Property.Name is nameof(VmProcessorSettings.Count) or nameof(VmProcessorSettings.RelativeWeight) or nameof(VmProcessorSettings.MaxProcessorCountPerL3) or nameof(VmProcessorSettings.MaxClusterCountPerSocket) ? 1 : 0;
        maximum = Property.Name == nameof(VmProcessorSettings.MaxClusterCountPerSocket) ? 8
            : Property.Name == nameof(VmProcessorSettings.L3CacheWays) ? 1024
            : Property.Name == nameof(VmProcessorSettings.MaxHwIsolatedGuests) ? 64
            : scale == 1000 ? 100 : null;
        HideApplyButton();
        // Clear layout before re-parenting `text` into `inner`, otherwise WinUI throws E_FAIL / 0x800F1000.
        layout.Children.Clear();
        layout.ColumnDefinitions.Clear();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnSpacing = 0;

        MakeBorderlessTransparent(text, clearInteractive: true);
        text.FontSize = 15;
        text.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        text.Padding = new Thickness(0);
        text.MinWidth = 48;
        text.Width = 48;
        text.MinHeight = 0;
        text.Height = double.NaN;
        text.TextAlignment = TextAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.VerticalContentAlignment = VerticalAlignment.Center;
        text.PlaceholderText = Nullable.GetUnderlyingType(Property.PropertyType) != null ? L10n.Get("Common_Auto") : "";
        text.Loaded += (_, _) =>
        {
            if (VisualTreeHelper.GetChildrenCount(text) == 0) return;
            var templateRoot = VisualTreeHelper.GetChild(text, 0);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(templateRoot); i++)
            {
                if (VisualTreeHelper.GetChild(templateRoot, i) is not FrameworkElement child) continue;
                if (child.Name is "ContentElement" or "PlaceholderTextContentPresenter")
                    child.VerticalAlignment = VerticalAlignment.Center;
                else if (child.Name == "DeleteButton")
                {
                    child.Width = 0;
                    child.MinWidth = 0;
                    child.MaxWidth = 0;
                    child.Height = 0;
                    child.MinHeight = 0;
                    child.MaxHeight = 0;
                    child.Opacity = 0;
                    child.IsHitTestVisible = false;
                }
            }
        };

        var textHost = new Grid
        {
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Stretch
        };
        textHost.PointerPressed += (_, _) =>
        {
            if (text.IsEnabled && text.FocusState == FocusState.Unfocused)
                text.Focus(FocusState.Pointer);
        };
        textHost.Children.Add(text);

        decrease = CreateStepperButton(L10n.Format("Step_Decrease", title), "\uE738");
        increase = CreateStepperButton(L10n.Format("Step_Increase", title), "\uE710");
        decrease.Click += (_, _) => Step(-1);
        increase.Click += (_, _) => Step(1);

        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        Grid.SetColumn(decrease, 0);
        Grid.SetColumn(textHost, 1);
        Grid.SetColumn(increase, 2);
        inner.Children.Add(decrease);
        inner.Children.Add(textHost);
        inner.Children.Add(increase);

        stepperBox = new Border
        {
            Height = 36,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = inner,
            VerticalAlignment = VerticalAlignment.Center
        };
        void ApplyBoxTheme()
        {
            bool light = stepperBox.ActualTheme == ElementTheme.Light;
            stepperBox.Background = new SolidColorBrush(light
                ? Windows.UI.Color.FromArgb(8, 0, 0, 0)
                : Windows.UI.Color.FromArgb(61, 0, 0, 0));
            stepperBox.BorderBrush = new SolidColorBrush(light
                ? Windows.UI.Color.FromArgb(20, 0, 0, 0)
                : Windows.UI.Color.FromArgb(22, 255, 255, 255));
        }
        ApplyBoxTheme();
        stepperBox.ActualThemeChanged += (_, _) => ApplyBoxTheme();

        layout.Children.Add(stepperBox);
        UpdateStepper();
    }

    private void Step(int direction)
    {
        if (text != null && SettingsNumericStep.TryGetValue(text.Text, Property.PropertyType, scale, direction, minimum, maximum, out var value))
            text.Text = value;
    }

    private void UpdateStepper()
    {
        if (stepperBox != null) stepperBox.Opacity = IsFieldEnabled ? 1.0 : 0.45;
        if (text == null || decrease == null || increase == null) return;
        decrease.IsEnabled = IsFieldEnabled && SettingsNumericStep.TryGetValue(text.Text, Property.PropertyType, scale, -1, minimum, maximum, out _);
        increase.IsEnabled = IsFieldEnabled && SettingsNumericStep.TryGetValue(text.Text, Property.PropertyType, scale, 1, minimum, maximum, out _);
    }

    public void HideApplyButton()
    {
        if (apply == null) return;
        apply.Visibility = Visibility.Collapsed;
        layout.ColumnSpacing = 0;
    }

    public void UseAvailableWidth()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Center;
        if (text != null) { text.Width = double.NaN; text.MinWidth = 0; }
    }

    public void SetOptions(IEnumerable<SettingOption> options)
    {
        if (pillChoice == null) return;
        loading = true;
        try
        {
            pillChoice.SetOptions(options);
        }
        finally { loading = false; }
    }

    public void UpdateSliderBounds(int min, int max)
    {
        int nextMax = Math.Max(min, max);
        if (customSlider != null)
        {
            effectiveSliderMin = min;
            effectiveSliderMax = nextMax;
        }
        energySlider?.SetRange(min, nextMax);
    }

    public void Load(object? value, bool enabled, string? reason)
    {
        loading = true;
        try
        {
            loadedValue = value;
            if (customSlider != null)
            {
                currentCustomValue = value;
                if (energySlider != null)
                {
                    int pos = customSlider.ToSliderPos(value);
                    energySlider.Value = pos;
                }
                if (text != null)
                {
                    loadedText = FormatCustomSliderValue(value, customSlider.AllowAutoZero);
                    text.Text = loadedText;
                    if (customUnitText != null)
                        customUnitText.Visibility = string.IsNullOrEmpty(loadedText) ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            else
            {
                if (toggle != null) toggle.IsOn = value is true;
                if (text != null)
                {
                    loadedText = value == null ? "" : scale == 1 ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : (Convert.ToDecimal(value) / scale).ToString(CultureInfo.InvariantCulture);
                    text.Text = loadedText;
                    if (energySlider != null && decimal.TryParse(loadedText, NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out decimal sliderVal) && sliderVal is >= 0 and <= int.MaxValue)
                        energySlider.Value = (int)Math.Round(sliderVal);
                    if (apply != null) apply.IsEnabled = false;
                }
                if (pillChoice != null) pillChoice.SelectedValue = value;
            }
            SetAvailability(enabled, reason);
        }
        finally { loading = false; }
        UpdateStepper();
    }

    public int ReadEnergyInt(int fallback) =>
        text != null && decimal.TryParse(text.Text.Trim(), NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out decimal val) && val is >= 0 and <= int.MaxValue
            ? (int)Math.Round(val) : fallback;

    public void UpdateEnergyContext(int vmCoreCount, int hostLogicalCount) => energySlider?.SetContext(vmCoreCount, hostLogicalCount);

    public void SetEnergyConstraint(int? minAllowed, int? maxAllowed, string? reason) => energySlider?.SetCoupledBounds(minAllowed, maxAllowed, reason);

    public void SetAvailability(bool enabled, string? reason)
    {
        IsFieldEnabled = enabled;
        IsEnabled = enabled;
        if (text != null) text.IsEnabled = enabled;
        if (energySlider != null) energySlider.IsEnabled = enabled;
        pillChoice?.SetControlEnabled(enabled);
        ToolTipService.SetToolTip(this, reason);
        AvailabilityChanged?.Invoke(reason);
        UpdateStepper();
    }

    public bool TryRead(out object? value)
    {
        try { value = Read(); return true; }
        catch (InvalidOperationException) { value = null; return false; }
    }

    public object? Read()
    {
        if (customSlider != null) return currentCustomValue;
        if (toggle != null) return toggle.IsOn;
        if (pillChoice != null) return pillChoice.SelectedValue;
        var type = Nullable.GetUnderlyingType(Property.PropertyType) ?? Property.PropertyType;
        if (type == typeof(string)) return text!.Text;
        if (string.IsNullOrWhiteSpace(text!.Text) && Nullable.GetUnderlyingType(Property.PropertyType) != null) return null;
        try
        {
            if (type.IsEnum) return Enum.Parse(type, text.Text);
            if (scale == 1) return Convert.ChangeType(text.Text, type, CultureInfo.InvariantCulture);
            decimal scaled = decimal.Parse(text.Text, CultureInfo.InvariantCulture) * scale;
            if (decimal.Truncate(scaled) != scaled) throw new FormatException();
            return Convert.ChangeType(scaled, type, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException)
        { throw new InvalidOperationException(L10n.Get("Editor_InvalidNumberRange"), ex); }
    }
}
