
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared.Feedback;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.Host;

internal sealed partial class HostStatusView : UserControl
{
    private enum RailCategory { Cpu, Memory, Gpu, Disk, Nic, Switch }

    private sealed class RailEntry
    {
        public required string Key { get; init; }
        public required RailCategory Category { get; init; }
        public required string DetailTitle { get; init; }
        public required string DetailSubtitle { get; init; }
        public required string DetailGlyph { get; init; }
        public double DetailGlyphRotation { get; init; }
        public required Color CategoryColor { get; init; }
        public required Button Button { get; init; }
        public required VmMiniSparkline Sparkline { get; init; }
        public required TextBlock TitleText { get; init; }
        public TextBlock? SubtitleText { get; init; }
        public required TextBlock StatText { get; init; }
        public Action<HostLiveSample>? UpdateSample { get; set; }
    }

    private sealed class DetailPanelEntry
    {
        public required string Key { get; init; }
        public required RailCategory Category { get; init; }
        public required UIElement Root { get; init; }
        public required Action<HostLiveSample> ApplySample { get; init; }
        public required Func<HostLiveSample, string> FormatReading { get; init; }
        public Action? RefreshTheme { get; init; }
    }

    private readonly Grid shellGrid = new() { Margin = new Thickness(16, 0, 16, 16), ColumnSpacing = 12, RowSpacing = 10 };
    private readonly ColumnDefinition railColumn = new() { Width = new GridLength(244) };
    private readonly ColumnDefinition detailColumn = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly RowDefinition topRow = new() { Height = new GridLength(1, GridUnitType.Star) };
    private readonly RowDefinition bottomRow = new() { Height = new GridLength(0) };

    private readonly ScrollViewer railScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly StackPanel railPanel = new() { Spacing = 4 };

    private readonly Border detailCard = new()
    {
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16, 13, 16, 13)
    };
    private readonly Grid detailRoot = new() { RowSpacing = 10 };
    private readonly Border detailHeader = new() { BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 0, 0, 10) };
    private readonly Border detailIconBox = new()
    {
        Width = 36,
        Height = 36,
        CornerRadius = new CornerRadius(7),
        BorderThickness = new Thickness(1),
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly FontIcon detailIcon = new() { FontSize = 16, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5) };
    private readonly RotateTransform detailIconRotation = new();
    private readonly TextBlock detailTitle = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock detailSubtitle = new() { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock detailReading = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    private readonly ScrollViewer detailBodyScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private readonly Grid detailBodyHost = new();

    private readonly List<RailEntry> railEntries = [];
    private readonly Dictionary<string, DetailPanelEntry> detailPanels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<DetailPanelEntry>> detailFactories = new(StringComparer.Ordinal);
    private string selectedKey = "cpu";
    private HostInventorySnapshot? currentInventory;
    private HostLiveSample? lastSample;
    private IDashboardFeedbackHost? feedbackHost;

    private static Color ColorForCategory(RailCategory category) => category switch
    {
        RailCategory.Cpu => Color.FromArgb(255, 0x38, 0x98, 0xEC),
        RailCategory.Memory => Color.FromArgb(255, 0x9D, 0x5C, 0xE6),
        RailCategory.Gpu => Color.FromArgb(255, 0x8B, 0x5C, 0xF6),
        RailCategory.Disk => Color.FromArgb(255, 0x6C, 0xCB, 0x5F),
        RailCategory.Nic => Color.FromArgb(255, 0x46, 0xB3, 0xE6),
        RailCategory.Switch => Color.FromArgb(255, 0x00, 0xB7, 0xC3),
        _ => Color.FromArgb(255, 0x38, 0x98, 0xEC)
    };

    public HostStatusView()
    {
        shellGrid.ColumnDefinitions.Add(railColumn);
        shellGrid.ColumnDefinitions.Add(detailColumn);
        shellGrid.RowDefinitions.Add(topRow);
        shellGrid.RowDefinitions.Add(bottomRow);

        railScroll.AttachScrollbarClearance(bottomSpacing: 0);
        detailBodyScroll.AttachScrollbarClearance(bottomSpacing: 0);

        railScroll.Content = railPanel;
        Grid.SetRow(railScroll, 0);
        Grid.SetColumn(railScroll, 0);
        shellGrid.Children.Add(railScroll);

        BuildDetailCardShell();
        Grid.SetRow(detailCard, 0);
        Grid.SetColumn(detailCard, 1);
        shellGrid.Children.Add(detailCard);

        Content = shellGrid;

        shellGrid.SizeChanged += (_, _) => UpdateResponsiveLayout();
        ActualThemeChanged += (_, _) => UpdateTheme();
        UpdateTheme();
    }

    internal void BindFeedbackHost(IDashboardFeedbackHost host) => feedbackHost = host;

    private void BuildDetailCardShell()
    {
        detailRoot.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        detailRoot.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        detailIcon.RenderTransform = detailIconRotation;
        detailIconBox.Child = detailIcon;

        var titleStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(detailTitle);
        titleStack.Children.Add(detailSubtitle);

        Typography.SetNumeralAlignment(detailReading, FontNumeralAlignment.Tabular);

        var headGrid = new Grid { ColumnSpacing = 12 };
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(detailIconBox, 0);
        Grid.SetColumn(titleStack, 1);
        Grid.SetColumn(detailReading, 2);
        headGrid.Children.Add(detailIconBox);
        headGrid.Children.Add(titleStack);
        headGrid.Children.Add(detailReading);
        detailHeader.Child = headGrid;

        Grid.SetRow(detailHeader, 0);
        detailRoot.Children.Add(detailHeader);

        detailBodyScroll.Content = detailBodyHost;
        Grid.SetRow(detailBodyScroll, 1);
        detailRoot.Children.Add(detailBodyScroll);

        detailCard.Child = detailRoot;
    }

    public void ApplyInventory(HostInventorySnapshot inventory)
    {
        currentInventory = inventory;
        double savedRailScroll = railScroll.VerticalOffset;

        railPanel.Children.Clear();
        railEntries.Clear();
        detailBodyHost.Children.Clear();
        detailPanels.Clear();
        detailFactories.Clear();

        // 1. CPU
        AddCpuRailItem(inventory);

        // 2. Memory
        AddMemoryItem(inventory);

        // 3. GPUs
        for (int i = 0; i < inventory.Gpus.Count; i++)
            AddGpuItem(inventory, inventory.Gpus[i], i);

        // 4. Physical Disks
        foreach (var disk in inventory.Disks)
            AddDiskItem(inventory, disk);

        // 5. Connected Upstream NICs
        foreach (var nic in inventory.PhysicalAdapters)
        {
            if (!nic.LinkUp) continue;
            AddNicItem(inventory, nic);
        }

        // 6. Virtual Switches
        foreach (var sw in inventory.Switches)
            AddSwitchItem(inventory, sw);

        if (!detailFactories.ContainsKey(selectedKey))
            selectedKey = "cpu";

        SelectRailKey(selectedKey);
        railScroll.ChangeView(null, savedRailScroll, null, disableAnimation: true);
    }

    private DetailPanelEntry? EnsureDetailPanel(string key)
    {
        if (detailPanels.TryGetValue(key, out var existing)) return existing;
        if (!detailFactories.TryGetValue(key, out var factory)) return null;
        var created = factory();
        detailPanels[key] = created;
        detailBodyHost.Children.Add(created.Root);
        if (lastSample != null)
            created.ApplySample(lastSample);
        created.RefreshTheme?.Invoke();
        return created;
    }

    public void ApplyProcessorAndMemory(HostLiveSample sample)
    {
        lastSample = MergeSamples(lastSample, sample);
        foreach (var entry in railEntries)
            if (entry.Category is RailCategory.Cpu or RailCategory.Memory)
                entry.UpdateSample?.Invoke(lastSample);

        if (selectedKey is "cpu" or "mem" && detailPanels.TryGetValue(selectedKey, out var active))
        {
            active.ApplySample(lastSample);
            detailReading.Text = active.FormatReading(lastSample);
        }
    }

    public void ApplyDeviceSample(HostLiveSample sample)
    {
        lastSample = MergeSamples(lastSample, sample);
        foreach (var entry in railEntries)
            if (entry.Category is not (RailCategory.Cpu or RailCategory.Memory))
                entry.UpdateSample?.Invoke(lastSample);
            else if (entry.Category == RailCategory.Cpu)
                entry.UpdateSample?.Invoke(lastSample);

        if (detailPanels.TryGetValue(selectedKey, out var activeDetail))
        {
            activeDetail.ApplySample(lastSample);
            detailReading.Text = activeDetail.FormatReading(lastSample);
        }
    }

    private static HostLiveSample MergeSamples(HostLiveSample? previous, HostLiveSample incoming)
    {
        if (previous == null) return incoming;
        return new HostLiveSample
        {
            CpuPercent = incoming.CpuPercent ?? previous.CpuPercent,
            CpuFreqGHz = incoming.CpuFreqGHz ?? previous.CpuFreqGHz,
            CpuReport = incoming.CpuReport ?? previous.CpuReport,
            Memory = incoming.Memory ?? previous.Memory,
            VmSummary = incoming.VmSummary ?? previous.VmSummary,
            Disks = incoming.Disks.Count > 0 ? incoming.Disks : previous.Disks,
            Adapters = incoming.Adapters.Count > 0 ? incoming.Adapters : previous.Adapters,
            Switches = incoming.Switches.Count > 0 ? incoming.Switches : previous.Switches,
            Gpus = incoming.Gpus.Count > 0 ? incoming.Gpus : previous.Gpus,
            VmGpus = incoming.VmGpus ?? previous.VmGpus,
            VmStorage = incoming.VmStorage ?? previous.VmStorage,
            VmNetworks = incoming.VmNetworks ?? previous.VmNetworks
        };
    }

    public void RefreshTheme()
    {
        UpdateTheme();
    }

    private void SelectRailKey(string key)
    {
        selectedKey = key;

        detailHeader.Visibility = Visibility.Visible;
        detailBodyScroll.Visibility = Visibility.Visible;
        detailRoot.RowSpacing = 10;

        var activePanel = EnsureDetailPanel(key);
        foreach (var pair in detailPanels)
            pair.Value.Root.Visibility = pair.Key == key ? Visibility.Visible : Visibility.Collapsed;

        var selectedEntry = railEntries.FirstOrDefault(e => e.Key == key);
        if (selectedEntry != null)
        {
            detailIcon.Glyph = selectedEntry.DetailGlyph;
            detailIconRotation.Angle = selectedEntry.DetailGlyphRotation;
            detailTitle.Text = selectedEntry.DetailTitle;
            detailSubtitle.Text = selectedEntry.DetailSubtitle;
            detailSubtitle.Visibility = string.IsNullOrWhiteSpace(selectedEntry.DetailSubtitle) ? Visibility.Collapsed : Visibility.Visible;
            if (activePanel != null)
                detailReading.Text = activePanel.FormatReading(lastSample ?? new HostLiveSample());
        }

        UpdateTheme();
    }

    private void UpdateTheme()
    {
        bool light = ActualTheme == ElementTheme.Light;
        bool accentMode = HostUiHelpers.IsAccentEnabled;

        detailCard.Background = ThemeHelper.TryResourceBrush(ActualTheme, "VmCardBrush",
            light ? Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));
        detailCard.BorderBrush = ThemeHelper.TryResourceBrush(ActualTheme, "VmStrokeBrush",
            light ? Color.FromArgb(0x0F, 0, 0, 0) : Color.FromArgb(0x19, 0, 0, 0));
        detailHeader.BorderBrush = ThemeHelper.TryResourceBrush(ActualTheme, "DividerStrokeColorDefaultBrush",
            Color.FromArgb(0x22, 0x80, 0x80, 0x80));
        detailIconBox.Background = ThemeHelper.TryResourceBrush(ActualTheme, "SubtleFillColorSecondaryBrush",
            Color.FromArgb(0x12, 0x88, 0x88, 0x88));
        detailIconBox.BorderBrush = ThemeHelper.TryResourceBrush(ActualTheme, "DividerStrokeColorDefaultBrush",
            Color.FromArgb(0x22, 0x80, 0x80, 0x80));

        Color primary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorPrimaryBrush",
            light ? Color.FromArgb(0xE4, 0, 0, 0) : Colors.White);
        Color secondary = ThemeHelper.ThemeColor(ActualTheme, "TextFillColorSecondaryBrush",
            light ? Color.FromArgb(0x9E, 0, 0, 0) : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF));
        Color accent = ThemeHelper.ThemeColor(ActualTheme, "VmAccentBrush", Color.FromArgb(255, 0x4C, 0xC2, 0xFF));
        Color headerBg = ThemeHelper.ThemeColor(ActualTheme, "VmHeaderCheckedBrush", accent);
        Color headerFg = ThemeHelper.ThemeColor(ActualTheme, "VmHeaderForegroundBrush", Colors.White);

        detailIcon.Foreground = new SolidColorBrush(primary);
        detailTitle.Foreground = new SolidColorBrush(primary);
        detailSubtitle.Foreground = new SolidColorBrush(secondary);
        detailReading.Foreground = new SolidColorBrush(primary);

        Color neutralBg = light ? Color.FromArgb(0x06, 0, 0, 0) : Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF);
        Color neutralBorder = Color.FromArgb(0x26, 0x88, 0x88, 0x88);
        var transparent = new SolidColorBrush(Colors.Transparent);

        foreach (var entry in railEntries)
        {
            bool sel = entry.Key == selectedKey;
            if (accentMode)
            {
                if (sel)
                {
                    entry.Button.Background = new SolidColorBrush(headerBg);
                    entry.Button.BorderBrush = transparent;
                    entry.TitleText.FontWeight = FontWeights.SemiBold;
                    entry.TitleText.Foreground = new SolidColorBrush(headerFg);
                    entry.StatText.Foreground = new SolidColorBrush(headerFg);
                    entry.StatText.Opacity = 0.9;
                    if (entry.SubtitleText != null)
                    {
                        entry.SubtitleText.Foreground = new SolidColorBrush(headerFg);
                        entry.SubtitleText.Opacity = 0.9;
                    }
                    entry.Sparkline.SetPalette(
                        Color.FromArgb(26, headerFg.R, headerFg.G, headerFg.B),
                        Color.FromArgb(77, headerFg.R, headerFg.G, headerFg.B),
                        headerFg,
                        Color.FromArgb(51, headerFg.R, headerFg.G, headerFg.B),
                        Color.FromArgb(80, headerFg.R, headerFg.G, headerFg.B));
                }
                else
                {
                    entry.Button.Background = transparent;
                    entry.Button.BorderBrush = transparent;
                    entry.TitleText.FontWeight = FontWeights.Medium;
                    entry.TitleText.Foreground = new SolidColorBrush(primary);
                    entry.StatText.Foreground = new SolidColorBrush(secondary);
                    entry.StatText.Opacity = 1;
                    if (entry.SubtitleText != null)
                    {
                        entry.SubtitleText.Foreground = new SolidColorBrush(secondary);
                        entry.SubtitleText.Opacity = 1;
                    }
                    entry.Sparkline.SetPalette(
                        neutralBg,
                        neutralBorder,
                        accent,
                        Color.FromArgb(0x40, accent.R, accent.G, accent.B),
                        Color.FromArgb(0x50, secondary.R, secondary.G, secondary.B));
                }
            }
            else
            {
                entry.Button.Background = sel
                    ? ThemeHelper.TryResourceBrush(ActualTheme, "SubtleFillColorSecondaryBrush", Color.FromArgb(0x10, 0x80, 0x80, 0x80))
                    : transparent;
                entry.Button.BorderBrush = sel
                    ? ThemeHelper.TryResourceBrush(ActualTheme, "ControlStrokeColorDefaultBrush", neutralBorder)
                    : transparent;
                entry.TitleText.FontWeight = FontWeights.Medium;
                entry.TitleText.Foreground = new SolidColorBrush(primary);
                entry.StatText.Foreground = new SolidColorBrush(secondary);
                entry.StatText.Opacity = 1;
                if (entry.SubtitleText != null)
                {
                    entry.SubtitleText.Foreground = new SolidColorBrush(secondary);
                    entry.SubtitleText.Opacity = 1;
                }
                entry.Sparkline.SetPalette(
                    neutralBg,
                    neutralBorder,
                    entry.CategoryColor,
                    Color.FromArgb(0x45, entry.CategoryColor.R, entry.CategoryColor.G, entry.CategoryColor.B),
                    Color.FromArgb(0x50, secondary.R, secondary.G, secondary.B));
            }
        }

        if (detailPanels.TryGetValue(selectedKey, out var visibleDetail))
            visibleDetail.RefreshTheme?.Invoke();
    }

    private void UpdateResponsiveLayout()
    {
        double width = shellGrid.ActualWidth;
        if (width <= 0) return;

        if (width < 560)
        {
            railColumn.Width = new GridLength(1, GridUnitType.Star);
            detailColumn.Width = new GridLength(0);
            topRow.Height = GridLength.Auto;
            bottomRow.Height = new GridLength(1, GridUnitType.Star);
            railScroll.MaxHeight = 176;
            Grid.SetRow(railScroll, 0);
            Grid.SetColumn(railScroll, 0);
            Grid.SetRow(detailCard, 1);
            Grid.SetColumn(detailCard, 0);
        }
        else
        {
            railColumn.Width = new GridLength(width < 680 ? 200 : 244);
            detailColumn.Width = new GridLength(1, GridUnitType.Star);
            topRow.Height = new GridLength(1, GridUnitType.Star);
            bottomRow.Height = new GridLength(0);
            railScroll.MaxHeight = double.PositiveInfinity;
            Grid.SetRow(railScroll, 0);
            Grid.SetColumn(railScroll, 0);
            Grid.SetRow(detailCard, 0);
            Grid.SetColumn(detailCard, 1);
        }
    }

    private void CopySuccessToast(string val) => feedbackHost?.ShowSuccessToast(L10n.Format("Host_CopiedNotice", val));
    private void CopyErrorBanner(string msg) => feedbackHost?.ShowSettingsNotice(msg, InfoBarSeverity.Error);
}
