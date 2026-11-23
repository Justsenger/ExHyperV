using System.Globalization;
using System.IO;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines.Settings;

public sealed partial class VmStorageSettingsView
{
    private sealed class DetailDraft(VmStorageAttachment item)
    {
        public readonly VmStorageAttachment Item = item;
        public VmDiskInspection? Access;
        public VmResizeSafety? Safety;
        public VmDiskChainInspection? SavedChain, ParentChain;
        public string? InspectionError;
        public bool PathsTab, IgnoreId, ConfirmDelete, MergeOpen, ExportOpen;
        public int MergeDepth = 1;
        public string ExportPath = "";
        public string Media = item.PathOrDiskNumber, Parent = item.ParentPath ?? "", Type = item.VhdType ?? "";
        public readonly string InitialCapacity = item.VirtualCapacityGiB?.ToString("0.#########", CultureInfo.InvariantCulture) ?? "";
        public string Capacity = item.VirtualCapacityGiB?.ToString("0.#########", CultureInfo.InvariantCulture) ?? "";
        public readonly SlotDraft Slot = new() { Automatic = false, Type = item.ControllerType, Number = item.ControllerNumber, Location = item.ControllerLocation };
        public FileCheck? MediaCheck;
        public string? ParentError;
        public readonly DraftProbe MediaProbe = new(), ParentProbe = new(), ExportProbe = new();
        public void Cancel() { MediaProbe.Cancel(); ParentProbe.Cancel(); ExportProbe.Cancel(); }
        public bool MediaDirty => !Physical(Item) && !SamePath(Media, Item.PathOrDiskNumber);
        public bool ParentDirty => Item.VhdType == "Differencing" && (!SamePath(Parent, Item.ParentPath) || IgnoreId);
        public bool ResizeDirty => Capacity != InitialCapacity &&
            !(long.TryParse(Capacity, out var n) && n is >= 1 and <= 65536 && n * 1073741824L == Item.VirtualCapacityBytes);
        public bool TypeDirty => Type != (Item.VhdType ?? "");
        public bool SlotDirty => Slot.Type != Item.ControllerType || Slot.Number != Item.ControllerNumber || Slot.Location != Item.ControllerLocation;
    }

    private Button? apply;
    private TextBox? mediaBox;

    private async Task OpenDetailsAsync(VmStorageAttachment item)
    {
        editing?.Cancel();
        taskView = null;
        var d = editing = new(item);
        Render();
        scroll.ChangeView(null, 0, null, true);
        var access = VmStorageService.InspectDiskAsync(VmId, item);
        var chain = item.VhdType == "Differencing"
            ? VmStorageService.InspectDifferencingChainAsync(item.PathOrDiskNumber) : null;
        var safety = item.VhdType is "Dynamic" or "Fixed" && powerState is VmState.PoweredOff or VmState.Running
            ? VmStorageService.InspectResizeSafetyAsync(VmId, item) : null;
        try
        {
            d.Access = Data(await access.WaitAsync(CancellationToken));
            if (chain != null) d.ParentChain = d.SavedChain = Data(await chain.WaitAsync(CancellationToken));
            if (safety != null) d.Safety = Data(await safety.WaitAsync(CancellationToken));
        }
        catch (OperationCanceledException) when (IsStopped) { return; }
        catch (Exception ex) { d.InspectionError = Clean(CoreMessageResolver.ResolveException(ex)); }
        if (!IsStopped && editing == d) Render();
    }

    private async Task ReinspectAsync(DetailDraft d)
    {
        await ReadSnapshotAsync();
        var current = attachments.FirstOrDefault(i => i.DeviceToken == d.Item.DeviceToken);
        if (current == null) { ShowList(); return; }
        await OpenDetailsAsync(current);
    }

    private void RenderDetails(DetailDraft d)
    {
        mediaBox = null;
        void Tab(bool paths)
        {
            d.PathsTab = paths;
            d.ConfirmDelete = false;
            Render();
            scroll.ChangeView(null, 0, null, true);
        }
        apply = Command("应用", () => _ = RunAsync(() => ApplyDetailsAsync(d)), accent: true, enabled: false);
        pivot.Content = PivotHeader(this, ((string, string, bool, Action)[])[
            ("\uE70F", "编辑", !d.PathsTab, () => Tab(false)), ("\uE8B7", "路径", d.PathsTab, () => Tab(true))
        ], ShowList, apply);
        var retry = Command("重新检测", () => _ = RunAsync(() => ReinspectAsync(d)));
        if (d.InspectionError != null) page.Children.Add(Banner(d.InspectionError, InfoBarSeverity.Warning, retry));
        if (d.Access is not { } access)
        {
            if (d.InspectionError == null) page.Children.Add(Banner("正在检测介质和可用操作…"));
            return;
        }
        if (access.Kind != VmDiskAccessKind.Normal)
        {
            Button? action = access.Kind switch
            {
                VmDiskAccessKind.Missing => InlineCommand("定位路径", () =>
                {
                    Tab(true);
                    DispatcherQueue.TryEnqueue(() => mediaBox?.Focus(FocusState.Programmatic));
                }),
                VmDiskAccessKind.HostMounted => InlineCommand("卸载挂载", () => _ = RunAsync(() => DismountAsync(d)), enabled: CanWrite),
                VmDiskAccessKind.LockedExternal or VmDiskAccessKind.Unavailable => InlineCommand("重新检测", () => _ = RunAsync(() => ReinspectAsync(d))),
                _ => null
            };
            page.Children.Add(Banner(access.Message, access.Kind == VmDiskAccessKind.Missing ? InfoBarSeverity.Error : InfoBarSeverity.Warning, action));
        }
        if (d.Item.IsPassthroughStale)
            page.Children.Add(Banner(d.Item.PassthroughStatus, InfoBarSeverity.Warning, InlineCommand("重新检测", () => _ = RunAsync(() => ReinspectAsync(d)))));

        string? disabledReason = access.Kind switch
        {
            VmDiskAccessKind.VmRunning => "关闭虚拟机后可修改或执行磁盘维护",
            VmDiskAccessKind.VmSavedOrPaused => "请彻底关闭虚拟机后再编辑",
            VmDiskAccessKind.Missing => "介质文件缺失，请先在路径页重新定位",
            VmDiskAccessKind.HostMounted => "已在宿主机挂载，请先卸载后再操作",
            VmDiskAccessKind.LockedExternal => "正被其他虚拟机或进程占用，请先解除占用",
            VmDiskAccessKind.ParentInUse => "其他磁盘正在引用此文件",
            VmDiskAccessKind.Unavailable => Clean(access.Message),
            _ => null
        };

        var rows = new List<UIElement>();
        if (d.PathsTab)
        {
            if (!Physical(d.Item)) rows.Add(MediaEditorRow(d, disabledReason));
            rows.Add(SlotCard(d.Slot, d.Item.DriveType, d.Item, CanWrite && access.CanEditSlot, () => UpdateApply(d)));
            page.Children.Add(GroupedCard([.. rows]));
        }
        else if (Physical(d.Item))
        {
            page.Children.Add(DeviceFacts(d.Item, showStaleWarning: false));
            page.Children.Add(Banner(access.CanEditSlot ? "可在「路径」页签迁移控制器插槽" : "当前状态限制插槽迁移，请核对直通设备并关闭虚拟机"));
        }
        else if (IsDvd(d.Item))
        {
            rows.Add(MediaEditorRow(d, disabledReason));
            rows.Add(SlotCard(d.Slot, d.Item.DriveType, d.Item, CanWrite && access.CanEditSlot, () => UpdateApply(d)));
            if (d.Item.HasIsoMedia) AddDeleteRows(d, rows, disabledReason);
            page.Children.Add(GroupedCard([.. rows]));
        }
        else
        {
            if (d.Item.VhdType == "Differencing") AddDifferencingRows(d, rows, disabledReason);
            else AddStandardRows(d, rows, disabledReason);
            string compactHint = d.Item.VhdType == "Fixed" ? "固定大小磁盘不支持压缩体积。"
                : access.Kind == VmDiskAccessKind.HostMounted && access.CanCompact ? "已在宿主机挂载，压缩将直接执行文件系统感知回收。"
                : "回收未分配空闲块以缩小宿主机磁盘文件体积。";
            bool canCompact = CanWrite && access.CanCompact;
            rows.Add(SettingRow(
                "释放空间",
                compactHint,
                Command("压缩", () => _ = RunAsync(() => RunSingleAsync("压缩磁盘",
                    () => VmStorageService.CompactDiskAsync(VmId, d.Item), d.Item.DeviceToken)), enabled: canCompact),
                canCompact,
                canCompact ? null : (d.Item.VhdType == "Fixed" ? "固定大小磁盘不支持压缩体积" : disabledReason)));
            if (SamePath(Path.GetExtension(d.Item.PathOrDiskNumber), ".vhd") && d.Item.VhdType != "Differencing")
            {
                bool canUpgrade = CanWrite && access.CanUpgradeVhdx;
                rows.Add(SettingRow(
                    "升级为 VHDX 格式",
                    "转换为 .vhdx 容器以突破 2040 GB 容量限制并启用日志保护。",
                    Command("转换格式", () => _ = RunAsync(() => RunStorageTaskAsync("升级为 VHDX", ["转换并切换磁盘"],
                        p => VmStorageService.UpgradeVhdToVhdxAsync(VmId, d.Item, new UpgradeProgress(p)), d.Item.DeviceToken)),
                        enabled: canUpgrade),
                    canUpgrade,
                    canUpgrade ? null : disabledReason));
            }
            AddDeleteRows(d, rows, disabledReason);
            page.Children.Add(GroupedCard([.. rows]));
        }
        UpdateApply(d);
    }

    private sealed class UpgradeProgress(IProgress<(int, int, string)> target) : IProgress<int>
    {
        public void Report(int value) => target.Report((0, value, "正在转换并核验 VHDX。"));
    }

    private async Task DismountAsync(DetailDraft d)
    {
        await RunSingleAsync("卸载宿主挂载", () => HostDiskService.DismountVhdxAsync(d.Item.PathOrDiskNumber), d.Item.DeviceToken);
        if (IsStopped || taskView is not { Success: true, ReadbackError: null }) return;
        var item = attachments.FirstOrDefault(i => i.DeviceToken == d.Item.DeviceToken);
        if (item != null) await OpenDetailsAsync(item);
        else ShowList();
    }

    private Border MediaEditorRow(DetailDraft d, string? disabledReason)
    {
        bool dvd = IsDvd(d.Item);
        bool editable = CanWrite && d.Access!.CanEditMedia;
        var feedback = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void UpdateFeedback()
        {
            feedback.Content = !d.MediaDirty ? null : d.MediaCheck == null ? Banner("正在检查介质…") :
                Banner(d.MediaCheck.Message, d.MediaCheck.Severity);
            UpdateApply(d);
        }
        void Check(string path)
        {
            d.Media = path;
            d.MediaCheck = null;
            d.MediaProbe.Cancel();
            UpdateFeedback();
            if (!d.MediaDirty) return;
            _ = d.MediaProbe.StartAsync(CancellationToken, async token =>
            {
                var check = await Task.Run(() => CheckMedia(path, dvd, false), token).WaitAsync(token);
                token.ThrowIfCancellationRequested();
                d.MediaCheck = check;
                UpdateFeedback();
            }, error => { d.MediaCheck = FileCheck.Error(error); UpdateFeedback(); });
        }
        var row = PathInput(d.Media, dvd ? @"D:\ISO\system.iso（可留空弹出介质）" : "输入 .vhd / .vhdx 路径",
            () => OpenMedia(dvd), Check, out var input);
        row.IsEnabled = editable;
        mediaBox = input;
        if (dvd && editable && d.Item.HasIsoMedia && row.Content is Grid inputGrid)
        {
            row.Content = null;
            var clear = Command("清空介质", () => input.Text = "");
            var wrapped = Split(inputGrid, clear);
            row = EditorHost(wrapped);
        }
        if (d.MediaDirty && d.MediaCheck == null) Check(d.Media);
        else UpdateFeedback();
        return FieldRowCard(
            dvd ? "ISO 映像路径" : "磁盘文件路径",
            null,
            null,
            row,
            feedback,
            editable,
            editable ? null : disabledReason);
    }

    private void AddStandardRows(DetailDraft d, List<UIElement> rows, string? disabledReason)
    {
        var check = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void Update()
        {
            var info = ResizeCheck(d);
            check.Content = string.IsNullOrEmpty(info.Message) ? null : Banner(info.Message, info.Severity);
            UpdateApply(d);
        }
        bool canResize = CanWrite && d.Safety is { } s && (s.CanExpand || s.CanShrink);
        string? resizeLock = !CanWrite ? null : d.Safety is null ? "当前无法取得可调整的容量范围"
            : (!d.Safety.CanExpand && !d.Safety.CanShrink) ? (Clean(d.Safety.Message) is { Length: > 0 } msg ? msg : disabledReason)
            : null;
        rows.Add(CapacitySection(
            "修改容量",
            "自动检测分区末尾与安全收缩下限，.vhd 不支持收缩，SCSI .vhdx 支持联机扩展。",
            d.Capacity,
            value => { d.Capacity = value; Update(); },
            check,
            canResize,
            resizeLock));
        Update();
        bool canType = CanWrite && d.Access!.CanChangeType;
        var type = Pill(d.Type, value => { d.Type = value; UpdateApply(d); }, new("Dynamic", "动态扩展"), new("Fixed", "固定大小"));
        type.SetControlEnabled(canType);
        rows.Add(SettingRow(
            "修改类型",
            "动态扩展按需分配空间，固定大小一次性预分配全部空间。",
            type,
            canType,
            canType ? null : disabledReason));
    }

    private static FileCheck ResizeCheck(DetailDraft d)
    {
        var s = d.Safety;
        if (s == null) return new(!d.ResizeDirty, "当前无法取得可调整的容量范围。", InfoBarSeverity.Warning);
        string layout = s.Layout switch
        {
            VmPartitionLayoutMode.NoTailSpace => "磁盘尾部没有可收缩空间。",
            VmPartitionLayoutMode.Conservative => "BitLocker 或 Linux 等分区仅能按已知边界保守收缩。",
            VmPartitionLayoutMode.Empty => "未检测到已分配的分区。",
            VmPartitionLayoutMode.Corrupt => "分区表损坏，已禁止收缩。",
            VmPartitionLayoutMode.Unavailable => s.Message,
            _ => ""
        };
        string floor = s.SafeMinimumGiB is long min ? $"安全下限为 {min} GiB。" : "";
        if (!d.ResizeDirty) return new(true, layout + floor, InfoBarSeverity.Informational);
        if (!long.TryParse(d.Capacity, out var n) || n <= 0 || n > s.MaximumGiB)
            return FileCheck.Error($"请输入 1 至 {s.MaximumGiB} GiB 的整数。");
        if (s.CurrentCapacityBytes == null) return FileCheck.Error("无法核实当前容量。");
        if (n * 1073741824L > s.CurrentCapacityBytes)
            return s.CanExpand ? new(true, "扩展后需在系统内扩容分区。", InfoBarSeverity.Informational) : FileCheck.Error(s.Message);
        if (!s.CanShrink || s.Layout is VmPartitionLayoutMode.NoTailSpace or VmPartitionLayoutMode.Corrupt or VmPartitionLayoutMode.Unavailable)
            return FileCheck.Error(layout + " 当前无法安全收缩。" + floor);
        if (n < s.SafeMinimumGiB) return FileCheck.Error(floor);
        return new(true, layout + floor + $" 将收缩至 {n} GiB。", InfoBarSeverity.Warning);
    }

    private void AddDeleteRows(DetailDraft d, List<UIElement> rows, string? disabledReason)
    {
        string what = IsDvd(d.Item) ? "ISO 文件" : "磁盘文件";
        bool canDelete = CanWrite && d.Access!.CanDeleteFile;
        var toggle = DangerButton("删除", () => { d.ConfirmDelete = !d.ConfirmDelete; Render(); }, canDelete);
        if (!canDelete && !string.IsNullOrEmpty(disabledReason))
            ToolTipService.SetToolTip(toggle, disabledReason);
        var label = new TextBlock { Text = "彻底删除" + what, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        if (!canDelete) label.Opacity = 0.45;
        rows.Add(CardRow(Split(label, toggle)));
        if (d.ConfirmDelete && canDelete)
        {
            var strip = DangerStrip(this, $"确认彻底删除{what}？", d.Item.PathOrDiskNumber,
                Command("取消", () => { d.ConfirmDelete = false; Render(); }),
                DangerButton("删除", () => _ = RunAsync(() => RunSingleAsync("删除" + what,
                    () => VmStorageService.DeleteAttachedMediaFileAsync(VmId, d.Item), openList: true)), canDelete));
            strip.BorderThickness = new(0);
            rows.Add(strip);
        }
    }

    private bool ValidDetails(DetailDraft d) => CanWrite && d.Access is { } a &&
        (d.ResizeDirty || d.TypeDirty || d.ParentDirty || d.MediaDirty || d.SlotDirty) &&
        (!d.ResizeDirty || ResizeCheck(d).Valid) && (!d.TypeDirty || a.CanChangeType) &&
        (!d.ParentDirty || a.CanEditParentPath && d.Parent.Length > 0 && d.ParentChain?.IsComplete == true) &&
        (!d.MediaDirty || a.CanEditMedia && d.MediaCheck?.Valid == true) &&
        (!d.SlotDirty || a.CanEditSlot && d.Slot.Error == null);

    private void UpdateApply(DetailDraft d)
    {
        if (editing == d && apply != null) apply.IsEnabled = ValidDetails(d);
    }
}
