using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>分段胶囊选择器，对齐设计工作台的 isa-pill-track 视觉风格。</summary>
internal sealed class SettingsSegmentedPill : UserControl
{
    private readonly Border track = new()
    {
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(3),
        BorderThickness = new Thickness(1),
        Height = 36
    };
    private readonly Grid buttonsGrid = new() { ColumnSpacing = 4 };
    private readonly List<PillItem> items = [];
    private object? selectedValue;
    private bool controlEnabled = true;

    private sealed class PillItem
    {
        public required Border Root { get; init; }
        public required TextBlock Text { get; init; }
        public required object Value { get; init; }
        public required bool Enabled { get; init; }
        public bool IsHovered { get; set; }
    }

    public event Action<object?>? SelectionChanged;

    public SettingsSegmentedPill()
    {
        track.Child = buttonsGrid;
        Content = track;
        ApplyThemeBrushes();
        ActualThemeChanged += (_, _) => ApplyThemeBrushes();
        Loaded += (_, _) => UpdateVisualStates();
        IsEnabledChanged += (_, _) => UpdateVisualStates();
    }

    public void SetControlEnabled(bool enabled)
    {
        controlEnabled = enabled;
        IsEnabled = enabled;
        UpdateVisualStates();
    }

    public object? SelectedValue
    {
        get => selectedValue;
        set
        {
            selectedValue = value;
            UpdateVisualStates();
        }
    }

    public void SetOptions(IEnumerable<SettingOption> options)
    {
        items.Clear();
        buttonsGrid.Children.Clear();
        buttonsGrid.ColumnDefinitions.Clear();

        var list = options.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var opt = list[i];
            buttonsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = opt.Label,
                FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var pill = new Border
            {
                Child = label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderBrush = new SolidColorBrush(Colors.Transparent)
            };

            var item = new PillItem { Root = pill, Text = label, Value = opt.Value, Enabled = opt.Enabled };
            pill.PointerEntered += (_, _) =>
            {
                item.IsHovered = true;
                UpdateVisualStates();
            };
            pill.PointerExited += (_, _) =>
            {
                item.IsHovered = false;
                UpdateVisualStates();
            };
            pill.PointerPressed += (_, e) =>
            {
                if (!controlEnabled || !IsEnabled || !item.Enabled) return;
                if (!e.GetCurrentPoint(pill).Properties.IsLeftButtonPressed) return;
                if (Equals(selectedValue, item.Value)) return;
                selectedValue = item.Value;
                UpdateVisualStates();
                SelectionChanged?.Invoke(selectedValue);
            };

            Grid.SetColumn(pill, i);
            buttonsGrid.Children.Add(pill);
            items.Add(item);
        }
        UpdateVisualStates();
    }

    private void ApplyThemeBrushes()
    {
        var res = Application.Current.Resources;
        bool isLight = ActualTheme == ElementTheme.Light;
        track.Background = new SolidColorBrush(isLight ? Color.FromArgb(14, 0, 0, 0) : Color.FromArgb(62, 0, 0, 0));
        track.BorderBrush = new SolidColorBrush(isLight ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(16, 255, 255, 255));
        UpdateVisualStates();
    }

    private void UpdateVisualStates()
    {
        var res = Application.Current.Resources;
        bool isLight = ActualTheme == ElementTheme.Light;
        var accentBg = (Brush)res["VmHeaderCheckedBrush"];
        var accentHoverBg = accentBg is SolidColorBrush scb
            ? new SolidColorBrush(Color.FromArgb(scb.Color.A,
                (byte)Math.Min(255, scb.Color.R + 12),
                (byte)Math.Min(255, scb.Color.G + 12),
                (byte)Math.Min(255, scb.Color.B + 12)))
            : accentBg;
        var activeFg = new SolidColorBrush(isLight ? Colors.White : Color.FromArgb(255, 16, 20, 24));
        var primaryFg = (Brush)res["TextFillColorPrimaryBrush"];
        var secondaryFg = (Brush)res["TextFillColorSecondaryBrush"];
        var disabledFg = (Brush)res["TextFillColorDisabledBrush"];

        var transparent = new SolidColorBrush(Colors.Transparent);
        var hoverBg = new SolidColorBrush(isLight ? Color.FromArgb(14, 0, 0, 0) : Color.FromArgb(14, 255, 255, 255));
        var disabledActiveBg = new SolidColorBrush(isLight ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255));
        var disabledActiveBorder = new SolidColorBrush(isLight ? Color.FromArgb(16, 0, 0, 0) : Color.FromArgb(16, 255, 255, 255));

        foreach (var item in items)
        {
            bool effectiveEnabled = controlEnabled && IsEnabled && item.Enabled;
            bool active = Equals(item.Value, selectedValue);

            if (!effectiveEnabled)
            {
                item.Root.Background = active ? disabledActiveBg : transparent;
                item.Root.BorderBrush = active ? disabledActiveBorder : transparent;
                item.Text.Foreground = active ? secondaryFg : disabledFg;
                item.Text.FontWeight = FontWeights.Normal;
            }
            else if (active)
            {
                item.Root.Background = item.IsHovered ? accentHoverBg : accentBg;
                item.Root.BorderBrush = item.IsHovered ? accentHoverBg : accentBg;
                item.Text.Foreground = activeFg;
                item.Text.FontWeight = FontWeights.SemiBold;
            }
            else
            {
                item.Root.Background = item.IsHovered ? hoverBg : transparent;
                item.Root.BorderBrush = transparent;
                item.Text.Foreground = item.IsHovered ? primaryFg : secondaryFg;
                item.Text.FontWeight = FontWeights.Normal;
            }
        }
    }
}
