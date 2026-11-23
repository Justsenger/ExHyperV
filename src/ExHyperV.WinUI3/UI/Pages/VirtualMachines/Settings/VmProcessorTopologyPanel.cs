using ExHyperV.Models;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>处理器拓扑底部实时可视化预览卡片（插槽 / NUMA / L3 / 核心线程）。</summary>
internal sealed partial class VmProcessorTopologyPreview : UserControl
{
    private readonly Border card = new()
    {
        CornerRadius = new CornerRadius(8),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(12, 10, 12, 10)
    };
    private readonly TextBlock titleText = new()
    {
        Text = L10n.Get("Topo_PreviewTitle"),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock summaryText = new()
    {
        FontSize = 11.5,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right
    };
    private readonly Grid stageGrid = new() { ColumnSpacing = 8, RowSpacing = 8 };

    public VmProcessorTopologyPreview()
    {
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(summaryText, 1);
        header.Children.Add(titleText);
        header.Children.Add(summaryText);

        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(header);
        root.Children.Add(stageGrid);
        card.Child = root;
        Content = card;

        ApplyThemeBrushes();
        ActualThemeChanged += (_, _) => ApplyThemeBrushes();
    }

    private void ApplyThemeBrushes()
    {
        var res = Application.Current.Resources;
        card.Background = (Brush)res["CardBackgroundFillColorDefaultBrush"];
        card.BorderBrush = (Brush)res["CardStrokeColorDefaultBrush"];
        titleText.Foreground = (Brush)res["TextFillColorPrimaryBrush"];
        summaryText.Foreground = (Brush)res["TextFillColorSecondaryBrush"];
    }

    public static int[] GetAchievableCounts(int total)
    {
        int n = Math.Max(1, total);
        // ceil(n / group) = 1 + floor((n - 1) / group). Skip each run of equal quotients.
        var counts = new List<int>();
        for (long group = 1; group <= n;)
        {
            long quotient = (n - 1L) / group;
            counts.Add((int)(quotient + 1));
            if (quotient == 0) break;
            group = (n - 1L) / quotient + 1;
        }
        counts.Reverse();
        return counts.ToArray();
    }

    public void Update(
        int vcpu,
        SmtMode smt,
        int maxPerNuma,
        int maxNumaPerSocket,
        int? maxPerL3,
        int? maxClusters,
        L3DistributionPolicy l3Policy)
    {
        int totalVp = Math.Max(1, vcpu);
        int perNumaCap = Math.Max(1, maxPerNuma);
        int numaPerSocketCap = Math.Max(1, maxNumaPerSocket);

        int numaCount = Math.Max(1, (totalVp + perNumaCap - 1) / perNumaCap);
        int socketCount = Math.Max(1, (numaCount + numaPerSocketCap - 1) / numaPerSocketCap);
        int l3Cap = maxPerL3 is > 0 ? maxPerL3.Value : 0;

        var res = Application.Current.Resources;
        bool isLight = ActualTheme == ElementTheme.Light;
        var primaryBrush = (Brush)res["TextFillColorPrimaryBrush"];
        var secondaryBrush = (Brush)res["TextFillColorSecondaryBrush"];
        var accentBrush = (Brush)res["VmHeaderCheckedBrush"];

        var socketBg = new SolidColorBrush(isLight ? Color.FromArgb(10, 0, 0, 0) : Color.FromArgb(56, 0, 0, 0));
        var socketBorder = new SolidColorBrush(isLight ? Color.FromArgb(20, 0, 0, 0) : Color.FromArgb(23, 255, 255, 255));
        var numaBg = new SolidColorBrush(Color.FromArgb(13, 76, 194, 255));
        var numaAccentBorder = new SolidColorBrush(Color.FromArgb(115, 76, 194, 255));
        var l3Bg = new SolidColorBrush(isLight ? Color.FromArgb(8, 0, 0, 0) : Color.FromArgb(8, 255, 255, 255));
        var l3AccentBorder = new SolidColorBrush(isLight ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(51, 255, 255, 255));
        var chipBg = new SolidColorBrush(isLight ? Color.FromArgb(14, 0, 0, 0) : Color.FromArgb(18, 255, 255, 255));
        var chipBorder = new SolidColorBrush(isLight ? Color.FromArgb(26, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255));
        var smtChipBg = new SolidColorBrush(Color.FromArgb(26, 76, 194, 255));
        var smtChipBorder = new SolidColorBrush(Color.FromArgb(72, 76, 194, 255));
        var threadTagBg = new SolidColorBrush(isLight ? Color.FromArgb(20, 0, 0, 0) : Color.FromArgb(90, 0, 0, 0));
        var monoFont = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");

        stageGrid.Children.Clear();
        stageGrid.ColumnDefinitions.Clear();
        stageGrid.RowDefinitions.Clear();

        int cols = socketCount <= 1 ? 1 : 2;
        for (int c = 0; c < cols; c++)
            stageGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = (socketCount + cols - 1) / cols;
        for (int r = 0; r < rows; r++)
            stageGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        int vpCursor = 0;
        int numaCursor = 0;
        string clusterBadge = maxClusters is > 0 ? L10n.Format("Topo_ClusterBadge", maxClusters) : string.Empty;

        Border BuildNodeContent(int vpsInNuma)
        {
            var l3Groups = l3Cap > 0 ? SplitIntoL3Groups(vpsInNuma, l3Cap, l3Policy) : [vpsInNuma];

            WrapPanel BuildChipsWrap(int count)
            {
                var wrap = new WrapPanel { HorizontalSpacing = 4, VerticalSpacing = 4 };
                if (count > 24)
                {
                    int start = vpCursor;
                    vpCursor += count;
                    wrap.Children.Add(BuildPlainChip(L10n.Format("Topo_PlainRange", count, start, start + count - 1), chipBg, chipBorder, primaryBrush, monoFont));
                    return wrap;
                }

                if (smt == SmtMode.MultiThread)
                {
                    int end = vpCursor + count;
                    while (vpCursor < end)
                    {
                        int t0 = vpCursor++;
                        int? t1 = vpCursor < end ? vpCursor++ : null;
                        wrap.Children.Add(BuildSmtChip(L10n.Format("Topo_CoreLabel", t0 / 2), t0, t1, smtChipBg, smtChipBorder, threadTagBg, primaryBrush, accentBrush, monoFont));
                    }
                }
                else if (smt == SmtMode.SingleThread)
                {
                    for (int k = 0; k < count; k++)
                        wrap.Children.Add(BuildPlainChip(L10n.Format("Topo_CoreLabel", vpCursor++), chipBg, chipBorder, primaryBrush, monoFont));
                }
                else
                {
                    for (int k = 0; k < count; k++)
                        wrap.Children.Add(BuildPlainChip($"CPU {vpCursor++}", chipBg, chipBorder, primaryBrush, monoFont));
                }
                return wrap;
            }

            if (l3Groups.Length <= 1)
                return new Border { Child = BuildChipsWrap(vpsInNuma) };

            var l3Stack = new StackPanel { Spacing = 4 };
            for (int l = 0; l < l3Groups.Length; l++)
            {
                int groupVps = l3Groups[l];
                var l3Header = new Grid { ColumnSpacing = 6 };
                l3Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                l3Header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                l3Header.Children.Add(new TextBlock
                {
                    Text = $"L3 {l}",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = primaryBrush
                });
                var sub = new TextBlock { Text = L10n.Format("Topo_CoresUnit", groupVps), FontSize = 11, Foreground = secondaryBrush };
                Grid.SetColumn(sub, 1);
                l3Header.Children.Add(sub);

                var l3Inner = new StackPanel { Spacing = 4 };
                l3Inner.Children.Add(l3Header);
                l3Inner.Children.Add(BuildChipsWrap(groupVps));

                l3Stack.Children.Add(new Border
                {
                    Background = l3Bg,
                    BorderBrush = l3AccentBorder,
                    BorderThickness = new Thickness(2, 0, 0, 0),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 5, 7, 5),
                    Child = l3Inner
                });
            }
            return new Border { Child = l3Stack };
        }

        for (int s = 0; s < socketCount; s++)
        {
            int remainingNumas = numaCount - numaCursor;
            int remainingSockets = socketCount - s;
            if (remainingNumas <= 0) break;
            int takeNumas = Math.Clamp(remainingNumas - (remainingSockets - 1), 1, maxNumaPerSocket);

            var socketPanel = new StackPanel { Spacing = 6 };
            var socketHdr = new Grid { ColumnSpacing = 6 };
            socketHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            socketHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            socketHdr.Children.Add(new TextBlock
            {
                Text = L10n.Format("Topo_SocketLabel", s),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = primaryBrush
            });

            if (takeNumas == 1)
            {
                int nIdx = numaCursor++;
                int remVps = totalVp - vpCursor;
                int remNumaGlobal = Math.Max(1, numaCount - nIdx);
                int vpsInNuma = Math.Max(1, (remVps + remNumaGlobal - 1) / remNumaGlobal);

                var rightTag = new TextBlock
                {
                    Text = $"NUMA {nIdx}{clusterBadge}",
                    FontSize = 11,
                    Foreground = accentBrush
                };
                Grid.SetColumn(rightTag, 1);
                socketHdr.Children.Add(rightTag);
                socketPanel.Children.Add(socketHdr);
                socketPanel.Children.Add(BuildNodeContent(vpsInNuma));
            }
            else
            {
                var rightTag = new TextBlock
                {
                    Text = $"{takeNumas} NUMA{clusterBadge}",
                    FontSize = 11,
                    Foreground = secondaryBrush
                };
                Grid.SetColumn(rightTag, 1);
                socketHdr.Children.Add(rightTag);
                socketPanel.Children.Add(socketHdr);

                var numaGrid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
                int nCols = takeNumas <= 1 ? 1 : 2;
                for (int c = 0; c < nCols; c++)
                    numaGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                int nRows = (takeNumas + nCols - 1) / nCols;
                for (int r = 0; r < nRows; r++)
                    numaGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (int n = 0; n < takeNumas && numaCursor < numaCount; n++)
                {
                    int nIdx = numaCursor++;
                    int remVps = totalVp - vpCursor;
                    int remNumaGlobal = Math.Max(1, numaCount - nIdx);
                    int vpsInNuma = Math.Max(1, (remVps + remNumaGlobal - 1) / remNumaGlobal);

                    var nHdr = new Grid { ColumnSpacing = 6 };
                    nHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    nHdr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    nHdr.Children.Add(new TextBlock { Text = $"NUMA {nIdx}", FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = accentBrush });
                    var nSub = new TextBlock { Text = L10n.Format("Topo_CoresUnit", vpsInNuma), FontSize = 11, Foreground = secondaryBrush };
                    Grid.SetColumn(nSub, 1);
                    nHdr.Children.Add(nSub);

                    var nInner = new StackPanel { Spacing = 5 };
                    nInner.Children.Add(nHdr);
                    nInner.Children.Add(BuildNodeContent(vpsInNuma));

                    var numaBox = new Border
                    {
                        Background = numaBg,
                        BorderBrush = numaAccentBorder,
                        BorderThickness = new Thickness(2, 0, 0, 0),
                        CornerRadius = new CornerRadius(5),
                        Padding = new Thickness(8, 6, 8, 6),
                        Child = nInner
                    };
                    Grid.SetRow(numaBox, n / nCols);
                    Grid.SetColumn(numaBox, n % nCols);
                    numaGrid.Children.Add(numaBox);
                }
                socketPanel.Children.Add(numaGrid);
            }

            var socketBox = new Border
            {
                Background = socketBg,
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = socketBorder,
                Padding = new Thickness(10, 8, 10, 8),
                Child = socketPanel
            };
            Grid.SetRow(socketBox, s / cols);
            Grid.SetColumn(socketBox, s % cols);
            stageGrid.Children.Add(socketBox);
        }

        summaryText.Text = L10n.Format("Topo_SummaryTotal", totalVp);
    }

    private static Border BuildPlainChip(string text, Brush bg, Brush border, Brush fg, FontFamily font) => new()
    {
        Background = bg,
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(4),
        Height = 22,
        Padding = new Thickness(6, 0, 6, 0),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontFamily = font,
            Foreground = fg,
            VerticalAlignment = VerticalAlignment.Center
        }
    };

    private static Border BuildSmtChip(string coreLabel, int t0, int? t1, Brush bg, Brush border, Brush tagBg, Brush fg, Brush accentFg, FontFamily font)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = coreLabel, FontSize = 11, FontFamily = font, Foreground = fg, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new Border
        {
            Background = tagBg,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1, 4, 1),
            Child = new TextBlock { Text = t0.ToString(), FontSize = 10, FontFamily = font, Foreground = accentFg }
        });
        if (t1.HasValue)
        {
            row.Children.Add(new Border
            {
                Background = tagBg,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(4, 1, 4, 1),
                Child = new TextBlock { Text = t1.Value.ToString(), FontSize = 10, FontFamily = font, Foreground = accentFg }
            });
        }
        return new Border
        {
            Background = bg,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Height = 22,
            Padding = new Thickness(6, 0, 4, 0),
            Child = row
        };
    }

    internal static int[] SplitIntoL3Groups(int vps, int maxPerL3, L3DistributionPolicy policy)
    {
        if (vps <= 0) return [0];
        if (maxPerL3 <= 0 || maxPerL3 >= vps) return [vps];
        int groups = (vps + maxPerL3 - 1) / maxPerL3;
        var sizes = new int[groups];
        if (policy is L3DistributionPolicy.EvenSmallToLarge or L3DistributionPolicy.EvenLargeToSmall)
        {
            int baseCount = vps / groups;
            int extra = vps % groups;
            for (int i = 0; i < groups; i++)
                sizes[i] = baseCount + (i < extra ? 1 : 0);
            if (policy == L3DistributionPolicy.EvenSmallToLarge) Array.Reverse(sizes);
        }
        else
        {
            int rem = vps;
            for (int i = 0; i < groups; i++)
            {
                sizes[i] = Math.Min(maxPerL3, rem);
                rem -= sizes[i];
            }
            if (policy == L3DistributionPolicy.SmallToLarge) Array.Reverse(sizes);
        }
        return sizes;
    }

    private sealed partial class WrapPanel : Panel
    {
        public double HorizontalSpacing { get; set; } = 4;
        public double VerticalSpacing { get; set; } = 4;

        protected override Size MeasureOverride(Size availableSize)
        {
            double maxW = double.IsInfinity(availableSize.Width) ? double.MaxValue : availableSize.Width;
            double x = 0, y = 0, lineH = 0;
            foreach (var child in Children)
            {
                child.Measure(new Size(maxW, double.PositiveInfinity));
                double cw = child.DesiredSize.Width;
                double ch = child.DesiredSize.Height;
                if (x > 0 && x + HorizontalSpacing + cw > maxW)
                {
                    x = 0;
                    y += lineH + VerticalSpacing;
                    lineH = 0;
                }
                x = x == 0 ? cw : x + HorizontalSpacing + cw;
                lineH = Math.Max(lineH, ch);
            }
            return new Size(double.IsInfinity(availableSize.Width) ? x : availableSize.Width, y + lineH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double maxW = finalSize.Width;
            double x = 0, y = 0, lineH = 0;
            foreach (var child in Children)
            {
                double cw = child.DesiredSize.Width;
                double ch = child.DesiredSize.Height;
                if (x > 0 && x + HorizontalSpacing + cw > maxW)
                {
                    x = 0;
                    y += lineH + VerticalSpacing;
                    lineH = 0;
                }
                child.Arrange(new Rect(x, y, cw, ch));
                x += cw + HorizontalSpacing;
                lineH = Math.Max(lineH, ch);
            }
            return finalSize;
        }
    }
}
