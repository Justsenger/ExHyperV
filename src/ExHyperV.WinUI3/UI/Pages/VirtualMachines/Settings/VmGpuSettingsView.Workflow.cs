using System.Text;
using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmGpuSettingsView
{
    private readonly StackPanel deployment = new() { Spacing = 16, Visibility = Visibility.Collapsed };
    private readonly TextBlock deploymentTitle = Label("GPU 部署", 18);
    private readonly InfoBar deploymentState = new() { IsOpen = true, IsClosable = false };
    private readonly StackPanel steps = new() { Spacing = 8 };
    private readonly StackPanel decisions = new() { Spacing = 12 };
    private readonly ContentControl decisionsContent = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button cancel = new() { Content = "取消部署" };
    private readonly Button reconcile = new() { Content = "核实原操作" };
    private readonly Button retryCleanup = new() { Content = "重试清理" };
    private readonly Button done = new() { Content = "返回显卡列表" };
    private readonly Button restart = new() { Content = "重新选择显卡" };
    private readonly TextBox log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 180, MaxHeight = 320, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
    private readonly TextBox recoveryText = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 320 };
    private readonly Expander recoveryDetails = new() { Header = "恢复详情", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly SortedDictionary<long, string> logEntries = new();
    private GpuDeploymentSession? workflow;
    private GpuDeploymentSnapshot? snapshot;
    private Guid? displayedDecision;
    private bool cancellationRequested;

    private UIElement BuildDeploymentPanel()
    {
        deployment.Children.Add(deploymentTitle);
        deployment.Children.Add(deploymentState);
        deployment.Children.Add(steps);
        decisionsContent.Content = decisions;
        deployment.Children.Add(decisionsContent);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var button in new[] { cancel, reconcile, retryCleanup, done, restart }) actions.Children.Add(button);
        deployment.Children.Add(actions);
        recoveryDetails.Content = recoveryText;
        deployment.Children.Add(recoveryDetails);
        var logHeading = new Grid();
        logHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        logHeading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = Label("配置日志");
        heading.VerticalAlignment = VerticalAlignment.Center;
        logHeading.Children.Add(heading);
        var copy = new Button { Content = "复制日志与恢复详情" };
        Grid.SetColumn(copy, 1);
        logHeading.Children.Add(copy);
        deployment.Children.Add(logHeading);
        deployment.Children.Add(log);
        copy.Click += (_, _) => CopyReport();
        cancel.Click += async (_, _) => await CancelDeploymentAsync();
        reconcile.Click += async (_, _) => await RunGpuOperationAsync(() => PerformSessionActionAsync(GpuDeploymentAction.Reconcile));
        retryCleanup.Click += async (_, _) => await RunGpuOperationAsync(() => PerformSessionActionAsync(GpuDeploymentAction.RetryCleanup));
        done.Click += async (_, _) => await RunGpuOperationAsync(async () =>
        {
            EnsureNoUnfinishedDeployment();
            await RefreshAssignmentsAsync();
            overview.Visibility = Visibility.Visible;
            deployment.Visibility = selection.Visibility = Visibility.Collapsed;
        });
        restart.Click += async (_, _) => await RunGpuOperationAsync(ShowSelectionAsync);
        return deployment;
    }

    private async Task StartDeploymentAsync(HostGpuInfo gpu, GpuDeploymentMode mode)
    {
        EnsureNoUnfinishedDeployment();
        var identity = await QueryAsync(VmIdentityFields.Name);
        var name = identity.Get(VmIdentityFields.Name);
        if (!name.HasValue) throw new InvalidOperationException(name.Error ?? "无法读取虚拟机名称。");
        if (closing || IsStopped) return;
        var session = new GpuDeploymentSession(VmId, name.Value ?? throw new InvalidOperationException("虚拟机名称为空。"), gpu, service, query, mode);
        workflow = session;
        snapshot = null;
        displayedDecision = null;
        cancellationRequested = false;
        logEntries.Clear();
        log.Text = string.Empty;
        recoveryText.Text = string.Empty;
        decisions.Children.Clear();
        recoveryDetails.Visibility = Visibility.Collapsed;
        deploymentTitle.Text = $"{(mode == GpuDeploymentMode.UpdateDrivers ? "更新驱动" : "配置显卡")} · {gpu.Name}";
        overview.Visibility = selection.Visibility = Visibility.Collapsed;
        deployment.Visibility = Visibility.Visible;
        ApplySnapshot(session, session.GetStatus());
        // Core 可同步调用底层能力；整步在后台执行，通知只排队，不阻塞 Core。
        var result = await Task.Run(() => session.StartAsync(value =>
        {
            if (!DispatcherQueue.TryEnqueue(() => ApplySnapshot(session, value))) session.RequestCancellation();
        }));
        await FinishStepAsync(session, result);
    }

    private async Task FinishStepAsync(GpuDeploymentSession session, GpuDeploymentSnapshot result)
    {
        if (!ReferenceEquals(session, workflow)) return;
        ApplySnapshot(session, result);
        if (result.State is GpuDeploymentState.Completed or GpuDeploymentState.Cancelled or GpuDeploymentState.Failed or GpuDeploymentState.CleanupRequired)
        {
            NotifyChanged();
            try { await RefreshAssignmentsAsync(); }
            catch (Exception ex) { Notice($"GPU 清单刷新失败：{ex.Message}", InfoBarSeverity.Warning); }
        }
    }

    private void ApplySnapshot(GpuDeploymentSession source, GpuDeploymentSnapshot value)
    {
        if (IsStopped || !ReferenceEquals(source, workflow)) return;
        // 最终返回值可能先于队列里的通知抵达。旧快照的日志仍需保存，避免漏掉警告。
        if (!string.IsNullOrWhiteSpace(value.LastLog) && !logEntries.ContainsKey(value.Revision))
        {
            logEntries[value.Revision] = value.LastLog;
            log.Text = string.Join(Environment.NewLine, logEntries.Values);
        }
        if (snapshot != null && value.Revision <= snapshot.Revision) return;
        snapshot = value;
        deploymentState.Title = value.State switch
        {
            GpuDeploymentState.Created => "准备开始",
            GpuDeploymentState.Running => cancellationRequested ? "正在安全停止" : "正在配置",
            GpuDeploymentState.WaitingForDecision => "等待确认",
            GpuDeploymentState.Completed => "部署完成",
            GpuDeploymentState.Failed => "部署失败",
            GpuDeploymentState.Cancelled => "部署已取消",
            GpuDeploymentState.CleanupRequired => value.OutcomeUnknown ? "原操作结果尚未确认" : "仍有资源需要清理",
            _ => "未识别的部署状态"
        };
        deploymentState.Severity = value.State switch
        {
            GpuDeploymentState.Completed => InfoBarSeverity.Success,
            GpuDeploymentState.Failed or GpuDeploymentState.CleanupRequired => InfoBarSeverity.Error,
            GpuDeploymentState.Cancelled => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational
        };
        var errors = new[] { value.Error, value.CleanupError }.Where(text => !string.IsNullOrWhiteSpace(text)).Distinct();
        deploymentState.Message = string.Join(Environment.NewLine, errors);
        if (value.State == GpuDeploymentState.Cancelled)
            deploymentState.Message = "已完成的策略、MMIO、文件与电源修改保留；本次临时资源按 Core 的取消流程收尾。";
        else if (value.State == GpuDeploymentState.CleanupRequired)
            deploymentState.Message += "\n请在此页核实或清理。结果未知时不会重新执行部署或擅自删除资源。";
        else if (value.State == GpuDeploymentState.Completed)
            deploymentState.Message = "执行详情与警告保留在下方配置日志中。";

        steps.Children.Clear();
        foreach (var step in value.Steps)
        {
            var stateText = step.Status switch
            {
                GpuDeploymentStepStatus.Pending => "等待",
                GpuDeploymentStepStatus.Running => "进行中",
                GpuDeploymentStepStatus.Success => "完成",
                GpuDeploymentStepStatus.Failed => "未完成",
                _ => "未知"
            };
            var body = new StackPanel { Spacing = 4, Margin = new Thickness(16, 12, 16, 12) };
            body.Children.Add(Label($"{step.Name} · {stateText}"));
            if (!string.IsNullOrWhiteSpace(step.Description)) body.Children.Add(Label(step.Description, 12, secondary: true));
            steps.Children.Add(new Border { Background = Brush("CardBackgroundFillColorDefaultBrush"), CornerRadius = new CornerRadius(8), Child = body });
        }

        recoveryText.Text = FormatRecovery(value);
        recoveryDetails.Visibility = value.Recovery != null || value.State == GpuDeploymentState.CleanupRequired
            ? Visibility.Visible : Visibility.Collapsed;
        if (value.State == GpuDeploymentState.WaitingForDecision && value.PendingDecision is { } request)
        {
            if (displayedDecision != request.Id)
            {
                displayedDecision = request.Id;
                RenderDecision(request);
            }
        }
        else
        {
            displayedDecision = null;
            decisions.Children.Clear();
        }
        RefreshActions();
    }

    private void RefreshActions()
    {
        var idle = !working && !closing && !IsStopped;
        workingProgress.Visibility = working ? Visibility.Visible : Visibility.Collapsed;
        overviewContent.IsEnabled = idle;
        selectionContent.IsEnabled = idle;
        decisionsContent.IsEnabled = idle && snapshot?.AllowedActions.Contains(GpuDeploymentAction.SubmitDecision) == true;
        add.IsEnabled = idle && !removalOutcomeUnknown;
        begin.IsEnabled = idle && hosts.SelectedItem is ListViewItem { Tag: HostGpuInfo } && !removalOutcomeUnknown;
        refresh.IsEnabled = idle;
        var actions = snapshot?.AllowedActions ?? [];
        cancel.Visibility = actions.Contains(GpuDeploymentAction.Cancel) ? Visibility.Visible : Visibility.Collapsed;
        cancel.IsEnabled = !closing && !cancellationRequested && !IsStopped;
        reconcile.Visibility = actions.Contains(GpuDeploymentAction.Reconcile) ? Visibility.Visible : Visibility.Collapsed;
        reconcile.IsEnabled = idle;
        retryCleanup.Visibility = actions.Contains(GpuDeploymentAction.RetryCleanup) ? Visibility.Visible : Visibility.Collapsed;
        retryCleanup.IsEnabled = idle;
        done.Visibility = restart.Visibility = CanReleaseWorkflow() ? Visibility.Visible : Visibility.Collapsed;
        done.IsEnabled = restart.IsEnabled = idle;
    }

    private bool CanReleaseWorkflow() => snapshot == null ||
        (snapshot.State is GpuDeploymentState.Completed or GpuDeploymentState.Cancelled or GpuDeploymentState.Failed
         && !snapshot.OutcomeUnknown && !snapshot.HasPendingCleanup);

    private void EnsureNoUnfinishedDeployment()
    {
        if (!CanReleaseWorkflow()) throw new InvalidOperationException("请先完成、取消或清理当前 GPU 部署。");
        if (removalOutcomeUnknown) throw new InvalidOperationException("先前 GPU 移除操作的结果尚未确认，不能开始新的修改。");
    }

    private Task CancelDeploymentAsync()
    {
        if (closing || workflow == null || cancellationRequested || !workflow.GetStatus().AllowedActions.Contains(GpuDeploymentAction.Cancel))
            return Task.CompletedTask;
        cancellationRequested = true;
        workflow.RequestCancellation();
        deploymentState.Title = "正在安全停止";
        deploymentState.Message = "等待当前底层操作结束并清理本次临时资源。";
        RefreshActions();
        if (working) return operation;
        return RunGpuOperationAsync(() => PerformSessionActionAsync(GpuDeploymentAction.Cancel));
    }

    private async Task PerformSessionActionAsync(GpuDeploymentAction action)
    {
        var session = workflow;
        if (session == null || !session.GetStatus().AllowedActions.Contains(action))
            throw new InvalidOperationException("当前部署状态不允许此操作，请根据最新状态继续。");
        var result = action switch
        {
            GpuDeploymentAction.Cancel => await Task.Run(session.CancelAsync),
            GpuDeploymentAction.Reconcile => await Task.Run(session.ReconcileAsync),
            GpuDeploymentAction.RetryCleanup => await Task.Run(session.RetryCleanupAsync),
            _ => throw new InvalidOperationException("不支持此部署操作。")
        };
        await FinishStepAsync(session, result);
    }

    public override async Task StopAsync()
    {
        await stopGate.WaitAsync();
        try
        {
            if (IsStopped) return;
            closing = true;
            workflow?.RequestCancellation();
            RefreshActions();
            await operation;
            if (workflow is { } session)
            {
                var current = await Task.Run(session.CancelAsync);
                ApplySnapshot(session, current);
                if (!CanReleaseWorkflow())
                {
                    deployment.Visibility = Visibility.Visible;
                    selection.Visibility = overview.Visibility = Visibility.Collapsed;
                    throw new InvalidOperationException(current.CleanupError ?? current.Error ?? "GPU 部署尚未安全结束，请先核实或清理，恢复详情已保留。");
                }
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

    private static string FormatRecovery(GpuDeploymentSnapshot value)
    {
        var text = new StringBuilder();
        text.AppendLine($"部署编号：{value.SessionId}");
        text.AppendLine($"状态：{value.State}；结果未知：{value.OutcomeUnknown}");
        if (!string.IsNullOrWhiteSpace(value.AddedAdapterId)) text.AppendLine($"本次新增适配器：{value.AddedAdapterId}");
        if (!string.IsNullOrWhiteSpace(value.PendingCleanupAdapterId)) text.AppendLine($"待清理适配器：{value.PendingCleanupAdapterId}");
        if (!string.IsNullOrWhiteSpace(value.Error)) text.AppendLine(value.Error);
        if (!string.IsNullOrWhiteSpace(value.CleanupError)) text.AppendLine(value.CleanupError);
        if (value.Recovery != null)
            text.AppendLine(JsonSerializer.Serialize(value.Recovery, new JsonSerializerOptions { WriteIndented = true }));
        return text.ToString();
    }

    private void CopyReport()
    {
        try
        {
            var data = new DataPackage();
            data.SetText(log.Text + Environment.NewLine + recoveryText.Text);
            Clipboard.SetContent(data);
            Clipboard.Flush();
        }
        catch (Exception ex) { Notice($"复制失败：{ex.Message}", InfoBarSeverity.Error); }
    }
}
