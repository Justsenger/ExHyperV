using ExHyperV.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmGpuSettingsView
{
    private void RenderDecision(GpuDecisionRequest request)
    {
        decisions.Children.Clear();
        decisions.Children.Add(Label(request.Title, 18));
        if (request.IsDestructive)
            decisions.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = request.Message });
        else
            decisions.Children.Add(Label(request.Message));

        switch (request.Input)
        {
            case GpuDecisionInput.Confirmation:
                RenderConfirmation(request);
                break;
            case GpuDecisionInput.Partition:
                RenderPartitions(request);
                break;
            case GpuDecisionInput.LinuxCredentials:
                RenderLinuxInput(request);
                break;
            default:
                decisions.Children.Add(Label("此版本尚不能处理 Core 请求的输入类型，请取消部署。"));
                break;
        }
    }

    private void RenderConfirmation(GpuDecisionRequest request)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var accept = new Button { Content = "确认继续", Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] };
        var reject = new Button { Content = "取消部署" };
        accept.Click += async (_, _) => await SubmitDecisionAsync(new GpuConfirmation(request.Id, true));
        reject.Click += async (_, _) => await SubmitDecisionAsync(new GpuConfirmation(request.Id, false));
        actions.Children.Add(accept);
        actions.Children.Add(reject);
        AddBackToPartitions(actions, request);
        decisions.Children.Add(actions);
    }

    private void RenderPartitions(GpuDecisionRequest request)
    {
        var partitions = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 380 };
        foreach (var partition in request.Partitions)
        {
            var description = new StackPanel { Spacing = 6, Margin = new Thickness(8) };
            description.Children.Add(Label(partition.DisplayName));
            description.Children.Add(Label(partition.TypeDescription, 12, secondary: true));
            if (partition.IsBitLocker) description.Children.Add(Label("检测到 BitLocker 加密", 12, secondary: true));
            var disk = Label(partition.DiskPath, 12, secondary: true);
            disk.IsTextSelectionEnabled = true;
            description.Children.Add(disk);
            partitions.Items.Add(new ListViewItem { Tag = partition, Content = description, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
        decisions.Children.Add(partitions);
        if (request.Partitions.Count == 0) decisions.Children.Add(Label("本次扫描未提供可选分区。"));
        var choose = new Button { Content = "选择此分区", IsEnabled = false, Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] };
        partitions.SelectionChanged += (_, _) => choose.IsEnabled = partitions.SelectedItem is ListViewItem { Tag: GpuDeploymentPartition };
        choose.Click += async (_, _) =>
        {
            if (partitions.SelectedItem is ListViewItem { Tag: GpuDeploymentPartition selected })
                await SubmitDecisionAsync(new GpuPartitionSelection(request.Id, selected.Id));
        };
        decisions.Children.Add(choose);
    }

    private void RenderLinuxInput(GpuDecisionRequest request)
    {
        var form = new StackPanel { Spacing = 16 };
        var availableScripts = new ComboBox
        {
            Header = "部署方案", ItemsSource = scripts, DisplayMemberPath = nameof(LinuxGpuDeploymentScript.Name),
            SelectedItem = scripts.FirstOrDefault(), HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var scriptDescription = Label(string.Empty, 12, secondary: true);
        void UpdateDescription()
        {
            scriptDescription.Text = availableScripts.SelectedItem is LinuxGpuDeploymentScript script
                ? $"{script.Author} · {script.Version}\n{script.Description}"
                : "未取得可用脚本。可重新加载在线清单，或返回选择其他分区。";
        }
        UpdateDescription();
        availableScripts.SelectionChanged += (_, _) => UpdateDescription();
        var reloadScripts = new Button { Content = "重新加载部署方案" };
        reloadScripts.Click += async (_, _) => await RunGpuOperationAsync(async () =>
        {
            await LoadScriptsAsync();
            if (workflow?.GetStatus().PendingDecision?.Id != request.Id) return;
            availableScripts.ItemsSource = scripts;
            availableScripts.SelectedItem = scripts.FirstOrDefault();
            UpdateDescription();
        });
        form.Children.Add(availableScripts);
        form.Children.Add(scriptDescription);
        form.Children.Add(reloadScripts);

        var host = new TextBox { Header = "来宾 IP 地址", Text = request.SuggestedHost, MinWidth = 160 };
        var port = new NumberBox { Header = "SSH 端口", Value = 22, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 110 };
        var username = new TextBox { Header = "用户名", Text = "root", MinWidth = 160 };
        var password = new PasswordBox { Header = "密码", MinWidth = 160 };
        form.Children.Add(InputPair(host, port));
        form.Children.Add(InputPair(username, password));
        var graphics = new CheckBox { Content = "安装图形支持", IsChecked = true };
        form.Children.Add(graphics);

        var useProxy = new ToggleSwitch { Header = "下载时使用 HTTP 代理", OnContent = "", OffContent = "" };
        var proxyHost = new TextBox { Header = "代理主机", MinWidth = 160 };
        var proxyPort = new NumberBox { Header = "代理端口", Value = 7890, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, MinWidth = 110 };
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
        catch { /* 可选表单默认值失败不影响部署选择。 */ }
        var proxyInputs = InputPair(proxyHost, proxyPort);
        proxyHost.IsEnabled = proxyPort.IsEnabled = false;
        useProxy.Toggled += (_, _) => proxyHost.IsEnabled = proxyPort.IsEnabled = useProxy.IsOn;
        form.Children.Add(useProxy);
        form.Children.Add(Label("代理供来宾下载部署脚本使用，不用于 SSH 连接。", 12, secondary: true));
        form.Children.Add(proxyInputs);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var connect = new Button { Content = "连接并部署", Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] };
        connect.Click += async (_, _) =>
        {
            var credentials = new SshCredentials
            {
                Host = host.Text.Trim(), Port = PortValue(port.Value), Username = username.Text,
                Password = password.Password, InstallGraphics = graphics.IsChecked == true,
                UseProxy = useProxy.IsOn, ProxyHost = proxyHost.Text.Trim(),
                ProxyPort = useProxy.IsOn ? PortValue(proxyPort.Value) : null
            };
            await SubmitDecisionAsync(new GpuLinuxInput(request.Id,
                availableScripts.SelectedItem as LinuxGpuDeploymentScript, credentials));
            // 凭据只交给本次调用；不写入配置、日志或恢复材料。
            if (workflow?.GetStatus().PendingDecision?.Id != request.Id) password.Password = string.Empty;
        };
        actions.Children.Add(connect);
        AddBackToPartitions(actions, request);
        form.Children.Add(actions);
        decisions.Children.Add(form);
    }

    private void AddBackToPartitions(Panel actions, GpuDecisionRequest request)
    {
        if (!request.CanGoBack || snapshot?.AllowedActions.Contains(GpuDeploymentAction.BackToPartitions) != true) return;
        var back = new Button { Content = "返回分区选择" };
        back.Click += async (_, _) => await SubmitDecisionAsync(new GpuBackToPartitions(request.Id));
        actions.Children.Add(back);
    }

    private Task SubmitDecisionAsync(GpuDeploymentDecision decision) => RunGpuOperationAsync(async () =>
    {
        var session = workflow ?? throw new InvalidOperationException("GPU 部署已经结束。");
        var current = session.GetStatus();
        if (current.PendingDecision?.Id != decision.RequestId || !current.AllowedActions.Contains(GpuDeploymentAction.SubmitDecision))
            throw new InvalidOperationException("此确认已过期，请使用当前页面的请求。");
        var result = await Task.Run(() => session.SubmitDecisionAsync(decision));
        await FinishStepAsync(session, result);
    });

    private static int PortValue(double value) => double.IsFinite(value) && value == Math.Truncate(value)
        && value is >= 0 and <= 65535 ? (int)value : 0;

    private static Grid InputPair(FrameworkElement left, FrameworkElement right)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }
}
