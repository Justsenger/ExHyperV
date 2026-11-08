using System.Collections.Concurrent;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>GPU 分配与部署的界面适配；部署步骤、判定与恢复均由 Core 会话提供。</summary>
public sealed partial class VmGpuSettingsView : VmSettingsView
{
    // 页面重建不能证明原生作业已经结束。保留当前进程内的未知回执，不以重新查询替代核实。
    private sealed record UnconfirmedRemoval(string Message, string Details);
    private static readonly ConcurrentDictionary<Guid, UnconfirmedRemoval> unconfirmedRemovals = new();
    private readonly VmQueryService query = new();
    private readonly VmGpuService service;
    private readonly StackPanel overview = new() { Spacing = 12 };
    private readonly StackPanel assignments = new() { Spacing = 12 };
    private readonly StackPanel selection = new() { Spacing = 16, Visibility = Visibility.Collapsed };
    private readonly ContentControl overviewContent = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl selectionContent = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ListView hosts = new() { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 420 };
    private readonly ToggleSwitch installDrivers = new() { IsOn = true, OnContent = "", OffContent = "" };
    private readonly Button add = new() { Content = "添加显卡" };
    private readonly Button refresh = new() { Content = "刷新" };
    private readonly Button begin = new() { Content = "开始配置", IsEnabled = false };
    private readonly Button selectionBack = new() { Content = "返回显卡列表" };
    private readonly InfoBar operationNotice = new() { IsClosable = true, IsOpen = false };
    private readonly TextBox lastOperationDetails = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 260 };
    private readonly Expander lastOperation = new() { Header = "操作详情", Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ProgressBar workingProgress = new() { Height = 2, IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly SemaphoreSlim stopGate = new(1, 1);
    private Task operation = Task.CompletedTask;
    private bool working;
    private bool closing;
    private bool removalOutcomeUnknown;
    private IReadOnlyList<LinuxGpuDeploymentScript> scripts = [];

    public VmGpuSettingsView(Guid vmId) : base(vmId)
    {
        service = new VmGpuService(query);
        var panel = new StackPanel { Margin = new Thickness(24, 4, 24, 24), Spacing = 16 };
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        add.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
        toolbar.Children.Add(add);
        toolbar.Children.Add(refresh);
        overview.Children.Add(toolbar);
        overview.Children.Add(assignments);
        lastOperation.Content = lastOperationDetails;
        overview.Children.Add(lastOperation);

        selection.Children.Add(Label("选择宿主显卡", 18));
        selection.Children.Add(hosts);
        selection.Children.Add(Card(Row("安装驱动", "配置 GPU 分区后，继续选择来宾系统并部署驱动", installDrivers)));
        var selectionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        begin.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
        selectionActions.Children.Add(begin);
        selectionActions.Children.Add(selectionBack);
        selection.Children.Add(selectionActions);

        panel.Children.Add(workingProgress);
        panel.Children.Add(operationNotice);
        overviewContent.Content = overview;
        selectionContent.Content = selection;
        panel.Children.Add(overviewContent);
        panel.Children.Add(selectionContent);
        panel.Children.Add(BuildDeploymentPanel());
        SetBody(panel);
        if (unconfirmedRemovals.TryGetValue(vmId, out var previousRemoval))
        {
            removalOutcomeUnknown = true;
            lastOperationDetails.Text = previousRemoval.Details;
            lastOperation.Visibility = Visibility.Visible;
            Notice(previousRemoval.Message, InfoBarSeverity.Error);
        }

        add.Click += async (_, _) => await RunGpuOperationAsync(ShowSelectionAsync);
        refresh.Click += async (_, _) => await RunGpuOperationAsync(RefreshAssignmentsAsync);
        selectionBack.Click += (_, _) => ShowOverview();
        hosts.SelectionChanged += (_, _) => RefreshActions();
        begin.Click += async (_, _) =>
        {
            if (hosts.SelectedItem is ListViewItem { Tag: HostGpuInfo gpu })
                await RunGpuOperationAsync(() => StartDeploymentAsync(gpu,
                    installDrivers.IsOn ? GpuDeploymentMode.AddWithDrivers : GpuDeploymentMode.AddWithoutDrivers));
        };
    }

    protected override async Task LoadAsync()
    {
        await RefreshAssignmentsAsync();
        RefreshActions();
    }

    private async Task RefreshAssignmentsAsync()
    {
        var result = await Task.Run(() => service.GetVmGpuAssignmentsAsync(VmId));
        if (!result.HasData) throw new InvalidOperationException(result.Error);
        if (IsStopped) return;
        assignments.Children.Clear();
        foreach (var gpu in result.Data!) assignments.Children.Add(AssignmentCard(gpu));
        if (result.Data!.Count == 0)
            assignments.Children.Add(Label("未分配显卡", 14, secondary: true));
    }

    private UIElement AssignmentCard(VmGpuAssignment gpu)
    {
        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new FontIcon { Glyph = "\uF211", FontSize = 32, VerticalAlignment = VerticalAlignment.Center });
        var name = Label(gpu.Name, 16);
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 1);
        heading.Children.Add(name);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var update = new Button { Content = "更新驱动", IsEnabled = !string.IsNullOrWhiteSpace(gpu.HostDeviceInstanceId) && !removalOutcomeUnknown };
        var remove = new Button { Content = "移除", IsEnabled = !removalOutcomeUnknown };
        update.Click += async (_, _) => await RunGpuOperationAsync(() => UpdateDriversAsync(gpu));
        remove.Click += async (_, _) => await RunGpuOperationAsync(() => RemoveAssignmentAsync(gpu));
        buttons.Children.Add(update);
        buttons.Children.Add(remove);
        Grid.SetColumn(buttons, 2);
        heading.Children.Add(buttons);
        var details = new StackPanel { Spacing = 10 };
        details.Children.Add(ValueRow("制造商", gpu.Vendor));
        details.Children.Add(ValueRow("宿主专用显存", gpu.MemoryBytes is { } bytes ? $"{bytes / (1024.0 * 1024):N0} MB" : "未取得"));
        details.Children.Add(ValueRow("驱动版本", gpu.DriverVersion));
        details.Children.Add(ValueRow("设备实例", gpu.HostDeviceInstanceId));
        return new Expander
        {
            Header = heading, Content = details, IsExpanded = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
    }

    private async Task ShowSelectionAsync()
    {
        EnsureNoUnfinishedDeployment();
        var result = await Task.Run(HostGpuService.GetHostGpusAsync);
        if (!result.HasData) throw new InvalidOperationException(result.Error);
        hosts.Items.Clear();
        foreach (var gpu in result.Data!)
        {
            var content = new StackPanel { Spacing = 6, Margin = new Thickness(8) };
            content.Children.Add(Label(gpu.Name, 16));
            content.Children.Add(Label(gpu.InstanceId, 12, secondary: true));
            content.Children.Add(Label($"驱动 {gpu.DriverVersion}  ·  {(gpu.IsPartitionable ? "已发现可分区资源" : "未发现可分区资源")}", 12, secondary: true));
            hosts.Items.Add(new ListViewItem { Tag = gpu, Content = content, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        if (result.Data!.Count == 0) Notice("未找到宿主显卡。", InfoBarSeverity.Informational);
        hosts.SelectedItem = null;
        overview.Visibility = deployment.Visibility = Visibility.Collapsed;
        selection.Visibility = Visibility.Visible;
        await LoadScriptsAsync();
    }

    private async Task LoadScriptsAsync()
    {
        scripts = await Task.Run(service.GetAvailableScriptsAsync);
        // 在线脚本入口沿用现有 Core 合同；空清单不等于确认没有 Linux 方案。
        if (scripts.Count == 0)
            Notice("未取得 Linux 部署脚本清单，可能是网络或索引不可用。Windows 驱动部署不受影响。", InfoBarSeverity.Warning);
    }

    private async Task UpdateDriversAsync(VmGpuAssignment assignment)
    {
        EnsureNoUnfinishedDeployment();
        var result = await Task.Run(HostGpuService.GetHostGpusAsync);
        if (!result.HasData) throw new InvalidOperationException(result.Error);
        var gpu = result.Data!.FirstOrDefault(item => string.Equals(item.InstanceId,
            assignment.HostDeviceInstanceId, StringComparison.OrdinalIgnoreCase));
        if (gpu == null) throw new InvalidOperationException("没有找到此分配对应的宿主显卡，请刷新分配信息后再试。");
        await LoadScriptsAsync();
        await StartDeploymentAsync(gpu, GpuDeploymentMode.UpdateDrivers);
    }

    private async Task RemoveAssignmentAsync(VmGpuAssignment assignment)
    {
        ApiResponse result;
        try { result = await Task.Run(() => service.RemoveGpuPartitionAsync(VmId, assignment)); }
        catch (CoreOperationException ex) { result = ex.Result; }
        if (result.ErrorKind == ApiErrorKind.OutcomeUnknown || result.Steps.Any(step => step.State == OperationStepState.OutcomeUnknown))
            removalOutcomeUnknown = true;
        lastOperationDetails.Text = System.Text.Json.JsonSerializer.Serialize(new
        {
            VirtualMachineId = VmId, assignment.HostDeviceInstanceId, assignment.Name,
            result.Success, result.ErrorKind, result.ErrorSource, result.Code, result.Error,
            result.Steps, result.RequiresRestart, result.RestartReasons
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        lastOperation.Visibility = Visibility.Visible;
        var detail = removalOutcomeUnknown
            ? "移除操作的最终结果尚未确认；不会自动重试，请先核实底层操作。\n" + result.Error
            : result.Error;
        if (removalOutcomeUnknown)
            unconfirmedRemovals[VmId] = new UnconfirmedRemoval(detail, lastOperationDetails.Text);
        NotifyChanged();
        // 无论成功还是失败都重新读取实际分配，不用本地删除冒充底层成功。
        string? refreshError = null;
        try { await RefreshAssignmentsAsync(); }
        catch (Exception ex) { refreshError = $"分配清单刷新失败：{ex.Message}"; }
        if (!result.Success || removalOutcomeUnknown)
        {
            Notice(string.Join(Environment.NewLine, new[] { detail, refreshError }.Where(text => !string.IsNullOrWhiteSpace(text))), InfoBarSeverity.Error);
            return;
        }
        var restartRequired = result.RequiresRestart ? "\n此操作报告需要重启宿主。" : string.Empty;
        Notice("GPU 分配已移除。" + restartRequired + (refreshError == null ? string.Empty : "\n" + refreshError),
            refreshError != null || result.RequiresRestart ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    private void ShowOverview()
    {
        if (working || closing) return;
        overview.Visibility = Visibility.Visible;
        selection.Visibility = deployment.Visibility = Visibility.Collapsed;
        RefreshActions();
    }

    private Task RunGpuOperationAsync(Func<Task> action)
    {
        if (working || closing || IsStopped || IsBusy) return Task.CompletedTask;
        working = true;
        RefreshActions();
        operation = RunOperationCoreAsync(action);
        return operation;
    }

    private async Task RunOperationCoreAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            Notice(ex.Message, InfoBarSeverity.Error);
            if (workflow != null) ApplySnapshot(workflow, workflow.GetStatus());
        }
        finally { working = false; RefreshActions(); }
    }

    private void Notice(string text, InfoBarSeverity severity)
    {
        operationNotice.Title = severity == InfoBarSeverity.Error ? "操作未完成" : string.Empty;
        operationNotice.Message = text;
        operationNotice.Severity = severity;
        operationNotice.IsOpen = true;
    }

    private static TextBlock Label(string text, double size = 14, bool secondary = false) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap,
        Foreground = Brush(secondary ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush")
    };

    private static UIElement ValueRow(string title, string? value)
    {
        var row = new Grid { ColumnSpacing = 20 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(Label(title, secondary: true));
        var content = Label(string.IsNullOrWhiteSpace(value) ? "未取得" : value);
        content.IsTextSelectionEnabled = true;
        Grid.SetColumn(content, 1);
        row.Children.Add(content);
        return row;
    }
}
