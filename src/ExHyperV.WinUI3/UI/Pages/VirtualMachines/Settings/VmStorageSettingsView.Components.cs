using System.IO;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.WinUI3.Localization;
using ExHyperV.WinUI3.UI.Shared;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmStorageSettingsView
{
    private static readonly string[] DiskExtensions = [".vhdx", ".vhd", ".avhdx", ".avhd"];
    private static readonly int[] CapacityStops = [16, 32, 40, 50, 64, 80, 100, 128, 160, 200, 256, 320, 512, 768, 1024, 1536, 2048, 4096];

    // Core and provider messages may use punctuation from the host locale.
    private static string Clean(string text) => text.Replace('\u003b', '，').Replace('\uff1b', '，').Replace('\u00b7', ' ');
    private static bool SamePath(string? a, string? b) => string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);
    private static TextBlock Text(string value, double size = 14) =>
        new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private static StackPanel Stack(params UIElement[] children)
    {
        var stack = new StackPanel { Spacing = 8 };
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }

    private static Grid WithLeading(FrameworkElement leading, FrameworkElement content)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new());
        grid.Children.Add(leading);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return grid;
    }

    private static Button Command(string label, Action click, bool accent = false, bool enabled = true)
    {
        var b = new Button { Content = label, IsEnabled = enabled };
        if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        AutomationProperties.SetName(b, label);
        b.Click += (_, _) => click();
        return b;
    }

    private static Button InlineCommand(string label, Action click, bool enabled = true, string? tooltip = null)
    {
        var b = Command(label, click, enabled: enabled);
        b.Height = 24;
        b.MinHeight = 24;
        b.Padding = new(10, 0, 10, 0);
        b.FontSize = 12;
        if (!string.IsNullOrEmpty(tooltip)) ToolTipService.SetToolTip(b, Clean(tooltip));
        return b;
    }

    private Border Banner(string message, InfoBarSeverity severity = InfoBarSeverity.Informational,
        FrameworkElement? action = null, FrameworkElement? inlineExtra = null, string? tooltip = null)
    {
        var (background, accent, glyph) = severity switch
        {
            InfoBarSeverity.Error => ("InfoBarErrorSeverityBackgroundBrush", "SystemFillColorCriticalBrush", "\uE711"),
            InfoBarSeverity.Warning => ("InfoBarWarningSeverityBackgroundBrush", "SystemFillColorCautionBrush", "\uE7BA"),
            InfoBarSeverity.Success => ("InfoBarSuccessSeverityBackgroundBrush", "SystemFillColorSuccessBrush", "\uE73E"),
            _ => ("InfoBarInformationalSeverityBackgroundBrush", "SystemFillColorAttentionBrush", "\uE946")
        };
        var icon = new FontIcon
        {
            Glyph = glyph, FontFamily = new(SegoeFont), FontSize = 13,
            Foreground = ThemeBrush(this, accent), VerticalAlignment = VerticalAlignment.Center
        };
        var titleText = new TextBlock
        {
            Text = Clean(message), FontSize = 13,
            Foreground = ThemeBrush(this, "TextFillColorPrimaryBrush"),
            TextWrapping = inlineExtra == null ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        FrameworkElement textArea = titleText;
        if (inlineExtra != null)
        {
            var inline = new Grid { ColumnSpacing = 10, VerticalAlignment = VerticalAlignment.Center };
            inline.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            inline.ColumnDefinitions.Add(new());
            inline.Children.Add(titleText);
            Grid.SetColumn(inlineExtra, 1);
            inline.Children.Add(inlineExtra);
            textArea = inline;
        }
        var line = WithLeading(icon, textArea);
        var banner = new Border
        {
            MinHeight = 36, CornerRadius = new(6), BorderThickness = new(1), Padding = new(12, 7, 12, 7),
            BorderBrush = ThemeBrush(this, "CardStrokeColorDefaultBrush"), Background = ThemeBrush(this, background),
            Child = action == null ? line : Split(line, action)
        };
        ToolTipService.SetToolTip(banner, string.IsNullOrWhiteSpace(tooltip) ? Clean(message) : Clean(tooltip));
        AutomationProperties.SetLiveSetting(banner, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        return banner;
    }

    private Border GroupedCard(params UIElement[] rows)
    {
        var card = Card(rows);
        card.Margin = new(0);
        if (card.Child is StackPanel stack)
        {
            foreach (var divider in stack.Children.OfType<Border>())
                divider.Margin = new(0);
        }
        return card;
    }

    private static Border CardRow(UIElement content, Thickness? padding = null, Brush? background = null) => new()
    {
        Padding = padding ?? new(16, 13, 16, 13),
        Background = background,
        Child = content
    };

    private static Border SettingRow(string title, string? description, FrameworkElement control,
        bool enabled = true, string? disabledReason = null)
    {
        var label = SettingLabel(title, description, out var info);
        info.SetDescription(description, disabledReason);
        label.Opacity = enabled ? 1.0 : 0.45;
        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, title);
        var row = Split(label, control);
        row.MinHeight = 32;
        return CardRow(row);
    }

    private Border FieldRowCard(string title, string? description, string? hint, FrameworkElement content,
        FrameworkElement? footer = null, bool enabled = true, string? disabledReason = null)
    {
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new());
        head.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        FrameworkElement titleEl;
        if (!string.IsNullOrEmpty(description) || !string.IsNullOrEmpty(disabledReason))
        {
            var lbl = SettingLabel(title, description, out var info);
            info.SetDescription(description, disabledReason);
            titleEl = lbl;
        }
        else
        {
            titleEl = new TextBlock { Text = title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        }
        titleEl.Opacity = enabled ? 1.0 : 0.45;
        head.Children.Add(titleEl);
        if (!string.IsNullOrEmpty(hint))
        {
            var hintText = new TextBlock
            {
                Text = Clean(hint), FontSize = 12,
                Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(hintText, 1);
            head.Children.Add(hintText);
        }
        var stack = new StackPanel { Spacing = 8, Children = { head, content } };
        if (footer != null) stack.Children.Add(footer);
        AutomationProperties.SetName(content, title);
        return CardRow(stack);
    }

    private Border PanelCard(params UIElement[] fields)
    {
        var content = Stack(fields);
        content.Padding = new(16, 14, 16, 14);
        content.Spacing = 12;
        return CardShell(this, content);
    }

    private static ContentControl EditorHost(UIElement content) => new()
    {
        IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = content
    };

    private static FrameworkElement Field(string label, FrameworkElement input)
    {
        AutomationProperties.SetName(input, label);
        return Stack(Text(label, 13), input);
    }

    private static SettingsSegmentedPill Pill(string selected, Action<string> change, params SettingOption[] options)
    {
        var pill = new SettingsSegmentedPill();
        pill.SetOptions(options);
        pill.SelectedValue = selected;
        pill.SelectionChanged += value => change((string)value!);
        return pill;
    }

    private ContentControl PathInput(string value, string placeholder, Func<Task<string?>> pick, Action<string> change,
        out TextBox input, string buttonLabel = "浏览")
    {
        var box = new TextBox { Text = value, PlaceholderText = placeholder };
        AutomationProperties.SetName(box, placeholder);
        box.TextChanged += (_, _) => change(box.Text.Trim());
        var browse = Command(buttonLabel, () => _ = RunAsync(async () =>
        {
            var path = await pick();
            if (path != null && !IsStopped && box.IsLoaded) box.Text = path;
        }));
        input = box;
        return EditorHost(Split(box, browse));
    }

    private static Task<string?> OpenMedia(bool dvd) => StoragePickers.OpenAsync(dvd ? [".iso"] : DiskExtensions);
    private static Task<string?> SaveMedia(bool dvd) => dvd ? StoragePickers.SaveAsync("NewISO.iso", "ISO 映像", ".iso")
        : StoragePickers.SaveAsync("NewDisk.vhdx", "虚拟硬盘", ".vhdx", ".vhd");

    private Border CapacitySection(string title, string? description, string value, Action<string> change,
        UIElement? footer = null, bool enabled = true, string? disabledReason = null)
    {
        var box = new TextBox { Text = value, Width = 86, TextAlignment = TextAlignment.Right, IsEnabled = enabled };
        AutomationProperties.SetName(box, $"{title}（GiB）");
        var slider = new SettingsEnergySlider(EnergySliderKind.Custom, compact: true)
        {
            CustomHintFormatter = i => $"{CapacityStops[i]} GiB",
            VerticalAlignment = VerticalAlignment.Center
        };
        slider.SetRange(0, CapacityStops.Length - 1);
        slider.IsEnabled = enabled;
        void SyncSlider()
        {
            if (int.TryParse(box.Text, out var n))
                slider.Value = Enumerable.Range(0, CapacityStops.Length).MinBy(i => Math.Abs((long)CapacityStops[i] - n));
        }
        SyncSlider();
        box.TextChanged += (_, _) => { SyncSlider(); change(box.Text.Trim()); };
        slider.ValueChanged += i => box.Text = CapacityStops[i].ToString();
        AutomationProperties.SetName(slider, "容量档位");

        var label = SettingLabel(title, description, out var info);
        info.SetDescription(description, disabledReason);
        label.Opacity = enabled ? 1.0 : 0.45;
        var unit = new TextBlock
        {
            Text = "GiB", FontSize = 12,
            Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = enabled ? 1.0 : 0.45
        };
        var head = Split(label, Actions(6, box, unit));
        var stack = new StackPanel { Spacing = 8, Children = { head, slider } };
        if (footer != null) stack.Children.Add(footer);
        return CardRow(stack);
    }

    private sealed record FileCheck(bool Valid, string Message, InfoBarSeverity Severity)
    {
        public static FileCheck Error(string message) => new(false, message, InfoBarSeverity.Error);
        public static FileCheck Ok(string message = "") => new(true, message, InfoBarSeverity.Success);
    }

    private static FileCheck CheckMedia(string path, bool dvd, bool create)
    {
        if (path.Length == 0 && dvd && !create) return FileCheck.Ok("将连接空光驱。");
        if (!Path.IsPathFullyQualified(path)) return FileCheck.Error("请输入完整的介质文件路径。");
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (!(dvd ? ext == ".iso" : create ? ext is ".vhdx" or ".vhd" : DiskExtensions.Contains(ext)))
            return FileCheck.Error(dvd ? "请选择 .iso 文件。" : create ? "新磁盘须使用 .vhd 或 .vhdx 扩展名。" : "请选择 VHD 或 VHDX 磁盘文件。");
        if (create)
        {
            if (File.Exists(path) || Directory.Exists(path)) return FileCheck.Error("目标路径已存在，请选择新的文件名。");
            return !dvd && !Directory.Exists(Path.GetDirectoryName(path)) ? FileCheck.Error("目标文件夹不存在。") : FileCheck.Ok();
        }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (dvd)
            {
                // ISO 9660 and UDF volume recognition sequences start at logical sector 16.
                bool recognized = false;
                Span<byte> descriptor = stackalloc byte[6];
                for (int sector = 16; sector < 48 && stream.Length >= sector * 2048L + descriptor.Length; sector++)
                {
                    stream.Position = sector * 2048L;
                    stream.ReadExactly(descriptor);
                    var id = descriptor[1..];
                    if (id.SequenceEqual("CD001"u8) || id.SequenceEqual("NSR02"u8) || id.SequenceEqual("NSR03"u8))
                    {
                        recognized = true;
                        break;
                    }
                }
                if (!recognized) return FileCheck.Error("未识别到 ISO 9660 或 UDF 卷描述符，请核对映像文件。");
            }
            else
            {
                bool vhdx = ext.EndsWith('x');
                if (!vhdx && stream.Length >= 512) stream.Position = stream.Length - 512;
                Span<byte> signature = stackalloc byte[8];
                stream.ReadExactly(signature);
                if (!signature.SequenceEqual(vhdx ? "vhdxfile"u8 : "conectix"u8))
                    return FileCheck.Error("文件内容与磁盘扩展名不匹配。");
            }
            return FileCheck.Ok("文件可读取，介质格式检查通过。");
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        {
            return new(true, "介质正在使用，暂时无法核验文件内容。请检查宿主挂载和其他进程。", InfoBarSeverity.Warning);
        }
        catch (Exception ex) { return FileCheck.Error(CoreMessageResolver.ResolveException(ex)); }
    }

    // Cancel the draft's wait even when a provider has no cancellation overload. Only the
    // current draft may publish a result, and this helper is never used for submitted writes.
    private sealed class DraftProbe
    {
        private CancellationTokenSource? current;
        public void Cancel() { current?.Cancel(); current = null; }

        public async Task StartAsync(CancellationToken lifetime, Func<CancellationToken, Task> read, Action<string> failure)
        {
            Cancel();
            using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            current = request;
            try
            {
                await Task.Delay(220, request.Token);
                await read(request.Token);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception ex) { if (!request.IsCancellationRequested) failure(Clean(CoreMessageResolver.ResolveException(ex))); }
            finally { if (ReferenceEquals(current, request)) current = null; }
        }
    }

    private sealed class SlotDraft
    {
        public bool Automatic = true;
        public string Type = "";
        public int Number;
        public int Location;
        public string? Error;
    }

    private Border SlotCard(SlotDraft slot, string driveType, VmStorageAttachment? original, bool editable, Action changed)
    {
        var others = original == null ? attachments : attachments.Where(d => d.DeviceToken != original.DeviceToken).ToArray();
        bool active = original == null && running;
        var type = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var number = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var location = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var automatic = new ToggleSwitch { IsOn = slot.Automatic, OnContent = "", OffContent = "", MinWidth = 0 };
        var notice = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new());
        grid.ColumnDefinitions.Add(new());
        grid.ColumnDefinitions.Add(new());
        FrameworkElement Col(string lbl, ComboBox combo)
        {
            AutomationProperties.SetName(combo, lbl);
            return new StackPanel
            {
                Spacing = 6,
                Children = { new TextBlock { Text = lbl, FontSize = 12.5, Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush") }, combo }
            };
        }
        var c0 = Col("控制器类型", type);
        var c1 = Col("编号", number);
        var c2 = Col("位置", location);
        Grid.SetColumn(c1, 1);
        Grid.SetColumn(c2, 2);
        grid.Children.Add(c0);
        grid.Children.Add(c1);
        grid.Children.Add(c2);

        var fieldsStack = new StackPanel { Spacing = 8 };
        if (original != null)
            fieldsStack.Children.Add(new TextBlock { Text = "控制器插槽", FontSize = 14 });
        fieldsStack.Children.Add(grid);
        fieldsStack.Children.Add(notice);
        var fieldsRow = CardRow(fieldsStack);

        var summary = new TextBlock
        {
            FontSize = 12,
            Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        bool syncing = false;
        void Sync()
        {
            syncing = true;
            try
            {
                var plan = slot.Automatic ? VmStorageService.GetBestSlot(generation, active, driveType, others) : null;
                var types = VmStorageService.GetControllerTypes(generation, active, driveType);
                slot.Type = plan?.Slot?.ControllerType ?? (types.Contains(slot.Type) ? slot.Type : types.FirstOrDefault() ?? "");
                var nums = VmStorageService.GetControllerNumbers(slot.Type);
                slot.Number = plan?.Slot?.ControllerNumber ?? (nums.Contains(slot.Number) ? slot.Number : nums.FirstOrDefault());
                var locations = VmStorageService.GetAvailableLocations(slot.Type, slot.Number, others);
                slot.Location = plan?.Slot?.Location ?? (locations.Contains(slot.Location) ? slot.Location : locations.FirstOrDefault(-1));
                type.ItemsSource = types;
                type.SelectedItem = slot.Type;
                number.ItemsSource = nums;
                number.SelectedItem = slot.Number;
                location.ItemsSource = locations;
                location.SelectedItem = slot.Location;
                type.IsEnabled = number.IsEnabled = location.IsEnabled = editable && !slot.Automatic;
                if (original == null) fieldsRow.Visibility = slot.Automatic ? Visibility.Collapsed : Visibility.Visible;
                summary.Text = slot.Automatic && slot.Location >= 0
                    ? $"自动分配：{slot.Type} 控制器 {slot.Number}（位置 {slot.Location}）"
                    : "手动选择控制器、编号和位置";
                slot.Error = plan?.Error ?? VmStorageService.GetAddSlotError(generation, active, new()
                {
                    DriveType = driveType, ControllerType = slot.Type, ControllerNumber = slot.Number, Location = slot.Location
                }, others);
                notice.Content = slot.Error == null ? null : Banner(slot.Error, InfoBarSeverity.Warning);
                notice.Visibility = slot.Error == null ? Visibility.Collapsed : Visibility.Visible;
            }
            finally { syncing = false; }
            changed();
        }
        type.SelectionChanged += (_, _) => { if (!syncing && type.SelectedItem is string v) { slot.Type = v; Sync(); } };
        number.SelectionChanged += (_, _) => { if (!syncing && number.SelectedItem is int v) { slot.Number = v; Sync(); } };
        location.SelectionChanged += (_, _) => { if (!syncing && location.SelectedItem is int v) { slot.Location = v; Sync(); } };
        automatic.Toggled += (_, _) => { slot.Automatic = automatic.IsOn; Sync(); };
        Sync();
        if (original != null) return CardRow(fieldsStack);
        var autoHeader = CardRow(Split(new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new TextBlock { Text = "自动选择插槽", FontSize = 14 }, summary }
        }, automatic));
        return GroupedCard(autoHeader, fieldsRow);
    }
}
