
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using static ExHyperV.App.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings;

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
                Text = L10n.Get("Serial_NoDevice"),
                Foreground = ThemeHelper.ThemeBrush(this, "TextFillColorSecondaryBrush")
            });
        panel.Children.Add(cards);
        AddLockNotice();
    }

    private void AddLockNotice()
    {
        if (lockReason == null) return;
        string title = powerState == VmState.Running ? L10n.Get("Serial_RunningLocked") : L10n.Get("Serial_ConfigLocked");
        string message = powerState == VmState.Running ? L10n.Get("Serial_OfflineHelp") : lockReason;
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
            ? (port.IsConnected ? L10n.Get("Serial_ClearPipe") : L10n.Get("Serial_EnableDefaultPipe"))
            : L10n.Get("Common_ModifyWhenOff"));
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
            badge: port.DebuggerMode ? L10n.Get("Serial_KernelDebug") : null,
            toggle: toggle,
            configure: () => OpenEditor(port),
            configureTooltip: L10n.Get("Serial_Configure"));

        var body = KvTile(
            this,
            L10n.Get("Serial_PipePath"),
            port.IsConnected ? port.PipePath : L10n.Get("Common_Unconfigured"),
            copyValue: port.IsConnected ? port.PipePath : null,
            copyLabel: port.IsConnected ? L10n.Format("Dev_CopyValueTooltip", port.PipePath) : null,
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
            PlaceholderText = L10n.Get("Serial_PipePlaceholder"),
            IsEnabled = canModify
        };
        var debug = Switch();
        Updating = true;
        try { debug.IsOn = draftDebuggerMode; }
        finally { Updating = false; }

        var apply = new Button { Content = L10n.Get("Common_Apply"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        bool PipeChanged() => !string.Equals(VmSerialPortService.NormalizePipePath(input.Text), port.PipePath, StringComparison.OrdinalIgnoreCase);
        bool DebugChanged() => port.DebuggerModeAvailable && debug.IsOn != port.DebuggerMode;

        void Sync()
        {
            bool hasPipe = !string.IsNullOrWhiteSpace(input.Text);
            bool canDebug = canModify && port.DebuggerModeAvailable && (hasPipe || debug.IsOn);
            debug.IsEnabled = canDebug;
            apply.IsEnabled = canModify && (PipeChanged() || DebugChanged());
            ToolTipService.SetToolTip(debug, !canModify
                ? L10n.Get("Common_ModifyWhenOff")
                : !port.DebuggerModeAvailable
                    ? L10n.Get("Serial_NoDebuggerMode")
                    : canDebug ? L10n.Get("Serial_ToggleDebugger") : L10n.Get("Serial_PipeRequired"));
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
            L10n.Get("Serial_PipePath"),
            L10n.Get("Serial_PipeHelp"),
            input,
            enabled: canModify,
            disabledTooltip: L10n.Get("Common_ModifyWhenOff")));

        panel.Children.Add(SettingCard(
            this,
            L10n.Get("Serial_DebuggerMode"),
            port.DebuggerModeAvailable
                ? L10n.Get("Serial_DebuggerHelp")
                : L10n.Get("Serial_DebuggerUnavailable"),
            debug,
            enabled: canModify,
            disabledTooltip: L10n.Get("Common_ModifyWhenOff")));

        AddLockNotice();

        var back = new Button { Content = L10n.Get("Pcie_Back") };
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
