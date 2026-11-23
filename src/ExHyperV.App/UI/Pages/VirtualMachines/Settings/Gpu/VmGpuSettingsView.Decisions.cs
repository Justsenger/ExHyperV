
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
            var mountIsoBtn = new Button
            {
                Content = L10n.Get("Gpu_MountDriverDisc"),
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"]
            };
            skipBtn.Click += async (_, _) => await SubmitDecisionAsync(
                new GpuDriverFallbackSelection(request.Id, GpuDriverFallbackAction.SkipDriver));
            mountIsoBtn.Click += async (_, _) => await SubmitDecisionAsync(
                new GpuDriverFallbackSelection(request.Id, GpuDriverFallbackAction.MountIso));
            actions.Children.Add(skipBtn);
            actions.Children.Add(mountIsoBtn);
            return actions;
        }

        if (step.Type == GpuDeploymentStepKind.Driver && request.Kind == GpuDecisionKind.StartLinux)
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
            var startBtn = new Button
            {
                Content = L10n.Get("Gpu_StartAndContinue"),
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"]
            };
            startBtn.Click += async (_, _) => await SubmitDecisionAsync(new GpuConfirmation(request.Id, true));
            actions.Children.Add(startBtn);
            return actions;
        }

        return null;
    }

    private UIElement BuildDecisionPanel(GpuDecisionRequest request) => request.Kind switch
    {
        GpuDecisionKind.SelectPartition => BuildPartitionsDecisionPanel(request),
        GpuDecisionKind.LinuxCredentials => BuildLinuxCredentialsPanel(request),
        _ => new Border()
    };

    private UIElement BuildPartitionsDecisionPanel(GpuDecisionRequest request)
    {
        var list = new StackPanel { Spacing = 8 };
        GpuDeploymentPartition? selectedPartition = request.Partitions.FirstOrDefault(p =>
            !p.IsBitLocker && p.OsType is OperatingSystemType.Windows or OperatingSystemType.Linux)
            ?? request.Partitions.FirstOrDefault();

        var actionRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var skipBtn = new Button { Content = L10n.Get("Gpu_SkipDriver") };
        var primaryBtn = new Button
        {
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"]
        };

        void UpdateActionButtons()
        {
            bool useIso = selectedPartition == null
                || selectedPartition.IsBitLocker
                || selectedPartition.OsType is not (OperatingSystemType.Windows or OperatingSystemType.Linux);
            primaryBtn.Content = useIso ? L10n.Get("Gpu_MountDriverDisc") : L10n.Get("Common_Continue");
        }

        skipBtn.Click += async (_, _) => await SubmitDecisionAsync(
            new GpuDriverFallbackSelection(request.Id, GpuDriverFallbackAction.SkipDriver));
        primaryBtn.Click += async (_, _) =>
        {
            if (selectedPartition == null
                || selectedPartition.IsBitLocker
                || selectedPartition.OsType is not (OperatingSystemType.Windows or OperatingSystemType.Linux))
            {
                await SubmitDecisionAsync(new GpuDriverFallbackSelection(request.Id, GpuDriverFallbackAction.MountIso));
            }
            else
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
            if (partition.IsBitLocker)
            {
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
                string bitLockerDesc = L10n.Get("Gpu_BitLockerHelp");
                ToolTipService.SetToolTip(infoBtn, bitLockerDesc);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(infoBtn, bitLockerDesc);
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

            var sizeText = new TextBlock
            {
                Text = $"{partition.SizeGiB:F1} GB",
                FontSize = 12.5,
                Foreground = Brush("TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };

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
            Grid.SetColumn(sizeText, 3);
            rowGrid.Children.Add(sizeText);

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

    private UIElement BuildLinuxCredentialsPanel(GpuDecisionRequest request)
    {
        var shownScripts = scripts;
        var availableScripts = new ComboBox
        {
            Header = L10n.Get("Gpu_DeploymentPlan"),
            ItemsSource = shownScripts.Select(script => script.Name).ToArray(),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var scriptDescription = Label(string.Empty, 12, secondary: true);
        LinuxGpuDeploymentScript? SelectedScript()
        {
            int index = availableScripts.SelectedIndex;
            return index >= 0 && index < shownScripts.Count ? shownScripts[index] : null;
        }
        void UpdateDescription() => scriptDescription.Text = SelectedScript() is { } selected
            ? L10n.Format("Gpu_ScriptAttribution", selected.Description, selected.Author, selected.Version)
            : scripts.Count == 0 ? L10n.Get("Gpu_ScriptListPending") : L10n.Get("Gpu_SelectLinuxScript");
        availableScripts.SelectionChanged += (_, _) => UpdateDescription();
        availableScripts.SelectedIndex = scripts.Count > 0 ? 0 : -1;
        UpdateDescription();
        if (scripts.Count == 0)
        {
            async Task PopulateScriptsAsync()
            {
                try
                {
                    await EnsureScriptsLoadedAsync(showWarningOnEmpty: false);
                    if (closing || IsStopped) return;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (closing || IsStopped) return;
                        shownScripts = scripts;
                        availableScripts.ItemsSource = shownScripts.Select(script => script.Name).ToArray();
                        availableScripts.SelectedIndex = scripts.Count > 0 ? 0 : -1;
                        UpdateDescription();
                    });
                }
                catch (OperationCanceledException) when (closing || IsStopped)
                {
                    // The script selector has left the page.
                }
                catch (Exception error)
                {
                    Notice(CoreMessageResolver.ResolveException(error), InfoBarSeverity.Warning);
                }
            }
            _ = PopulateScriptsAsync();
        }

        var host = new TextBox { Header = L10n.Get("Gpu_HostAddress"), Text = request.SuggestedHost, HorizontalAlignment = HorizontalAlignment.Stretch };
        var port = new NumberBox { Header = L10n.Get("Gpu_Port"), Value = 22, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Stretch };
        var username = new TextBox { Header = L10n.Get("Gpu_UserName"), Text = "root", HorizontalAlignment = HorizontalAlignment.Stretch };
        var password = new PasswordBox { Header = L10n.Get("Gpu_Password"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var graphics = new CheckBox { Content = L10n.Get("Gpu_InstallGraphics"), IsChecked = true };
        var useProxy = new ToggleSwitch { Header = L10n.Get("Gpu_UseDownloadProxy"), IsOn = false };
        var proxyHost = new TextBox { Header = L10n.Get("Gpu_ProxyHost"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var proxyPort = new NumberBox { Header = L10n.Get("Gpu_ProxyPort"), Value = 7890, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, HorizontalAlignment = HorizontalAlignment.Stretch };

        // 只读取客户端现有代理作为表单默认值；默认不开启，不修改代理设置。
        try
        {
            var proxy = System.Net.WebRequest.DefaultWebProxy?.GetProxy(new Uri("https://github.com"));
            if (proxy != null && !proxy.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            {
                proxyHost.Text = proxy.Host;
                proxyPort.Value = proxy.Port;
            }
        }
        catch (Exception error) when (error is System.Net.WebException or System.Net.Http.HttpRequestException
            or System.Net.Sockets.SocketException or System.Security.SecurityException or PlatformNotSupportedException)
        {
            // An unavailable system proxy leaves the editable connection defaults in place.
        }

        proxyHost.IsEnabled = proxyPort.IsEnabled = false;
        useProxy.Toggled += (_, _) => proxyHost.IsEnabled = proxyPort.IsEnabled = useProxy.IsOn;

        var credGrid = new Grid { ColumnSpacing = 12, RowSpacing = 10 };
        credGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        credGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        credGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        credGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetColumn(host, 0);
        Grid.SetRow(host, 0);
        credGrid.Children.Add(host);
        Grid.SetColumn(port, 1);
        Grid.SetRow(port, 0);
        credGrid.Children.Add(port);

        Grid.SetColumn(username, 0);
        Grid.SetRow(username, 1);
        credGrid.Children.Add(username);
        Grid.SetColumn(password, 1);
        Grid.SetRow(password, 1);
        credGrid.Children.Add(password);

        var proxyGrid = new Grid { ColumnSpacing = 12 };
        proxyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        proxyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(proxyHost, 0);
        Grid.SetColumn(proxyPort, 1);
        proxyGrid.Children.Add(proxyHost);
        proxyGrid.Children.Add(proxyPort);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        if (request.CanGoBack)
        {
            var backBtn = new Button { Content = L10n.Get("Gpu_ReselectPartition") };
            backBtn.Click += async (_, _) => await SubmitDecisionAsync(new GpuBackToPartitions(request.Id));
            actions.Children.Add(backBtn);
        }

        var connectBtn = new Button
        {
            Content = L10n.Get("Gpu_StartDeployment"),
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"]
        };
        connectBtn.Click += async (_, _) =>
        {
            var credentials = new SshCredentials
            {
                Host = host.Text.Trim(),
                Port = PortValue(port.Value),
                Username = username.Text,
                Password = password.Password,
                InstallGraphics = graphics.IsChecked == true,
                UseProxy = useProxy.IsOn,
                ProxyHost = proxyHost.Text.Trim(),
                ProxyPort = useProxy.IsOn ? PortValue(proxyPort.Value) : null
            };
            await SubmitDecisionAsync(new GpuLinuxInput(
                request.Id,
                SelectedScript(),
                credentials));
            // 凭据只交给本次调用；不写入配置、日志或恢复材料。
            if (workflow?.GetStatus().PendingDecision?.Id != request.Id) password.Password = string.Empty;
        };
        actions.Children.Add(connectBtn);

        var form = new StackPanel { Spacing = 10 };
        form.Children.Add(credGrid);
        form.Children.Add(availableScripts);
        form.Children.Add(scriptDescription);
        form.Children.Add(graphics);
        form.Children.Add(useProxy);
        form.Children.Add(Label(L10n.Get("Gpu_ProxyHelp"), 12, secondary: true));
        form.Children.Add(proxyGrid);
        form.Children.Add(actions);

        return new Border
        {
            Background = ThemeHelper.SubtleHighlightBrush(ActualTheme, 6, 5),
            BorderBrush = ThemeHelper.TryBrush("CardStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 2, 0, 4),
            Child = form
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

    private static int PortValue(double value)
        => double.IsNaN(value) || value < 1 || value > 65535 ? 0 : (int)Math.Round(value);
}
