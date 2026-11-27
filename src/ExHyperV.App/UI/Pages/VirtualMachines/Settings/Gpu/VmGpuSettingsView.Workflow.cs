using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Gpu;

public sealed partial class VmGpuSettingsView
{
    private readonly StackPanel stepsPanel = new() { Spacing = 8 };
    private readonly ContentControl stepsHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel fallbackDecisionPanel = new() { Spacing = 10 };
    private readonly ContentControl fallbackDecisionHost = new() { Visibility = Visibility.Collapsed, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button cancel = new() { Content = L10n.Get("Gpu_CancelDeployment") };
    private readonly Button reconcile = new() { Content = L10n.Get("Gpu_Reconcile") };
    private readonly Button retryCleanup = new() { Content = L10n.Get("Gpu_RetryCleanup") };
    private readonly Button done = new() { Content = L10n.Get("Gpu_BackToList") };

    private readonly StackPanel footerActions = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
    private GpuDeploymentSession? workflow;
    private GpuDeploymentSnapshot? snapshot;
    private bool cancellationRequested;
    private string? renderedStepsSignature;

    private ProgressBar? activeStepProgressBar;
    private TextBlock? activeStepProgressText;
    private string? renderedStructureSignature;

    private UIElement BuildDeploymentPanel()
    {
        cancel.Click += async (_, _) => await CancelDeploymentAsync();
        reconcile.Click += async (_, _) => await RunGpuOperationAsync(() => PerformSessionActionAsync(GpuDeploymentAction.Reconcile));
        retryCleanup.Click += async (_, _) => await RunGpuOperationAsync(() => PerformSessionActionAsync(GpuDeploymentAction.RetryCleanup));
        done.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
        done.Click += async (_, _) => await ReturnFromDeploymentAsync();

        footerActions.Children.Clear();
        foreach (var button in new[] { cancel, reconcile, retryCleanup, done })
            footerActions.Children.Add(button);

        stepsHost.Content = stepsPanel;
        fallbackDecisionHost.Content = fallbackDecisionPanel;

        deployment.Children.Add(stepsHost);
        deployment.Children.Add(fallbackDecisionHost);
        deployment.Children.Add(footerActions);
        return deployment;
    }

    private Task ReturnFromDeploymentAsync() => RunGpuOperationAsync(async () =>
    {
        EnsureNoUnfinishedDeployment();
        await RefreshAssignmentsAsync();
        overview.Visibility = Visibility.Visible;
        deployment.Visibility = selection.Visibility = Visibility.Collapsed;
    });

    private async Task StartDeploymentAsync(IReadOnlyList<HostGpuInfo> gpus, GpuDeploymentMode mode)
    {
        EnsureNoUnfinishedDeployment();
        var identity = await QueryAsync(VmIdentityFields.Name);
        var name = identity.Get(VmIdentityFields.Name);
        if (!name.HasValue) throw new InvalidOperationException(name.Error ?? L10n.Get("Gpu_ReadVmNameFailed"));
        if (closing || IsStopped) return;

        var vmName = name.Value ?? throw new InvalidOperationException(L10n.Get("Gpu_EmptyVmName"));
        var session = new GpuDeploymentSession(VmId, vmName, gpus, service, query, mode);
        workflow = session;
        snapshot = null;
        cancellationRequested = false;
        renderedStepsSignature = null;
        renderedStructureSignature = null;
        activeStepProgressBar = null;
        activeStepProgressText = null;
        selectedBundleSource = GpuLinuxBundleSource.Online;
        selectedLocalBundlePath = string.Empty;
        inspectedBundleInfo = null;
        inspectedBundleError = null;
        inspectingBundle = false;
        bundleInspectCts?.Cancel();
        bundleInspectCts?.Dispose();
        bundleInspectCts = null;
        fallbackDecisionPanel.Children.Clear();
        fallbackDecisionHost.Visibility = Visibility.Collapsed;

        SetHeaderTrailingAction(null);
        overview.Visibility = selection.Visibility = Visibility.Collapsed;
        deployment.Visibility = Visibility.Visible;
        ApplySnapshot(session, session.GetStatus());

        // Core 可同步调用底层能力；整步在后台执行，通知只排队，不阻塞 Core。
        var result = await Task.Run(() => session.StartAsync(value =>
        {
            if (IsStopped) return;
            DispatcherQueue.TryEnqueue(() => ApplySnapshot(session, value));
        }));
        await FinishStepAsync(session, result);
    }

    private async Task FinishStepAsync(GpuDeploymentSession session, GpuDeploymentSnapshot result)
    {
        ApplySnapshot(session, result);
        if (result.State == GpuDeploymentState.Completed)
        {
            bool refreshOk = true;
            try { await RefreshAssignmentsAsync(); }
            catch (Exception ex)
            {
                refreshOk = false;
                Notice(L10n.Format("Gpu_RefreshFailed", CoreMessageResolver.ResolveException(ex)), InfoBarSeverity.Warning);
            }
            NotifyChanged();
            bool hasErrors = !string.IsNullOrWhiteSpace(result.Error) || !string.IsNullOrWhiteSpace(result.CleanupError);
            bool hasLinuxCommand = !string.IsNullOrWhiteSpace(result.GuestSetupCommand);
            if (refreshOk
                && !result.HasPendingCleanup
                && !result.OutcomeUnknown
                && !result.IsDriverSkipped
                && !hasLinuxCommand
                && !hasErrors
                && !closing && !IsStopped && ReferenceEquals(workflow, session))
            {
                overview.Visibility = Visibility.Visible;
                deployment.Visibility = selection.Visibility = Visibility.Collapsed;
                Notice(L10n.Get("Common_Applied"), InfoBarSeverity.Success);
            }
        }
        else if (result.State is GpuDeploymentState.Cancelled or GpuDeploymentState.Failed or GpuDeploymentState.CleanupRequired)
        {
            try { await RefreshAssignmentsAsync(); }
            catch (InvalidOperationException error)
            {
                Notice(L10n.Format("Gpu_RefreshFailed", CoreMessageResolver.ResolveException(error)), InfoBarSeverity.Warning);
            }
            NotifyChanged();
        }
    }

    private void ApplySnapshot(GpuDeploymentSession source, GpuDeploymentSnapshot value)
    {
        if (IsStopped || !ReferenceEquals(source, workflow)) return;
        if (snapshot != null && value.Revision <= snapshot.Revision) return;
        snapshot = value;

        bool hasErrors = !string.IsNullOrWhiteSpace(value.Error) || !string.IsNullOrWhiteSpace(value.CleanupError);
        if (value.State is GpuDeploymentState.Failed or GpuDeploymentState.CleanupRequired or GpuDeploymentState.Cancelled || hasErrors)
        {
            string title = value.State switch
            {
                GpuDeploymentState.Failed => L10n.Get("Gpu_DeploymentFailed"),
                GpuDeploymentState.CleanupRequired => value.OutcomeUnknown ? L10n.Get("Gpu_OutcomeUnknown") : L10n.Get("Gpu_CleanupRequired"),
                GpuDeploymentState.Cancelled => L10n.Get("Gpu_DeploymentCancelled"),
                _ => L10n.Get("Gpu_StatusNotice")
            };
            var severity = value.State switch
            {
                GpuDeploymentState.Failed or GpuDeploymentState.CleanupRequired => InfoBarSeverity.Error,
                GpuDeploymentState.Cancelled => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational
            };
            var errors = new[] { value.Error, value.CleanupError }.Where(text => !string.IsNullOrWhiteSpace(text)).Distinct();
            Notice(string.Join(Environment.NewLine, errors.Prepend(title)), severity);
        }

        RenderTaskSteps(value);

        RefreshActions();
    }

    private void RenderTaskSteps(GpuDeploymentSnapshot value)
    {
        bool showActiveProgress = value.State == GpuDeploymentState.Running
            && value.StepProgressPercent.HasValue
            && value.Steps.Any(s => s.Status == GpuDeploymentStepStatus.Running
                && s.Type is GpuDeploymentStepKind.DownloadBundle or GpuDeploymentStepKind.SynthesizeBundle);

        string structureSignature = string.Join("|", value.Steps.Select(s =>
            $"{s.Type}:{s.Name}:{s.Status}:{value.IsDriverSkipped}"))
            + $"#state={value.State}#prog={showActiveProgress}#dec={value.PendingDecision?.Id}:{value.PendingDecision?.Kind}:{value.PendingDecision?.Message}#cmd={!string.IsNullOrWhiteSpace(value.GuestSetupCommand)}#berr={value.BundleError}";

        if (string.Equals(structureSignature, renderedStructureSignature, StringComparison.Ordinal))
        {
            if (showActiveProgress && activeStepProgressBar != null && activeStepProgressText != null && value.StepProgressPercent is int livePct)
            {
                var runningStep = value.Steps.FirstOrDefault(s => s.Status == GpuDeploymentStepStatus.Running);
                activeStepProgressBar.Value = livePct;
                activeStepProgressText.Text = runningStep?.Description ?? $"{livePct}%";
            }
            return;
        }

        renderedStructureSignature = structureSignature;
        renderedStepsSignature = structureSignature;
        activeStepProgressBar = null;
        activeStepProgressText = null;

        stepsPanel.Children.Clear();
        fallbackDecisionPanel.Children.Clear();
        fallbackDecisionHost.Visibility = Visibility.Collapsed;

        bool placedDecisionCard = false;
        var defaultBg = ThemeHelper.TryBrush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(13, 255, 255, 255));
        var activeBg = ThemeHelper.SubtleHighlightBrush(ActualTheme, 12, 10);
        var defaultStroke = ThemeHelper.TryBrush("CardStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255));

        foreach (GpuDeploymentStep step in value.Steps)
        {
            bool isSkipped = step.Type == GpuDeploymentStepKind.Driver && value.IsDriverSkipped;
            string displayLabel = step.Name;

            bool isActive = step.Status == GpuDeploymentStepStatus.Running;
            bool isDone = !isSkipped && step.Status == GpuDeploymentStepStatus.Success;
            bool isFailed = step.Status == GpuDeploymentStepStatus.Failed;
            bool isPending = !isActive && !isDone && !isSkipped && !isFailed;
            bool hasStepProgress = isActive
                && step.Type is GpuDeploymentStepKind.DownloadBundle or GpuDeploymentStepKind.SynthesizeBundle
                && value.StepProgressPercent.HasValue;

            var cardGrid = new Grid();
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var mainRow = new Grid
            {
                ColumnSpacing = 14,
                Margin = new Thickness(16, 12, 16, hasStepProgress ? 8 : 12)
            };
            mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            mainRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement statusIcon = DeviceWorkbench.BuildStepIcon(isSkipped, isDone, isFailed, isActive);
            var iconHost = new Grid { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Center };
            iconHost.Children.Add(statusIcon);
            Grid.SetColumn(iconHost, 0);
            mainRow.Children.Add(iconHost);

            var titleStack = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = displayLabel,
                FontSize = 14.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush("TextFillColorPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (isActive && value.State == GpuDeploymentState.WaitingForDecision
                && value.PendingDecision?.Kind is GpuDecisionKind.PowerOff or GpuDecisionKind.DriverFallback or GpuDecisionKind.SelectPartition or GpuDecisionKind.SelectLinuxBundle
                && !string.IsNullOrWhiteSpace(value.PendingDecision.Message)
                && string.IsNullOrWhiteSpace(value.BundleError))
            {
                titleStack.Children.Add(new TextBlock
                {
                    Text = value.PendingDecision.Message,
                    FontSize = 12,
                    Foreground = Brush("TextFillColorSecondaryBrush"),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            Grid.SetColumn(titleStack, 1);
            mainRow.Children.Add(titleStack);

            FrameworkElement? rightContent = null;
            if (isSkipped)
            {
                rightContent = new TextBlock
                {
                    Text = L10n.Get("Gpu_Skipped"),
                    FontSize = 12.5,
                    Foreground = Brush("TextFillColorSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            else if (hasStepProgress && value.StepProgressPercent is int pctVal)
            {
                var pctLabel = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(step.Description) ? $"{pctVal}%" : step.Description,
                    FontSize = 12.5,
                    Foreground = Brush("TextFillColorSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                activeStepProgressText = pctLabel;
                rightContent = pctLabel;
            }
            else if (isActive && value.State == GpuDeploymentState.WaitingForDecision && value.PendingDecision != null)
            {
                rightContent = BuildInlineStepActions(step, value.PendingDecision);
            }

            if (rightContent != null)
            {
                Grid.SetColumn(rightContent, 2);
                mainRow.Children.Add(rightContent);
            }

            Grid.SetRow(mainRow, 0);
            cardGrid.Children.Add(mainRow);

            if (hasStepProgress && value.StepProgressPercent is int barPct)
            {
                cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var bar = new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = barPct,
                    IsIndeterminate = false,
                    Height = 3,
                    Margin = new Thickness(16, 0, 16, 10)
                };
                activeStepProgressBar = bar;
                Grid.SetRow(bar, 1);
                cardGrid.Children.Add(bar);
            }

            var taskCard = new Border
            {
                Background = isActive ? activeBg : defaultBg,
                BorderBrush = isActive ? Brush("VmAccentBrush") : defaultStroke,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Opacity = isPending ? 0.62 : 1.0,
                Child = cardGrid
            };
            stepsPanel.Children.Add(taskCard);

            bool attachInlineDecision = isActive && value.State == GpuDeploymentState.WaitingForDecision && value.PendingDecision != null
                && ((step.Type == GpuDeploymentStepKind.Driver && value.PendingDecision.Kind == GpuDecisionKind.SelectPartition)
                    || (step.Type == GpuDeploymentStepKind.SelectBundle && value.PendingDecision.Kind == GpuDecisionKind.SelectLinuxBundle));
            if (attachInlineDecision)
            {
                UIElement decisionCard = BuildDecisionPanel(value.PendingDecision!, value);
                stepsPanel.Children.Add(decisionCard);
                placedDecisionCard = true;
            }
        }

        if (value.State == GpuDeploymentState.WaitingForDecision && value.PendingDecision != null && !placedDecisionCard
            && value.PendingDecision.Kind is GpuDecisionKind.SelectPartition or GpuDecisionKind.SelectLinuxBundle)
        {
            fallbackDecisionPanel.Children.Add(BuildDecisionPanel(value.PendingDecision, value));
            fallbackDecisionHost.Visibility = Visibility.Visible;
        }

        if (value.State == GpuDeploymentState.Completed && !string.IsNullOrWhiteSpace(value.GuestSetupCommand))
        {
            stepsPanel.Children.Add(BuildLinuxCompletionPanel(value.GuestSetupCommand));
        }
    }

    private UIElement BuildLinuxCompletionPanel(string command)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = L10n.Get("Gpu_LinuxGuestRunHint"),
            FontSize = 12.5,
            Foreground = Brush("TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        });

        var cmdInnerGrid = new Grid { ColumnSpacing = 6 };
        cmdInnerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cmdInnerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var cmdScroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock
            {
                Text = command,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
                FontSize = 12.5,
                Foreground = Brush("TextFillColorPrimaryBrush"),
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        cmdInnerGrid.Children.Add(cmdScroller);

        var copyIcon = new FontIcon
        {
            Glyph = "\uE8C8",
            FontFamily = AppFonts.SegoeFluent,
            FontSize = 13,
            Foreground = Brush("TextFillColorSecondaryBrush")
        };
        var copyBtn = new Button
        {
            Content = copyIcon,
            Width = 26,
            Height = 26,
            MinWidth = 26,
            MinHeight = 26,
            Padding = new Thickness(0),
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["VmSelectorButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        string copyTip = L10n.Get("Gpu_CopyCommand");
        ToolTipService.SetToolTip(copyBtn, copyTip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copyBtn, copyTip);
        copyBtn.Click += async (_, _) => await UiEvent.RunAsync(async () =>
        {
            if (!CopyTextToClipboard(command, L10n.Get("Gpu_CommandLabel"))) return;
            copyIcon.Glyph = "\uE73E";
            ToolTipService.SetToolTip(copyBtn, L10n.Get("Dev_Copied"));
            await Task.Delay(1600);
            copyIcon.Glyph = "\uE8C8";
            ToolTipService.SetToolTip(copyBtn, copyTip);
        });
        Grid.SetColumn(copyBtn, 1);
        cmdInnerGrid.Children.Add(copyBtn);

        var cmdBox = new Border
        {
            Background = Brush("VmKvWellBrush"),
            BorderBrush = Brush("VmKvWellBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 6, 6, 6),
            MinHeight = 36,
            Child = cmdInnerGrid
        };

        var finishBtn = new Button
        {
            Content = L10n.Get("Gpu_BackToList"),
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center
        };
        finishBtn.Click += async (_, _) => await ReturnFromDeploymentAsync();

        var cmdRow = new Grid { ColumnSpacing = 12 };
        cmdRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        cmdRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cmdRow.Children.Add(cmdBox);
        Grid.SetColumn(finishBtn, 1);
        cmdRow.Children.Add(finishBtn);

        stack.Children.Add(cmdRow);

        return new Border
        {
            Background = ThemeHelper.SubtleHighlightBrush(ActualTheme, 6, 5),
            BorderBrush = ThemeHelper.TryBrush("CardStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 2, 0, 4),
            Child = stack
        };
    }

    private void RefreshActions()
    {
        var actions = snapshot?.AllowedActions ?? [];
        bool busy = working || closing || IsBusy;
        bool terminal = snapshot == null || CanReleaseWorkflow();
        cancel.Visibility = actions.Contains(GpuDeploymentAction.Cancel)
            ? Visibility.Visible
            : Visibility.Collapsed;
        cancel.IsEnabled = actions.Contains(GpuDeploymentAction.Cancel) && !closing && !cancellationRequested;

        reconcile.Visibility = actions.Contains(GpuDeploymentAction.Reconcile) ? Visibility.Visible : Visibility.Collapsed;
        reconcile.IsEnabled = !busy && actions.Contains(GpuDeploymentAction.Reconcile);

        retryCleanup.Visibility = actions.Contains(GpuDeploymentAction.RetryCleanup) ? Visibility.Visible : Visibility.Collapsed;
        retryCleanup.IsEnabled = !busy && actions.Contains(GpuDeploymentAction.RetryCleanup);

        bool hasLinuxCompletion = snapshot?.State == GpuDeploymentState.Completed
            && !string.IsNullOrWhiteSpace(snapshot.GuestSetupCommand);
        bool canReturnToList = !hasLinuxCompletion && terminal
            && snapshot?.State is GpuDeploymentState.Completed or GpuDeploymentState.Cancelled or GpuDeploymentState.Failed;
        done.Visibility = canReturnToList ? Visibility.Visible : Visibility.Collapsed;
        done.IsEnabled = !busy && canReturnToList;

        bool hasFooterAction = cancel.Visibility == Visibility.Visible
            || reconcile.Visibility == Visibility.Visible
            || retryCleanup.Visibility == Visibility.Visible
            || done.Visibility == Visibility.Visible;
        footerActions.Visibility = hasFooterAction ? Visibility.Visible : Visibility.Collapsed;

        bool runningStep = working || snapshot?.State == GpuDeploymentState.Running;
        overview.IsEnabled = !busy;
        selection.IsEnabled = !busy;
        stepsHost.IsEnabled = !busy;
        fallbackDecisionHost.IsEnabled = !busy;
        UpdateBeginSelectionState();
        SetNestedBusy(false);
        NotifyProgressBar(runningStep);
    }

    private bool CanReleaseWorkflow() => snapshot == null ||
        (snapshot.State is GpuDeploymentState.Completed or GpuDeploymentState.Cancelled or GpuDeploymentState.Failed
        && !snapshot.OutcomeUnknown && !snapshot.HasPendingCleanup);

    private void EnsureNoUnfinishedDeployment()
    {
        if (unconfirmedRemovals.TryGetValue(VmId, out var removal)) throw new InvalidOperationException(removal);
        if (!CanReleaseWorkflow()) throw new InvalidOperationException(L10n.Get("Gpu_UnfinishedDeployment"));
    }

    private async Task CancelDeploymentAsync()
    {
        var session = workflow;
        if (session == null || closing || IsStopped || cancellationRequested ||
            snapshot?.AllowedActions.Contains(GpuDeploymentAction.Cancel) != true) return;
        cancellationRequested = true;
        RefreshActions();
        session.RequestCancellation();
        await operation;
        if (!closing && !IsStopped && ReferenceEquals(workflow, session))
            await RunGpuOperationAsync(async () => await FinishStepAsync(session, await Task.Run(session.CancelAsync)));
    }

    private async Task PerformSessionActionAsync(GpuDeploymentAction action)
    {
        if (workflow == null) return;
        var session = workflow;
        GpuDeploymentSnapshot next = action switch
        {
            GpuDeploymentAction.Reconcile => await Task.Run(session.ReconcileAsync),
            GpuDeploymentAction.RetryCleanup => await Task.Run(session.RetryCleanupAsync),
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        await FinishStepAsync(session, next);
    }

    public override async Task StopAsync()
    {
        await stopGate.WaitAsync();
        closing = true;
        bundleInspectCts?.Cancel();
        bundleInspectCts?.Dispose();
        bundleInspectCts = null;
        RefreshActions();
        try
        {
            if (workflow != null && !CanReleaseWorkflow())
            {
                cancellationRequested = true;
                workflow.RequestCancellation();
                var session = workflow;
                var final = await Task.Run(session.CancelAsync);
                ApplySnapshot(session, final);
                if (final.State == GpuDeploymentState.CleanupRequired)
                    throw new InvalidOperationException(L10n.Get("Gpu_CleanupPending"));
            }
            try { await operation; }
            catch (OperationCanceledException) when (cancellationRequested || CancellationToken.IsCancellationRequested)
            {
                // Shutdown requested cancellation and still needs to dispose the query.
            }
            await base.StopAsync();
            await query.DisposeAsync();
        }
        finally
        {
            closing = false;
            RefreshActions();
            stopGate.Release();
        }
    }

}
