using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

internal static class GpuDiskRecovery
{
    private static ApiResponse<bool> Fail(ApiResponse result) => ApiResponse<bool>.Fail(result.Error, result.Code, result.ErrorSource, result.Exception, result.ErrorKind);
    private static ApiResponse Failure<T>(ApiResponse<T> result) => ApiResponse.Fail(result.Error, result.Code, result.ErrorSource, result.Exception, result.ErrorKind);
    private static ApiResponse Conflict(string text) => ApiResponse.Fail(text, kind: ApiErrorKind.Conflict);

    internal static void DiskState(Guid vmId, HostDiskSnapshot original)
    {
        GpuRecoveryJournal.Current?.SetScope(action => HostDiskService.WithDiskLockAsync(original.Number, action));
        GpuRecoveryJournal.Current?.Register("DiskState", original.UniqueId + "/" + original.SerialNumber,
            new Dictionary<string, string> { ["DiskNumber"] = original.Number.ToString(), ["IsOffline"] = original.IsOffline.ToString(), ["IsReadOnly"] = original.IsReadOnly.ToString() },
            async () =>
            {
                var read = await HostDiskService.ReadDiskSnapshotAsync(original.Number);
                if (!read.HasData) return Fail(Failure(read));
                if (!original.SameDevice(read.Data)) return Fail(Conflict("原磁盘身份已变化，未恢复替代设备。"));
                return ApiResponse<bool>.Ok(read.Data.IsOffline == original.IsOffline && read.Data.IsReadOnly == original.IsReadOnly);
            }, async () =>
            {
                var off = await EnsureOffAsync(vmId);
                return off.Success ? await HostDiskService.RestoreDiskCoreAsync(original) : off;
            });
    }

    internal static void Attachment(Guid vmId, HostDiskSnapshot disk, VmStorageSlot slot, string controllerId)
    {
        async Task<ApiResponse<bool>> Inspect()
        {
            var current = await HostDiskService.ReadDiskSnapshotAsync(disk.Number);
            if (!current.HasData) return Fail(Failure(current));
            if (!disk.SameDevice(current.Data)) return Fail(Conflict("物理磁盘身份变化，未重新挂接。"));
            var storage = await VmStorageService.GetVmStorageItemsAsync(vmId);
            if (!storage.HasData) return Fail(Failure(storage));
            var occupying = storage.Data.Where(i => i.ControllerType == slot.ControllerType && i.ControllerNumber == slot.ControllerNumber && i.ControllerLocation == slot.Location).ToArray();
            if (occupying.Length == 0)
            {
                if (storage.Data.Any(i => i.DiskType == "Physical" && i.DiskNumber == disk.Number))
                    return Fail(Conflict("原物理磁盘已挂接到其他位置，未重新分配。"));
                return ApiResponse<bool>.Ok(false);
            }
            if (occupying.Length == 1 && occupying[0].DiskType == "Physical" && occupying[0].DiskNumber == disk.Number
                && string.Equals(occupying[0].Selection?.Controller.InstanceId, controllerId, StringComparison.OrdinalIgnoreCase))
                return ApiResponse<bool>.Ok(true);
            return Fail(Conflict("原槽位或控制器已变化，未替换现有设备。"));
        }
        GpuRecoveryJournal.Current?.Register("PhysicalAttachment", disk.UniqueId + "/" + disk.SerialNumber,
            new Dictionary<string, string> { ["VmId"] = vmId.ToString("D"), ["ControllerId"] = controllerId,
                ["ControllerType"] = slot.ControllerType, ["ControllerNumber"] = slot.ControllerNumber.ToString(), ["Location"] = slot.Location.ToString() },
            Inspect, async () =>
            {
                var off = await EnsureOffAsync(vmId);
                if (!off.Success) return off;
                var check = await Inspect();
                if (!check.HasData) return Failure(check);
                if (check.Data) return ApiResponse.Ok();
                var add = await VmStorageService.AddDriveUnderLockAsync(vmId, new()
                {
                    ControllerType = slot.ControllerType, ControllerNumber = slot.ControllerNumber, Location = slot.Location,
                    ExpectedControllerId = controllerId, DriveType = "HardDisk", IsPhysical = true, PathOrNumber = disk.Number.ToString(),
                    PhysicalDisk = new() { Number = disk.Number, UniqueId = disk.UniqueId, SerialNumber = disk.SerialNumber, SizeBytes = disk.SizeBytes }
                });
                return add.Success ? ApiResponse.Ok() : Failure(add);
            });
    }

    internal static async Task<GpuRecoveryWork?> DriveLetterAsync(Guid vmId, int diskNumber, int partitionNumber, char letter, string? imagePath)
    {
        var journal = GpuRecoveryJournal.Current;
        if (journal is null) return null;
        var disk = await HostDiskService.GetDiskReferenceAsync(diskNumber);
        if (!disk.HasData) throw new CoreOperationException(Failure(disk));
        var partition = await HostDiskService.GetPartitionReferenceCoreAsync(disk.Data, partitionNumber);
        if (!partition.HasData) throw new CoreOperationException(Failure(partition));
        string accessPath = $"{char.ToUpperInvariant(letter)}:\\";
        string? imageIdentity = imagePath is null ? null : HostDiskResource.Image(imagePath);
        async Task<ApiResponse<bool>> Inspect()
        {
            if (imagePath != null)
            {
                if (HostDiskResource.Image(imagePath) != imageIdentity) return Fail(Conflict("临时盘符所属映像已被替换。"));
                var image = await HostDiskService.ReadImageAsync(imagePath);
                if (!image.Success) return Fail(Failure(image));
                if (!image.HasData || !image.Data.Attached) return ApiResponse<bool>.Ok(true);
                if (image.Data.Number != diskNumber) return Fail(Conflict("映像挂载身份已经变化，未修改替代分区。"));
            }
            var now = await HostDiskService.GetPartitionReferenceCoreAsync(disk.Data, partitionNumber);
            if (!now.HasData) return Fail(Failure(now));
            if (!partition.Data.Matches(now.Data)) return Fail(Conflict("临时盘符原分区身份变化，未修改替代分区。"));
            var paths = await WmiApi.QueryFirstCimAsync($"SELECT ObjectId, AccessPaths FROM MSFT_Partition WHERE DiskNumber = {diskNumber} AND PartitionNumber = {partitionNumber}",
                row => (Id: row["ObjectId"]?.ToString(), Paths: row["AccessPaths"] as string[] ?? []));
            if (!paths.HasData) return Fail(Failure(paths));
            if (!string.Equals(paths.Data.Id, partition.Data.ObjectId, StringComparison.OrdinalIgnoreCase)) return Fail(Conflict("分区身份已变化。"));
            return ApiResponse<bool>.Ok(!paths.Data.Paths.Contains(accessPath, StringComparer.OrdinalIgnoreCase));
        }
        var before = await Inspect();
        if (!before.HasData) throw new CoreOperationException(Failure(before));
        if (!before.Data) throw new CoreOperationException(Conflict("所选临时盘符已经存在，未认领或修改该盘符。"));
        return journal.Register("DriveLetter", partition.Data.ObjectId, new Dictionary<string, string> { ["AccessPath"] = accessPath, ["OriginallyPresent"] = "False" }, Inspect,
            async () =>
            {
                var off = await EnsureOffAsync(vmId);
                if (!off.Success) return off;
                var check = await Inspect();
                if (!check.HasData) return Failure(check);
                return check.Data ? ApiResponse.Ok() : await HostDiskService.ModifyPartitionAccessPathAsync(partition.Data, letter, false);
            });
    }

    internal static async Task<Action<HostDiskMountResult>> ImageAsync(Guid vmId, string path)
    {
        var journal = GpuRecoveryJournal.Current;
        if (journal is null) return _ => { };
        string identity = HostDiskResource.Image(path);
        journal.SetScope(action => HostDiskService.WithImageLockAsync(path, async () =>
        {
            if (HostDiskResource.Image(path) != identity) return Conflict("原映像已被替换，未执行恢复。");
            var now = await HostDiskService.ReadImageAsync(path);
            if (!now.Success) return Failure(now);
            return now.HasData && now.Data.Attached && now.Data.Number is int number
                ? await HostDiskService.WithDiskLockAsync(number, action) : await action();
        }));
        var original = await HostDiskService.ReadImageAsync(path);
        if (!original.Success) throw new CoreOperationException(Failure(original));
        if (original.HasData && original.Data.Attached) return _ => { };
        HostDiskMountResult? mounted = null;
        WmiOperationReceipt? attach = null;
        async Task<ApiResponse<bool>> Inspect()
        {
            if (HostDiskResource.Image(path) != identity) return Fail(Conflict("映像文件身份已变化，未卸载替代映像。"));
            var now = await HostDiskService.ReadImageAsync(path);
            return now.Success ? ApiResponse<bool>.Ok(!now.HasData || !now.Data.Attached) : Fail(Failure(now));
        }
        journal.Register("ImageMount", identity, new Dictionary<string, string> { ["ImagePath"] = path, ["OriginallyMounted"] = "False" }, Inspect,
            async () =>
            {
                var off = await EnsureOffAsync(vmId);
                if (!off.Success) return off;
                var check = await Inspect();
                if (!check.HasData) return Failure(check);
                if (check.Data) return ApiResponse.Ok();
                if (mounted?.CreatedByCall != true && attach?.Result?.Success != true)
                    return Conflict("未确认本次挂载成功，不能认领当前挂载。原状态与作业记录已保留。");
                var now = await HostDiskService.ReadImageAsync(path);
                if (!now.HasData || now.Data.Number is not int number) return Conflict("尚未取得挂载磁盘身份，未执行卸载。");
                return await HostDiskService.DismountVhdxCoreAsync(path, number);
            });
        return result => { mounted = result; attach = journal.LastOperation("AttachVirtualHardDisk"); };
    }

    private static async Task<ApiResponse> EnsureOffAsync(Guid vmId)
    {
        var state = await VmConfigurationState.ReadAsync(vmId);
        if (!state.HasData) return Failure(state);
        return state.Data.IsPoweredOff ? ApiResponse.Ok() : ApiResponse.Fail("恢复磁盘前需要虚拟机保持关闭；Core 未自动关机。", kind: ApiErrorKind.InvalidState);
    }
}
