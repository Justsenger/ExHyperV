using System.IO;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmStorageSettingsView : VmSettingsView
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> unresolvedChanges = new();
    private readonly StackPanel page = new() { Margin = new Thickness(24, 12, 24, 24), Spacing = 12 };
    private readonly StackPanel devices = new() { Spacing = 12 };
    private readonly ContentControl deviceHost = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ContentControl editor = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Button add = new() { Content = "添加存储", IsEnabled = false };
    private readonly Button refresh = new() { Content = "刷新" };
    private readonly InfoBar blockedWarning = new() { Severity = InfoBarSeverity.Warning, IsClosable = false };
    private IReadOnlyList<VmStorageAttachment> attachments = [];
    private int generation;
    private bool running;
    private bool snapshotKnown;
    private bool mutationBlocked;

    public VmStorageSettingsView(Guid vmId) : base(vmId)
    {
        if (unresolvedChanges.TryGetValue(vmId, out string? unresolved))
        {
            mutationBlocked = true;
            blockedWarning.Title = "此前的存储操作需要核对";
            blockedWarning.Message = unresolved;
            blockedWarning.IsOpen = true;
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        add.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"];
        actions.Children.Add(refresh);
        actions.Children.Add(add);
        page.Children.Add(blockedWarning);
        page.Children.Add(actions);
        deviceHost.Content = devices;
        page.Children.Add(deviceHost);
        page.Children.Add(editor);
        SetBody(page);
        refresh.Click += async (_, _) => await RunAsync(LoadAsync);
        add.Click += async (_, _) => await RunAsync(OpenAddAsync);
    }

    protected override async Task LoadAsync()
    {
        snapshotKnown = false;
        add.IsEnabled = false;
        deviceHost.IsEnabled = false;
        var contextTask = ReadContextAsync();
        var storageTask = Task.Run(() => VmStorageService.GetVmStorageItemsAsync(VmId));
        await Task.WhenAll(contextTask, storageTask);
        var storage = await storageTask;
        if (!storage.HasData) throw new InvalidOperationException(storage.Error);
        var context = await contextTask;
        if (IsStopped) return;
        generation = context.Generation;
        running = context.IsRunning;
        attachments = storage.Data;
        snapshotKnown = true;
        RenderDevices();
        deviceHost.IsEnabled = true;
        add.IsEnabled = !mutationBlocked && editor.Content is null;
    }

    private void RenderDevices()
    {
        devices.Children.Clear();
        if (attachments.Count == 0)
        {
            devices.Children.Add(new TextBlock { Text = "此虚拟机尚未挂接存储设备。", Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap });
            return;
        }
        foreach (var item in attachments)
        {
            var header = new Grid { ColumnSpacing = 16 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new FontIcon { Glyph = item.DriveType == "DvdDrive" ? "\uE958" : "\uEC59", FontSize = 26 });
            var label = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = DisplayName(item), FontSize = 15, TextWrapping = TextWrapping.Wrap });
            label.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.PathOrDiskNumber) ? "空驱动器" : FormatSize(item.DiskSizeGiB),
                Foreground = Brush("TextFillColorSecondaryBrush"), FontSize = 12
            });
            Grid.SetColumn(label, 1);
            header.Children.Add(label);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            if (item.DiskType != "Physical" && !string.IsNullOrWhiteSpace(item.PathOrDiskNumber))
                actions.Children.Add(ActionButton("\uF6B0", "在资源管理器中定位", () => RevealMediaAsync(item.PathOrDiskNumber), true));
            if (item.DriveType == "HardDisk" && item.DiskType == "Virtual" && !running)
                actions.Children.Add(ActionButton("\uF202", "压缩虚拟磁盘", () => CompactAsync(item)));
            if (VmStorageService.CanEditStoragePath(item))
            {
                var edit = ActionButton("\uF3DE", item.DriveType == "DvdDrive" ? item.HasIsoMedia ? "更换 ISO" : "插入 ISO" : "更换虚拟磁盘", () => EditMediaAsync(item));
                var editError = VmStorageService.GetEditStoragePathError(item, running);
                edit.IsEnabled &= editError is null;
                if (editError is not null) ToolTipService.SetToolTip(edit, editError);
                actions.Children.Add(edit);
            }
            if (item.HasIsoMedia)
                actions.Children.Add(ActionButton("\uE0BE", "弹出 ISO", () => ChangeAsync(
                    () => VmStorageService.ModifyDvdDrivePathAsync(VmId, item, string.Empty), "已弹出介质，光驱保留。")));
            var remove = ActionButton("\uF34D", item.DriveType == "DvdDrive" ? "移除光驱" : "移除挂接（保留磁盘文件）", () => ChangeAsync(
                () => VmStorageService.RemoveDriveAsync(VmId, item), "已移除存储挂接。"));
            if (running && item.ControllerType == "IDE")
            {
                remove.IsEnabled = false;
                ToolTipService.SetToolTip(remove, "运行中的虚拟机不能移除 IDE 设备。");
            }
            actions.Children.Add(remove);
            Grid.SetColumn(actions, 2);
            header.Children.Add(actions);

            var details = new StackPanel { Spacing = 10, Margin = new Thickness(16, 8, 16, 16) };
            details.Children.Add(Detail("类型", item.DiskType == "Physical" ? "宿主物理设备" : "虚拟介质文件"));
            details.Children.Add(Detail("插槽", $"{item.ControllerType} 控制器 {item.ControllerNumber} · 位置 {item.ControllerLocation}"));
            details.Children.Add(Detail("路径", string.IsNullOrWhiteSpace(item.PathOrDiskNumber) ? "空" : item.PathOrDiskNumber));
            if (item.IsPassthroughStale)
                details.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
                    Message = "物理磁盘的直通引用已失效。磁盘可能已在宿主联机，请核对当前设备后重新挂接。" });
            devices.Children.Add(new Expander { Header = header, Content = details, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch });
        }
    }

    private Button ActionButton(string glyph, string title, Func<Task> action, bool readOnly = false)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("ms-appx:///Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular") },
            Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["RoundIconButtonStyle"], IsEnabled = readOnly || !mutationBlocked };
        ToolTipService.SetToolTip(button, title);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, title);
        button.Click += async (_, _) => await RunAsync(action);
        return button;
    }

    private async Task EditMediaAsync(VmStorageAttachment item)
    {
        if (!CanChange()) return;
        var context = await ReadContextAsync();
        if (VmStorageService.GetEditStoragePathError(item, context.IsRunning) is string reason)
            throw new InvalidOperationException(reason);
        var path = await PickOpenAsync(item.DriveType == "DvdDrive" ? [".iso"] : [".vhd", ".vhdx", ".avhd", ".avhdx"]);
        if (path is null || IsStopped) return;
        await ChangeAsync(() => item.DriveType == "DvdDrive"
            ? VmStorageService.ModifyDvdDrivePathAsync(VmId, item, path)
            : VmStorageService.ModifyHardDrivePathAsync(VmId, item, path), "已更新介质路径。");
    }

    private async Task CompactAsync(VmStorageAttachment item)
    {
        if (!CanChange()) return;
        var context = await ReadContextAsync();
        if (context.IsRunning) throw new InvalidOperationException("请先关闭虚拟机，再压缩虚拟磁盘。");
        await ChangeAsync(() => VmStorageService.CompactDiskAsync(item.PathOrDiskNumber), "虚拟磁盘压缩完成。");
    }

    private bool CanChange() => snapshotKnown && !mutationBlocked && !IsStopped;

    private async Task ChangeAsync(Func<Task<ApiResponse>> operation, string successMessage)
    {
        if (!CanChange()) return;
        // 底层操作一旦提交必须等待结果，离开页面不会中断等待并伪装为已取消。
        CancellationToken.ThrowIfCancellationRequested();
        ApiResponse result;
        try { result = await Task.Run(operation); }
        catch (Exception error) { result = ApiResponse.Fail(error.Message, kind: ApiErrorKind.OutcomeUnknown); }
        await CompleteChangeAsync(result.Success, result.ErrorKind, result.Error, successMessage);
    }

    private async Task CompleteChangeAsync(bool success, ApiErrorKind kind, string error, string successMessage)
    {
        // 离页等待中的结果也要使概览缓存失效；失败或未知结果仍可能已经改变资源。
        NotifyChanged();
        if (kind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired)
        {
            mutationBlocked = true;
            var warning = error + "\n本次程序运行期间已停止对此虚拟机的存储修改。请先核对底层任务和实际资源；刷新列表不会自动重试或清理。";
            unresolvedChanges[VmId] = warning;
            if (IsStopped) { ShowError(warning); return; }
            blockedWarning.Title = kind == ApiErrorKind.OutcomeUnknown ? "操作结果尚未确认" : "需要处理剩余资源";
            blockedWarning.Message = warning;
            blockedWarning.IsOpen = true;
        }
        if (IsStopped) { if (!success) ShowError(error); return; }
        string? refreshError = null;
        try { await LoadAsync(); }
        catch (OperationCanceledException) when (IsStopped && success) { return; }
        catch (Exception readError) { refreshError = readError.Message; }
        if (!success || refreshError is not null)
            ShowError(string.Join(Environment.NewLine, new[] { success ? null : error, refreshError is null ? null : "回读存储状态失败：" + refreshError }
                .Where(text => !string.IsNullOrWhiteSpace(text))));
        else if (!IsStopped) ShowInformation(successMessage);
    }

    private static string DisplayName(VmStorageAttachment item)
    {
        if (item.DiskType == "Physical" && !string.IsNullOrWhiteSpace(item.DiskModel)) return item.DiskModel;
        if (item.DiskType != "Physical" && !string.IsNullOrWhiteSpace(item.PathOrDiskNumber))
            return Path.GetFileName(item.PathOrDiskNumber);
        return item.DriveType == "DvdDrive" ? "光驱" : "虚拟硬盘";
    }

    private static string FormatSize(double sizeGiB) => sizeGiB <= 0 ? "容量未知"
        : sizeGiB < 1.0 / 1024 ? $"{sizeGiB * 1048576:N0} KB" : sizeGiB < 1 ? $"{sizeGiB * 1024:N1} MB" : $"{sizeGiB:N1} GB";

    private static Grid Detail(string title, string value)
    {
        var row = new Grid { ColumnSpacing = 16 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock { Text = title, Foreground = Brush("TextFillColorSecondaryBrush") });
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        return row;
    }
}
