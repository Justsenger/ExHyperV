using System.Numerics;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ExHyperV.WinUI3.UI.Shared;

/// <summary>Dashboard 统一通知宿主契约：合并成功胶囊与底部限高提示条。</summary>
internal interface IDashboardFeedbackHost
{
    bool HasSettingsError { get; }
    void ShowSuccessToast(string? text = null);
    void HideSuccessToast();
    void ShowSettingsNotice(string? text, InfoBarSeverity severity = InfoBarSeverity.Error, Button? actionButton = null);
    void ClearSettingsNotice();
}

/// <summary>B1 顶栏/右上轻量成功胶囊：零排版偏移且不遮挡设置卡片。</summary>
internal sealed class FloatingSuccessToast : UserControl
{
    private readonly Border outerBorder = new() { CornerRadius = new CornerRadius(14) };
    private readonly Border innerBorder = new()
    {
        CornerRadius = new CornerRadius(14),
        BorderThickness = new Thickness(1),
        Height = 28,
        Padding = new Thickness(9, 0, 12, 0)
    };
    private readonly Border badge = new()
    {
        Width = 16,
        Height = 16,
        CornerRadius = new CornerRadius(8),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly FontIcon checkIcon = new() { Glyph = "\uE73E", FontSize = 10 };
    private readonly TextBlock label = new()
    {
        FontSize = 12.5,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };
    private bool elevated;

    public FloatingSuccessToast()
    {
        Visibility = Visibility.Collapsed;
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Right;

        badge.Child = checkIcon;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(badge);
        row.Children.Add(label);
        innerBorder.Child = row;
        outerBorder.Child = innerBorder;
        Content = outerBorder;

        ApplyThemeBrushes();
        ActualThemeChanged += (_, _) => ApplyThemeBrushes();
    }

    public bool IsOpen { get; private set; }

    public void Show(string? text, bool elevated = false)
    {
        this.elevated = elevated;
        label.Text = string.IsNullOrWhiteSpace(text) ? L10n.Get("Common_Applied") : text.Trim();
        outerBorder.Shadow = elevated ? new ThemeShadow() : null;
        outerBorder.Translation = elevated ? new Vector3(0, 0, 16) : Vector3.Zero;
        ApplyThemeBrushes();
        Visibility = Visibility.Visible;
        IsOpen = true;
    }

    public void Hide()
    {
        IsOpen = false;
        Visibility = Visibility.Collapsed;
    }

    private void ApplyThemeBrushes()
    {
        outerBorder.Background = FeedbackThemeBrushes.Resolve(ActualTheme, "SolidBackgroundFillColorQuarternaryBrush");
        innerBorder.Background = FeedbackThemeBrushes.Resolve(ActualTheme, "InfoBarSuccessSeverityBackgroundBrush");
        innerBorder.BorderBrush = FeedbackThemeBrushes.Resolve(ActualTheme, elevated ? "ControlStrokeColorDefaultBrush" : "CardStrokeColorDefaultBrush");
        badge.Background = FeedbackThemeBrushes.Resolve(ActualTheme, "SystemFillColorSuccessBrush");
        checkIcon.Foreground = FeedbackThemeBrushes.Resolve(ActualTheme, "TextOnAccentFillColorPrimaryBrush");
        label.Foreground = FeedbackThemeBrushes.Resolve(ActualTheme, "TextFillColorPrimaryBrush");
    }
}

/// <summary>B1 底部悬浮限高通知条：最多 2 行摘要，长文本自动显示“查看详情”弹窗看全文与复制。</summary>
internal sealed class FloatingNoticeBanner : UserControl
{
    private readonly Border outerBorder = new()
    {
        CornerRadius = new CornerRadius(8),
        Shadow = new ThemeShadow(),
        Translation = new Vector3(0, 0, 24)
    };
    private readonly Border innerBorder = new()
    {
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(14, 9, 10, 9)
    };
    private readonly Border badge = new()
    {
        Width = 18,
        Height = 18,
        CornerRadius = new CornerRadius(9),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly FontIcon badgeIcon = new() { FontSize = 10 };
    private readonly TextBlock messageBlock = new()
    {
        FontSize = 13,
        TextWrapping = TextWrapping.Wrap,
        MaxLines = 2,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly ContentControl actionHost = new() { Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button detailsButton = new()
    {
        Content = L10n.Get("Action_ViewDetails"),
        Padding = new Thickness(10, 4, 10, 4),
        FontSize = 12.5,
        Visibility = Visibility.Collapsed,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Button closeButton = new()
    {
        Width = 28,
        Height = 28,
        MinWidth = 28,
        MinHeight = 28,
        Padding = new Thickness(0),
        BorderThickness = new Thickness(0),
        CornerRadius = new CornerRadius(4),
        VerticalAlignment = VerticalAlignment.Center,
        Content = new FontIcon { Glyph = "\uE711", FontSize = 11 }
    };

    private string fullText = string.Empty;
    private bool explicitOverflow;
    private bool dialogOpen;

    public FloatingNoticeBanner()
    {
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        MaxWidth = 780;

        badge.Child = badgeIcon;
        messageBlock.IsTextTrimmedChanged += (_, _) => UpdateDetailsButtonVisibility();

        detailsButton.Click += async (_, _) => await OpenDetailsDialogAsync();
        closeButton.Click += (_, _) =>
        {
            Hide();
            Closed?.Invoke(this, EventArgs.Empty);
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        actions.Children.Add(actionHost);
        actions.Children.Add(detailsButton);
        actions.Children.Add(closeButton);

        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(badge, 0);
        Grid.SetColumn(messageBlock, 1);
        Grid.SetColumn(actions, 2);
        row.Children.Add(badge);
        row.Children.Add(messageBlock);
        row.Children.Add(actions);

        innerBorder.Child = row;
        outerBorder.Child = innerBorder;
        Content = outerBorder;

        ApplyThemeBrushes();
        ActualThemeChanged += (_, _) => ApplyThemeBrushes();
    }

    public bool IsOpen { get; private set; }
    public InfoBarSeverity Severity { get; private set; } = InfoBarSeverity.Error;
    public event EventHandler? Closed;

    public void Show(string? text, InfoBarSeverity severity = InfoBarSeverity.Error, Button? actionButton = null)
    {
        Severity = severity;
        fullText = string.IsNullOrWhiteSpace(text) ? L10n.Get("Err_NoValidData") : text.Trim();

        string[] lines = fullText.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        explicitOverflow = lines.Length > 2 || fullText.Length > 110;
        messageBlock.Text = lines.Length > 2 ? string.Join(" ", lines) : fullText;

        actionHost.Content = actionButton;
        actionHost.Visibility = actionButton != null ? Visibility.Visible : Visibility.Collapsed;
        detailsButton.Content = L10n.Get("Action_ViewDetails");

        ApplyThemeBrushes();
        UpdateDetailsButtonVisibility();
        Visibility = Visibility.Visible;
        IsOpen = true;
    }

    public void Hide()
    {
        IsOpen = false;
        Visibility = Visibility.Collapsed;
        actionHost.Content = null;
        actionHost.Visibility = Visibility.Collapsed;
    }

    private void UpdateDetailsButtonVisibility()
    {
        detailsButton.Visibility = explicitOverflow || messageBlock.IsTextTrimmed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyThemeBrushes()
    {
        outerBorder.Background = FeedbackThemeBrushes.Resolve(ActualTheme, "SolidBackgroundFillColorQuarternaryBrush");
        innerBorder.BorderBrush = FeedbackThemeBrushes.Resolve(ActualTheme, "CardStrokeColorDefaultBrush");
        innerBorder.Background = FeedbackThemeBrushes.Resolve(ActualTheme, Severity switch
        {
            InfoBarSeverity.Warning => "InfoBarWarningSeverityBackgroundBrush",
            InfoBarSeverity.Informational => "InfoBarInformationalSeverityBackgroundBrush",
            InfoBarSeverity.Success => "InfoBarSuccessSeverityBackgroundBrush",
            _ => "InfoBarErrorSeverityBackgroundBrush"
        });
        badge.Background = FeedbackThemeBrushes.Resolve(ActualTheme, Severity switch
        {
            InfoBarSeverity.Warning => "SystemFillColorCautionBrush",
            InfoBarSeverity.Informational => "SystemFillColorAttentionBrush",
            InfoBarSeverity.Success => "SystemFillColorSuccessBrush",
            _ => "SystemFillColorCriticalBrush"
        });
        badgeIcon.Glyph = Severity switch
        {
            InfoBarSeverity.Warning => "\uE7BA",
            InfoBarSeverity.Informational => "\uE946",
            InfoBarSeverity.Success => "\uE73E",
            _ => "\uE711"
        };
        badgeIcon.Foreground = FeedbackThemeBrushes.Resolve(ActualTheme, "TextOnAccentFillColorPrimaryBrush");
        messageBlock.Foreground = FeedbackThemeBrushes.Resolve(ActualTheme, "TextFillColorPrimaryBrush");
        closeButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    private async Task OpenDetailsDialogAsync()
    {
        if (dialogOpen || XamlRoot == null || string.IsNullOrWhiteSpace(fullText)) return;
        dialogOpen = true;
        try
        {
            var body = new TextBlock
            {
                Text = fullText,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = FeedbackThemeBrushes.Resolve(ActualTheme, "TextFillColorPrimaryBrush")
            };
            var scroll = new ScrollViewer
            {
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body
            };
            var container = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                Background = FeedbackThemeBrushes.Resolve(ActualTheme, "CardBackgroundFillColorSecondaryBrush"),
                BorderBrush = FeedbackThemeBrushes.Resolve(ActualTheme, "CardStrokeColorDefaultBrush"),
                Child = scroll
            };
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
                Title = L10n.Get("Dialog_ErrorDetailsTitle"),
                Content = container,
                PrimaryButtonText = L10n.Get("Action_CopyAll"),
                CloseButtonText = L10n.Get("Common_Close"),
                DefaultButton = ContentDialogButton.Close
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                args.Cancel = true;
                var package = new DataPackage();
                package.SetText(fullText);
                Clipboard.SetContent(package);
                dialog.PrimaryButtonText = L10n.Get("Dev_Copied");
            };
            await dialog.ShowAsync();
        }
        finally
        {
            dialogOpen = false;
        }
    }
}

internal static class FeedbackThemeBrushes
{
    public static Brush Resolve(ElementTheme theme, string key)
    {
        string dictKey = theme == ElementTheme.Light ? "Light" : "Default";
        var resources = Application.Current.Resources;
        foreach (var merged in resources.MergedDictionaries)
        {
            if (merged.ThemeDictionaries.TryGetValue(dictKey, out var entry) && entry is ResourceDictionary td
                && td.TryGetValue(key, out var val) && val is Brush b)
                return b;
        }
        if (resources.ThemeDictionaries.TryGetValue(dictKey, out var topEntry) && topEntry is ResourceDictionary topTd
            && topTd.TryGetValue(key, out var topVal) && topVal is Brush tb)
            return tb;
        return resources.TryGetValue(key, out var direct) && direct is Brush db
            ? db
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }
}
