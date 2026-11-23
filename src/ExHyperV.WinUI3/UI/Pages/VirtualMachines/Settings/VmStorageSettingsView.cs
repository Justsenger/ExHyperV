using System.Collections.Concurrent;
using System.IO;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;
using Windows.System;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmStorageSettingsView : VmSettingsView
{
    private static readonly ConcurrentDictionary<Guid, string> unresolvedChanges = new();
    private readonly StackPanel page = new() { Spacing = 10 };
    private readonly ContentControl pivot = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly ScrollViewer scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly HashSet<string> expanded = [];
    private IReadOnlyList<VmStorageAttachment> attachments = [];
    private int generation;
    private bool running;
    private VmState powerState;
    private bool snapshotKnown;
    private AddDraft? adding;
    private DetailDraft? editing;
    private StorageTask? taskView;
    private readonly DraftProbe draftProbe = new();

    public VmStorageSettingsView(Guid vmId) : base(vmId)
    {
        var layout = new Grid { RowSpacing = 8 };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new());
        layout.Children.Add(pivot);
        scroll.Content = page;
        AttachScrollbarClearance(scroll, page, bottomSpacing: 24);
        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        SetBody(layout, handlesScrolling: true);
        ActualThemeChanged += (_, _) =>
        {
            if (!IsStopped && (snapshotKnown || taskView != null || adding != null || editing != null)) Render();
        };
    }

    private bool CanWrite => snapshotKnown && !IsStopped && !unresolvedChanges.ContainsKey(VmId);

    protected override async Task LoadAsync()
    {
        if (!snapshotKnown)
        {
            SetHeaderTrailingAction(null);
            page.Children.Clear();
            if (unresolvedChanges.TryGetValue(VmId, out var warning)) page.Children.Add(Banner(warning, InfoBarSeverity.Error));
            page.Children.Add(Text("正在读取存储设备…", 13));
        }
        try { await ReadSnapshotAsync(); }
        catch (Exception ex) when (!IsStopped)
        {
            pivot.Content = null;
            page.Children.Clear();
            if (unresolvedChanges.TryGetValue(VmId, out var warning)) page.Children.Add(Banner(warning, InfoBarSeverity.Error));
            page.Children.Add(Banner("读取存储设备失败。" + CoreMessageResolver.ResolveException(ex), InfoBarSeverity.Error));
            SetHeaderTrailingAction(Command("重新检测", () => _ = RunAsync(LoadAsync)));
            return;
        }
        ShowList();
    }

    private async Task ReadSnapshotAsync()
    {
        snapshotKnown = false;
        var contextTask = ReadContextAsync();
        var itemsTask = Task.Run(() => VmStorageService.GetVmStorageItemsAsync(VmId));
        await Task.WhenAll(contextTask, itemsTask).WaitAsync(CancellationToken);
        var context = await contextTask;
        attachments = Data(await itemsTask);
        (generation, powerState, running) = context;
        snapshotKnown = true;
    }

    private static T Data<T>(ApiResponse<T> result) => result.HasData ? result.Data!
        : throw new InvalidOperationException(Clean(CoreMessageResolver.ResolveError(result)));

    private static ApiResponse Failure<T>(ApiResponse<T> result) =>
        ApiResponse.Fail(CoreMessageResolver.ResolveError(result), result.Code, result.ErrorSource, result.Exception, result.ErrorKind);

    public override async Task StopAsync()
    {
        try { await base.StopAsync(); }
        finally
        {
            // The base can keep the page open to report a failure that arrived during navigation.
            if (!IsStopped) Render();
        }
    }

    private void ShowList()
    {
        draftProbe.Cancel();
        editing?.Cancel();
        adding = null;
        editing = null;
        taskView = null;
        Render();
        scroll.ChangeView(null, 0, null, true);
    }

    private void Render()
    {
        if (IsStopped) return;
        SetHeaderTrailingAction(null);
        pivot.Content = null;
        page.Children.Clear();
        if (unresolvedChanges.TryGetValue(VmId, out var warning))
            page.Children.Add(Banner(warning, InfoBarSeverity.Error));
        if (taskView != null) RenderTask(taskView);
        else if (adding != null) RenderAdd(adding);
        else if (editing != null) RenderDetails(editing);
        else RenderList();
    }

    private void RenderList()
    {
        SetHeaderTrailingAction(Command("刷新", () => _ = RunAsync(LoadAsync)));
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        var hoverBrush = ThemeBrush(this, "SubtleFillColorTertiaryBrush");

        foreach (var item in attachments)
        {
            bool open = expanded.Contains(item.DeviceToken);
            bool emptyOptical = IsDvd(item) && !Physical(item) && !item.HasIsoMedia;
            var icon = new Border
            {
                Width = 38, Height = 38, CornerRadius = new(7),
                Opacity = emptyOptical ? 0.62 : 1,
                Background = ThemeBrush(this, "SubtleFillColorSecondaryBrush"),
                BorderBrush = ThemeBrush(this, "DividerStrokeColorDefaultBrush"),
                BorderThickness = new(1),
                Child = new FontIcon
                {
                    Glyph = IsDvd(item) ? "\uE958" : Physical(item) ? "\uEC59" : "\uEDA2",
                    FontFamily = new FontFamily(SegoeFont),
                    FontSize = 17,
                    Foreground = ThemeBrush(this, "TextFillColorPrimaryBrush")
                }
            };
            var title = new TextBlock
            {
                Text = DisplayName(item), FontSize = 15, FontWeight = FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(title, DisplayName(item));
            var heading = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
            heading.ColumnDefinitions.Add(new());
            heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var badge = Badge(this, TypeLabel(item));
            badge.Height = 20;
            badge.Padding = new(7, 0, 7, 0);
            ((TextBlock)badge.Child).VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(badge, 1);
            heading.Children.Add(title);
            heading.Children.Add(badge);
            string sizes = item.VirtualCapacityGiB is double cap ? $"{Size(item.DiskSizeGiB)} / {Size(cap)}"
                : item.DiskType == "Empty" ? "未挂载" : Size(item.DiskSizeGiB);
            string status = item.IsPassthroughStale ? "直通引用失效" : item.MetadataState switch
            {
                VmDiskMetadataState.Missing => "文件缺失", VmDiskMetadataState.Locked => "介质被占用", _ => ""
            };
            var subtitle = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            subtitle.Children.Add(new TextBlock
            {
                Text = sizes, FontSize = 12,
                Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush")
            });
            if (status.Length > 0)
            {
                subtitle.Children.Add(new TextBlock
                {
                    Text = status, FontSize = 12,
                    Foreground = ThemeBrush(this, "SystemFillColorCautionBrush")
                });
            }
            var identity = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { heading, subtitle } };

            var gear = IconButton(this, "\uF6AA", "设备设置与磁盘工具", () => _ = RunAsync(() => OpenDetailsAsync(item)), useFluentFont: true);
            var media = item.HasIsoMedia
                ? IconButton(this, "\uE0BE", "弹出 ISO", () => _ = RunAsync(() => RunSingleAsync("弹出 ISO",
                    () => VmStorageService.ModifyDvdDrivePathAsync(VmId, item, ""))), useFluentFont: true)
                : IconButton(this, "\uF60A", "移除存储设备", () => _ = RunAsync(() => RunSingleAsync("移除存储设备",
                    () => VmStorageService.RemoveDriveAsync(VmId, item))), useFluentFont: true);
            ((FontIcon)gear.Content).FontSize = ((FontIcon)media.Content).FontSize = 16;
            gear.Tapped += (_, e) => e.Handled = true;
            media.Tapped += (_, e) => e.Handled = true;
            var condition = item.HasIsoMedia ? VmStorageService.GetMediaModificationCondition(item, powerState)
                : VmStorageService.GetRemovalCondition(item, powerState);
            media.IsEnabled = CanWrite && condition.CanModify;
            if (!condition.CanModify) ToolTipService.SetToolTip(media, Clean(condition.Reason));

            void Toggle()
            {
                if (!expanded.Remove(item.DeviceToken)) expanded.Add(item.DeviceToken);
                Render();
            }
            var chevronIcon = new FontIcon
            {
                Glyph = open ? "\uE70E" : "\uE70D",
                FontFamily = new FontFamily(SegoeFont),
                FontSize = 12,
                Foreground = ThemeBrush(this, "TextFillColorSecondaryBrush")
            };
            var toggle = new Button
            {
                Width = 26, Height = 26, MinWidth = 0, MinHeight = 0, Padding = new(0),
                Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"],
                Content = chevronIcon
            };
            AutomationProperties.SetName(toggle, open ? "收起详情" : "展开详情");
            toggle.Click += (_, _) => Toggle();

            var headerGrid = Split(WithLeading(icon, identity), Actions(6, gear, media, toggle));
            var headerHost = new Border
            {
                Padding = new(16, 12, 16, 12),
                Background = transparent,
                CornerRadius = open ? new CornerRadius(7, 7, 0, 0) : new CornerRadius(7),
                Child = headerGrid
            };
            headerHost.PointerEntered += (_, _) => headerHost.Background = hoverBrush;
            headerHost.PointerExited += (_, _) => headerHost.Background = transparent;
            headerHost.PointerCanceled += (_, _) => headerHost.Background = transparent;
            headerHost.Tapped += (_, e) =>
            {
                if (e.Handled) return;
                for (var node = e.OriginalSource as DependencyObject; node != null && node != headerHost; node = VisualTreeHelper.GetParent(node))
                    if (node is Button) return;
                Toggle();
            };
            page.Children.Add(CardShell(this, headerHost, open ? DeviceFacts(item) : null));
        }

        bool empty = attachments.Count == 0;
        var addBadge = new Border
        {
            Width = 38, Height = 38, CornerRadius = new(7),
            Background = ThemeBrush(this, "SubtleFillColorSecondaryBrush"),
            BorderBrush = ThemeBrush(this, "DividerStrokeColorDefaultBrush"),
            BorderThickness = new(1),
            Child = new FontIcon
            {
                Glyph = "\uE710",
                FontFamily = new FontFamily(SegoeFont),
                FontSize = 16,
                Foreground = ThemeBrush(this, "TextFillColorPrimaryBrush")
            }
        };
        var addLabel = new TextBlock
        {
            Text = "添加存储设备",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeBrush(this, "TextFillColorPrimaryBrush")
        };
        var addRow = new Grid { ColumnSpacing = 12 };
        addRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        addRow.ColumnDefinitions.Add(new());
        addRow.Children.Add(addBadge);
        Grid.SetColumn(addLabel, 1);
        addRow.Children.Add(addLabel);

        var add = new Button
        {
            Content = addRow,
            Style = (Style)Application.Current.Resources["VmSelectorButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = ThemeBrush(this, empty ? "CardBackgroundFillColorDefaultBrush" : "SubtleFillColorTertiaryBrush"),
            BorderBrush = ThemeBrush(this, "CardStrokeColorDefaultBrush"),
            BorderThickness = new(1),
            CornerRadius = new(8),
            Padding = new(16, empty ? 14 : 12, 16, empty ? 14 : 12),
            MinHeight = empty ? 68 : 60,
            IsEnabled = CanWrite
        };
        AutomationProperties.SetName(add, "添加存储设备");
        add.Click += (_, _) =>
        {
            if (!CanWrite) return;
            adding = new();
            Render();
            scroll.ChangeView(null, 0, null, true);
        };
        page.Children.Add(add);
    }

    private StackPanel DeviceFacts(VmStorageAttachment d, bool showStaleWarning = true)
    {
        var cells = new List<(FrameworkElement, bool)>();
        void Add(string title, string? value, bool wide = false, bool reveal = false)
        {
            cells.Add((KvTile(this, title, string.IsNullOrEmpty(value) ? "未取得" : value,
                wide ? value : null, onError: ShowError, onReveal: reveal && !string.IsNullOrEmpty(value)
                    ? () => _ = RunAsync(() => RevealAsync(value)) : null), wide));
        }
        Add("存储来源", Physical(d) ? "宿主物理设备" : d.DiskType == "Empty" ? "空光驱" : "虚拟介质文件");
        Add("类型", TypeLabel(d));
        Add("插槽", $"{d.ControllerType} 控制器 {d.ControllerNumber}（位置 {d.ControllerLocation}）");
        if (Physical(d)) Add("序列号", d.SerialNumber);
        else if (d.DiskType != "Empty")
        {
            Add("格式", IsDvd(d) ? "ISO" : d.VhdFormat?.TrimStart('.').ToUpperInvariant());
            Add("文件大小", d.FileSizeBytes is long bytes ? $"{bytes / 1073741824d:N2} GiB" : Size(d.DiskSizeGiB));
            if (!IsDvd(d)) Add("虚拟容量", Size(d.VirtualCapacityGiB));
        }
        if (d.VhdType == "Differencing") Add("父盘路径", d.ParentPath, true, true);
        if (d.DiskType != "Empty") Add("介质路径", d.PathOrDiskNumber, true, !Physical(d));
        var result = Stack();
        if (showStaleWarning && d.IsPassthroughStale) result.Children.Add(Banner(d.PassthroughStatus, InfoBarSeverity.Warning,
            Command("重新检测", () => _ = RunAsync(LoadAsync))));
        result.Children.Add(KvGrid(cells.ToArray()));
        return result;
    }

    private static bool IsDvd(VmStorageAttachment d) => d.DriveType == "DvdDrive";
    private static bool Physical(VmStorageAttachment d) => d.DiskType == "Physical";
    private static string DisplayName(VmStorageAttachment d) => Physical(d)
        ? !string.IsNullOrWhiteSpace(d.DiskModel) ? d.DiskModel : IsDvd(d) ? "物理光驱" : $"宿主磁盘 {d.DiskNumber}"
        : string.IsNullOrWhiteSpace(d.PathOrDiskNumber) ? "空光驱" : Path.GetFileName(d.PathOrDiskNumber);
    private static string TypeLabel(VmStorageAttachment d) => IsDvd(d) ? Physical(d) ? "物理光驱" : "虚拟光驱"
        : Physical(d) ? "物理直通" : d.VhdType switch { "Dynamic" => "动态扩展", "Fixed" => "固定大小", "Differencing" => "差异", _ => "虚拟硬盘" };
    private static string Size(double? gib) => gib is > 0 ? gib < 1 ? $"{gib * 1024:N1} MiB" : $"{gib:N1} GiB" : "容量未知";

    private static async Task RevealAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var folder = await file.GetParentAsync();
        var options = new FolderLauncherOptions();
        options.ItemsToSelect.Add(file);
        if (folder == null || !await Launcher.LaunchFolderAsync(folder, options))
            throw new IOException("无法打开介质所在的文件夹。");
    }
}
