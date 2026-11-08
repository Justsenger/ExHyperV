using System.Globalization;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed class VmNetworkSettingsView : VmSettingsView
{
    private readonly StackPanel adaptersPanel = new() { Spacing = 12 };
    private readonly Button add = new() { Content = "添加网卡" };
    private static readonly Dictionary<Guid, string> UncertainOperations = [];
    private IReadOnlyDictionary<Guid, string> switches = new Dictionary<Guid, string>();
    private bool canEditMac;

    public VmNetworkSettingsView(Guid vmId) : base(vmId)
    {
        var layout = new StackPanel { Margin = new(0, 8, 0, 0), Spacing = 16 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(add);
        actions.Children.Add(ActionButton("刷新", LoadAsync));
        layout.Children.Add(actions);
        layout.Children.Add(adaptersPanel);
        add.Click += async (_, _) => await RunAsync(AddAsync);
        SetBody(layout);
    }

    protected override async Task LoadAsync()
    {
        var switchTask = VmNetworkService.GetAvailableSwitchesAsync();
        var adapterTask = VmNetworkService.GetNetworkAdaptersAsync(VmId);
        var contextTask = ReadContextAsync();
        await Task.WhenAll(switchTask, adapterTask, contextTask);
        var switchResult = await switchTask;
        var adapterResult = await adapterTask;
        if (!switchResult.HasData) throw new InvalidOperationException(switchResult.Error);
        if (!adapterResult.HasData) throw new InvalidOperationException(adapterResult.Error);
        if (IsStopped) return;
        switches = switchResult.Data!;
        var context = await contextTask;
        canEditMac = VmNetworkService.CanEditMacAddress(context.IsRunning);
        string? ipError = null;
        if (context.IsRunning)
        {
            var ipResult = await VmNetworkService.FillDynamicIpsAsync(VmId, adapterResult.Data!);
            if (!ipResult.Success) ipError = ipResult.Error;
        }
        if (IsStopped) return;
        Updating = true;
        try
        {
            adaptersPanel.Children.Clear();
            foreach (var adapter in adapterResult.Data!) adaptersPanel.Children.Add(BuildAdapter(adapter));
            if (adapterResult.Data!.Count == 0)
                adaptersPanel.Children.Add(new TextBlock { Text = "此虚拟机尚未添加网卡。", Margin = new(0, 12, 0, 12) });
            add.IsEnabled = !UncertainOperations.ContainsKey(VmId);
        }
        finally { Updating = false; }
        if (UncertainOperations.TryGetValue(VmId, out var warning)) ShowError(warning);
        else if (ipError != null) ShowInformation($"网卡配置已加载，IP 查询未完成：{ipError}");
    }

    private UIElement BuildAdapter(VmNetworkAdapter adapter)
    {
        var content = new StackPanel();
        var connected = new ToggleSwitch { IsOn = adapter.IsConnected, OnContent = "已连接", OffContent = "已断开", MinWidth = 120 };
        var targets = switches.Select(item => new SwitchChoice(item.Key, item.Value)).ToList();
        if (adapter.SwitchId is { } existing && targets.All(item => item.Id != existing))
            targets.Add(new(existing, $"{adapter.SwitchName}（当前不可用）"));
        var target = new ComboBox { ItemsSource = targets, DisplayMemberPath = nameof(SwitchChoice.Name), MinWidth = 200, MaxWidth = 360 };
        target.SelectedItem = targets.FirstOrDefault(item => item.Id == adapter.SwitchId);
        var applyConnection = ActionButton("应用", async () =>
        {
            if (VmNetworkService.GetConnectionError(connected.IsOn, switches.Count) is { } error) throw new InvalidOperationException(error);
            adapter.IsConnected = connected.IsOn;
            await MutateAsync(() => VmNetworkService.UpdateConnectionAsync(VmId, adapter, (target.SelectedItem as SwitchChoice)?.Id));
        });
        var mac = new TextBox { Text = adapter.MacAddress, Width = 190, IsEnabled = canEditMac };
        var applyMac = ActionButton("应用", () => MutateAsync(() => VmNetworkService.SetMacAddressAsync(VmId, adapter, mac.Text)));
        applyMac.IsEnabled = canEditMac;
        var ip = new StackPanel { Spacing = 4 };
        foreach (var address in adapter.IpAddresses) ip.Children.Add(CopyButton(address));
        if (adapter.IpAddresses.Count == 0) ip.Children.Add(new TextBlock { Text = "暂无 IP 地址", Opacity = .6 });
        AddSection(content, "基本连接", Card(
            Row("连接", null, connected),
            Row("虚拟交换机", null, Horizontal(target, applyConnection)),
            Row("MAC 地址", canEditMac ? "留空恢复自动分配" : "关闭虚拟机后可修改", Horizontal(mac, applyMac, CopyButton("复制", adapter.MacAddress))),
            Row("IP 地址", null, ip)));
        content.Children.Add(BuildVlan(adapter));
        content.Children.Add(BuildBandwidth(adapter));
        content.Children.Add(BuildSecurity(adapter));
        content.Children.Add(BuildOffload(adapter));
        var remove = ActionButton("移除网卡", async () =>
        {
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "移除网卡", Content = $"移除 {adapter.Name}？此操作会断开这张网卡的连接。", PrimaryButtonText = "移除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || IsStopped) return;
            await MutateAsync(() => VmNetworkService.RemoveNetworkAdapterAsync(VmId, adapter));
        });
        content.Children.Add(remove);
        var expander = new Expander { Header = adapter.Name, IsExpanded = true, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = content };
        expander.IsEnabled = !UncertainOperations.ContainsKey(VmId);
        return expander;
    }

    private Border BuildVlan(VmNetworkAdapter adapter)
    {
        var changes = new HashSet<string>();
        var mode = Options((adapter.VlanMode == VlanOperationMode.Unknown ? VlanOperationMode.Access : adapter.VlanMode),
            (VlanOperationMode.Access, "Access"), (VlanOperationMode.Trunk, "Trunk"), (VlanOperationMode.Private, "Private VLAN"));
        var access = Number(adapter.AccessVlanId, 4094);
        var native = Number(adapter.NativeVlanId, 4094);
        var allowed = new TextBox { Text = string.Join(",", adapter.TrunkAllowedVlanIds), Width = 240, PlaceholderText = "例如 10,20,30-40" };
        var role = Options(adapter.PvlanMode, (PvlanMode.None, "未设置"), (PvlanMode.Isolated, "Isolated"), (PvlanMode.Community, "Community"), (PvlanMode.Promiscuous, "Promiscuous"));
        var primary = Number(adapter.PvlanPrimaryId, 4094);
        var secondary = Number(adapter.PvlanSecondaryId, 4094);
        var accessRow = Row("VLAN ID", "0 表示不标记", access);
        var nativeRow = Row("Native VLAN ID", null, native);
        var allowedRow = Row("允许的 VLAN", null, allowed);
        var roleRow = Row("PVLAN 角色", null, role);
        var primaryRow = Row("主 VLAN ID", null, primary);
        var secondaryRow = Row("辅助 VLAN ID", null, secondary);
        void UpdateMode()
        {
            var selected = Selected<VlanOperationMode>(mode);
            accessRow.Visibility = selected == VlanOperationMode.Access ? Visibility.Visible : Visibility.Collapsed;
            nativeRow.Visibility = allowedRow.Visibility = selected == VlanOperationMode.Trunk ? Visibility.Visible : Visibility.Collapsed;
            roleRow.Visibility = primaryRow.Visibility = selected == VlanOperationMode.Private ? Visibility.Visible : Visibility.Collapsed;
            secondaryRow.Visibility = selected == VlanOperationMode.Private && Selected<PvlanMode>(role) != PvlanMode.Promiscuous ? Visibility.Visible : Visibility.Collapsed;
        }
        mode.SelectionChanged += (_, _) => { Track(changes, nameof(adapter.VlanMode)); UpdateMode(); };
        role.SelectionChanged += (_, _) => { Track(changes, nameof(adapter.PvlanMode)); UpdateMode(); };
        Watch(access, changes, nameof(adapter.AccessVlanId)); Watch(native, changes, nameof(adapter.NativeVlanId));
        Watch(primary, changes, nameof(adapter.PvlanPrimaryId)); Watch(secondary, changes, nameof(adapter.PvlanSecondaryId));
        allowed.TextChanged += (_, _) => Track(changes, nameof(adapter.TrunkAllowedVlanIds));
        UpdateMode();
        return Card(Row("VLAN 隔离", null, mode), accessRow, nativeRow, allowedRow, roleRow, primaryRow, secondaryRow,
            Row("", null, ActionButton("应用", async () =>
            {
                if (changes.Count == 0) return;
                adapter.VlanMode = Selected<VlanOperationMode>(mode);
                var selected = changes.Where(name => name == nameof(adapter.VlanMode) || adapter.VlanMode switch
                {
                    VlanOperationMode.Access => name == nameof(adapter.AccessVlanId),
                    VlanOperationMode.Trunk => name is nameof(adapter.NativeVlanId) or nameof(adapter.TrunkAllowedVlanIds),
                    _ => name is nameof(adapter.PvlanMode) or nameof(adapter.PvlanPrimaryId) or nameof(adapter.PvlanSecondaryId)
                }).ToArray();
                if (adapter.VlanMode == VlanOperationMode.Access) adapter.AccessVlanId = Integer(access);
                if (adapter.VlanMode == VlanOperationMode.Trunk) { adapter.NativeVlanId = Integer(native); adapter.TrunkAllowedVlanIds = ParseVlans(allowed.Text); }
                if (adapter.VlanMode == VlanOperationMode.Private) { adapter.PvlanMode = Selected<PvlanMode>(role); adapter.PvlanPrimaryId = Integer(primary); adapter.PvlanSecondaryId = Integer(secondary); }
                if (selected.Length != 0) await MutateAsync(() => VmNetworkService.ApplyVlanSettingsAsync(VmId, adapter, selected));
            })));
    }

    private Border BuildBandwidth(VmNetworkAdapter adapter)
    {
        var changes = new HashSet<string>();
        var min = Bandwidth(adapter.BandwidthReservationBitsPerSecond);
        var max = Bandwidth(adapter.BandwidthLimitBitsPerSecond);
        min.TextChanged += (_, _) => Track(changes, nameof(adapter.BandwidthReservationBitsPerSecond));
        max.TextChanged += (_, _) => Track(changes, nameof(adapter.BandwidthLimitBitsPerSecond));
        return Card(Row("最低带宽", "Mbit/s，0 表示不保留", min), Row("最高带宽", "Mbit/s，0 表示不限速", max),
            Row("", null, ActionButton("应用", async () =>
            {
                if (changes.Count == 0) return;
                if (changes.Contains(nameof(adapter.BandwidthReservationBitsPerSecond))) adapter.BandwidthReservationBitsPerSecond = ParseBandwidth(min.Text);
                if (changes.Contains(nameof(adapter.BandwidthLimitBitsPerSecond))) adapter.BandwidthLimitBitsPerSecond = ParseBandwidth(max.Text);
                await MutateAsync(() => VmNetworkService.ApplyBandwidthSettingsAsync(VmId, adapter, changes.ToArray()));
            })));
    }

    private Border BuildSecurity(VmNetworkAdapter adapter)
    {
        var changes = new HashSet<string>();
        var spoof = Toggle(adapter.MacSpoofingAllowed, changes, nameof(adapter.MacSpoofingAllowed));
        var dhcp = Toggle(adapter.DhcpGuardEnabled, changes, nameof(adapter.DhcpGuardEnabled));
        var router = Toggle(adapter.RouterGuardEnabled, changes, nameof(adapter.RouterGuardEnabled));
        var team = Toggle(adapter.TeamingAllowed, changes, nameof(adapter.TeamingAllowed));
        var mirror = Options(adapter.MonitorMode, (PortMonitorMode.None, "禁用"), (PortMonitorMode.Source, "源端口"), (PortMonitorMode.Destination, "目标端口"));
        mirror.SelectionChanged += (_, _) => Track(changes, nameof(adapter.MonitorMode));
        var storm = Number(adapter.StormLimit, uint.MaxValue);
        Watch(storm, changes, nameof(adapter.StormLimit));
        return Card(Row("MAC 地址欺骗", null, spoof), Row("DHCP 防护", null, dhcp), Row("路由器通告防护", null, router),
            Row("允许网卡组队", null, team), Row("端口镜像", null, mirror), Row("风暴限制", "0 表示不限", storm),
            Row("", null, ActionButton("应用", async () =>
            {
                if (changes.Count == 0) return;
                adapter.MacSpoofingAllowed = spoof.IsOn; adapter.DhcpGuardEnabled = dhcp.IsOn; adapter.RouterGuardEnabled = router.IsOn;
                adapter.TeamingAllowed = team.IsOn; adapter.MonitorMode = Selected<PortMonitorMode>(mirror);
                adapter.StormLimit = checked((uint)WholeNumber(storm));
                await MutateAsync(() => VmNetworkService.ApplySecuritySettingsAsync(VmId, adapter, changes.ToArray()));
            })));
    }

    private Border BuildOffload(VmNetworkAdapter adapter)
    {
        var changes = new HashSet<string>();
        var vmq = Toggle(adapter.VmqEnabled, changes, nameof(adapter.VmqEnabled));
        var sriov = Toggle(adapter.SriovEnabled, changes, nameof(adapter.SriovEnabled));
        var ipsec = Toggle(adapter.IpsecOffloadEnabled, changes, nameof(adapter.IpsecOffloadEnabled));
        return Card(Row("虚拟机队列 VMQ", null, vmq), Row("SR-IOV", "需要宿主硬件与交换机支持", sriov), Row("IPsec 任务卸载", null, ipsec),
            Row("", null, ActionButton("应用", async () =>
            {
                if (changes.Count == 0) return;
                adapter.VmqEnabled = vmq.IsOn; adapter.SriovEnabled = sriov.IsOn; adapter.IpsecOffloadEnabled = ipsec.IsOn;
                await MutateAsync(() => VmNetworkService.ApplyOffloadSettingsAsync(VmId, adapter, changes.ToArray()));
            })));
    }

    private async Task AddAsync()
    {
        EnsureWriteAllowed();
        var result = await VmNetworkService.AddNetworkAdapterAsync(VmId);
        string? error = result.Success ? null : string.Join(Environment.NewLine,
            new[] { result.Error, result.CleanupError, result.RetainedDeviceIds.Count == 0 ? null : $"剩余设备：{string.Join(", ", result.RetainedDeviceIds)}" }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (result.AdapterState == VmNetworkAdapterCreationState.Unknown || result.InitializationState == VmNetworkAdapterInitializationState.Unknown)
            UncertainOperations[VmId] = error = $"{error}\n底层任务的最终结果尚未确认。已暂停本次程序内对此虚拟机的网卡修改，避免重复操作。";
        await RefreshAfterWriteAsync(error);
    }

    private async Task MutateAsync(Func<Task<ApiResponse>> operation)
    {
        EnsureWriteAllowed();
        var result = await operation();
        if (result.ErrorKind == ApiErrorKind.OutcomeUnknown)
            UncertainOperations[VmId] = $"{result.Error}\n底层任务的最终结果尚未确认，已暂停本次程序内的网卡修改。";
        await RefreshAfterWriteAsync(result.Success ? null : result.Error);
    }

    private async Task RefreshAfterWriteAsync(string? error)
    {
        NotifyChanged();
        try { await LoadAsync(); }
        catch (Exception ex) { error = string.IsNullOrEmpty(error) ? ex.Message : $"{error}\n刷新失败：{ex.Message}"; }
        if (error != null) ShowError(UncertainOperations.GetValueOrDefault(VmId) ?? error);
        else ShowInformation("已应用。");
    }

    private void EnsureWriteAllowed()
    {
        if (IsStopped) throw new OperationCanceledException();
        if (UncertainOperations.TryGetValue(VmId, out var message)) throw new InvalidOperationException(message);
    }
    private Button ActionButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text };
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }
    private void Track(HashSet<string> changes, string name) { if (!Updating && !IsStopped) changes.Add(name); }
    private void Watch(NumberBox box, HashSet<string> changes, string name) => box.ValueChanged += (_, _) => Track(changes, name);
    private ToggleSwitch Toggle(bool value, HashSet<string> changes, string name)
    {
        var control = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0 };
        control.Toggled += (_, _) => Track(changes, name);
        return control;
    }
    private static StackPanel Horizontal(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }
    private Button CopyButton(string text, string? value = null)
    {
        var button = new Button { Content = text };
        button.Click += (_, _) =>
        {
            try { var data = new DataPackage(); data.SetText(value ?? text); Clipboard.SetContent(data); }
            catch (Exception ex) { ShowError(ex.Message); }
        };
        return button;
    }
    private static NumberBox Number(double value, double max) => new() { Value = value, Minimum = 0, Maximum = max, SmallChange = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 180 };
    private static double WholeNumber(NumberBox control) => double.IsFinite(control.Value) && control.Value == Math.Truncate(control.Value)
        ? control.Value : throw new ArgumentException("请输入整数。");
    private static int Integer(NumberBox control) => checked((int)WholeNumber(control));
    private static TextBox Bandwidth(ulong bits) => new() { Width = 180, Text = (bits / 1_000_000m).ToString("0.######", CultureInfo.CurrentCulture) };
    private static ulong ParseBandwidth(string text)
    {
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var megabits) || megabits < 0)
            throw new ArgumentException("带宽必须是非负数，单位为 Mbit/s。");
        decimal bits = checked(megabits * 1_000_000m);
        if (decimal.Truncate(bits) != bits) throw new ArgumentException("带宽精度不能小于 1 bit/s。");
        return checked((ulong)bits);
    }
    private static ComboBox Options<T>(T selected, params (T Value, string Label)[] values) where T : struct
    {
        var combo = new ComboBox { MinWidth = 180 };
        foreach (var item in values)
        {
            var option = new ComboBoxItem { Content = item.Label, Tag = item.Value };
            combo.Items.Add(option);
            if (EqualityComparer<T>.Default.Equals(item.Value, selected)) combo.SelectedItem = option;
        }
        return combo;
    }
    private static T Selected<T>(ComboBox combo) where T : struct => (combo.SelectedItem as ComboBoxItem)?.Tag is T value ? value : throw new ArgumentException("请选择一个有效选项。");
    private static List<int> ParseVlans(string text)
    {
        var result = new SortedSet<int>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var bounds = part.Split('-', StringSplitOptions.TrimEntries);
            if (bounds.Length > 2 || !int.TryParse(bounds[0], out var first) || first is < 0 or > 4094) throw new ArgumentException("VLAN 列表格式不正确。");
            int last = first;
            if (bounds.Length == 2 && (!int.TryParse(bounds[1], out last) || last < first || last > 4094)) throw new ArgumentException("VLAN 范围不正确。");
            for (int id = first; id <= last; id++) result.Add(id);
        }
        return result.ToList();
    }
    private sealed record SwitchChoice(Guid Id, string Name);
}
