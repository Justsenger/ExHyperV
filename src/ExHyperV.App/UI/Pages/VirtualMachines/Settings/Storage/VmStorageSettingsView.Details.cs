using System.Globalization;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using static ExHyperV.App.UI.Pages.VirtualMachines.Settings.DeviceWorkbench;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Storage;

public sealed partial class VmStorageSettingsView
{
    private sealed class DetailDraft(VmStorageAttachment item)
    {
        public readonly VmStorageAttachment Item = item;
        public VmDiskInspection Access = new(VmDiskAccessKind.Unavailable, "", VmState.Unknown);
        public bool AccessKnown;
        public VmResizeSafety? Safety;
        public VmDiskChainInspection? SavedChain, ParentChain;
        public string? InspectionError;
        public bool PathsTab, IgnoreId, MergeOpen, ExportOpen;
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

    private async Task OpenDetailsAsync(VmStorageAttachment item, bool pathsTab = false)
    {
        editing?.Cancel();
        taskView = null;
        var d = editing = new(item) { PathsTab = pathsTab };
        Render();
        scroll.ChangeView(null, 0, null, true);
        bool checkResize = item.VhdType is "Dynamic" or "Fixed" && powerState is VmState.PoweredOff or VmState.Running;
        var access = Task.Run(() => VmStorageService.InspectDiskAsync(VmId, item));
        var chain = item.VhdType == "Differencing"
            ? Task.Run(() => VmStorageService.InspectDifferencingChainAsync(item.PathOrDiskNumber)) : null;
        var safety = checkResize ? Task.Run(() => VmStorageService.InspectResizeSafetyAsync(VmId, item)) : null;
        try
        {
            await Task.WhenAll(access, (Task?)chain ?? Task.CompletedTask, (Task?)safety ?? Task.CompletedTask).WaitAsync(CancellationToken);
            d.Access = Data(await access);
            d.AccessKnown = true;
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
            Render();
            scroll.ChangeView(null, 0, null, true);
        }
        apply = Command(L10n.Get("Common_Apply"), () => _ = RunAsync(() => ApplyDetailsAsync(d)), accent: true, enabled: false);
        pivot.Content = PivotHeader(this, ((string, string, bool, Action)[])[
            ("\uE70F", L10n.Get("Common_Edit"), !d.PathsTab, () => Tab(false)), ("\uE8B7", L10n.Get("Dev_Path"), d.PathsTab, () => Tab(true))
        ], ShowList, apply);
        var retry = Command(L10n.Get("Storage_Rescan"), () => _ = RunAsync(() => ReinspectAsync(d)));
        if (d.InspectionError != null) page.Children.Add(Banner(d.InspectionError, InfoBarSeverity.Warning, retry));
        var access = d.Access;
        if (d.AccessKnown && access.Kind != VmDiskAccessKind.Normal)
        {
            Button? action = access.Kind switch
            {
                VmDiskAccessKind.Missing => InlineCommand(L10n.Get("Storage_LocatePath"), () =>
                {
                    Tab(true);
                    DispatcherQueue.TryEnqueue(() => mediaBox?.Focus(FocusState.Programmatic));
                }),
                VmDiskAccessKind.HostMounted => InlineCommand(L10n.Get("Storage_Dismount"), () => _ = RunAsync(() => DismountAsync(d)), enabled: CanWrite),
                VmDiskAccessKind.LockedExternal or VmDiskAccessKind.Unavailable => InlineCommand(L10n.Get("Storage_Rescan"), () => _ = RunAsync(() => ReinspectAsync(d))),
                _ => null
            };
            page.Children.Add(Banner(access.Message, access.Kind == VmDiskAccessKind.Missing ? InfoBarSeverity.Error : InfoBarSeverity.Warning, action));
        }
        if (d.Item.IsPassthroughStale)
            page.Children.Add(Banner(d.Item.PassthroughStatus, InfoBarSeverity.Warning, InlineCommand(L10n.Get("Storage_Rescan"), () => _ = RunAsync(() => ReinspectAsync(d)))));

        string? disabledReason = access.Kind switch
        {
            VmDiskAccessKind.VmRunning => L10n.Get("Storage_OfflineMaintenance"),
            VmDiskAccessKind.VmSavedOrPaused => L10n.Get("Storage_ShutdownToEdit"),
            VmDiskAccessKind.Missing => L10n.Get("Storage_RelocateMissingMedia"),
            VmDiskAccessKind.HostMounted => L10n.Get("Storage_DismountFirst"),
            VmDiskAccessKind.LockedExternal => L10n.Get("Storage_ReleaseBusyMedia"),
            VmDiskAccessKind.ParentInUse => L10n.Get("Storage_HasDependents"),
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
            if (d.AccessKnown) page.Children.Add(Banner(access.CanEditSlot ? L10n.Get("Storage_MoveSlotHint") : L10n.Get("Storage_MoveSlotRestricted")));
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
            string compactHint = d.Item.VhdType == "Fixed" ? L10n.Get("Storage_FixedCannotCompactSentence")
                : access.Kind == VmDiskAccessKind.HostMounted && access.CanCompact ? L10n.Get("Storage_MountedCompactHelp")
                : L10n.Get("Storage_CompactHelp");
            bool canCompact = CanWrite && access.CanCompact;
            rows.Add(SettingRow(
                L10n.Get("Storage_ReclaimSpace"),
                compactHint,
                Command(L10n.Get("Storage_Compact"), () => _ = RunAsync(() => RunSingleAsync(L10n.Get("Storage_CompactDisk"),
                    () => VmStorageService.CompactDiskAsync(VmId, d.Item), d.Item.DeviceToken)), enabled: canCompact),
                canCompact,
                canCompact ? null : (d.Item.VhdType == "Fixed" ? L10n.Get("Storage_FixedCannotCompact") : disabledReason)));
            if (SamePath(Path.GetExtension(d.Item.PathOrDiskNumber), ".vhd") && d.Item.VhdType != "Differencing")
            {
                bool canUpgrade = CanWrite && access.CanUpgradeVhdx;
                rows.Add(SettingRow(
                    L10n.Get("Storage_UpgradeVhdxFormat"),
                    L10n.Get("Storage_UpgradeVhdxHelp"),
                    Command(L10n.Get("Storage_ConvertFormat"), () => _ = RunAsync(() => RunStorageTaskAsync(L10n.Get("Storage_UpgradeVhdx"), [L10n.Get("Storage_ConvertAndSwitch")],
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
        public void Report(int value) => target.Report((0, value, L10n.Get("Storage_ConvertingVhdx")));
    }

    private async Task DismountAsync(DetailDraft d)
    {
        await RunSingleAsync(L10n.Get("Storage_DismountHost"), () => HostDiskService.DismountVhdxAsync(d.Item.PathOrDiskNumber), d.Item.DeviceToken);
        if (IsStopped || taskView is not { Success: true, ReadbackError: null }) return;
        var item = attachments.FirstOrDefault(i => i.DeviceToken == d.Item.DeviceToken);
        if (item != null) await OpenDetailsAsync(item);
        else ShowList();
    }

    private Border MediaEditorRow(DetailDraft d, string? disabledReason)
    {
        bool dvd = IsDvd(d.Item);
        bool editable = CanWrite && d.Access.CanEditMedia;
        var feedback = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        void UpdateFeedback()
        {
            feedback.Content = !d.MediaDirty ? null : d.MediaCheck == null ? Banner(L10n.Get("Storage_CheckingMedia")) :
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
        var row = PathInput(d.Media, dvd ? L10n.Get("Storage_EjectIsoPlaceholder") : L10n.Get("Storage_DiskPathPlaceholder"),
            () => OpenMedia(dvd), Check, out var input);
        row.IsEnabled = editable;
        mediaBox = input;
        if (dvd && editable && d.Item.HasIsoMedia && row.Content is Grid inputGrid)
        {
            row.Content = null;
            var clear = Command(L10n.Get("Storage_ClearMedia"), () => input.Text = "");
            var wrapped = Split(inputGrid, clear);
            row = EditorHost(wrapped);
        }
        if (d.MediaDirty && d.MediaCheck == null) Check(d.Media);
        else UpdateFeedback();
        return FieldRowCard(
            dvd ? L10n.Get("Storage_IsoPath") : L10n.Get("Storage_DiskPath"),
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
        string? resizeLock = !CanWrite || !d.AccessKnown ? null : d.Safety is null ? L10n.Get("Storage_ResizeUnavailable")
            : (!d.Safety.CanExpand && !d.Safety.CanShrink) ? (Clean(d.Safety.Message) is { Length: > 0 } msg ? msg : disabledReason)
            : null;
        rows.Add(CapacitySection(
            L10n.Get("Storage_ChangeCapacity"),
            L10n.Get("Storage_ResizeHelp"),
            d.Capacity,
            value => { d.Capacity = value; Update(); },
            check,
            canResize,
            resizeLock));
        Update();
        bool canType = CanWrite && d.Access.CanChangeType;
        var type = Pill(d.Type, value => { d.Type = value; UpdateApply(d); }, new("Dynamic", L10n.Get("Dev_VhdDynamic")), new("Fixed", L10n.Get("Dev_VhdFixed")));
        type.SetControlEnabled(canType);
        rows.Add(SettingRow(
            L10n.Get("Storage_ChangeType"),
            L10n.Get("Storage_ChangeTypeHelp"),
            type,
            canType,
            canType ? null : disabledReason));
    }

    private static FileCheck ResizeCheck(DetailDraft d)
    {
        if (!d.AccessKnown) return FileCheck.Ok();
        var s = d.Safety;
        if (s == null) return new(!d.ResizeDirty, L10n.Get("Storage_ResizeUnavailableSentence"), InfoBarSeverity.Warning);
        string layout = s.Layout switch
        {
            VmPartitionLayoutMode.NoTailSpace => L10n.Get("Storage_NoShrinkSpace"),
            VmPartitionLayoutMode.Conservative => L10n.Get("Storage_ConservativeShrink"),
            VmPartitionLayoutMode.Empty => L10n.Get("Storage_NoPartitions"),
            VmPartitionLayoutMode.Corrupt => L10n.Get("Storage_CorruptPartitionTable"),
            VmPartitionLayoutMode.Unavailable => s.Message,
            _ => ""
        };
        string floor = s.SafeMinimumGiB is long min ? L10n.Format("Storage_MinimumCapacity", min) : "";
        if (!d.ResizeDirty) return new(true, layout + floor, InfoBarSeverity.Informational);
        if (!long.TryParse(d.Capacity, out var n) || n <= 0 || n > s.MaximumGiB)
            return FileCheck.Error(L10n.Format("Storage_EnterCapacity", s.MaximumGiB));
        if (s.CurrentCapacityBytes == null) return FileCheck.Error(L10n.Get("Storage_VerifyCapacityFailed"));
        if (n * 1073741824L > s.CurrentCapacityBytes)
            return s.CanExpand ? new(true, L10n.Get("Storage_ExtendGuestPartition"), InfoBarSeverity.Informational) : FileCheck.Error(s.Message);
        if (!s.CanShrink || s.Layout is VmPartitionLayoutMode.NoTailSpace or VmPartitionLayoutMode.Corrupt or VmPartitionLayoutMode.Unavailable)
            return FileCheck.Error(layout + L10n.Get("Storage_UnsafeShrinkSuffix") + floor);
        if (s.SafeMinimumGiB is long minCap && n < minCap)
        {
            long partEndGiB = s.LastPartitionEndBytes is long end && end > 0 ? 1 + (end - 1) / 1073741824L : 0;
            return FileCheck.Error(partEndGiB >= minCap
                ? L10n.Format("Storage_BelowPartitionEnd", partEndGiB)
                : floor);
        }
        return s.Layout == VmPartitionLayoutMode.Conservative
            ? new(true, layout + floor + L10n.Format("Storage_ShrinkTargetSuffix", n), InfoBarSeverity.Warning)
            : FileCheck.Ok();
    }

    private void AddDeleteRows(DetailDraft d, List<UIElement> rows, string? disabledReason)
    {
        string what = IsDvd(d.Item) ? L10n.Get("Storage_IsoFile") : L10n.Get("Storage_DiskFile");
        bool canDelete = CanWrite && d.Access.CanDeleteFile;
        var deleteBtn = DangerButton(L10n.Get("Host_Switches_Delete"),
            () => _ = UiEvent.RunAsync(() => ConfirmAndDeleteMediaAsync(d, what), message => ShowError(message)),
            canDelete);
        if (!canDelete && !string.IsNullOrEmpty(disabledReason))
            ToolTipService.SetToolTip(deleteBtn, disabledReason);
        var label = new TextBlock { Text = L10n.Get("Storage_DeletePermanently") + what, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        if (!canDelete) label.Opacity = 0.45;
        rows.Add(CardRow(Split(label, deleteBtn)));
    }

    private async Task ConfirmAndDeleteMediaAsync(DetailDraft d, string what)
    {
        if (!CanWrite || editing != d) return;
        string title = L10n.Format("Storage_ConfirmDeleteFile", what);
        string body = L10n.Format("Storage_ConfirmDeleteFileBody", what);
        if (!await ConfirmAsync(title, body, d.Item.PathOrDiskNumber, L10n.Get("Host_Switches_Delete"))) return;
        if (!CanWrite || editing != d) return;
        await RunAsync(() => RunSingleAsync(L10n.Get("Host_Switches_Delete") + what,
            () => VmStorageService.DeleteAttachedMediaFileAsync(VmId, d.Item), openList: true));
    }

    private bool ValidDetails(DetailDraft d) => CanWrite && d.AccessKnown && d.Access is { } a &&
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
