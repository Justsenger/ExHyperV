
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Gpu;

public sealed partial class VmGpuSettingsView
{
    private FrameworkElement? BuildInlineStepActions(GpuDeploymentStep step, GpuDecisionRequest request)
    {
        if (request.Kind == GpuDecisionKind.PowerOff)
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            var powerOffBtn = new Button
            {
                Content = L10n.Get("Gpu_ForceOffContinue")
            };
            powerOffBtn.Click += async (_, _) => await SubmitDecisionAsync(new GpuConfirmation(request.Id, true));
            actions.Children.Add(powerOffBtn);
            return actions;
        }

        if (step.Type == GpuDeploymentStepKind.Driver && request.Kind == GpuDecisionKind.DriverFallback)
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (request.CanGoBack)
            {
                var reselectBtn = new Button { Content = L10n.Get("Gpu_ReselectPartition") };
                reselectBtn.Click += async (_, _) => await SubmitDecisionAsync(new GpuBackToPartitions(request.Id));
                actions.Children.Add(reselectBtn);
            }
            var skipBtn = new Button { Content = L10n.Get("Gpu_SkipDriver") };
            skipBtn.Click += async (_, _) => await SubmitDecisionAsync(
                new GpuDriverFallbackSelection(request.Id, GpuDriverFallbackAction.SkipDriver));
            actions.Children.Add(skipBtn);
            return actions;
        }

        return null;
    }

    private UIElement BuildDecisionPanel(GpuDecisionRequest request) => request.Kind switch
    {
        GpuDecisionKind.SelectPartition => BuildPartitionsDecisionPanel(request),
        _ => new Border()
    };

    private UIElement BuildPartitionsDecisionPanel(GpuDecisionRequest request)
    {
        var list = new StackPanel { Spacing = 8 };
        GpuDeploymentPartition? selectedPartition = request.Partitions.FirstOrDefault(p =>
            !p.IsBitLocker && p.OsType == OperatingSystemType.Windows)
            ?? request.Partitions.FirstOrDefault();

        var hintText = new TextBlock
        {
            FontSize = 12,
            Foreground = Brush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var skipBtn = new Button { Content = L10n.Get("Gpu_SkipDriver") };
        var primaryBtn = new Button
        {
            Content = L10n.Get("Common_Continue"),
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"]
        };

        void UpdateActionButtons()
        {
            bool canInject = selectedPartition != null
                && !selectedPartition.IsBitLocker
                && selectedPartition.OsType == OperatingSystemType.Windows;
            primaryBtn.IsEnabled = canInject;
            if (canInject)
            {
                hintText.Text = string.Empty;
                hintText.Visibility = Visibility.Collapsed;
            }
            else
            {
                hintText.Text = selectedPartition switch
                {
                    { IsBitLocker: true } => L10n.Get("Gpu_SelectBitLockerNotice"),
                    { OsType: OperatingSystemType.Linux } => L10n.Get("Gpu_SelectLinuxNotice"),
                    _ => L10n.Get("Gpu_SelectUnsupportedNotice")
                };
                hintText.Visibility = Visibility.Visible;
            }
        }

        skipBtn.Click += async (_, _) => await SubmitDecisionAsync(
            new GpuDriverFallbackSelection(request.Id, GpuDriverFallbackAction.SkipDriver));
        primaryBtn.Click += async (_, _) =>
        {
            if (selectedPartition != null
                && !selectedPartition.IsBitLocker
                && selectedPartition.OsType == OperatingSystemType.Windows)
            {
                await SubmitDecisionAsync(new GpuPartitionSelection(request.Id, selectedPartition.Id));
            }
        };

        actionRow.Children.Add(skipBtn);
        actionRow.Children.Add(primaryBtn);

        var rowSyncActions = new List<(GpuDeploymentPartition Partition, Action<bool> SetSelected)>();
        foreach (GpuDeploymentPartition partition in request.Partitions)
        {
            var radioOuter = new Ellipse
            {
                Width = 16,
                Height = 16,
                Stroke = Brush("TextFillColorTertiaryBrush"),
                StrokeThickness = 1.5
            };
            var radioInner = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Brush("VmAccentBrush"),
                Visibility = Visibility.Collapsed
            };
            var radioHost = new Grid
            {
                Width = 16,
                Height = 16,
                VerticalAlignment = VerticalAlignment.Center
            };
            radioHost.Children.Add(radioOuter);
            radioHost.Children.Add(radioInner);

            var partIcon = new FontIcon
            {
                Glyph = partition.IsBitLocker ? "\uE72E" : "\uEDA2",
                FontFamily = AppFonts.SegoeFluent,
                FontSize = 18,
                Foreground = Brush("TextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };

            var titleLine = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleLine.Children.Add(new TextBlock
            {
                Text = partition.DisplayName,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });
            if (partition.IsBitLocker || partition.OsType == OperatingSystemType.Linux)
            {
                string helpText = L10n.Get(partition.IsBitLocker ? "Gpu_BitLockerHelp" : "Gpu_LinuxHelp");
                var infoBtn = new Button
                {
                    Content = new FontIcon
                    {
                        Glyph = "\uE946",
                        FontFamily = AppFonts.SegoeFluent,
                        FontSize = 13.5,
                        Foreground = Brush("TextFillColorSecondaryBrush")
                    },
                    Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["VmSelectorButtonStyle"],
                    Width = 24,
                    Height = 24,
                    MinWidth = 24,
                    MinHeight = 24,
                    CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                ToolTipService.SetToolTip(infoBtn, helpText);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(infoBtn, helpText);
                titleLine.Children.Add(infoBtn);
            }

            string subDesc = string.IsNullOrWhiteSpace(partition.TypeDescription)
                ? partition.DiskPath
                : $"{partition.TypeDescription}，{partition.DiskPath}";
            var infoStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            infoStack.Children.Add(titleLine);
            infoStack.Children.Add(new TextBlock
            {
                Text = subDesc,
                FontSize = 12,
                Foreground = Brush("TextFillColorSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var rightMeta = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (partition.IsBitLocker || partition.OsType == OperatingSystemType.Linux)
            {
                rightMeta.Children.Add(new Border
                {
                    Background = ThemeHelper.TryBrush("VmBadgeBrush", Color.FromArgb(14, 255, 255, 255)),
                    BorderBrush = Brush("VmKvWellBorderBrush"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(7, 2, 7, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = L10n.Get(partition.IsBitLocker ? "Gpu_BitLockerBadge" : "Gpu_LinuxUnsupportedBadge"),
                        FontSize = 11,
                        Foreground = Brush("TextFillColorSecondaryBrush")
                    }
                });
            }
            rightMeta.Children.Add(new TextBlock
            {
                Text = $"{partition.SizeGiB:F1} GB",
                FontSize = 12.5,
                Foreground = Brush("TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            });

            var rowGrid = new Grid { ColumnSpacing = 12 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            Grid.SetColumn(radioHost, 0);
            rowGrid.Children.Add(radioHost);
            Grid.SetColumn(partIcon, 1);
            rowGrid.Children.Add(partIcon);
            Grid.SetColumn(infoStack, 2);
            rowGrid.Children.Add(infoStack);
            Grid.SetColumn(rightMeta, 3);
            rowGrid.Children.Add(rightMeta);

            var accentBar = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(0, 2, 2, 0),
                Background = Brush("VmAccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Stretch,
                Margin = new Thickness(-16, 2, 0, 2),
                Visibility = Visibility.Collapsed
            };
            var container = new Grid();
            container.Children.Add(accentBar);
            container.Children.Add(rowGrid);

            var defaultBg = ThemeHelper.TryBrush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(13, 255, 255, 255));
            var hoverBg = ThemeHelper.TryBrush("SubtleFillColorTertiaryBrush", Color.FromArgb(14, 255, 255, 255));
            var selectedBg = ThemeHelper.SubtleHighlightBrush(ActualTheme, 12, 10);
            var defaultStroke = ThemeHelper.TryBrush("CardStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255));
            var selectedStroke = Brush("VmAccentBrush");

            var rowCard = new Border
            {
                Background = defaultBg,
                BorderBrush = defaultStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(16, 12, 16, 12),
                Child = container
            };

            void SetSelected(bool active)
            {
                rowCard.Background = active ? selectedBg : defaultBg;
                rowCard.BorderBrush = active ? selectedStroke : defaultStroke;
                radioOuter.Stroke = active ? selectedStroke : Brush("TextFillColorTertiaryBrush");
                radioInner.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                accentBar.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }

            rowSyncActions.Add((partition, SetSelected));
            rowCard.PointerEntered += (_, _) =>
            {
                if (!ReferenceEquals(partition, selectedPartition)) rowCard.Background = hoverBg;
            };
            rowCard.PointerExited += (_, _) =>
            {
                rowCard.Background = ReferenceEquals(partition, selectedPartition) ? selectedBg : defaultBg;
            };
            rowCard.PointerCanceled += (_, _) =>
            {
                rowCard.Background = ReferenceEquals(partition, selectedPartition) ? selectedBg : defaultBg;
            };
            rowCard.PointerPressed += (_, _) =>
            {
                selectedPartition = partition;
                foreach (var entry in rowSyncActions)
                    entry.SetSelected(ReferenceEquals(entry.Partition, selectedPartition));
                UpdateActionButtons();
            };

            list.Children.Add(rowCard);
        }

        foreach (var entry in rowSyncActions)
            entry.SetSelected(ReferenceEquals(entry.Partition, selectedPartition));
        UpdateActionButtons();

        var decisionBody = new StackPanel { Spacing = 10 };
        decisionBody.Children.Add(list);
        decisionBody.Children.Add(hintText);
        decisionBody.Children.Add(actionRow);

        return new Border
        {
            Background = ThemeHelper.SubtleHighlightBrush(ActualTheme, 6, 5),
            BorderBrush = ThemeHelper.TryBrush("CardStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 2, 0, 4),
            Child = decisionBody
        };
    }


    private Task SubmitDecisionAsync(GpuDeploymentDecision decision)
    {
        if (workflow == null) return Task.CompletedTask;
        var session = workflow;
        return RunGpuOperationAsync(async () =>
        {
            var next = await Task.Run(() => session.SubmitDecisionAsync(decision));
            await FinishStepAsync(session, next);
        });
    }

}
