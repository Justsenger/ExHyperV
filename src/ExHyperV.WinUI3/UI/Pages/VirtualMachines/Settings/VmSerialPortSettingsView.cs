using System.Text;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>虚拟机串口（COM 1 / COM 2）设置视图。</summary>
public sealed class VmSerialPortSettingsView : VmSettingsView
{
    private readonly StackPanel portsPanel = new() { Margin = new(0, 8, 0, 0), Spacing = 4 };
    private string? cachedVmName;
    private bool canModify;

    public VmSerialPortSettingsView(Guid vmId) : base(vmId)
    {
        SetBody(portsPanel);
    }

    protected override async Task LoadAsync()
    {
        var portsTask = VmSerialPortService.GetSerialPortsAsync(VmId);
        var contextTask = ReadContextAsync();
        var nameTask = cachedVmName is null ? ReadVmNameAsync() : Task.FromResult<string?>(cachedVmName);
        await Task.WhenAll(portsTask, contextTask, nameTask);

        var portsResult = await portsTask;
        if (!portsResult.HasData) throw new InvalidOperationException(portsResult.Error);
        if (IsStopped) return;

        cachedVmName ??= await nameTask;
        var context = await contextTask;
        var condition = VmSerialPortService.GetModificationCondition(context.State);
        canModify = condition.CanModify;
        if (IsStopped) return;

        Updating = true;
        try
        {
            portsPanel.Children.Clear();
            var ports = portsResult.Data!;
            if (ports.Count == 0)
            {
                portsPanel.Children.Add(new TextBlock { Text = "未检测到虚拟串口设备。", Margin = new(0, 12, 0, 12) });
            }
            else
            {
                foreach (var port in ports)
                    AddSection(portsPanel, port.Name, BuildPortCard(port, condition.Reason));
            }
        }
        finally
        {
            Updating = false;
        }
    }

    private async Task<string?> ReadVmNameAsync()
    {
        await using var query = new VmQueryService();
        var result = await query.QueryAsync(new([VmIdentityFields.Name], [VmId]));
        if (!result.HasData || result.Data!.VirtualMachines.Count == 0) return null;
        var cell = result.Data.VirtualMachines[0].Get(VmIdentityFields.Name);
        return cell.HasValue ? cell.Value : null;
    }

    private Border BuildPortCard(VmSerialPort port, string? lockReason)
    {
        bool hasPipe = !string.IsNullOrWhiteSpace(port.PipePath);
        string fallbackLock = lockReason ?? "关闭虚拟机后可修改";

        var enableToggle = Switch();
        enableToggle.IsOn = hasPipe;
        enableToggle.IsEnabled = canModify;

        var pipeBox = new TextBox
        {
            Text = port.PipePath,
            PlaceholderText = @"管道名或 \\.\pipe\名称",
            Width = 260,
            IsEnabled = canModify && hasPipe
        };

        var applyPipe = new Button
        {
            Content = "应用",
            IsEnabled = canModify && hasPipe,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };

        var copyPipe = new Button
        {
            Content = "复制",
            IsEnabled = hasPipe
        };

        var debuggerToggle = Switch();
        debuggerToggle.IsOn = port.DebuggerMode;
        debuggerToggle.IsEnabled = canModify && port.DebuggerModeAvailable && (hasPipe || port.DebuggerMode);

        enableToggle.Toggled += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped) return;
            string nextPipe = enableToggle.IsOn
                ? BuildDefaultPipePath(cachedVmName, VmId, port.PortNumber)
                : string.Empty;
            await ApplyChangeAsync(() => VmSerialPortService.SetPipePathAsync(VmId, port.PortNumber, nextPipe));
        };

        applyPipe.Click += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped || !enableToggle.IsOn) return;
            string input = pipeBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(input))
            {
                ShowError("请输入命名管道名称或完整路径；如需断开连接，请关闭“命名管道连接”开关。");
                return;
            }
            if (VmSerialPortService.GetPipePathError(input) is { } pipeError)
            {
                ShowError(pipeError);
                return;
            }
            await ApplyChangeAsync(() => VmSerialPortService.SetPipePathAsync(VmId, port.PortNumber, input));
        };

        copyPipe.Click += (_, _) =>
        {
            string text = !string.IsNullOrWhiteSpace(pipeBox.Text) ? pipeBox.Text : port.PipePath;
            string normalized = VmSerialPortService.NormalizePipePath(text);
            if (string.IsNullOrEmpty(normalized)) return;
            var package = new DataPackage();
            package.SetText(normalized);
            Clipboard.SetContent(package);
            ShowAppliedToast("已复制管道路径");
        };

        debuggerToggle.Toggled += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped) return;
            await ApplyChangeAsync(() =>
                VmSerialPortService.SetDebuggerModeAsync(VmId, port.PortNumber, debuggerToggle.IsOn));
        };

        var pipeEditor = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pipeEditor.Children.Add(pipeBox);
        pipeEditor.Children.Add(applyPipe);
        pipeEditor.Children.Add(copyPipe);

        string enableDesc = canModify
            ? "开启后将此串口连接到宿主机命名管道；关闭则断开连接并清空管道配置。"
            : fallbackLock;
        string pipeDesc = !canModify
            ? fallbackLock
            : hasPipe
                ? (ApiFieldHelp.For(typeof(VmSerialPort).GetProperty(nameof(VmSerialPort.PipePath))!) ?? "输入管道短名会自动补全为 \\\\.\\pipe\\名称")
                : "请先开启上方开关以启用命名管道连接。";
        string debugDesc = !port.DebuggerModeAvailable
            ? "当前宿主未提供 DebuggerMode 属性"
            : !canModify
                ? fallbackLock
                : hasPipe || port.DebuggerMode
                    ? (ApiFieldHelp.For(typeof(VmSerialPort).GetProperty(nameof(VmSerialPort.DebuggerMode))!) ?? "为 Windows 内核调试器（WinDbg）优化传输；普通串口控制台无需开启")
                    : "需先启用命名管道后才可开启内核调试模式。";

        return Card(
            Row("命名管道连接", enableDesc, enableToggle),
            Row("命名管道路径", pipeDesc, pipeEditor),
            Row("内核调试模式", debugDesc, debuggerToggle));
    }

    private static string BuildDefaultPipePath(string? vmName, Guid vmId, int portNumber)
    {
        var sb = new StringBuilder();
        bool lastDash = false;
        foreach (char ch in (vmName ?? string.Empty).Trim())
        {
            if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-')
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastDash = ch == '-';
            }
            else if (!lastDash && sb.Length > 0)
            {
                sb.Append('-');
                lastDash = true;
            }
        }
        string prefix = sb.ToString().Trim('-');
        if (string.IsNullOrEmpty(prefix))
            prefix = $"vm-{vmId.ToString("N")[..8]}";
        return $@"\\.\pipe\{prefix}-com{portNumber}";
    }

    private Task ApplyChangeAsync(Func<Task<ApiResponse>> write) => RunAsync(async () =>
    {
        var result = await write();
        await ReloadAfterWriteAsync(result);
        NotifyChanged();
    });
}
