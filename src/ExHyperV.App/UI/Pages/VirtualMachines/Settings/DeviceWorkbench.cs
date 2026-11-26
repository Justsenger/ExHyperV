
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared.Feedback;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings;

/// <summary>构建与显卡页及 WebUI 设计系统对齐的设备卡片、KV 信息块、危险确认条与子页签。</summary>
internal static class DeviceWorkbench
{

    internal static FrameworkElement BuildStepIcon(bool isSkipped, bool isDone, bool isFailed, bool isActive)
    {
        if (isSkipped)
        {
            return new FontIcon
            {
                Glyph = "\uE738",
                FontFamily = AppFonts.SegoeFluent,
                FontSize = 15,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
            };
        }
        if (isDone)
        {
            return new FontIcon
            {
                Glyph = "\uEC61",
                FontFamily = AppFonts.SegoeFluent,
                FontSize = 18,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 60, 203, 95))
            };
        }
        if (isFailed)
        {
            return new FontIcon
            {
                Glyph = "\uEA39",
                FontFamily = AppFonts.SegoeFluent,
                FontSize = 17,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 241, 112, 122))
            };
        }
        if (isActive)
        {
            return new ProgressRing
            {
                Width = 17,
                Height = 17,
                IsActive = true
            };
        }
        return new FontIcon
        {
            Glyph = "\uEA3A",
            FontFamily = AppFonts.SegoeFluent,
            FontSize = 17,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
        };
    }

    internal static Grid Split(FrameworkElement left, FrameworkElement right)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    internal static StackPanel Actions(params UIElement[] items) => Actions(spacing: 8, items);

    internal static StackPanel Actions(double spacing, params UIElement[] items)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = spacing,
            VerticalAlignment = VerticalAlignment.Center
        };
        foreach (var item in items) panel.Children.Add(item);
        return panel;
    }

    internal static Button IconButton(
        FrameworkElement scope,
        string glyph,
        string label,
        Action click,
        bool useFluentFont = false)
    {
        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = glyph,
                FontFamily = useFluentFont ? AppFonts.FluentSystem : AppFonts.SegoeFluent,
                FontSize = 15,
                Foreground = ThemeHelper.ThemeBrush(scope, "TextFillColorPrimaryBrush")
            },
            Style = (Style)Application.Current.Resources["RoundIconButtonStyle"]
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        button.Click += (_, _) => click();
        return button;
    }

    internal static Button DangerButton(string label, Action click, bool enabled = true)
    {
        var button = new Button
        {
            Content = label,
            IsEnabled = enabled,
            Background = new SolidColorBrush(Color.FromArgb(255, 196, 43, 28)),
            Foreground = new SolidColorBrush(Colors.White),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            MinHeight = 32,
            Padding = new Thickness(12, 5, 12, 5)
        };
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(255, 209, 52, 56));
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(255, 164, 38, 44));
        button.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Colors.White);
        button.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Colors.White);
        button.Click += (_, _) => click();
        return button;
    }

    internal static Border Badge(FrameworkElement scope, string text) => new()
    {
        Background = ThemeHelper.ThemeBrush(scope, "VmBadgeBrush"),
        BorderBrush = ThemeHelper.ThemeBrush(scope, "VmKvWellBorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(7, 1, 7, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.ThemeBrush(scope, "TextFillColorSecondaryBrush")
        }
    };

    internal static Grid Header(
        FrameworkElement scope,
        string fluentGlyph,
        string title,
        string? subtitle,
        bool active,
        string? badge,
        ToggleSwitch toggle,
        Action configure,
        string configureTooltip,
        bool subtitleCaution = false)
    {
        var iconBox = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(7),
            Opacity = active ? 1.0 : 0.62,
            Background = ThemeHelper.ThemeBrush(scope, "SubtleFillColorSecondaryBrush"),
            BorderBrush = ThemeHelper.ThemeBrush(scope, "DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            Child = new FontIcon
            {
                Glyph = fluentGlyph,
                FontFamily = AppFonts.FluentSystem,
                FontSize = 17,
                Foreground = ThemeHelper.ThemeBrush(scope, "TextFillColorPrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        heading.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.ThemeBrush(scope, "TextFillColorPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrWhiteSpace(badge))
            heading.Children.Add(Badge(scope, badge));

        var identity = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 2
        };
        identity.Children.Add(heading);
        if (!string.IsNullOrEmpty(subtitle))
        {
            var subText = new TextBlock
            {
                Text = subtitle,
                FontSize = 12,
                Foreground = ThemeHelper.ThemeBrush(scope, subtitleCaution ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1
            };
            ToolTipService.SetToolTip(subText, subtitle);
            identity.Children.Add(subText);
        }

        var actions = Actions(spacing: 6, IconButton(scope, "\uE713", configureTooltip, configure), toggle);
        var header = new Grid
        {
            Padding = new Thickness(16, 12, 16, 12),
            ColumnSpacing = 12
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(iconBox);
        Grid.SetColumn(identity, 1);
        header.Children.Add(identity);
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);
        return header;
    }

    internal static Border CardShell(
        FrameworkElement scope,
        UIElement header,
        UIElement? body = null,
        UIElement? dangerStrip = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(header);
        if (dangerStrip != null)
            stack.Children.Add(dangerStrip);
        if (body != null)
        {
            stack.Children.Add(new Border
            {
                Height = 1,
                Background = ThemeHelper.ThemeBrush(scope, "DividerStrokeColorDefaultBrush")
            });
            stack.Children.Add(new Border
            {
                Padding = new Thickness(16, 12, 16, 14),
                Child = body
            });
        }

        return new Border
        {
            Background = ThemeHelper.ThemeBrush(scope, "CardBackgroundFillColorDefaultBrush"),
            BorderBrush = ThemeHelper.ThemeBrush(scope, "CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = stack
        };
    }

    internal static Border KvTile(
        FrameworkElement scope,
        string title,
        string value,
        string? copyValue = null,
        string? copyLabel = null,
        Action<string>? onError = null,
        Action? onReveal = null,
        string? revealLabel = null,
        bool caution = false)
    {
        var headRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6
        };
        headRow.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.ThemeBrush(scope, "TextFillColorPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center
        });

        if (!string.IsNullOrWhiteSpace(copyValue))
        {
            var copyIcon = new FontIcon
            {
                Glyph = "\uE8C8",
                FontFamily = AppFonts.SegoeFluent,
                FontSize = 11.5,
                Foreground = ThemeHelper.ThemeBrush(scope, "TextFillColorSecondaryBrush")
            };
            var copyBtn = InlineMiniButton(copyIcon, copyLabel ?? L10n.Format("Dev_CopyLabeledValue", title, copyValue));
            int copyGeneration = 0;
            copyBtn.Click += async (_, _) => await UiEvent.RunAsync(async () =>
            {
                if (!TryCopyText(copyValue, onError)) return;
                int token = ++copyGeneration;
                copyIcon.Glyph = "\uE73E";
                ToolTipService.SetToolTip(copyBtn, L10n.Get("Dev_Copied"));
                await Task.Delay(1600);
                if (copyGeneration != token || !copyBtn.IsLoaded) return;
                copyIcon.Glyph = "\uE8C8";
                ToolTipService.SetToolTip(copyBtn, copyLabel ?? L10n.Format("Dev_CopyValueTooltip", copyValue));
            });
            headRow.Children.Add(copyBtn);
        }

        if (onReveal != null)
        {
            var revealIcon = new FontIcon
            {
                Glyph = "\uE838",
                FontFamily = AppFonts.SegoeFluent,
                FontSize = 11.5,
                Foreground = ThemeHelper.ThemeBrush(scope, "TextFillColorSecondaryBrush")
            };
            var revealBtn = InlineMiniButton(revealIcon, revealLabel ?? L10n.Get("Dev_OpenFolder"));
            revealBtn.Click += (_, _) => onReveal();
            headRow.Children.Add(revealBtn);
        }

        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 12,
            FontWeight = FontWeights.Normal,
            Foreground = ThemeHelper.ThemeBrush(scope, caution ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            IsTextSelectionEnabled = true
        };
        if (!string.IsNullOrWhiteSpace(value))
            ToolTipService.SetToolTip(valueText, value);

        return new Border
        {
            Background = ThemeHelper.ThemeBrush(scope, "VmKvWellBrush"),
            BorderBrush = ThemeHelper.ThemeBrush(scope, "VmKvWellBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 10, 14, 10),
            Child = new StackPanel
            {
                Spacing = 2,
                Children = { headRow, valueText }
            }
        };
    }

    internal static Grid KvGrid(params (FrameworkElement Cell, bool Wide)[] items)
    {
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0;
        int col = 0;
        foreach (var (cell, wide) in items)
        {
            if (wide && col > 0)
            {
                row++;
                col = 0;
            }
            while (grid.RowDefinitions.Count <= row)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, wide ? 0 : col);
            if (wide) Grid.SetColumnSpan(cell, 2);
            grid.Children.Add(cell);

            if (wide)
            {
                row++;
                col = 0;
            }
            else if (col == 1)
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

    internal static Border DangerStrip(
        FrameworkElement scope,
        string title,
        string message,
        Button cancelButton,
        Button confirmButton)
    {
        bool light = scope.ActualTheme == ElementTheme.Light;
        var badge = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(0, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = ThemeHelper.ThemeBrush(scope, "SystemFillColorCautionBrush", Color.FromArgb(255, 252, 225, 0)),
            Child = new TextBlock
            {
                Text = "!",
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.Black),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var textStack = new StackPanel { Spacing = 2 };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.ThemeBrush(scope, "TextFillColorPrimaryBrush")
        });
        textStack.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 12,
            Foreground = ThemeHelper.ThemeBrush(scope, "TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        var info = new Grid { ColumnSpacing = 10 };
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        info.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        info.Children.Add(badge);
        Grid.SetColumn(textStack, 1);
        info.Children.Add(textStack);

        var row = Split(info, Actions(cancelButton, confirmButton));
        return new Border
        {
            BorderBrush = ThemeHelper.ThemeBrush(scope, "DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = new SolidColorBrush(light ? Color.FromArgb(15, 196, 43, 28) : Color.FromArgb(26, 196, 43, 28)),
            Padding = new Thickness(16, 12, 16, 12),
            Child = row
        };
    }

    internal static Border SettingCard(
        FrameworkElement scope,
        string title,
        string? description,
        FrameworkElement control,
        UIElement? below = null,
        bool enabled = true,
        string? disabledTooltip = null)
    {
        var labelGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            ColumnSpacing = 4,
            Opacity = enabled ? 1.0 : 0.55
        };
        labelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        labelGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        labelGrid.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            Foreground = ThemeHelper.ThemeBrush(scope, "TextFillColorPrimaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        var infoBtn = new SettingInfoButton(title, description);
        Grid.SetColumn(infoBtn, 1);
        labelGrid.Children.Add(infoBtn);

        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, title);

        var topRow = Split(labelGrid, control);
        topRow.MinHeight = 32;

        UIElement content = topRow;
        if (below != null)
        {
            var stack = new StackPanel { Spacing = 10 };
            stack.Children.Add(topRow);
            stack.Children.Add(below);
            content = stack;
        }

        var card = new Border
        {
            Background = ThemeHelper.ThemeBrush(scope, "CardBackgroundFillColorDefaultBrush"),
            BorderBrush = ThemeHelper.ThemeBrush(scope, "CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(17, 14, 17, 14),
            MinHeight = 68,
            Child = content
        };
        if (!enabled && !string.IsNullOrWhiteSpace(disabledTooltip))
            ToolTipService.SetToolTip(card, disabledTooltip);
        return card;
    }

    internal static Border PivotHeader(
        FrameworkElement scope,
        IReadOnlyList<(string Glyph, string Label, bool Active, Action OnSelect)> tabs,
        Action onBack,
        Button? trailingButton = null) => PivotHeader(scope, tabs, onBack, trailingButton, actionBottomMargin: 4);

    internal static Border PivotHeader(
        FrameworkElement scope,
        IReadOnlyList<(string Glyph, string Label, bool Active, Action OnSelect)> tabs,
        Action onBack,
        Button? trailingButton,
        double actionBottomMargin)
    {
        var tabRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };

        foreach (var (glyph, label, active, onSelect) in tabs)
        {
            var fg = ThemeHelper.ThemeBrush(scope, active ? "TextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"],
                Height = 34,
                Padding = new Thickness(11, 0, 11, 0),
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new FontIcon { Glyph = glyph, FontFamily = AppFonts.SegoeFluent, FontSize = 14, Foreground = fg },
                        new TextBlock
                        {
                            Text = label,
                            FontSize = 13,
                            FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium,
                            Foreground = fg,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                }
            };
            button.Click += (_, _) => onSelect();

            var slot = new Grid();
            slot.Children.Add(button);
            if (active)
            {
                slot.Children.Add(new Border
                {
                    Height = 2.5,
                    Margin = new Thickness(10, 0, 10, 0),
                    CornerRadius = new CornerRadius(2),
                    Background = ThemeHelper.ThemeBrush(scope, "VmAccentBrush"),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    IsHitTestVisible = false
                });
            }
            tabRow.Children.Add(slot);
        }

        static void StyleCompactHeaderButton(Button btn)
        {
            btn.Height = 28;
            btn.MinHeight = 28;
            btn.Padding = new Thickness(12, 0, 12, 0);
            btn.FontSize = 13;
        }

        var backBtn = new Button { Content = L10n.Get("Pcie_Back") };
        StyleCompactHeaderButton(backBtn);
        backBtn.Click += (_, _) => onBack();

        FrameworkElement right;
        if (trailingButton is null)
        {
            right = backBtn;
        }
        else
        {
            StyleCompactHeaderButton(trailingButton);
            right = Actions(backBtn, trailingButton);
        }
        right.Margin = new Thickness(0, 0, 0, actionBottomMargin);
        right.VerticalAlignment = VerticalAlignment.Center;

        return new Border
        {
            BorderBrush = ThemeHelper.ThemeBrush(scope, "DividerStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = Split(tabRow, right)
        };
    }

    internal static InfoBar Notice(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Warning) =>
        new() { Title = title, Message = message, Severity = severity, IsOpen = true, IsClosable = false };

    internal static Button InlineMiniButton(FontIcon icon, string label)
    {
        var button = new Button
        {
            Content = icon,
            Width = 22,
            Height = 20,
            MinWidth = 22,
            MinHeight = 20,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        AutomationProperties.SetName(button, label);
        ToolTipService.SetToolTip(button, label);
        return button;
    }

    internal static bool TryCopyText(string value, Action<string>? onError)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(value);
            Clipboard.SetContent(data);
            return true;
        }
        catch (Exception e)
        {
            onError?.Invoke(L10n.Format("Dev_ClipboardFailure", e.Message));
            return false;
        }
    }
}
