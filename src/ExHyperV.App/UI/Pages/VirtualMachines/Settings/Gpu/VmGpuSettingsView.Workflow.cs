using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
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
    private TextBlock? activeIsoPctText;
    private ColumnDefinition? activeIsoFillCol;
    private ColumnDefinition? activeIsoTrackCol;

    private UIElement BuildDeploymentPanel()
    {
        cancel.Click += async (_, _) => await CancelDeploymentAsync();
        reconcile.Click += async (_, _) => await RunGpuOperationAsync(() => PerformSessionActionAsync(GpuDeploymentAction.Reconcile));
        retryCleanup.Click += async (_, _) => await RunGpuOperationAsync(() => PerformSessionActionAsync(GpuDeploymentAction.RetryCleanup));
        done.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
        done.Click += async (_, _) => await RunGpuOperationAsync(async () =>
        {
            EnsureNoUnfinishedDeployment();
            await RefreshAssignmentsAsync();
            overview.Visibility = Visibility.Visible;
            deployment.Visibility = selection.Visibility = Visibility.Collapsed;
        });

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
        activeIsoPctText = null;
        activeIsoFillCol = null;
        activeIsoTrackCol = null;
        fallbackDecisionPanel.Children.Clear();
        fallbackDecisionHost.Visibility = Visibility.Collapsed;

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
            try { await RefreshAssignmentsAsync(); }
            catch (Exception ex) { Notice(L10n.Format("Gpu_RefreshFailed", CoreMessageResolver.ResolveException(ex)), InfoBarSeverity.Warning); }
            NotifyChanged();
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
        string stepsSignature = string.Join("|", value.Steps.Select(s =>
        {
            bool isIso = s.Type == GpuDeploymentStepKind.Driver
                && (value.IsDriverIsoMounted || value.DriverIsoProgressPercent.HasValue);
            bool hasPct = isIso && s.Status == GpuDeploymentStepStatus.Running && value.DriverIsoProgressPercent.HasValue;
            return $"{s.Type}:{s.Name}:{s.Status}:{isIso}:{value.IsDriverSkipped}:{hasPct}";
        })) + $"#state={value.State}#dec={value.PendingDecision?.Id}:{value.PendingDecision?.Kind}";

        if (string.Equals(stepsSignature, renderedStepsSignature, StringComparison.Ordinal)
            && value.DriverIsoProgressPercent.HasValue
            && activeIsoPctText != null
            && activeIsoFillCol != null
            && activeIsoTrackCol != null)
        {
            int pct = Math.Clamp(value.DriverIsoProgressPercent.Value, 0, 100);
            activeIsoPctText.Text = $"{pct}%";
            activeIsoFillCol.Width = new GridLength(pct, GridUnitType.Star);
            activeIsoTrackCol.Width = new GridLength(Math.Max(0, 100 - pct), GridUnitType.Star);
            return;
        }

        renderedStepsSignature = stepsSignature;
        activeIsoPctText = null;
        activeIsoFillCol = null;
        activeIsoTrackCol = null;

        stepsPanel.Children.Clear();
        fallbackDecisionPanel.Children.Clear();
        fallbackDecisionHost.Visibility = Visibility.Collapsed;

        bool placedDecisionCard = false;
        var defaultBg = ThemeHelper.TryBrush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(13, 255, 255, 255));
        var activeBg = ThemeHelper.SubtleHighlightBrush(ActualTheme, 12, 10);
        var defaultStroke = ThemeHelper.TryBrush("CardStrokeColorDefaultBrush", Color.FromArgb(20, 255, 255, 255));

        foreach (GpuDeploymentStep step in value.Steps)
        {
            bool isIsoStep = step.Type == GpuDeploymentStepKind.Driver
                && (value.IsDriverIsoMounted || value.DriverIsoProgressPercent.HasValue);
            bool isSkipped = step.Type == GpuDeploymentStepKind.Driver && value.IsDriverSkipped;
            string displayLabel = isIsoStep ? L10n.Get("Gpu_MountDriverDisc") : step.Name;

            bool isActive = step.Status == GpuDeploymentStepStatus.Running;
            bool isDone = !isSkipped && step.Status == GpuDeploymentStepStatus.Success;
            bool isFailed = step.Status == GpuDeploymentStepStatus.Failed;
            bool isPending = !isActive && !isDone && !isSkipped && !isFailed;
            bool showIsoProgress = value.State == GpuDeploymentState.Running && isIsoStep && isActive && value.DriverIsoProgressPercent.HasValue;

            var cardGrid = new Grid();
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var mainRow = new Grid
            {
                ColumnSpacing = 14,
                Margin = new Thickness(16, 12, 16, 12)
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
                && value.PendingDecision?.Kind is GpuDecisionKind.PowerOff or GpuDecisionKind.DriverFallback
                && !string.IsNullOrWhiteSpace(value.PendingDecision.Message))
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
            else if (showIsoProgress)
            {
                int pct = Math.Clamp(value.DriverIsoProgressPercent!.Value, 0, 100);
                var pctBlock = new TextBlock
                {
                    Text = $"{pct}%",
                    FontSize = 12.5,
                    Foreground = Brush("TextFillColorSecondaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                Microsoft.UI.Xaml.Documents.Typography.SetNumeralAlignment(pctBlock, FontNumeralAlignment.Tabular);
                activeIsoPctText = pctBlock;
                rightContent = pctBlock;
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

            if (showIsoProgress)
            {
                int pct = Math.Clamp(value.DriverIsoProgressPercent!.Value, 0, 100);
                var fillCol = new ColumnDefinition { Width = new GridLength(pct, GridUnitType.Star) };
                var trackCol = new ColumnDefinition { Width = new GridLength(Math.Max(0, 100 - pct), GridUnitType.Star) };
                activeIsoFillCol = fillCol;
                activeIsoTrackCol = trackCol;

                var progressTrack = new Grid
                {
                    Height = 3,
                    Background = new SolidColorBrush(Color.FromArgb(15, 255, 255, 255))
                };
                progressTrack.ColumnDefinitions.Add(fillCol);
                progressTrack.ColumnDefinitions.Add(trackCol);
                var fillBar = new Border
                {
                    Background = Brush("VmAccentBrush"),
                    CornerRadius = new CornerRadius(0, 0, 0, 5)
                };
                Grid.SetColumn(fillBar, 0);
                progressTrack.Children.Add(fillBar);

                Grid.SetRow(progressTrack, 1);
                cardGrid.Children.Add(progressTrack);
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

            if (isActive && value.State == GpuDeploymentState.WaitingForDecision && value.PendingDecision != null
                && step.Type == GpuDeploymentStepKind.Driver
                && value.PendingDecision.Kind == GpuDecisionKind.SelectPartition)
            {
                UIElement decisionCard = BuildDecisionPanel(value.PendingDecision);
                stepsPanel.Children.Add(decisionCard);
                placedDecisionCard = true;
            }
        }

        if (value.State == GpuDeploymentState.WaitingForDecision && value.PendingDecision != null && !placedDecisionCard
            && value.PendingDecision.Kind == GpuDecisionKind.SelectPartition)
        {
            fallbackDecisionPanel.Children.Add(BuildDecisionPanel(value.PendingDecision));
            fallbackDecisionHost.Visibility = Visibility.Visible;
        }
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

        bool canReturnToList = terminal && snapshot?.State is GpuDeploymentState.Completed or GpuDeploymentState.Cancelled or GpuDeploymentState.Failed;
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
