using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace ExHyperV.WinUI3.UI.Pages.Host;

internal static class HostUiHelpers
{
    internal static bool IsAccentEnabled =>
        ((App)Application.Current).Appearance?.Settings.AccentEnabled ?? true;

    internal static Color ThemeColor(ElementTheme actualTheme, string key, Color? fallback = null)
    {
        string theme = actualTheme == ElementTheme.Light ? "Light" : "Default";
        foreach (var resources in Application.Current.Resources.MergedDictionaries)
        {
            if (resources.ThemeDictionaries.TryGetValue(theme, out var entry) && entry is ResourceDictionary dictionary
                && dictionary.TryGetValue(key, out var value) && value is SolidColorBrush brush)
                return brush.Color;
        }
        if (Application.Current.Resources.TryGetValue(key, out var topValue) && topValue is SolidColorBrush topBrush)
            return topBrush.Color;
        return fallback ?? Colors.Gray;
    }

    internal static Brush TryResourceBrush(ElementTheme actualTheme, string key, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
            return brush;
        return new SolidColorBrush(ThemeColor(actualTheme, key, fallback));
    }

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

    internal static string FormatSchedulerLabel(HyperVSchedulerType type, bool full = false) => type switch
    {
        HyperVSchedulerType.Classic => L10n.Get(full ? "Host_Sched_ClassicFull" : "Host_Sched_Classic"),
        HyperVSchedulerType.Core => L10n.Get(full ? "Host_Sched_CoreFull" : "Host_Sched_Core"),
        HyperVSchedulerType.Root => L10n.Get(full ? "Host_Sched_RootFull" : "Host_Sched_Root"),
        _ => L10n.Get("Host_Sched_Unknown")
    };

    internal static string FormatSwitchMode(SwitchMode mode, bool full = false) => mode switch
    {
        SwitchMode.Bridge => L10n.Get(full ? "Host_Sw_Mode_BridgeFull" : "Host_Sw_Mode_Bridge"),
        SwitchMode.NAT => L10n.Get("Host_Sw_Mode_Nat"),
        _ => L10n.Get(full ? "Host_Sw_Mode_IsolatedFull" : "Host_Sw_Mode_Isolated")
    };

    internal static string FormatMediaTitle(AdapterMediaKind kind) => kind switch
    {
        AdapterMediaKind.Wired => L10n.Get("Host_Nic_Media_Wired"),
        AdapterMediaKind.WiFi => L10n.Get("Host_Nic_Media_WiFi"),
        AdapterMediaKind.Cellular => L10n.Get("Host_Nic_Media_Cellular"),
        AdapterMediaKind.Tunnel => L10n.Get("Host_Nic_Media_Tunnel"),
        _ => L10n.Get("Host_Nic_Media_Other")
    };

    internal static string MediaGlyph(AdapterMediaKind kind) => kind switch
    {
        AdapterMediaKind.Wired => "\uE839",
        AdapterMediaKind.WiFi => "\uE701",
        AdapterMediaKind.Cellular => "\uEC3B",
        AdapterMediaKind.Tunnel => "\uE705",
        _ => "\uE774"
    };

    internal static Color MediaColor(AdapterMediaKind kind) => kind switch
    {
        AdapterMediaKind.Wired => Color.FromArgb(255, 0x46, 0xB3, 0xE6),
        AdapterMediaKind.WiFi => Color.FromArgb(255, 0x38, 0xBD, 0xF8),
        AdapterMediaKind.Cellular => Color.FromArgb(255, 0xF5, 0x9E, 0x0B),
        AdapterMediaKind.Tunnel => Color.FromArgb(255, 0x10, 0xB9, 0x81),
        _ => Color.FromArgb(255, 0x46, 0xB3, 0xE6)
    };

    internal static string FormatBitsPerSecond(double bytesPerSec)
    {
        double bps = Math.Max(0, bytesPerSec) * 8.0;
        if (bps >= 1_000_000_000) return $"{bps / 1_000_000_000:0.##} Gbps";
        if (bps >= 1_000_000) return $"{bps / 1_000_000:0.#} Mbps";
        return $"{Math.Round(bps / 1_000):0} Kbps";
    }

    internal static string FormatCompactNetRate(double txBytesPerSec, double rxBytesPerSec)
    {
        double txKbps = Math.Max(0, txBytesPerSec) * 8.0 / 1_000.0;
        double rxKbps = Math.Max(0, rxBytesPerSec) * 8.0 / 1_000.0;
        if (txKbps < 0.5 && rxKbps < 0.5) return "0 Kbps";

        static (string Val, string Unit, string Short) Parts(double kbps)
        {
            if (kbps >= 1_000_000) return ($"{kbps / 1_000_000:0.##}", "Gbps", "G");
            if (kbps >= 1_000) return ($"{kbps / 1_000:0.#}", "Mbps", "M");
            return ($"{Math.Round(kbps):0}", "Kbps", "K");
        }

        var t = Parts(txKbps);
        var r = Parts(rxKbps);
        if (string.Equals(t.Unit, r.Unit, StringComparison.OrdinalIgnoreCase))
            return L10n.Format("Host_Net_CompactRate", t.Val, $"{r.Val} {r.Unit}");
        return L10n.Format("Host_Net_CompactRate", t.Val + t.Short, r.Val + r.Short);
    }

    internal static string FormatBytesPerSec(double bytesPerSec)
    {
        double b = Math.Max(0, bytesPerSec);
        if (b >= 1024d * 1024d * 1024d) return $"{b / (1024d * 1024d * 1024d):0.##} GB/s";
        if (b >= 1024d * 1024d) return $"{b / (1024d * 1024d):0.#} MB/s";
        return $"{Math.Round(b / 1024d):0} KB/s";
    }

    internal static string FormatLinkSpeed(ulong? bitsPerSec)
    {
        if (bitsPerSec is not > 0) return L10n.Get("Host_Nic_ConnectedSimple");
        double bps = bitsPerSec.Value;
        if (bps >= 1_000_000_000) return $"{bps / 1_000_000_000:0.##} Gbps";
        if (bps >= 1_000_000) return $"{bps / 1_000_000:0.##} Mbps";
        return $"{bps / 1_000:0} Kbps";
    }

    internal static string FormatMacWithDashes(string? rawMac)
    {
        if (string.IsNullOrWhiteSpace(rawMac)) return string.Empty;
        string clean = rawMac.Trim().Replace(":", "").Replace("-", "").ToUpperInvariant();
        if (clean.Length != 12) return rawMac.Trim().ToUpperInvariant();
        return $"{clean[0..2]}-{clean[2..4]}-{clean[4..6]}-{clean[6..8]}-{clean[8..10]}-{clean[10..12]}";
    }

    internal static string MiddleTrim(string text, int maxLength = 22)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        int tail = Math.Max(4, maxLength / 2 - 1);
        int head = Math.Max(4, maxLength - tail - 1);
        return $"{text[..head]}…{text[^tail..]}";
    }

    internal static Grid BuildScaleBar(ElementTheme actualTheme, string leftText, string rightText)
    {
        bool light = actualTheme == ElementTheme.Light;
        Brush secondary = TryResourceBrush(actualTheme, "TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));

        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new TextBlock { Text = leftText, FontSize = 11.5, Foreground = secondary };
        var right = new TextBlock { Text = rightText, FontSize = 11.5, Foreground = secondary };
        Typography.SetNumeralAlignment(right, FontNumeralAlignment.Tabular);
        Grid.SetColumn(right, 1);
        row.Children.Add(left);
        row.Children.Add(right);
        return row;
    }

    internal sealed record KvTileSpec(
        string Title,
        string StateText = "",
        string? Tooltip = null,
        bool Caution = false,
        bool Wide = false,
        string? CopyValue = null);

    internal static Grid BuildTitleFirstKvGrid(
        ElementTheme actualTheme,
        IReadOnlyList<KvTileSpec> items,
        Action<string>? onCopied = null,
        Action<string>? onCopyFailed = null)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        bool light = actualTheme == ElementTheme.Light;
        Brush primaryBrush = TryResourceBrush(actualTheme, "TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        Brush secondaryBrush = TryResourceBrush(actualTheme, "TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        Brush cautionBrush = TryResourceBrush(actualTheme, "SystemFillColorCautionBrush",
            light ? Color.FromArgb(255, 0x9D, 0x5D, 0x00) : Color.FromArgb(255, 0xFC, 0xE1, 0x00));
        Brush wellBg = TryResourceBrush(actualTheme, "VmKvWellBrush",
            light ? Color.FromArgb(0x06, 0, 0, 0) : Color.FromArgb(0x09, 0xFF, 0xFF, 0xFF));
        Brush wellBorder = TryResourceBrush(actualTheme, "VmKvWellBorderBrush",
            light ? Color.FromArgb(0x10, 0, 0, 0) : Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF));

        int row = 0, col = 0;
        foreach (var item in items)
        {
            if (item.Wide && col != 0)
            {
                row++;
                col = 0;
            }
            while (grid.RowDefinitions.Count <= row)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var cell = new StackPanel { Spacing = 2 };
            var headRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            headRow.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 13.5,
                FontWeight = FontWeights.Medium,
                Foreground = primaryBrush,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (!string.IsNullOrWhiteSpace(item.CopyValue))
                headRow.Children.Add(CreateInlineCopyButton(item.CopyValue, item.Title, secondaryBrush, onCopied, onCopyFailed));

            cell.Children.Add(headRow);

            string displayVal = string.IsNullOrWhiteSpace(item.StateText) ? "---" : item.StateText;
            var stateBlock = new TextBlock
            {
                Text = item.Wide && displayVal.Length > 54 ? MiddleTrim(displayVal, 54) : displayVal,
                FontSize = 12,
                FontWeight = item.Caution ? FontWeights.Medium : FontWeights.Normal,
                Foreground = item.Caution ? cautionBrush : secondaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            if (item.Wide || displayVal.Length > 32)
                ToolTipService.SetToolTip(stateBlock, displayVal);
            Typography.SetNumeralAlignment(stateBlock, FontNumeralAlignment.Tabular);
            cell.Children.Add(stateBlock);

            var card = new Border
            {
                Background = wellBg,
                BorderBrush = wellBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14, 10, 14, 10),
                Child = cell
            };
            if (!string.IsNullOrWhiteSpace(item.Tooltip))
                ToolTipService.SetToolTip(card, item.Tooltip);

            Grid.SetRow(card, row);
            Grid.SetColumn(card, item.Wide ? 0 : col);
            if (item.Wide) Grid.SetColumnSpan(card, 2);
            grid.Children.Add(card);

            if (item.Wide || col == 1)
            {
                row++;
                col = 0;
            }
            else
            {
                col = 1;
            }
        }
        return grid;
    }

    internal static Button CreateInlineCopyButton(
        string copyValue,
        string label,
        Brush iconBrush,
        Action<string>? onCopied = null,
        Action<string>? onCopyFailed = null)
    {
        var icon = new FontIcon
        {
            Glyph = "\uE8C8",
            FontSize = 11.5,
            Foreground = iconBrush
        };
        var button = new Button
        {
            Content = icon,
            Width = 22,
            Height = 20,
            MinWidth = 22,
            MinHeight = 20,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTipService.SetToolTip(button, L10n.Format("Dev_CopyValueTooltip", copyValue));
        AutomationProperties.SetName(button, L10n.Format("Dev_CopyValueAria", label, copyValue));
        button.Click += async (_, _) =>
        {
            try
            {
                var data = new DataPackage();
                data.SetText(copyValue);
                Clipboard.SetContent(data);
                onCopied?.Invoke(copyValue);
                icon.Glyph = "\uE73E";
                ToolTipService.SetToolTip(button, L10n.Get("Dev_Copied"));
                await Task.Delay(1600);
                icon.Glyph = "\uE8C8";
                ToolTipService.SetToolTip(button, L10n.Format("Dev_CopyValueTooltip", copyValue));
            }
            catch (Exception error)
            {
                onCopyFailed?.Invoke(L10n.Format("Dev_CopyFailed", error.Message));
            }
        };
        return button;
    }
}
