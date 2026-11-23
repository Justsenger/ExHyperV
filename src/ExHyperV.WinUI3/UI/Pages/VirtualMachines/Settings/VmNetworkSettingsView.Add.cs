using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmNetworkSettingsView
{
    private const string MacExample = "00:15:5D:01:2A:0E";
    private string? addMacExample;
    private bool adding, addLegacy;
    private Guid? addSwitchId;
    private VmNetworkAdapter? addDraft;
    private string? LegacyLock => generation switch
    {
        2 => "第 2 代虚拟机不支持旧版网卡。",
        1 when power == VmState.PoweredOff => null,
        1 => "关闭第 1 代虚拟机后可添加旧版网卡。",
        _ => "无法确认虚拟机代数，旧版网卡不可用。"
    };

    private void OpenAdd()
    {
        if (!CanWrite) return;
        adding = true;
        addMacExample = VmNetworkPolicyDraft.SuggestStaticMac(adapters);
        addSwitchId = switches.Keys.Cast<Guid?>().FirstOrDefault();
        addDraft = new VmNetworkAdapter();
        addLegacy = false;
        RenderAdd();
        ScrollToTop();
    }

    private void RenderAdd()
    {
        if (!adding) return;
        var form = addDraft ??= new VmNetworkAdapter();
        BeginPage(12);
        string? legacyLock = LegacyLock;
        var legacy = PortSwitch();
        legacy.IsEnabled = legacyLock == null;
        legacy.IsOn = addLegacy && legacy.IsEnabled;
        legacy.Toggled += (_, _) => addLegacy = legacy.IsOn;
        var group = Card(
            SwitchRow(addSwitchId, null, value => addSwitchId = value),
            MacRow(form),
            PortSetting("旧版网卡", "默认关闭，绝大多数系统请使用标准高速网卡。仅在第 1 代虚机安装 XP 等老旧系统，或需要传统 BIOS PXE 引导时开启。只能在关机状态添加，且不支持硬件加速。",
                legacy, legacyLock));
        group.Margin = new Thickness(0);
        // The grouped Web card's separators reach both edges.
        foreach (var divider in ((StackPanel)group.Child).Children.OfType<Border>()) divider.Margin = new Thickness(0);
        page.Children.Add(group);
        var busy = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        var submit = Command("添加", async () => await RunAsync(async () =>
        {
            if (!CanWrite) return;
            string? mac = form.IsStaticMac ? RequireStaticMac(form.MacAddress) : null;
            Guid? targetSwitch = addSwitchId;
            string success = targetSwitch is Guid id ? $"已添加网卡并连接到 {switches[id]}" : "已添加未连接的网卡";
            VmNetworkAdapterCreationResult result;
            busy.Visibility = Visibility.Visible;
            busy.IsActive = true;
            try { result = await VmNetworkService.AddNetworkAdapterAsync(VmId, targetSwitch, targetSwitch.HasValue, mac, addLegacy); }
            finally { busy.IsActive = false; busy.Visibility = Visibility.Collapsed; }
            if (result.Success && result.DeviceId is Guid createdId) CardExpansion.ExpandNew(VmId, createdId);
            string? error = result.Success ? null : string.Join(Environment.NewLine,
                new[] { result.Error, result.CleanupError, result.RetainedDeviceIds.Count == 0 ? null :
                    $"保留的网卡：{string.Join(", ", result.RetainedDeviceIds)}" }.Where(s => !string.IsNullOrWhiteSpace(s)));
            bool uncertain = result.AdapterState == VmNetworkAdapterCreationState.Unknown ||
                result.InitializationState == VmNetworkAdapterInitializationState.Unknown;
            await FinishWriteAsync(success, error, uncertain ? ApiErrorKind.OutcomeUnknown : result.ErrorKind);
            ScrollToTop();
        }), accent: true);
        var actions = Actions(busy, Command("返回", BackToList), submit);
        SetHeaderTrailingAction(actions);
    }

    private static ToggleSwitch PortSwitch()
    {
        var toggle = Switch();
        toggle.Width = 40;
        toggle.Resources["ToggleSwitchPreContentMargin"] = 6d;
        toggle.Resources["ToggleSwitchPostContentMargin"] = 6d;
        return toggle;
    }

    private FrameworkElement PortSetting(string title, string help, FrameworkElement control, string? lockReason = null)
    {
        var label = SettingLabel(title, help, out var info);
        info.SetDescription(help, lockReason);
        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, title);
        var row = Split(label, control);
        row.VerticalAlignment = VerticalAlignment.Center;
        row.MinHeight = 32;
        if (lockReason != null) ToolTipService.SetToolTip(row, lockReason);
        if (adding)
        {
            row.Margin = new Thickness(24, 16, 24, 16);
            row.ColumnSpacing = 24;
            if (lockReason != null) label.Opacity = 0.48;
            return row;
        }
        var card = PortCard(row);
        if (lockReason != null) card.Opacity = 0.55;
        return card;
    }

    private Border PortCard(Grid content, bool energy = false) => new()
    {
        Background = ThemeBrush(this, "CardBackgroundFillColorDefaultBrush"),
        BorderBrush = ThemeBrush(this, "CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = energy ? new Thickness(18, 16, 18, 16) : new Thickness(17, 14, 17, 14),
        MinHeight = energy ? 108 : 68,
        Child = content
    };

    private FrameworkElement SwitchRow(Guid? current, string? currentName, Action<Guid?> select)
    {
        var combo = new ComboBox { Width = 240, Height = 32, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        var disconnected = new ComboBoxItem { Content = "未连接" };
        if (!adding) combo.Items.Add(disconnected);
        foreach (var (id, name) in switches)
        {
            var option = new ComboBoxItem { Content = name, Tag = id };
            combo.Items.Add(option);
            if (id == current) combo.SelectedItem = option;
        }
        if (adding) combo.Items.Add(disconnected);
        if (!current.HasValue) combo.SelectedItem = disconnected;
        else if (current is Guid existing && !switches.ContainsKey(existing))
        {
            var missing = new ComboBoxItem { Content = $"{currentName}（当前不可用）", Tag = existing };
            combo.Items.Add(missing);
            combo.SelectedItem = missing;
        }
        combo.SelectionChanged += (_, _) => select((combo.SelectedItem as ComboBoxItem)?.Tag as Guid?);
        combo.IsEnabled = combo.Items.Count > 1;
        string noSwitch = adding ? "宿主机没有可用的虚拟交换机，可以先创建未连接的网卡。" : "宿主机没有可用的虚拟交换机。";
        string help = adding ? "选择要连接的宿主机虚拟交换机，或设为“未连接”" : "切换网卡接入的虚拟交换机，或设为“未连接”拔出虚拟网线";
        return PortSetting("虚拟交换机", help, combo, combo.IsEnabled ? null : noSwitch);
    }

    private string RequireStaticMac(string text) => VmNetworkService.NormalizeStaticMac(text)
        ?? throw new ArgumentException(addMacExample == null ? "静态 MAC 地址须为 12 位十六进制字符。"
            : $"静态 MAC 地址须为 12 位十六进制字符，例如 {addMacExample}。");

    private static void AttachCompactTextBehavior(TextBox box)
    {
        void Apply()
        {
            if (VisualTreeHelper.GetChildrenCount(box) == 0) return;
            var root = VisualTreeHelper.GetChild(box, 0);
            var zeroMargin = new Thickness(0);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                if (VisualTreeHelper.GetChild(root, i) is not FrameworkElement child) continue;
                if (child.Name is "ContentElement" or "PlaceholderTextContentPresenter")
                {
                    if (child.VerticalAlignment != VerticalAlignment.Center) child.VerticalAlignment = VerticalAlignment.Center;
                    if (child.Margin != zeroMargin) child.Margin = zeroMargin;
                }
                else if (child.Name == "DeleteButton")
                {
                    if (child.Width != 0) child.Width = child.MinWidth = child.MaxWidth = 0;
                    if (child.Visibility != Visibility.Collapsed) child.Visibility = Visibility.Collapsed;
                    if (child.IsHitTestVisible) child.IsHitTestVisible = false;
                    if (child is Control clearButton && clearButton.IsTabStop) clearButton.IsTabStop = false;
                }
            }
        }
        // Initially collapsed inputs (such as static MAC on Add/Connection) materialize their
        // template only once made visible, after Loaded has already run.
        box.Loaded += (_, _) => Apply();
        box.SizeChanged += (_, _) => Apply();
    }

    private static TextBox CompactTextBox(string text, string placeholder, double width, double height, bool monospace = false)
    {
        var input = new TextBox
        {
            Text = text,
            PlaceholderText = placeholder,
            Width = width,
            Height = height,
            MinHeight = height,
            FontSize = monospace ? 12.5 : 14,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        if (monospace) input.FontFamily = new FontFamily("Consolas");
        AttachCompactTextBehavior(input);
        return input;
    }

    private static string FormatMac(string address)
    {
        string? normalized = VmNetworkService.NormalizeStaticMac(address);
        return normalized == null ? address : string.Join(":", normalized.Chunk(2).Select(pair => new string(pair)));
    }

    private FrameworkElement MacRow(VmNetworkAdapter form, Action? changed = null)
    {
        var toggle = PortSwitch();
        AutomationProperties.SetName(toggle, "静态 MAC 地址");
        toggle.IsEnabled = MacLock == null;
        toggle.IsOn = form.IsStaticMac;
        string placeholder = adding ? addMacExample ?? "请输入静态 MAC 地址" : MacExample;
        var input = CompactTextBox(form.MacAddress, placeholder, adding ? 158 : 156, height: 30, monospace: true);
        input.IsEnabled = toggle.IsEnabled;
        AutomationProperties.SetName(input, "静态 MAC 地址输入框");
        string? dynamicMac = policy?.ReportedDynamicMac;
        var preview = new TextBlock
        {
            Text = dynamicMac ?? (policy?.Saved.IsStaticMac == true ? "自动分配" : "尚未分配"),
            FontFamily = new FontFamily("Consolas"), FontSize = 12.5,
            CharacterSpacing = 24, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush")
        };
        var copy = InlineMiniButton(new FontIcon
        {
            Glyph = "\uE8C8", FontFamily = new FontFamily(SegoeFont), FontSize = 11.5,
            Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush")
        }, "复制 MAC 地址");
        string? CopyValue() => form.IsStaticMac ? VmNetworkService.NormalizeStaticMac(input.Text) : dynamicMac;
        copy.Click += (_, _) =>
        {
            if (CopyValue() is string value && TryCopyText(FormatMac(value), ShowError))
                ShowAppliedToast("已复制 MAC 地址");
        };
        void Sync()
        {
            input.Visibility = form.IsStaticMac ? Visibility.Visible : Visibility.Collapsed;
            preview.Visibility = !adding && !form.IsStaticMac ? Visibility.Visible : Visibility.Collapsed;
            copy.Visibility = adding ? Visibility.Collapsed : Visibility.Visible;
            copy.IsEnabled = CopyValue() != null;
        }
        input.TextChanged += (_, _) =>
        {
            // A field losing focus during a power change can still raise TextChanged after the rollback.
            if (MacLock != null) return;
            form.MacAddress = input.Text;
            Sync();
            changed?.Invoke();
        };
        input.LostFocus += (_, _) =>
        {
            if (MacLock == null && VmNetworkService.NormalizeStaticMac(input.Text) is string valid) input.Text = FormatMac(valid);
        };
        toggle.Toggled += (_, _) =>
        {
            form.IsStaticMac = toggle.IsOn;
            Sync();
            changed?.Invoke();
        };
        Sync();
        string help = adding
            ? "关闭时由地址池动态分配，开启后可手动指定 12 位十六进制静态 MAC 地址，仅关机状态下可设置"
            : "关闭时由地址池自动分配，开启后可手动指定 12 位十六进制静态 MAC 地址，仅关机状态下可修改";
        return PortSetting("静态 MAC 地址", help, Actions(10, input, preview, copy, toggle), MacLock);
    }
}
