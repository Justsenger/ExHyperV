using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>虚拟串口概览与单口设置。</summary>
public sealed partial class VmSerialPortSettingsView : VmSettingsView
{
    private static string BuildDefaultPipePath(string? vmName, int portNumber)
    {
        string prefix = System.Text.RegularExpressions.Regex.Replace(vmName?.Trim() ?? "", "[^A-Za-z0-9._-]+", "-").Trim('-');
        if (prefix.Length == 0) prefix = "vm";
        return $@"\\.\pipe\{prefix}-com{portNumber}";
    }

    private readonly StackPanel panel = new() { Margin = new(0, 0, 0, 20), Spacing = 12 };
    private IReadOnlyList<VmSerialPort> ports = [];
    private string? vmName;
    private VmState powerState = VmState.Unknown;
    private string? lockReason;
    private bool hasLoadedData;
    private int? editing;
    private string draftPipePath = string.Empty;
    private bool draftDebuggerMode;

    public VmSerialPortSettingsView(Guid vmId) : base(vmId)
    {
        SetBody(panel);
        ActualThemeChanged += (_, _) =>
        {
            if (hasLoadedData && !IsStopped) Render();
        };
    }

    protected override async Task LoadAsync()
    {
        var devices = VmSerialPortService.GetSerialPortsAsync(VmId);
        var state = ReadContextAsync();
        var identity = QueryAsync(VmIdentityFields.Name);
        await Task.WhenAll(devices, state, identity);
        var result = await devices;
        if (!result.HasData) throw new InvalidOperationException(result.Error);
        if (IsStopped) return;
        ports = result.Data;
        vmName = (await identity).Get(VmIdentityFields.Name).Value;
        powerState = (await state).State;
        var condition = VmSerialPortService.GetModificationCondition(powerState);
        lockReason = condition.CanModify ? null : condition.Reason;
        hasLoadedData = true;
        Render();
    }

    private void Render()
    {
        panel.Children.Clear();
        if (ports.FirstOrDefault(p => p.PortNumber == editing) is { } port)
        {
            BuildEditor(port);
            return;
        }

        editing = null;
        var cards = new StackPanel { Spacing = 10 };
        foreach (var device in ports) cards.Children.Add(BuildCard(device));
        if (ports.Count == 0)
            cards.Children.Add(new TextBlock
            {
                Text = "未检测到虚拟串口设备。",
                Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush")
            });
        panel.Children.Add(cards);
        AddLockNotice();
    }

    private void AddLockNotice()
    {
        if (lockReason == null) return;
        string title = powerState == VmState.Running ? "虚拟机运行中，串口配置已锁定" : "串口配置已锁定";
        string message = powerState == VmState.Running ? "关闭虚拟机后才可开关连接或修改管道与内核调试设置" : lockReason;
        panel.Children.Add(Notice(title, message));
    }

    private void OpenEditor(VmSerialPort port)
    {
        editing = port.PortNumber;
        draftPipePath = port.PipePath;
        draftDebuggerMode = port.DebuggerMode;
        Render();
    }

    private Border BuildCard(VmSerialPort port)
    {
        string title = $"COM {port.PortNumber}";
        var toggle = Switch();
        Updating = true;
        try { toggle.IsOn = port.IsConnected; }
        finally { Updating = false; }
        toggle.IsEnabled = lockReason == null;
        ToolTipService.SetToolTip(toggle, lockReason == null
            ? (port.IsConnected ? "关闭并清空命名管道" : "开启默认命名管道连接")
            : "关闭虚拟机后可修改");
        toggle.Toggled += async (_, _) =>
        {
            if (Updating || IsBusy || IsStopped) return;
            string path = toggle.IsOn ? BuildDefaultPipePath(vmName, port.PortNumber) : "";
            await ApplyAsync(() => VmSerialPortService.SetPipePathAsync(VmId, port.PortNumber, path));
        };

        var header = Header(
            scope: this,
            fluentGlyph: "\uF6A3",
            title: title,
            subtitle: null,
            active: port.IsConnected,
            badge: port.DebuggerMode ? "内核调试" : null,
            toggle: toggle,
            configure: () => OpenEditor(port),
            configureTooltip: "配置串口设置");

        var body = KvTile(
            this,
            "命名管道路径",
            port.IsConnected ? port.PipePath : "未配置",
            copyValue: port.IsConnected ? port.PipePath : null,
            copyLabel: port.IsConnected ? $"点击复制：{port.PipePath}" : null,
            onError: ShowError);

        return CardShell(this, header, body);
    }

    private void BuildEditor(VmSerialPort port)
    {
        bool canModify = lockReason == null;
        var input = new TextBox
        {
            Text = draftPipePath,
            Width = 260,
            PlaceholderText = @"管道名或 \\.\pipe\名称",
            IsEnabled = canModify
        };
        var debug = Switch();
        Updating = true;
        try { debug.IsOn = draftDebuggerMode; }
        finally { Updating = false; }

        var apply = new Button { Content = "应用", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        bool PipeChanged() => !string.Equals(VmSerialPortService.NormalizePipePath(input.Text), port.PipePath, StringComparison.OrdinalIgnoreCase);
        bool DebugChanged() => port.DebuggerModeAvailable && debug.IsOn != port.DebuggerMode;

        void Sync()
        {
            bool hasPipe = !string.IsNullOrWhiteSpace(input.Text);
            bool canDebug = canModify && port.DebuggerModeAvailable && (hasPipe || debug.IsOn);
            debug.IsEnabled = canDebug;
            apply.IsEnabled = canModify && (PipeChanged() || DebugChanged());
            ToolTipService.SetToolTip(debug, !canModify
                ? "关闭虚拟机后可修改"
                : !port.DebuggerModeAvailable
                    ? "当前宿主未提供 DebuggerMode 属性"
                    : canDebug ? "切换内核调试模式" : "需先配置命名管道后才可开启内核调试模式");
        }

        input.TextChanged += (_, _) =>
        {
            draftPipePath = input.Text;
            Sync();
        };
        debug.Toggled += (_, _) =>
        {
            if (Updating) return;
            draftDebuggerMode = debug.IsOn;
            Sync();
        };
        Sync();

        panel.Children.Add(SettingCard(
            this,
            "命名管道路径",
            @"输入短名称会自动补全为 \\.\pipe\名称，供 PuTTY / WinDbg 连接，留空后应用可断开连接。",
            input,
            enabled: canModify,
            disabledTooltip: "关闭虚拟机后可修改"));

        panel.Children.Add(SettingCard(
            this,
            "内核调试模式",
            port.DebuggerModeAvailable
                ? "为 Windows 内核调试器（WinDbg）优化传输，普通串口控制台无需开启，需配置命名管道后启用。"
                : "当前宿主未提供 DebuggerMode 属性。",
            debug,
            enabled: canModify,
            disabledTooltip: "关闭虚拟机后可修改"));

        AddLockNotice();

        var back = new Button { Content = "返回" };
        back.Click += (_, _) => { editing = null; Render(); };

        apply.Click += async (_, _) =>
        {
            if (VmSerialPortService.GetPipePathError(input.Text) is { } error)
            {
                ShowError(error);
                return;
            }
            bool pipeChanged = PipeChanged(), debugChanged = DebugChanged();
            if (!pipeChanged && !debugChanged) return;
            var path = input.Text;
            bool mode = debug.IsOn;
            await ApplyAsync(() => pipeChanged && debugChanged
                ? VmSerialPortService.SetPortSettingsAsync(VmId, port.PortNumber, path, mode)
                : pipeChanged ? VmSerialPortService.SetPipePathAsync(VmId, port.PortNumber, path)
                : VmSerialPortService.SetDebuggerModeAsync(VmId, port.PortNumber, mode), closeEditor: true);
        };

        var actions = Actions(back, apply);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(actions);
    }

    private Task ApplyAsync(Func<Task<ApiResponse>> write, bool closeEditor = false) => RunAsync(async () =>
    {
        var result = await write();
        if (result.Success)
        {
            if (closeEditor) editing = null;
            NotifyChanged();
        }
        await ReloadAfterWriteAsync(result);
    });
}
