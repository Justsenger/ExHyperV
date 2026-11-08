using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

public sealed partial class VmDashboardView
{
    private readonly List<VmMetricCard> gpuCards = [];
    private readonly Dictionary<string, DeviceView> disks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceView> adapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TransferRateView> diskRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TransferRateView> networkRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> networkAddresses = new(StringComparer.OrdinalIgnoreCase);
    private string? selectedDisk, selectedAdapter;

    private sealed class DeviceView
    {
        public required VmMetricCard Card { get; init; }
        public required StackPanel Details { get; init; }
        public StackPanel? Addresses { get; init; }
        public string Mac { get; init; } = "";
        public bool ExpandedAddresses { get; set; }
    }

    private void ResetDeviceSelection()
    {
        disks.Clear(); adapters.Clear(); gpuCards.Clear();
        DiskSelectors.Children.Clear(); NetworkSelectors.Children.Clear(); GpuSelectors.Children.Clear();
        DiskRows.Children.Clear(); NetworkRows.Children.Clear();
        selectedDisk = selectedAdapter = null;
    }

    private static void LayoutDeviceSelectors(Grid grid)
    {
        int columns = grid.ActualWidth is > 0 and < 560 ? 1 : 2;
        grid.ColumnDefinitions.Clear(); grid.RowDefinitions.Clear();
        for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new());
        for (int i = 0; i < grid.Children.Count; i++)
        {
            if (i % columns == 0) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Grid.SetColumn((FrameworkElement)grid.Children[i], i % columns); Grid.SetRow((FrameworkElement)grid.Children[i], i / columns);
        }
    }

    private void ShowDevices(VmQueryRow vm)
    {
        string? oldDisk = selectedDisk, oldAdapter = selectedAdapter;
        var storage = vm.GetCollection(VmQueryCollection.Storage);
        var network = vm.GetCollection(VmQueryCollection.NetworkAdapters);
        var gpu = vm.GetCollection(VmQueryCollection.GpuAssignments);
        ResetDeviceSelection(); diskRates.Clear(); networkRates.Clear(); networkAddresses.Clear();
        foreach (var disk in storage.Rows) AddDisk(disk);
        foreach (var adapter in network.Rows) AddNetwork(adapter);
        foreach (var item in gpu.Rows)
        {
            var card = new VmMetricCard { Title = Text(item, VmGpuFields.Name), Glyph = "\uF211", Unit = "%", Selected = gpuCards.Count == 0 };
            card.Click += (_, _) => { foreach (var entry in gpuCards) entry.Selected = ReferenceEquals(entry, card); };
            gpuCards.Add(card); GpuSelectors.Children.Add(card);
        }
        hasGpu = gpu.State == VmQueryValueState.Available ? gpu.Rows.Count > 0 : null;
        GpuText.Text = hasGpu == true ? string.Join("、", gpu.Rows.Select(g => Text(g, VmGpuFields.Name))) : hasGpu == false ? "无" : "";
        DeviceTabs.SetTitle(0, $"显卡 {gpu.Rows.Count}"); DeviceTabs.SetTitle(1, $"存储 {disks.Count}"); DeviceTabs.SetTitle(2, $"网络 {adapters.Count}");
        if (disks.Count == 0) AddEmptyRow(DiskRows, storage.State == VmQueryValueState.Available ? "未挂载磁盘" : storage.Error ?? "无法读取存储");
        else SelectDisk(oldDisk != null && disks.ContainsKey(oldDisk) ? oldDisk : disks.Keys.First());
        if (adapters.Count == 0) AddEmptyRow(NetworkRows, network.State == VmQueryValueState.Available ? "未配置网络适配器" : network.Error ?? "无法读取网络");
        else SelectAdapter(oldAdapter != null && adapters.ContainsKey(oldAdapter) ? oldAdapter : adapters.Keys.First());
        foreach (var grid in new[] { GpuSelectors, DiskSelectors, NetworkSelectors }) LayoutDeviceSelectors(grid);
        UpdateGpuVisibility(); UpdateIpSummary();
        if (gpu.State != VmQueryValueState.Available) GpuMessage.Text = gpu.Error ?? "无法读取显卡";
    }

    private void SelectDisk(string key)
    {
        selectedDisk = key;
        foreach (var pair in disks) pair.Value.Card.Selected = pair.Key == key;
        DiskRows.Children.Clear(); DiskRows.Children.Add(disks[key].Details);
    }
    private void SelectAdapter(string key)
    {
        selectedAdapter = key;
        foreach (var pair in adapters) pair.Value.Card.Selected = pair.Key == key;
        NetworkRows.Children.Clear(); NetworkRows.Children.Add(adapters[key].Details);
    }

    private void AddDisk(VmQueryRow row)
    {
        string path = Text(row, VmStorageFields.Path), kind = Text(row, VmStorageFields.DiskType);
        bool optical = kind.Contains("DVD", StringComparison.OrdinalIgnoreCase) || kind == "ISO" || Text(row, VmStorageFields.DriveType).Contains("DVD", StringComparison.OrdinalIgnoreCase);
        if (kind == "Empty")
        {
            var emptyCard = new VmMetricCard { Title = optical ? "光驱" : "磁盘", Glyph = optical ? "\uE958" : "\uEC59", Subtitle = "未插入介质" };
            var emptyDetails = new StackPanel();
            disks[row.Key] = new DeviceView { Card = emptyCard, Details = emptyDetails };
            emptyCard.Click += (_, _) => SelectDisk(row.Key); DiskSelectors.Children.Add(emptyCard);
            return;
        }
        string mediaType = row.Get(VmStorageFields.VirtualDiskType) is { HasValue: true } format ? format.Value! : "";
        string type = kind == "Physical" ? "物理磁盘" : optical ? "光盘映像" : mediaType.ToUpperInvariant() switch
        { "DYNAMIC" => "动态磁盘", "FIXED" => "固定磁盘", "DIFFERENCING" => "差异磁盘", _ => mediaType };
        string title = kind == "Physical" && row.Get(VmStorageFields.DiskModel) is { HasValue: true } model ? model.Value! : System.IO.Path.GetFileName(path);
        var capacity = row.Get(VmStorageFields.CapacityBytes); var size = row.Get(VmStorageFields.FileSizeBytes);
        var card = new VmMetricCard { Title = title, Glyph = optical ? "\uE958" : "\uEC59", Subtitle = type,
            SecondaryReading = capacity.HasValue && size.HasValue ? $"{FormatDiskSize(size.Value)} / {FormatDiskSize(capacity.Value)}" : capacity.HasValue ? FormatDiskSize(capacity.Value) : "" };
        ToolTipService.SetToolTip(card, path);
        var details = new StackPanel { Spacing = 10 };
        if (capacity.HasValue && size.HasValue)
        {
            var line = new Grid { ColumnSpacing = 12 }; line.ColumnDefinitions.Add(new()); line.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            line.Children.Add(CapacityLabel("文件占用", size.Value));
            var limit = CapacityLabel(optical || kind == "Physical" ? "容量" : "虚拟容量", capacity.Value);
            Grid.SetColumn(limit, 1); line.Children.Add(limit);
            var capacityArea = new StackPanel();
            capacityArea.Children.Add(line);
            capacityArea.Children.Add(CapacityMeter(size.Value, capacity.Value));
            details.Children.Add(capacityArea);
        }
        else if (capacity.HasValue)
            details.Children.Add(Label($"容量 {FormatDiskSize(capacity.Value)}"));
        else
        {
            var unavailable = Label("磁盘容量暂未取得");
            ToolTipService.SetToolTip(unavailable, capacity.Error ?? size.Error);
            details.Children.Add(unavailable);
        }
        var rate = new TransferRateView(false); diskRates[row.Key] = rate; details.Children.Add(rate.Charts);
        disks[row.Key] = new DeviceView { Card = card, Details = details };
        card.Click += (_, _) => SelectDisk(row.Key); DiskSelectors.Children.Add(card);
    }

    private void AddNetwork(VmQueryRow row)
    {
        bool connected = row.Get(VmNetworkFields.IsConnected) is not { HasValue: true, Value: false };
        string connection = connected ? Text(row, VmNetworkFields.SwitchName) : "未连接";
        var card = new VmMetricCard { Title = Text(row, VmNetworkFields.Name), Glyph = "\uE839", Subtitle = connection };
        var details = new StackPanel { Spacing = 12 }; var addresses = new StackPanel { Spacing = 4 };
        details.Children.Add(addresses);
        var rate = new TransferRateView(true, summary => card.SecondaryReading = summary); networkRates[row.Key] = rate; details.Children.Add(rate.Charts);
        adapters[row.Key] = new DeviceView { Card = card, Details = details, Addresses = addresses, Mac = Text(row, VmNetworkFields.MacAddress) };
        card.Click += (_, _) => SelectAdapter(row.Key); NetworkSelectors.Children.Add(card);
        UpdateNetworkAddress(row, true);
    }

    private static TextBlock Label(string text, double size = 12) => new() { Text = text, FontSize = size, Opacity = .8, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private static TextBlock CapacityLabel(string label, long bytes)
    {
        string size = FormatDiskSize(bytes);
        int separator = size.LastIndexOf(' ');
        var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom };
        var caption = new Run { Text = label + "  " };
        text.Inlines.Add(caption);
        text.Inlines.Add(new Run { Text = size[..separator], FontSize = 22 });
        text.Inlines.Add(new Run { Text = size[separator..] });
        void ApplyTheme() => caption.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        ApplyTheme(); text.ActualThemeChanged += (_, _) => ApplyTheme();
        return text;
    }
    private static FrameworkElement CapacityMeter(long occupied, long capacity)
    {
        double ratio = capacity > 0 ? Math.Clamp((double)occupied / capacity, 0, 1) : 0;
        var meter = new Grid { Height = 8, Margin = new Thickness(0, 8, 0, 2) };
        var track = new Border { CornerRadius = new CornerRadius(3), Opacity = .08 };
        var fill = new Border { CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)Application.Current.Resources["VmHeaderCheckedBrush"] };
        void ApplyTheme() => track.Background = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        ApplyTheme(); meter.ActualThemeChanged += (_, _) => ApplyTheme();
        meter.Children.Add(track); meter.Children.Add(fill);
        meter.SizeChanged += (_, _) => fill.Width = meter.ActualWidth * ratio;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(meter, $"文件占用 {FormatDiskSize(occupied)}，容量 {FormatDiskSize(capacity)}");
        return meter;
    }
    private static string FormatDiskSize(long bytes) => FormatRate(bytes, false);
    private static string FormatRate(double value, bool rate = true)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}" + (rate ? "/s" : "");
    }

    private void UpdateNetworkAddresses(VmQueryRow row)
    {
        foreach (var adapter in row.GetCollection(VmQueryCollection.NetworkAdapters).Rows) UpdateNetworkAddress(adapter);
        UpdateIpSummary();
    }
    private void UpdateNetworkAddress(VmQueryRow row, bool initial = false)
    {
        if (!adapters.TryGetValue(row.Key, out var view)) return;
        var addresses = row.Get(VmNetworkFields.IpAddresses);
        if (!addresses.HasValue && !initial) return;
        var ips = addresses.HasValue ? addresses.Value ?? [] : [];
        if (!initial && networkAddresses.TryGetValue(row.Key, out var previous) && previous.SequenceEqual(ips)) return;
        networkAddresses[row.Key] = ips;
        RenderAddresses(row.Key, view);
    }
    private void ClearNetworkAddresses()
    {
        if (networkAddresses.Values.All(values => values.Count == 0)) return;
        networkAddresses.Clear();
        foreach (var pair in adapters) RenderAddresses(pair.Key, pair.Value);
        UpdateIpSummary();
    }
    private void RenderAddresses(string key, DeviceView view)
    {
        var addresses = networkAddresses.GetValueOrDefault(key) ?? [];
        var ipv4 = addresses.Where(ip => ip.Contains('.') && !ip.Contains(':')).ToArray();
        var ipv6 = addresses.Where(ip => ip.Contains(':')).ToArray();
        var target = view.Addresses!; target.Children.Clear();
        var pair = new Grid { ColumnSpacing = 16 }; pair.ColumnDefinitions.Add(new()); pair.ColumnDefinitions.Add(new());
        pair.Children.Add(AddressGroup("MAC", [view.Mac]));
        var v4 = AddressGroup("IPv4", ipv4); Grid.SetColumn(v4, 1); pair.Children.Add(v4); target.Children.Add(pair);
        var v6 = AddressGroup("IPv6", view.ExpandedAddresses ? ipv6 : ipv6.Take(1).ToArray()); target.Children.Add(v6);
        if (ipv6.Length > 1)
        {
            var more = new Button { Content = view.ExpandedAddresses ? "收起" : $"另 {ipv6.Length - 1} 个", MinHeight = 30,
                Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(48, 0, 0, 0), Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"] };
            more.Click += (_, _) => { view.ExpandedAddresses = !view.ExpandedAddresses; RenderAddresses(key, view); };
            target.Children.Add(more);
        }
    }
    private FrameworkElement AddressGroup(string title, IReadOnlyList<string> values)
    {
        var grid = new Grid { ColumnSpacing = 8 }; grid.ColumnDefinitions.Add(new() { Width = new GridLength(40) }); grid.ColumnDefinitions.Add(new());
        grid.Children.Add(Label(title, 11));
        var list = new StackPanel { Spacing = 2 }; Grid.SetColumn(list, 1); grid.Children.Add(list);
        if (values.Count == 0) list.Children.Add(Label("未获取"));
        foreach (string address in values) list.Children.Add(CopyAddressButton(address, title));
        return grid;
    }
    private Button CopyAddressButton(string address, string type)
    {
        var icon = new FontIcon { Glyph = "\uE8C8", FontSize = 12, Opacity = .5 };
        var text = new TextBlock { Text = address, FontFamily = new FontFamily("Consolas"), FontSize = 13, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { ColumnSpacing = 6 }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(text); Grid.SetColumn(icon, 1); grid.Children.Add(icon);
        var button = new Button { Content = grid, HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 32, Padding = new Thickness(4), Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"] };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"复制 {type}：{address}");
        ToolTipService.SetToolTip(button, "点击复制");
        button.Click += async (_, _) =>
        {
            try
            {
                var data = new DataPackage(); data.SetText(address); Clipboard.SetContent(data);
                icon.Glyph = "\uE73E"; icon.Opacity = 1; ToolTipService.SetToolTip(button, "已复制");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button, "已复制");
                await Task.Delay(1600);
                icon.Glyph = "\uE8C8"; icon.Opacity = .5; ToolTipService.SetToolTip(button, "点击复制");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button, "");
            }
            catch (Exception error) { ShowError("复制地址失败：" + error.Message); }
        };
        return button;
    }
    private void UpdateIpSummary()
    {
        var ips = networkAddresses.Values.SelectMany(values => values).Distinct().ToArray();
        copiedIp = string.Join(Environment.NewLine, ips);
        IpText.Text = ips.FirstOrDefault(ip => ip.Contains('.') && !ip.Contains(':')) ?? ips.FirstOrDefault() ?? "---";
    }
    private void UpdateDeviceRates(VmQueryRow row)
    {
        foreach (var disk in row.GetCollection(VmQueryCollection.Storage).Rows)
            if (diskRates.TryGetValue(disk.Key, out var rate))
            {
                var read = disk.Get(VmStorageFields.ReadBytesPerSecond); var write = disk.Get(VmStorageFields.WriteBytesPerSecond);
                rate.Update(read.HasValue ? read.Value : null, write.HasValue ? write.Value : null);
            }
        foreach (var adapter in row.GetCollection(VmQueryCollection.NetworkAdapters).Rows)
            if (networkRates.TryGetValue(adapter.Key, out var rate))
            {
                var send = adapter.Get(VmNetworkFields.SendBytesPerSecond); var receive = adapter.Get(VmNetworkFields.ReceiveBytesPerSecond);
                rate.Update(send.HasValue ? send.Value : null, receive.HasValue ? receive.Value : null);
            }
    }
    private sealed class TransferRateView
    {
        public Grid Charts { get; } = new() { ColumnSpacing = 8 };
        private readonly VmUsageChart first, second;
        private readonly Action<string>? updateSummary;
        private double? firstValue, secondValue;
        public TransferRateView(bool network, Action<string>? updateSummary = null)
        {
            this.updateSummary = updateSummary;
            first = new(network ? "发送" : "读取", VmUsageChartKind.Transfer);
            second = new(network ? "接收" : "写入", VmUsageChartKind.Transfer);
            Charts.ColumnDefinitions.Add(new()); Charts.ColumnDefinitions.Add(new());
            Charts.Children.Add(first); Grid.SetColumn(second, 1); Charts.Children.Add(second);
        }
        public void Update(double? a, double? b)
        {
            if (a.HasValue) { firstValue = a; first.Add(a.Value, FormatRate(a.Value)); }
            if (b.HasValue) { secondValue = b; second.Add(b.Value, FormatRate(b.Value)); }
            if (firstValue.HasValue && secondValue.HasValue) updateSummary?.Invoke($"↑ {FormatRate(firstValue.Value)}  ↓ {FormatRate(secondValue.Value)}");
        }
        public void SetInactive() { first.SetInactive(); second.SetInactive(); firstValue = secondValue = null; updateSummary?.Invoke(""); }
    }
}
