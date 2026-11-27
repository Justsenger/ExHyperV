
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Gpu;

public sealed partial class VmGpuSettingsView
{
    private GpuLinuxBundleSource selectedBundleSource = GpuLinuxBundleSource.Online;
    private string selectedLocalBundlePath = string.Empty;
    private LinuxGpuPvBundleInfo? inspectedBundleInfo;
    private string? inspectedBundleError;
    private bool inspectingBundle;
    private CancellationTokenSource? bundleInspectCts;

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

    private UIElement BuildDecisionPanel(GpuDecisionRequest request, GpuDeploymentSnapshot value) => request.Kind switch
    {
        GpuDecisionKind.SelectPartition => BuildPartitionsDecisionPanel(request),
        GpuDecisionKind.SelectLinuxBundle => BuildLinuxBundleDecisionPanel(request, value),
        _ => new Border()
    };

    private UIElement BuildPartitionsDecisionPanel(GpuDecisionRequest request)
    {
        var list = new StackPanel { Spacing = 8 };
        GpuDeploymentPartition? selectedPartition = request.Partitions.FirstOrDefault(p =>
            !p.IsBitLocker && p.OsType is OperatingSystemType.Windows or OperatingSystemType.Linux)
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
        var primaryBtn = new Button
        {
            Content = L10n.Get("Common_Continue"),
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"]
        };

        static bool CanSelectForDeployment(GpuDeploymentPartition? partition) =>
            partition != null
            && !partition.IsBitLocker
            && partition.OsType is OperatingSystemType.Windows or OperatingSystemType.Linux;

        void UpdateActionButtons()
        {
            bool canProceed = CanSelectForDeployment(selectedPartition);
            primaryBtn.IsEnabled = canProceed;
            if (canProceed)
            {
                hintText.Text = string.Empty;
                hintText.Visibility = Visibility.Collapsed;
            }
            else
            {
                hintText.Text = selectedPartition switch
                {
                    { IsBitLocker: true } => L10n.Get("Gpu_SelectBitLockerNotice"),
                    _ => L10n.Get("Gpu_SelectUnsupportedNotice")
                };
                hintText.Visibility = Visibility.Visible;
            }
        }

        primaryBtn.Click += async (_, _) =>
        {
            if (CanSelectForDeployment(selectedPartition))
                await SubmitDecisionAsync(new GpuPartitionSelection(request.Id, selectedPartition!.Id));
        };

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
            if (partition.IsBitLocker)
            {
                string helpText = L10n.Get("Gpu_BitLockerHelp");
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
            if (partition.IsBitLocker)
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
                        Text = L10n.Get("Gpu_BitLockerBadge"),
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

    private UIElement BuildLinuxBundleDecisionPanel(GpuDecisionRequest request, GpuDeploymentSnapshot value)
    {
        var decisionBody = new StackPanel { Spacing = 10 };

        if (!string.IsNullOrWhiteSpace(value.BundleError))
        {
            decisionBody.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Error,
                IsOpen = true,
                IsClosable = false,
                Message = value.BundleError
            });
        }

        var list = new StackPanel { Spacing = 8 };
        var localPickerSub = new StackPanel { Spacing = 8 };

        var pathBox = new TextBox
        {
            Text = selectedLocalBundlePath,
            PlaceholderText = @"D:\Downloads\linuxpv-bundle-x86_64-v0.1.1.zip",
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontSize = 12.5
        };
        var browseBtn = new Button { Content = L10n.Get("Gpu_BundleBrowse") };
        var pickerGrid = new Grid { ColumnSpacing = 8 };
        pickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pickerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pickerGrid.Children.Add(pathBox);
        Grid.SetColumn(browseBtn, 1);
        pickerGrid.Children.Add(browseBtn);
        localPickerSub.Children.Add(pickerGrid);

        var inspectBar = new InfoBar
        {
            IsClosable = false,
            IsOpen = false,
            Visibility = Visibility.Collapsed
        };
        localPickerSub.Children.Add(inspectBar);

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var backBtn = new Button
        {
            Content = L10n.Get("Gpu_BackToList"),
            IsEnabled = request.CanGoBack
        };
        var continueBtn = new Button
        {
            Content = L10n.Get("Common_Continue"),
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"]
        };

        void SyncBundleStateUi()
        {
            bool isLocal = selectedBundleSource == GpuLinuxBundleSource.LocalFile;
            localPickerSub.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;

            string trimmed = selectedLocalBundlePath.Trim();
            bool hasPath = trimmed.Length > 0;
            bool isZip = trimmed.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            bool hasMatchedValid = inspectedBundleInfo != null
                && string.Equals(inspectedBundleInfo.FilePath, trimmed, StringComparison.OrdinalIgnoreCase);

            if (!isLocal || !hasPath)
            {
                inspectBar.IsOpen = false;
                inspectBar.Visibility = Visibility.Collapsed;
            }
            else if (!isZip)
            {
                inspectBar.Severity = InfoBarSeverity.Error;
                inspectBar.Message = L10n.Get("Gpu_BundleMustBeZip");
                inspectBar.IsOpen = true;
                inspectBar.Visibility = Visibility.Visible;
            }
            else if (inspectingBundle)
            {
                inspectBar.Severity = InfoBarSeverity.Informational;
                inspectBar.Message = L10n.Get("Gpu_BundleChecking");
                inspectBar.IsOpen = true;
                inspectBar.Visibility = Visibility.Visible;
            }
            else if (!string.IsNullOrWhiteSpace(inspectedBundleError))
            {
                inspectBar.Severity = InfoBarSeverity.Error;
                inspectBar.Message = inspectedBundleError;
                inspectBar.IsOpen = true;
                inspectBar.Visibility = Visibility.Visible;
            }
            else if (hasMatchedValid)
            {
                inspectBar.Severity = InfoBarSeverity.Success;
                inspectBar.Message = L10n.Format(
                    "Gpu_BundleValidFormat",
                    inspectedBundleInfo!.Architecture,
                    inspectedBundleInfo.Version,
                    inspectedBundleInfo.KernelCount);
                inspectBar.IsOpen = true;
                inspectBar.Visibility = Visibility.Visible;
            }
            else
            {
                inspectBar.IsOpen = false;
                inspectBar.Visibility = Visibility.Collapsed;
            }

            continueBtn.IsEnabled = !isLocal || (isZip && !inspectingBundle && hasMatchedValid);
        }

        async Task InspectSelectedLocalBundleAsync(string path)
        {
            bundleInspectCts?.Cancel();
            bundleInspectCts?.Dispose();
            bundleInspectCts = null;

            selectedLocalBundlePath = path;
            inspectedBundleInfo = null;
            inspectedBundleError = null;
            inspectingBundle = false;

            string trimmed = path.Trim();
            if (trimmed.Length == 0 || !trimmed.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                SyncBundleStateUi();
                return;
            }

            var cts = new CancellationTokenSource();
            bundleInspectCts = cts;
            inspectingBundle = true;
            SyncBundleStateUi();

            try
            {
                var result = await Task.Run(() => VmGpuService.InspectLinuxGpuPvBundleAsync(trimmed, cts.Token), cts.Token);
                if (cts.IsCancellationRequested || closing || IsStopped) return;
                if (!string.Equals(selectedLocalBundlePath.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)) return;

                inspectingBundle = false;
                if (result.HasData)
                {
                    inspectedBundleInfo = result.Data;
                    inspectedBundleError = null;
                }
                else
                {
                    inspectedBundleInfo = null;
                    inspectedBundleError = CoreMessageResolver.ResolveError(result);
                }
                SyncBundleStateUi();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // Ignored when replaced by a newer path selection.
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested || closing || IsStopped) return;
                inspectingBundle = false;
                inspectedBundleInfo = null;
                inspectedBundleError = CoreMessageResolver.ResolveException(ex);
                SyncBundleStateUi();
            }
        }

        pathBox.TextChanged += async (_, _) =>
        {
            string current = pathBox.Text ?? string.Empty;
            if (string.Equals(current, selectedLocalBundlePath, StringComparison.Ordinal)) return;
            await InspectSelectedLocalBundleAsync(current);
        };

        browseBtn.Click += async (_, _) => await UiEvent.RunAsync(async () =>
        {
            string? picked = await StoragePickers.OpenAsync(".zip");
            if (string.IsNullOrWhiteSpace(picked) || closing || IsStopped) return;
            pathBox.Text = picked;
            await InspectSelectedLocalBundleAsync(picked);
        });

        backBtn.Click += async (_, _) => await SubmitDecisionAsync(new GpuBackToPartitions(request.Id));
        continueBtn.Click += async (_, _) =>
        {
            string? localPath = selectedBundleSource == GpuLinuxBundleSource.LocalFile
                ? selectedLocalBundlePath.Trim()
                : null;
            await SubmitDecisionAsync(new GpuLinuxBundleSelection(request.Id, selectedBundleSource, localPath));
        };

        actionRow.Children.Add(backBtn);
        actionRow.Children.Add(continueBtn);

        var rowSyncActions = new List<(GpuLinuxBundleSource Source, Action<bool> SetSelected)>();
        var sources = new (GpuLinuxBundleSource Source, string Glyph, string Title, string Desc)[]
        {
            (GpuLinuxBundleSource.Online, "\uE753", L10n.Get("Gpu_BundleOnline"), L10n.Get("Gpu_BundleOnlineDesc")),
            (GpuLinuxBundleSource.LocalFile, "\uE8B7", L10n.Get("Gpu_BundleLocal"), L10n.Get("Gpu_BundleLocalDesc"))
        };

        var defaultBg = ThemeHelper.TryBrush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(13, 255, 255, 255));
        var hoverBg = ThemeHelper.TryBrush("SubtleFillColorTertiaryBrush", Color.FromArgb(14, 255, 255, 255));
        var selectedBg = ThemeHelper.SubtleHighlightBrush(ActualTheme, 12, 10);
        var defaultStroke = ThemeHelper.TryBrush("CardStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255));
        var selectedStroke = Brush("VmAccentBrush");

        foreach (var item in sources)
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

            var optionIcon = new FontIcon
            {
                Glyph = item.Glyph,
                FontFamily = AppFonts.SegoeFluent,
                FontSize = 18,
                Foreground = Brush("TextFillColorPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };

            var infoStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            infoStack.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextFillColorPrimaryBrush")
            });
            infoStack.Children.Add(new TextBlock
            {
                Text = item.Desc,
                FontSize = 12,
                Foreground = Brush("TextFillColorSecondaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            var rowGrid = new Grid { ColumnSpacing = 12 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Grid.SetColumn(radioHost, 0);
            rowGrid.Children.Add(radioHost);
            Grid.SetColumn(optionIcon, 1);
            rowGrid.Children.Add(optionIcon);
            Grid.SetColumn(infoStack, 2);
            rowGrid.Children.Add(infoStack);

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

            rowSyncActions.Add((item.Source, SetSelected));
            rowCard.PointerEntered += (_, _) =>
            {
                if (selectedBundleSource != item.Source) rowCard.Background = hoverBg;
            };
            rowCard.PointerExited += (_, _) =>
            {
                rowCard.Background = selectedBundleSource == item.Source ? selectedBg : defaultBg;
            };
            rowCard.PointerCanceled += (_, _) =>
            {
                rowCard.Background = selectedBundleSource == item.Source ? selectedBg : defaultBg;
            };
            rowCard.PointerPressed += (_, _) =>
            {
                selectedBundleSource = item.Source;
                foreach (var entry in rowSyncActions)
                    entry.SetSelected(entry.Source == selectedBundleSource);
                SyncBundleStateUi();
            };

            list.Children.Add(rowCard);
        }

        foreach (var entry in rowSyncActions)
            entry.SetSelected(entry.Source == selectedBundleSource);
        SyncBundleStateUi();

        decisionBody.Children.Add(list);
        decisionBody.Children.Add(localPickerSub);
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
