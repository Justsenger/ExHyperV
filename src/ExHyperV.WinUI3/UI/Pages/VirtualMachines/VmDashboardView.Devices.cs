using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

public sealed partial class VmDashboardView
{
    private sealed class DiskEntry
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public required string RawSubtitle { get; init; }
        public string? Tooltip { get; init; }
        public bool IsOptical { get; init; }
        public string? Pill { get; init; }
        public string ControllerSummary { get; init; } = "";
        public required StackPanel Details { get; init; }
        public TransferRateView? Rate { get; init; }
        public string HeaderReadingText { get; set; } = "";
        public string RailStatText { get; set; } = "";
    }

    private sealed class NetworkEntry
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public required string RawSubtitle { get; init; }
        public required string SwitchDisplay { get; init; }
        public bool Connected { get; init; }
        public required StackPanel Details { get; init; }
        public required Border KvCard { get; init; }
        public required string Mac { get; init; }
        public TransferRateView? Rate { get; init; }
        public string RailStatText { get; set; } = "";
    }

    private sealed class GpuEntry
    {
        public required string Key { get; init; }
        public required string Title { get; init; }
        public string Manu { get; init; } = "";
        public string Vendor { get; init; } = "";
        public string MemoryText { get; init; } = MissingText;
        public string DriverVersion { get; init; } = MissingText;
        public string InstanceId { get; init; } = MissingText;
        public string ReadingText { get; set; } = "---";
    }

    private readonly List<DiskEntry> diskEntries = [];
    private readonly List<NetworkEntry> networkEntries = [];
    private readonly List<GpuEntry> gpuEntries = [];
    private readonly Dictionary<string, NetworkEntry> networkByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TransferRateView> diskRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TransferRateView> networkRates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<string>> networkAddresses = new(StringComparer.OrdinalIgnoreCase);

    private void ResetDeviceSelection()
    {
        diskEntries.Clear();
        networkEntries.Clear();
        gpuEntries.Clear();
        networkByKey.Clear();
        DiskDetailContent.Children.Clear();
        NetworkDetailContent.Children.Clear();
        GpuKvCard.Child = null;
    }

    private void ShowDevices(VmQueryRow vm)
    {
        var storage = vm.GetCollection(VmQueryCollection.Storage);
        var network = vm.GetCollection(VmQueryCollection.NetworkAdapters);
        var gpu = vm.GetCollection(VmQueryCollection.GpuAssignments);

        ResetDeviceSelection();
        diskRates.Clear();
        networkRates.Clear();
        networkAddresses.Clear();

        foreach (var disk in storage.Rows) AddDisk(disk);
        foreach (var adapter in network.Rows) AddNetwork(adapter);
        foreach (var item in gpu.Rows) AddGpu(item);

        hasGpu = gpu.State == VmQueryValueState.Available ? gpuEntries.Count > 0 : null;
        if (gpu.State != VmQueryValueState.Available) GpuMessage.Text = gpu.Error ?? L10n.Get("Overview_GpuReadFailed");

        RebuildOverviewRail();
        UpdateGpuVisibility();
    }

    private void AddDisk(VmQueryRow row)
    {
        string path = Text(row, VmStorageFields.Path);
        string kind = Text(row, VmStorageFields.DiskType);
        bool optical = kind.Contains("DVD", StringComparison.OrdinalIgnoreCase)
            || kind == "ISO"
            || Text(row, VmStorageFields.DriveType).Contains("DVD", StringComparison.OrdinalIgnoreCase);

        string ctrlType = row.Get(VmStorageFields.ControllerType) is { HasValue: true } ct ? ct.Value! : "";
        int? ctrlNum = row.Get(VmStorageFields.ControllerNumber) is { HasValue: true } cn ? cn.Value : null;
        int? ctrlLoc = row.Get(VmStorageFields.ControllerLocation) is { HasValue: true } cl ? cl.Value : null;
        string slot = !string.IsNullOrEmpty(ctrlType) && ctrlNum.HasValue && ctrlLoc.HasValue
            ? L10n.Format("Dev_ControllerLocation", ctrlType, ctrlNum, ctrlLoc)
            : L10n.Get("Dev_VirtualStorageController");

        if (kind == "Empty")
        {
            string titleEmpty = L10n.Get(optical ? "Dev_VirtualOpticalDrive" : "Dev_VirtualDisk");
            var emptyDetails = new StackPanel { Spacing = 10 };
            emptyDetails.Children.Add(BuildTitleFirstKvGrid([
                new KvTileSpec(L10n.Get("Dev_Source"), titleEmpty),
                new KvTileSpec(L10n.Get("Dev_Path"), L10n.Get("Dev_NoMedia"), Wide: true)
            ]));
            diskEntries.Add(new DiskEntry
            {
                Key = row.Key,
                Title = titleEmpty,
                RawSubtitle = L10n.Get("Dev_NoMedia"),
                IsOptical = optical,
                Pill = optical ? L10n.Get("Dev_Empty") : null,
                ControllerSummary = slot,
                Details = emptyDetails,
                HeaderReadingText = "",
                RailStatText = ""
            });
            return;
        }

        string mediaType = row.Get(VmStorageFields.VirtualDiskType) is { HasValue: true } format ? format.Value! : "";
        bool isPhysical = kind == "Physical";
        string sourceText = isPhysical
            ? L10n.Get(optical ? "Dev_PhysicalOpticalDrive" : "Dev_PhysicalDisk")
            : L10n.Get(optical ? "Dev_VirtualOpticalDrive" : "Dev_VirtualDisk");
        string type = mediaType.ToUpperInvariant() switch
        {
            "DYNAMIC" => L10n.Get("Dev_VhdDynamic"),
            "FIXED" => L10n.Get("Dev_VhdFixed"),
            "DIFFERENCING" => L10n.Get("Dev_VhdDifferencing"),
            _ => string.IsNullOrWhiteSpace(mediaType) ? L10n.Get("Dev_VhdDynamic") : mediaType
        };
        string title = isPhysical && row.Get(VmStorageFields.DiskModel) is { HasValue: true } model
            ? model.Value!
            : IsMissingText(path)
                ? (optical ? L10n.Get(isPhysical ? "Dev_PhysicalOpticalDrive" : "Dev_VirtualOpticalDrive") : L10n.Get("Dev_VirtualHardDisk"))
                : System.IO.Path.GetFileName(path);

        var capacity = row.Get(VmStorageFields.CapacityBytes);
        var size = row.Get(VmStorageFields.FileSizeBytes);
        string headerReading = (optical || isPhysical)
            ? (capacity.HasValue ? FormatDiskSize(capacity.Value) : size.HasValue ? FormatDiskSize(size.Value) : "")
            : (capacity.HasValue && size.HasValue ? $"{FormatDiskSize(size.Value)} / {FormatDiskSize(capacity.Value)}"
                : capacity.HasValue ? FormatDiskSize(capacity.Value) : "");

        var details = new StackPanel { Spacing = 10 };
        var entry = new DiskEntry
        {
            Key = row.Key,
            Title = title,
            RawSubtitle = title,
            Tooltip = path,
            IsOptical = optical,
            Pill = type,
            ControllerSummary = slot,
            Details = details,
            HeaderReadingText = headerReading,
            RailStatText = state == VmState.Running ? L10n.Format("Dev_DiskRate", "0 B/s", "0 B/s") : ""
        };

        var rate = new TransferRateView(network: false, singleReadOnly: optical, (a, b, combined) =>
        {
            if (a == null || b == null)
            {
                entry.RailStatText = "";
                if (overviewRailByKey.TryGetValue("disk:" + entry.Key, out var r))
                {
                    r.Sparkline.SetInactive();
                    r.StatText.Text = "";
                }
            }
            else
            {
                entry.RailStatText = L10n.Format("Dev_DiskRate", FormatRate(a.Value), FormatRate(b.Value));
                if (overviewRailByKey.TryGetValue("disk:" + entry.Key, out var r))
                {
                    r.Sparkline.Add(combined ?? 0);
                    r.StatText.Text = entry.RailStatText;
                }
            }
        });
        diskRates[row.Key] = rate;
        if (!optical && !isPhysical && capacity.HasValue && size.HasValue)
        {
            var meter = CapacityMeter(size.Value, capacity.Value);
            meter.Margin = new Thickness(0, 0, 0, 2);
            details.Children.Add(meter);
        }
        details.Children.Add(rate.Charts);

        string serial = row.Get(VmStorageFields.SerialNumber) is { HasValue: true } sn ? sn.Value ?? "" : "";
        string pathDisplay = isPhysical
            ? (!IsMissingText(path)
                ? path
                : row.Get(VmStorageFields.DiskNumber) is { HasValue: true } dn
                    ? (optical ? $"\\\\.\\CDROM{dn.Value}" : $"\\\\.\\PhysicalDrive{dn.Value}")
                    : title)
            : path;

        var kvSpecs = new List<KvTileSpec> { new(L10n.Get("Dev_Source"), sourceText) };
        if (isPhysical)
        {
            string serialDisplay = string.IsNullOrWhiteSpace(serial) ? "---" : serial;
            kvSpecs.Add(new KvTileSpec(L10n.Get("Dev_Serial"), serialDisplay, CopyValue: serialDisplay != "---" ? serialDisplay : null));
        }
        else if (!optical)
        {
            kvSpecs.Add(new KvTileSpec(L10n.Get("Dev_Type"), type));
        }
        kvSpecs.Add(new KvTileSpec(L10n.Get("Dev_Path"), pathDisplay, Wide: true, CopyValue: !IsMissingText(pathDisplay) ? pathDisplay : null));

        details.Children.Add(BuildTitleFirstKvGrid(kvSpecs));

        diskEntries.Add(entry);
    }

    private void AddNetwork(VmQueryRow row)
    {
        bool connected = row.Get(VmNetworkFields.IsConnected) is not { HasValue: true, Value: false };
        string switchName = row.Get(VmNetworkFields.SwitchName) is { HasValue: true } sw ? sw.Value ?? "" : "";
        bool isConnected = connected && !string.IsNullOrWhiteSpace(switchName);
        string connection = isConnected ? switchName : L10n.Get("Overview_Disconnected");
        string adapterTitle = Text(row, VmNetworkFields.Name);
        string mac = FormatMacAddress(Text(row, VmNetworkFields.MacAddress));

        var details = new StackPanel { Spacing = 10 };
        var kvCard = new Border();

        var entry = new NetworkEntry
        {
            Key = row.Key,
            Title = adapterTitle,
            RawSubtitle = connection,
            SwitchDisplay = connection,
            Connected = isConnected,
            Details = details,
            KvCard = kvCard,
            Mac = mac,
            RailStatText = state == VmState.Running && isConnected ? L10n.Format("Dev_NetRate", "0 B/s", "0 B/s") : ""
        };

        var rate = new TransferRateView(network: true, singleReadOnly: false, (a, b, combined) =>
        {
            if (a == null || b == null || !entry.Connected)
            {
                entry.RailStatText = "";
                if (overviewRailByKey.TryGetValue("net:" + entry.Key, out var r))
                {
                    r.Sparkline.SetInactive();
                    r.StatText.Text = "";
                }
            }
            else
            {
                entry.RailStatText = L10n.Format("Dev_NetRate", FormatRate(a.Value), FormatRate(b.Value));
                if (overviewRailByKey.TryGetValue("net:" + entry.Key, out var r))
                {
                    r.Sparkline.Add(combined ?? 0);
                    r.StatText.Text = entry.RailStatText;
                }
            }
        });
        networkRates[row.Key] = rate;
        details.Children.Add(rate.Charts);
        details.Children.Add(kvCard);

        networkEntries.Add(entry);
        networkByKey[row.Key] = entry;
        UpdateNetworkAddress(row, initial: true);
    }

    private void AddGpu(VmQueryRow row)
    {
        ulong? bytes = row.Get(VmGpuFields.MemoryBytes) is { HasValue: true } mem ? mem.Value : null;
        string memText = bytes switch
        {
            null => MissingText,
            _ => $"{bytes.Value / (1024d * 1024):0} MB"
        };
        gpuEntries.Add(new GpuEntry
        {
            Key = row.Key,
            Title = Text(row, VmGpuFields.Name),
            Manu = row.Get(VmGpuFields.Manu) is { HasValue: true } manu ? manu.Value ?? "" : "",
            Vendor = row.Get(VmGpuFields.Vendor) is { HasValue: true } vendor ? vendor.Value ?? "" : "",
            MemoryText = memText,
            DriverVersion = Text(row, VmGpuFields.DriverVersion),
            InstanceId = Text(row, VmGpuFields.HostDeviceInstanceId),
            ReadingText = gpuReadingText
        });
    }

    private static TextBlock Label(string text, double size = 12)
        => new() { Text = text, FontSize = size, Opacity = .8, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };

    private static TextBlock CapacityLabel(string label, long bytes)
    {
        string size = FormatDiskSize(bytes);
        int separator = size.LastIndexOf(' ');
        var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Bottom };
        var caption = new Run { Text = label + "  " };
        text.Inlines.Add(caption);
        text.Inlines.Add(new Run { Text = separator > 0 ? size[..separator] : size, FontSize = 20 });
        if (separator > 0) text.Inlines.Add(new Run { Text = size[separator..] });
        void ApplyTheme() => caption.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        ApplyTheme();
        text.ActualThemeChanged += (_, _) => ApplyTheme();
        return text;
    }

    private static FrameworkElement CapacityMeter(long occupied, long capacity)
    {
        double ratio = capacity > 0 ? Math.Clamp((double)occupied / capacity, 0, 1) : 0;
        var meter = new Grid { Height = 8, Margin = new Thickness(0, 8, 0, 2) };
        var track = new Border { CornerRadius = new CornerRadius(3), Opacity = .08 };
        var fill = new Border
        {
            CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)Application.Current.Resources["VmHeaderCheckedBrush"]
        };
        void ApplyTheme() => track.Background = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        ApplyTheme();
        meter.ActualThemeChanged += (_, _) => ApplyTheme();
        meter.Children.Add(track);
        meter.Children.Add(fill);
        meter.SizeChanged += (_, _) => fill.Width = meter.ActualWidth * ratio;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(meter, L10n.Format("Dev_DiskUsageAria", FormatDiskSize(occupied), FormatDiskSize(capacity)));
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
        foreach (var adapter in row.GetCollection(VmQueryCollection.NetworkAdapters).Rows)
            UpdateNetworkAddress(adapter);
    }

    private void UpdateNetworkAddress(VmQueryRow row, bool initial = false)
    {
        if (!networkByKey.TryGetValue(row.Key, out var view)) return;
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
        foreach (var pair in networkByKey) RenderAddresses(pair.Key, pair.Value);
    }

    private static string FormatMacAddress(string raw)
    {
        if (IsMissingText(raw)) return raw;
        Span<char> hex = stackalloc char[12];
        int count = 0;
        foreach (char c in raw)
        {
            if (Uri.IsHexDigit(c))
            {
                if (count < 12) hex[count++] = char.ToUpperInvariant(c);
                else return raw;
            }
        }
        if (count != 12) return raw;
        return $"{hex[0..2]}:{hex[2..4]}:{hex[4..6]}:{hex[6..8]}:{hex[8..10]}:{hex[10..12]}";
    }

    private void RenderAddresses(string key, NetworkEntry view)
    {
        var addresses = networkAddresses.GetValueOrDefault(key) ?? [];
        var ipv4 = addresses.Where(ip => ip.Contains('.') && !ip.Contains(':')).ToArray();
        var ipv6 = addresses.Where(ip => ip.Contains(':')).ToArray();

        static KvTileSpec BuildIpSpec(string title, string[] list, bool wide) => list.Length switch
        {
            0 => new KvTileSpec(title, MissingText, Wide: wide),
            1 => new KvTileSpec(title, list[0], Wide: wide, CopyValue: list[0]),
            _ => new KvTileSpec(title, Wide: wide, MultiValues: list)
        };

        string macText = IsMissingText(view.Mac) ? MissingText : view.Mac;
        string? macCopy = IsMissingText(macText) ? null : macText;

        view.KvCard.Child = BuildTitleFirstKvGrid([
            BuildIpSpec(L10n.Get("Dev_Ipv4"), ipv4, wide: false),
            new KvTileSpec(L10n.Get("Dev_Mac"), macText, CopyValue: macCopy),
            BuildIpSpec(L10n.Get("Dev_Ipv6"), ipv6, wide: true)
        ]);
    }

    private Button CopyAddressButton(string address, string type)
    {
        var icon = new FontIcon { Glyph = "\uE8C8", FontSize = 12, Opacity = .5 };
        var text = new TextBlock
        {
            Text = address,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        var grid = new Grid { ColumnSpacing = 6 };
        grid.ColumnDefinitions.Add(new());
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);
        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 28,
            Padding = new Thickness(4, 2, 4, 2),
            Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"]
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, L10n.Format("Dev_CopyTypeAria", type, address));
        ToolTipService.SetToolTip(button, L10n.Get("Dev_CopyTooltip"));
        button.Click += async (_, _) =>
        {
            try
            {
                var data = new DataPackage();
                data.SetText(address);
                Clipboard.SetContent(data);
                icon.Glyph = "\uE73E";
                icon.Opacity = 1;
                ToolTipService.SetToolTip(button, L10n.Get("Dev_Copied"));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button, L10n.Get("Dev_Copied"));
                await Task.Delay(1600);
                icon.Glyph = "\uE8C8";
                icon.Opacity = .5;
                ToolTipService.SetToolTip(button, L10n.Get("Dev_CopyTooltip"));
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button, "");
            }
            catch (Exception error)
            {
                ShowError(L10n.Format("Dev_CopyAddrFailed", error.Message));
            }
        };
        return button;
    }

    private void UpdateDeviceRates(VmQueryRow row)
    {
        foreach (var disk in row.GetCollection(VmQueryCollection.Storage).Rows)
            if (diskRates.TryGetValue(disk.Key, out var rate))
            {
                var read = disk.Get(VmStorageFields.ReadBytesPerSecond);
                var write = disk.Get(VmStorageFields.WriteBytesPerSecond);
                rate.Update(read.HasValue ? read.Value : null, write.HasValue ? write.Value : null);
            }
        foreach (var adapter in row.GetCollection(VmQueryCollection.NetworkAdapters).Rows)
            if (networkRates.TryGetValue(adapter.Key, out var rate))
            {
                var send = adapter.Get(VmNetworkFields.SendBytesPerSecond);
                var receive = adapter.Get(VmNetworkFields.ReceiveBytesPerSecond);
                rate.Update(send.HasValue ? send.Value : null, receive.HasValue ? receive.Value : null);
            }
    }

    private sealed class TransferRateView
    {
        public Grid Charts { get; } = new() { ColumnSpacing = 8 };
        private readonly VmUsageChart first;
        private readonly VmUsageChart? second;
        private readonly Action<double?, double?, double?>? onUpdated;
        private double? firstValue, secondValue;

        public TransferRateView(bool network, bool singleReadOnly = false, Action<double?, double?, double?>? onUpdated = null)
        {
            this.onUpdated = onUpdated;
            first = new(L10n.Get(network ? "Dev_Send" : "Dev_Read"), VmUsageChartKind.Transfer);
            Charts.ColumnDefinitions.Add(new());
            Charts.Children.Add(first);
            if (!singleReadOnly)
            {
                second = new(L10n.Get(network ? "Dev_Receive" : "Dev_Write"), VmUsageChartKind.Transfer);
                Charts.ColumnDefinitions.Add(new());
                Grid.SetColumn(second, 1);
                Charts.Children.Add(second);
            }
        }

        public void SetAccentColor(Color? color)
        {
            first.SetAccentColor(color);
            second?.SetAccentColor(color);
        }

        public void Update(double? a, double? b)
        {
            if (a.HasValue) { firstValue = a; first.Add(a.Value, FormatRate(a.Value)); }
            if (second == null)
            {
                if (firstValue.HasValue)
                    onUpdated?.Invoke(firstValue, 0, firstValue.Value);
                return;
            }
            if (b.HasValue) { secondValue = b; second.Add(b.Value, FormatRate(b.Value)); }
            if (firstValue.HasValue && secondValue.HasValue)
                onUpdated?.Invoke(firstValue, secondValue, firstValue.Value + secondValue.Value);
        }

        public void SetInactive()
        {
            first.SetInactive();
            second?.SetInactive();
            firstValue = secondValue = null;
            onUpdated?.Invoke(null, null, null);
        }
    }
}
