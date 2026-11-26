
using ExHyperV.App.Localization;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Storage;

public sealed partial class VmStorageSettingsView
{
    // Each step receives the verified attachment for that step. Moving the controller
    // changes DeviceToken, so move last. Maintenance always targets the original medium.
    private sealed record EditStep(string Label, Func<VmStorageAttachment, Task<ApiResponse>> Write,
        Func<VmStorageAttachment, VmStorageAttachment, bool>? Verify = null);

    private async Task ApplyDetailsAsync(DetailDraft d)
    {
        if (editing != d || !ValidDetails(d)) return;
        var item = d.Item;
        var steps = new List<EditStep>();
        if (d.ResizeDirty)
        {
            long size = long.Parse(d.Capacity);
            steps.Add(new(L10n.Format("Storage_ResizeStep", size), drive => VmStorageService.ResizeVirtualDiskAsync(VmId, drive, size),
                (prior, next) => SameFile(prior, next) && next.VirtualCapacityBytes == size * 1073741824L));
        }
        if (d.TypeDirty)
        {
            string type = d.Type;
            steps.Add(new(type == "Fixed" ? L10n.Get("Storage_ConvertFixed") : L10n.Get("Storage_ConvertDynamic"),
                drive => VmStorageService.ConvertVirtualDiskTypeAsync(VmId, drive, type),
                (prior, next) => next.VhdType == type && SamePath(next.PathOrDiskNumber,
                    Path.ChangeExtension(prior.PathOrDiskNumber, type.ToLowerInvariant() + Path.GetExtension(prior.PathOrDiskNumber)))));
        }
        if (d.ParentDirty)
        {
            string parent = d.Parent;
            bool ignore = d.IgnoreId;
            steps.Add(new(L10n.Get("Storage_ReconnectParent"), drive => VmStorageService.ReconnectParentDiskAsync(VmId, drive, parent, ignore),
                (prior, next) => SameFile(prior, next) && SamePath(next.ParentPath, parent)));
        }
        if (d.MediaDirty)
        {
            string media = d.Media;
            steps.Add(new(L10n.Get("Storage_UpdateMediaPath"), drive => IsDvd(drive)
                ? VmStorageService.ModifyDvdDrivePathAsync(VmId, drive, media)
                : VmStorageService.ModifyHardDrivePathAsync(VmId, drive, media),
                (_, next) => SamePath(next.PathOrDiskNumber, media)));
        }
        if (d.SlotDirty)
        {
            var slot = new VmStorageSlot(d.Slot.Type, d.Slot.Number, d.Slot.Location);
            steps.Add(new(L10n.Get("Storage_MoveSlot"), drive => VmStorageService.ModifyDriveSlotAsync(VmId, drive,
                slot.ControllerType, slot.ControllerNumber, slot.Location)));
        }
        await RunStorageTaskAsync(L10n.Get("Storage_ApplyDeviceSettings"), steps.Select(s => s.Label).ToArray(), async progress =>
        {
            var inventory = await VmStorageService.GetVmStorageItemsAsync(VmId);
            if (!inventory.HasData) return Failure(inventory);
            var selected = inventory.Data.SingleOrDefault(i => i.DeviceToken == item.DeviceToken);
            if (selected == null || selected.SelectionToken != item.SelectionToken || !SameFile(item, selected) ||
                selected.VirtualCapacityBytes != item.VirtualCapacityBytes || selected.VhdType != item.VhdType ||
                !SamePath(selected.ParentPath, item.ParentPath))
                return ApiResponse.Fail(L10n.Get("Storage_EditedDeviceChanged"), kind: ApiErrorKind.Conflict);

            for (int i = 0; i < steps.Count; i++)
            {
                var step = steps[i];
                progress.Report((i, -1, step.Label));
                var result = await step.Write(selected);
                if (!result.Success) return result;
                if (step.Verify != null)
                {
                    var readback = await VmStorageService.GetVmStorageItemsAsync(VmId);
                    var next = readback.HasData ? readback.Data.SingleOrDefault(j => j.DeviceToken == item.DeviceToken) : null;
                    if (next == null || !step.Verify(selected, next))
                        return ApiResponse.Fail(L10n.Format("Storage_AttachmentUnconfirmed", step.Label, readback.Error),
                            kind: ApiErrorKind.OutcomeUnknown);
                    selected = next;
                }
                progress.Report((i, 100, step.Label + L10n.Get("Common_Completed")));
            }
            return ApiResponse.Ok();
        }, item.DeviceToken, inlineEdit: d.ResizeDirty || d.TypeDirty ? null : d);
    }

    private static bool SameFile(VmStorageAttachment a, VmStorageAttachment b) =>
        SamePath(a.PathOrDiskNumber, b.PathOrDiskNumber) && (a.MediaFileToken == null || a.MediaFileToken == b.MediaFileToken);
}
