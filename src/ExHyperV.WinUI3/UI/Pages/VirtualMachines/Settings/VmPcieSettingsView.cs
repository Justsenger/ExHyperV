using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmPcieSettingsView : VmSettingsView
{
    private enum Page { List, Picker, Workflow }
    private readonly StackPanel body = new() { Spacing = 10, Margin = new(0, 4, 0, 20) };
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);
    private VmPcieSettings? settings;
    private VmState power = VmState.Unknown;
    private Page page;

    public VmPcieSettingsView(Guid vmId) : base(vmId)
    {
        scroll.Content = body;
        AttachScrollbarClearance(scroll);
        SetBody(scroll, handlesScrolling: true);
    }

    protected override async Task LoadAsync()
    {
        var read = Task.Run(() => VmPcieService.GetSettingsAsync(VmId));
        var context = ReadContextAsync();
        await Task.WhenAll(read, context);
        if (IsStopped) return;
        settings = Data(await read);
        power = (await context).State;
        RenderList();
    }

    internal void UpdatePowerState(VmState value)
    {
        if (power == value || IsStopped || settings == null) return;
        power = value;
        if (page == Page.List) RenderList();
    }

    private void BeginPage(Page next)
    {
        if (page != next) scroll.ChangeView(null, 0, null, true);
        page = next;
        body.Children.Clear();
        SetHeaderTrailingAction(null);
    }

    private void RenderList()
    {
        BeginPage(Page.List);
        if (settings == null) return;
        foreach (var device in settings.Devices)
        {
            string key = device.DeviceInstanceId + "|" + device.Path;
            bool open = expanded.Contains(key);
            void Toggle() { if (!expanded.Remove(key)) expanded.Add(key); RenderList(); }
            var disconnect = IconButton(this, "\uF60A", T("Disconnect"), () => _ = RunAsync(() => DisconnectAsync(device)), useFluentFont: true);
            var actions = Actions(4,
                WithLock(disconnect, power != VmState.PoweredOff ? T("DisconnectOff") :
                    string.IsNullOrEmpty(device.Path) || string.IsNullOrEmpty(device.DeviceInstanceId) ? T("IdentityChanged") : null),
                ExpandButton(open, Toggle));
            var title = Identity(device.Name, device.ClassType);
            var headerButton = new Button
            {
                Content = title, Padding = new(0), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch, Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"]
            };
            headerButton.Click += (_, _) => Toggle();
            var header = Split(headerButton, actions);
            header.Padding = new(16, 12, 12, 12);
            body.Children.Add(CardShell(this, header, open ? Details(device.ClassType, device.Vendor, device.DeviceInstanceId, device.Path) : null));
        }

        bool empty = settings.Devices.Count == 0;
        var add = new Button
        {
            Content = Identity(T("AddDevice"), "", "\uE710"),
            Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = ThemeBrush(this, empty ? "CardBackgroundFillColorDefaultBrush" : "SubtleFillColorTertiaryBrush"),
            BorderBrush = ThemeBrush(this, "CardStrokeColorDefaultBrush"), BorderThickness = new(1),
            Padding = new(16, 10, 16, 10), CornerRadius = new(8), MinHeight = empty ? 68 : 60
        };
        add.Click += async (_, _) => await RunAsync(OpenPickerAsync);
        body.Children.Add(WithLock(add, power == VmState.PoweredOff ? null : T("AddOff")));
    }

    private async Task DisconnectAsync(VmPcieDeviceSetting device)
    {
        power = (await ReadContextAsync()).State;
        if (power != VmState.PoweredOff) { RenderList(); ShowError(T("DisconnectOff")); return; }
        var inventory = Data(await HostPcieService.GetPCIeInfoAsync());
        var physical = inventory.Devices.SingleOrDefault(d => d.Assignment.VirtualMachineId == VmId
            && SameId(d.InstanceId, device.DeviceInstanceId) && SameId(d.Path, device.Path));
        if (physical == null) throw new InvalidOperationException(T("IdentityChanged"));
        CancellationToken.ThrowIfCancellationRequested();
        // Return just this function. The session also restores eligible disks on a returned controller.
        var session = new PcieAssignmentSession(physical, (PcieDeviceInfo[])[physical], PcieAssignmentTarget.Host);
        var result = await Task.Run(() => session.AdvanceAsync());
        NotifyChanged();
        try { await LoadAsync(); }
        catch (Exception ex) when (result.Step != PcieAssignmentStep.Completed)
        {
            ShowError(L10n.Format("SettingsView_ReadbackSuffix", result.Message, CoreMessageResolver.ResolveException(ex)));
            return;
        }
        if (result.Step == PcieAssignmentStep.Completed) ShowAppliedToast(F("Returned", device.Name));
        else ShowError(result.Message);
    }

    private Grid Details(string type, string vendor, string id, string path)
    {
        (FrameworkElement, bool) Tile(string key, string value, bool wide = false) =>
            (KvTile(this, T(key), string.IsNullOrWhiteSpace(value) ? "—" : value, wide ? value : null,
                L10n.Format("Dev_CopyValueTooltip", value), ShowError), wide);
        return KvGrid(Tile("Class", type), Tile("Vendor", vendor), Tile("InstanceId", id, true), Tile("Path", path, true));
    }

    private Grid Identity(string name, string type, string? glyph = null)
    {
        var badge = new Border
        {
            Width = 38, Height = 38, CornerRadius = new(7), BorderThickness = new(1),
            BorderBrush = ThemeBrush(this, "DividerStrokeColorDefaultBrush"), Background = ThemeBrush(this, "SubtleFillColorSecondaryBrush"),
            Child = new FontIcon { FontFamily = new FontFamily(SegoeFont), FontSize = 17, Glyph = glyph ?? DeviceGlyph(type, name) }
        };
        var title = new TextBlock { Text = name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTipService.SetToolTip(title, name);
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.Children.Add(badge);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        return grid;
    }

    private static string DeviceGlyph(string type, string name) => type.ToUpperInvariant() switch
    {
        "DISPLAY" => "\uF211", "NET" => "\uE839", "USB" => name.Contains("USB4", StringComparison.OrdinalIgnoreCase) ? "\uE945" : "\uECF0",
        "HIDCLASS" => "\uE928", "SCSIADAPTER" or "HDC" or "DISKDRIVE" => "\uEDA2", "COMPUTEACCELERATOR" => "\uEEA1",
        "MEDIA" => "\uE995", _ => "\uE950"
    };

    private Button ExpandButton(bool open, Action click) => IconButton(this, open ? "\uE70E" : "\uE70D", T(open ? "Collapse" : "Expand"), click);

    private static Border WithLock(Button button, string? reason)
    {
        var host = new Border { Child = button, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        if (reason != null)
        {
            button.IsEnabled = false;
            AutomationProperties.SetHelpText(button, reason);
            ToolTipService.SetToolTip(host, reason);
        }
        return host;
    }

    private static Button Command(string label, Action action, bool accent = false)
    {
        var button = new Button { Content = label };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => action();
        return button;
    }

    private static bool SameId(string a, string b) => StringComparer.OrdinalIgnoreCase.Equals(a, b);
    private static TData Data<TData>(ApiResponse<TData> response) => response.HasData
        ? response.Data! : throw new InvalidOperationException(CoreMessageResolver.ResolveError(response));
    private static string T(string key) => L10n.Get("Pcie_" + key);
    private static string F(string key, params object?[] args) => L10n.Format("Pcie_" + key, args);
}
