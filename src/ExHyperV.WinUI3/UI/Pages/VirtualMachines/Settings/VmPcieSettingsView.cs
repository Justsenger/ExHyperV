using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

/// <summary>PCIe 呈现方式、启动阶段 vPCI 和已分配设备设置。</summary>
public sealed class VmPcieSettingsView : UserControl
{
    private readonly Guid vmId;
    private readonly StackPanel body = new() { Spacing = 18 };
    private readonly InfoBar message = new() { IsClosable = true };
    private Task operation = Task.CompletedTask;
    private Task? stopping;
    private readonly List<string> stopFailures = [];
    private ContentDialog? dialog;
    private bool started, stopped, busy;
    private bool canEdit;
    private string stateRestriction = "尚未确认虚拟机运行状态，不能修改 PCIe 设置。";
    private VmPcieSettings? settings;

    public event EventHandler? Changed;
    internal event Action<bool>? BusyChanged;

    public VmPcieSettingsView(Guid vmId)
    {
        this.vmId = vmId;
        var layout = new Grid { RowSpacing = 12 };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        layout.Children.Add(message);
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        Content = layout;
        Loaded += (_, _) => { if (!started) { started = true; operation = RunAsync(LoadAsync); } };
    }

    public Task StopAsync()
    {
        if (stopping is { IsCompleted: false }) return stopping;
        if (stopped) return Task.CompletedTask;
        return stopping = StopCoreAsync();
    }

    private async Task StopCoreAsync()
    {
        stopFailures.Clear();
        stopped = true;
        dialog?.Hide();
        await operation;
        if (stopFailures.Count == 0) return;

        string failure = string.Join(Environment.NewLine, stopFailures);
        stopFailures.Clear();
        stopped = false;
        IsEnabled = true;
        ShowError(failure + "\n页面已保留；查看后可以再次返回。");
        throw new InvalidOperationException(failure);
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || stopped) return;
        busy = true;
        IsEnabled = false;
        BusyChanged?.Invoke(true);
        message.IsOpen = false;
        try { await action(); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            busy = false;
            IsEnabled = !stopped;
            BusyChanged?.Invoke(false);
        }
    }

    private async Task LoadAsync()
    {
        canEdit = false;
        stateRestriction = "尚未确认虚拟机运行状态，不能修改 PCIe 设置。";
        if (settings != null) Render();
        var settingsTask = VmPcieService.GetSettingsAsync(vmId);
        var stateTask = VmConsoleSupportService.GetConsoleStateAsync(vmId);
        await Task.WhenAll(settingsTask, stateTask);
        if (stopped) return;
        var response = await settingsTask;
        var state = await stateTask;
        canEdit = state.HasData && !state.Data!.IsRunning;
        stateRestriction = state.HasData ? "虚拟机关机后才能修改 PCIe 设置。"
            : "无法确认虚拟机运行状态，已禁用 PCIe 修改。" + state.Error;
        if (!response.Success || !response.HasData)
        {
            ShowError(response.Error);
            return;
        }
        settings = response.Data!;
        Render();
        if (!state.HasData) ShowError(stateRestriction);
    }

    private void Render()
    {
        if (settings is not { } current) return;
        body.Children.Clear();
        body.Children.Add(SectionTitle("PCIe 呈现方式"));
        var system = new StackPanel();
        var emulation = new ToggleSwitch { IsOn = current.EmulationEnabled, IsEnabled = canEdit && current.SystemSettingsAvailable && !current.EmulationEnabled, OnContent = "启用", OffContent = "关闭" };
        ToolTipService.SetToolTip(emulation, !current.SystemSettingsAvailable ? "此虚拟机不支持 PCIe 系统设置。" : current.EmulationEnabled ? "PCIe 仿真启用后，无法通过 Hyper-V 管理接口关闭。" : !canEdit ? stateRestriction : "启用 PCIe 仿真");
        emulation.Toggled += (_, _) =>
        {
            if (!emulation.IsOn || busy || stopped || !canEdit) return;
            operation = RunAsync(() => ApplyAsync(new(VmPcieChangeKind.EnableEmulation, vmId, current.EmulationEnabled, current.Topology)));
        };
        system.Children.Add(Row("PCIe 仿真", "让设备以标准 PCIe 设备的方式出现在虚拟机中", emulation));
        system.Children.Add(new Border { Height = 1, Background = Brush("CardStrokeColorDefaultBrush"), Margin = new(24, 0, 24, 0) });
        var topology = new ComboBox { Width = 180, IsEnabled = canEdit && current.SystemSettingsAvailable };
        topology.Items.Add(new ComboBoxItem { Content = "默认", Tag = (ushort)0 });
        topology.Items.Add(new ComboBoxItem { Content = "仿照主机", Tag = (ushort)1 });
        if (current.Topology > 1) topology.Items.Add(new ComboBoxItem { Content = $"当前值 {current.Topology}", Tag = current.Topology, IsEnabled = false });
        topology.SelectedIndex = current.Topology <= 1 ? current.Topology : 2;
        if (!current.SystemSettingsAvailable) ToolTipService.SetToolTip(topology, "此虚拟机不支持 PCIe 系统设置。");
        else if (!canEdit) ToolTipService.SetToolTip(topology, stateRestriction);
        topology.SelectionChanged += (_, _) =>
        {
            if (busy || stopped || !canEdit || topology.SelectedItem is not ComboBoxItem { Tag: ushort value } || value == current.Topology) return;
            operation = RunAsync(() => ApplyAsync(new(VmPcieChangeKind.Topology, vmId, current.EmulationEnabled, value)));
        };
        system.Children.Add(Row("PCIe 拓扑", "呈现设备的 PCIe 拓扑结构", topology));
        body.Children.Add(Card(system));

        body.Children.Add(SectionTitle("启动"));
        var boot = new ToggleSwitch { IsOn = current.BootPciExpress, IsEnabled = canEdit && current.BootPciExpressAvailable, OnContent = "启用", OffContent = "关闭" };
        if (!current.BootPciExpressAvailable) ToolTipService.SetToolTip(boot, "此虚拟机未提供启动阶段 vPCI 设置。");
        else if (!canEdit) ToolTipService.SetToolTip(boot, stateRestriction);
        boot.Toggled += (_, _) =>
        {
            if (busy || stopped || !canEdit) return;
            var requested = boot.IsOn;
            operation = RunAsync(async () =>
            {
                var response = await VmPcieService.SetBootPciExpressAsync(vmId, requested);
                if (response.Success) Changed?.Invoke(this, EventArgs.Empty);
                if (!response.Success) ShowError(response.Error);
                await LoadAsync();
                if (!response.Success) ShowError(response.Error);
            });
        };
        body.Children.Add(Card(Row("启动阶段 vPCI", "在 UEFI 中加载 vPCI 驱动，以识别受支持的设备（NVMe、AziHsm）", boot)));

        body.Children.Add(SectionTitle("已分配的 PCIe 设备"));
        if (current.Devices.Count == 0) body.Children.Add(new TextBlock { Text = "没有已分配的 PCIe 设备", FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush"), Margin = new(4, 4, 0, 24) });
        foreach (var device in current.Devices)
        {
            var mode = new ComboBox { Width = 180, IsEnabled = canEdit && device.GuestModeAvailable };
            mode.Items.Add(new ComboBoxItem { Content = "半虚拟化", Tag = VmPcieGuestMode.Paravirtualized });
            mode.Items.Add(new ComboBoxItem { Content = "仿真", Tag = VmPcieGuestMode.Emulated });
            bool knownMode = Enum.IsDefined(device.AppliedGuestMode);
            if (!knownMode) mode.Items.Add(new ComboBoxItem { Content = $"当前值 {(byte)device.AppliedGuestMode}", IsEnabled = false });
            mode.SelectedIndex = knownMode ? (int)device.AppliedGuestMode : 2;
            if (!device.GuestModeAvailable) ToolTipService.SetToolTip(mode, "此设备不支持修改来宾模式。");
            else if (!canEdit) ToolTipService.SetToolTip(mode, stateRestriction);
            mode.SelectionChanged += (_, _) =>
            {
                if (busy || stopped || !canEdit || mode.SelectedItem is not ComboBoxItem { Tag: VmPcieGuestMode value } || value == device.AppliedGuestMode) return;
                operation = RunAsync(() => ApplyAsync(new(VmPcieChangeKind.DeviceMode, vmId, current.EmulationEnabled, current.Topology, device, value)));
            };
            var header = new Grid { ColumnSpacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            header.Children.Add(new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = DeviceGlyph(device), FontSize = 24 });
            var name = new TextBlock { Text = device.Name, FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(name, device.Name);
            Grid.SetColumn(name, 1); header.Children.Add(name);
            Grid.SetColumn(mode, 2); header.Children.Add(mode);
            var details = new StackPanel { Spacing = 12, Margin = new(42, 10, 0, 6) };
            details.Children.Add(Detail("类别", device.ClassType));
            details.Children.Add(Detail("实例 ID", device.DeviceInstanceId));
            details.Children.Add(Detail("路径", device.Path));
            details.Children.Add(Detail("制造商", device.Vendor));
            body.Children.Add(new Expander { Header = header, Content = details, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
    }

    private async Task ApplyAsync(VmPcieChangeSession session)
    {
        bool confirmed = false;
        if (session.RequiresConfirmation)
        {
            dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "警告：PCIe 仿真无法关闭", Content = "PCIe 仿真启用后，无法通过 Hyper-V 管理接口关闭。是否继续？", PrimaryButtonText = "继续", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            try { confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary; }
            finally { dialog = null; }
            if (stopped) return;
        }
        var result = await session.ApplyAsync(confirmed);
        // 启用仿真可能已成功，而后续设备模式修改失败，因此始终重新读取真实状态。
        if (!result.Cancelled) Changed?.Invoke(this, EventArgs.Empty);
        if (!result.Cancelled && !result.Success) ShowError(result.Error);
        await LoadAsync();
        if (!result.Cancelled && !result.Success) ShowError(result.Error);
    }

    private void ShowError(string error)
    {
        string detail = string.IsNullOrWhiteSpace(error) ? "未能读取或修改 PCIe 设置。" : error;
        if (stopped)
        {
            if (!stopFailures.Contains(detail)) stopFailures.Add(detail);
            return;
        }
        message.Severity = InfoBarSeverity.Error;
        message.Message = detail;
        message.IsOpen = true;
    }

    private static TextBlock SectionTitle(string text) => new() { Text = text, FontSize = 14, Margin = new(4, 10, 0, -6) };
    private static string DeviceGlyph(VmPcieDeviceSetting device) => device.ClassType switch
    {
        "Switch" => "\uF597", "Upstream" => "\uE774", "Display" => "\uF211", "Net" => "\uE839",
        "USB" => device.Name.Contains("USB4", StringComparison.Ordinal) ? "\uE945" : "\uECF0",
        "HIDClass" => "\uE928", "SCSIAdapter" or "HDC" => "\uEDA2", "ComputeAccelerator" => "\uEEA1",
        _ => device.Name.Contains("Audio", StringComparison.Ordinal) ? "\uE995" : "\uE950"
    };
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
    private static Border Card(UIElement content) => new() { Child = content, Background = Brush("CardBackgroundFillColorDefaultBrush"), BorderBrush = Brush("CardStrokeColorDefaultBrush"), BorderThickness = new(1), CornerRadius = new(8) };
    private static Grid Row(string title, string description, FrameworkElement control)
    {
        var grid = new Grid { Margin = new(24, 16, 24, 16), ColumnSpacing = 20 };
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var labels = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        labels.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap });
        grid.Children.Add(labels);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1); grid.Children.Add(control);
        return grid;
    }
    private static Grid Detail(string label, string value)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new() { Width = new(125) });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = label, Foreground = Brush("TextFillColorSecondaryBrush") });
        var text = new TextBlock { Text = string.IsNullOrEmpty(value) ? "—" : value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Grid.SetColumn(text, 1); grid.Children.Add(text);
        return grid;
    }
}
