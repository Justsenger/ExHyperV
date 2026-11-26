using System.Globalization;
using System.Management;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class VmStorageService
{
    internal static string? ValidateMove(VmConfigurationState vm, VmStorageAttachment drive,
        VmStorageSlot slot, IEnumerable<VmStorageAttachment> items)
    {
        if (!vm.IsPoweredOff) return "请彻底关机后再迁移控制器槽位。";
        if (vm.Generation is not (1 or 2)) return "无法确认虚拟机代数。";
        if (drive.DiskType == "Physical" && drive.DriveType == "DvdDrive" && vm.Generation != 1)
            return "物理光驱仅支持第一代虚拟机。";
        if (drive.IsPassthroughStale) return "物理直通引用已失效，请重新选择设备。";
        return GetAddSlotError(vm.Generation, false, new VmStorageAddRequest
        {
            ControllerType = slot.ControllerType, ControllerNumber = slot.ControllerNumber,
            Location = slot.Location, DriveType = drive.DriveType
        }, items.Where(item => item.SelectionToken != drive.SelectionToken));
    }

    /// <summary>在关机状态迁移同一驱动器，按需添加 SCSI 控制器，保留所选介质并核实新槽位。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="drive">存储查询返回的挂接记录，使用原始选择令牌核验身份。</param>
    /// <param name="targetControllerType">目标控制器类型。</param>
    /// <param name="targetControllerNumber">目标控制器编号。</param>
    /// <param name="targetControllerLocation">目标控制器上的槽位。</param>
    /// <returns>槽位迁移及回读结果。</returns>
    public static Task<ApiResponse> ModifyDriveSlotAsync(Guid vmId, VmStorageAttachment drive, string targetControllerType,
        int targetControllerNumber, int targetControllerLocation)
        => WithMaintenance(vmId, drive, async t =>
        {
            var target = new VmStorageSlot(targetControllerType, targetControllerNumber, targetControllerLocation);
            var inventory = Require(await GetVmStorageItemsAsync(vmId));
            var state = Require(await VmConfigurationState.ReadAsync(vmId));
            string? error = ValidateMove(state, t.Drive, target, inventory);
            if (error != null) return Denied(error);
            if (AtSlot(t.Drive, target)) return ApiResponse.Ok();
            using var vm = WmiApi.GetVmComputerSystem(vmId);
            using var settings = vm == null ? null : WmiApi.GetVmSettings(vm);
            if (settings == null) return Conflict("虚拟机配置已变化。");
            var ownership = new VmStorageCreationOwnership();
            ApiResponse result;
            try { result = await MoveToController(); }
            catch (Exception ex) { result = Failure(ex); }
            if (result.Success || result.ErrorKind is ApiErrorKind.OutcomeUnknown or ApiErrorKind.CleanupRequired) return result;
            var rollback = await ownership.RollbackAsync(() => ReadCreatedStorageResourcesAsync(settings),
                path => WmiApi.InvokeAsync(VmManagement, "RemoveResourceSettings", p => p["ResourceSettings"] = new[] { path }),
                _ => throw new InvalidOperationException());
            return rollback.Success ? result : Partial(result.Error, rollback);

            async Task<ApiResponse> MoveToController()
            {
                int resourceType = target.ControllerType == "SCSI" ? 6 : 5;
                async Task<List<VmCreatedStorageResource>> Controllers() =>
                    Require(await ReadCreatedStorageResourcesAsync(settings)).Where(r => r.ResourceType == resourceType).ToList();
                var controllers = await Controllers();
                while (resourceType == 6 && target.ControllerNumber >= controllers.Count)
                {
                    using var cls = new ManagementClass(settings.Scope, new ManagementPath("Msvm_ResourceAllocationSettingData"), null);
                    using var ctrl = cls.CreateInstance();
                    ctrl["ResourceType"] = (ushort)6;
                    ctrl["ResourceSubType"] = "Microsoft:Hyper-V:Synthetic SCSI Controller";
                    ctrl["AutomaticAllocation"] = true;
                    string controllerXml = ctrl.GetText(TextFormat.CimDtd20);
                    var added = await WmiApi.InvokeCreationAsync(VmManagement, "AddResourceSettings", "Msvm_ResourceAllocationSettingData",
                        p => { p["AffectedConfiguration"] = settings.Path.Path; p["ResourceSettings"] = new[] { controllerXml }; });
                    var after = await ReadCreatedStorageResourcesAsync(settings);
                    var owned = ownership.RecordCreation(added, after.Data, 6);
                    if (ownership.IsUncertain) return ApiResponse.Fail("新增控制器的归属待确认，请检查虚拟机。", kind: ApiErrorKind.OutcomeUnknown);
                    if (!added.Response.Success) return added.Response;
                    var refreshed = Require(after).Where(r => r.ResourceType == 6).ToList();
                    if (owned == null || refreshed.Count != controllers.Count + 1)
                        return Conflict("控制器清单发生变化。");
                    controllers = refreshed;
                }
                var controller = resourceType == 6 ? controllers.ElementAtOrDefault(target.ControllerNumber)
                    : controllers.SingleOrDefault(c => int.TryParse(c.InstanceId.Split('\\')[^1], out int n) && n == target.ControllerNumber);
                if (controller == null) return Conflict("未找到目标控制器。");
                var fresh = await GetVmStorageItemsAsync(vmId);
                var selected = Require(VmStorageRemoval.Select(vmId, t.Drive.SelectionToken, fresh));
                string? changed = ValidateMove(Require(await VmConfigurationState.ReadAsync(vmId)), selected, target, Require(fresh));
                if (changed != null) return Conflict(changed);
                using var slot = new ManagementObject(selected.Selection!.Slot.ObjectPath);
                slot.Get();
                slot["Parent"] = controller.Path;
                slot["AddressOnParent"] = target.Location.ToString(CultureInfo.InvariantCulture);
                string xml = slot.GetText(TextFormat.CimDtd20);
                var write = await WmiApi.InvokeAsync(VmManagement, "ModifyResourceSettings", p => p["ResourceSettings"] = new[] { xml });
                if (!write.Success) return StorageWriteResult(write);
                var readback = await GetVmStorageItemsAsync(vmId);
                bool confirmed = readback.HasData && readback.Data.Any(d => AtSlot(d, target)
                    && string.Equals(d.Selection?.Slot.InstanceId, selected.Selection.Slot.InstanceId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(d.Selection?.Controller.InstanceId, controller.InstanceId, StringComparison.OrdinalIgnoreCase)
                    && d.DriveType == selected.DriveType && d.DiskType == selected.DiskType
                    && string.Equals(d.PathOrDiskNumber, selected.PathOrDiskNumber, StringComparison.OrdinalIgnoreCase));
                return confirmed ? ApiResponse.Ok() : ApiResponse.Fail("迁移已提交，请核对新槽位和介质。", kind: ApiErrorKind.CleanupRequired);
            }
        }, slotOnly: true);

    private static bool AtSlot(VmStorageAttachment drive, VmStorageSlot slot) => drive.ControllerType == slot.ControllerType &&
        drive.ControllerNumber == slot.ControllerNumber && drive.ControllerLocation == slot.Location;

    /// <summary>结合宿主清单与已实现的 VM 挂接，返回带归属和排序的物理硬盘及光驱。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>物理磁盘和光驱的可用性及已确认的归属。</returns>
    public static Task<ApiResponse<IReadOnlyList<VmPassthroughCandidate>>> GetPassthroughCandidatesAsync(Guid vmId)
        => InspectAsync<IReadOnlyList<VmPassthroughCandidate>>(async () =>
        {
            var current = Require(await VmConfigurationState.ReadAsync(vmId));
            var disks = Require(await HostDiskService.GetHostDisksAsync());
            var optical = Require(await HostDiskService.GetHostOpticalDrivesAsync());
            var owners = Require(await WmiApi.QueryAsync(
                "SELECT VirtualSystemIdentifier FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                obj => obj["VirtualSystemIdentifier"] as string ?? ""));
            var assignments = new List<(Guid VmId, VmStorageAttachment Drive)>();
            foreach (var id in owners.Select(Guid.Parse).Distinct())
            {
                var inventory = Require(await GetVmStorageItemsAsync(id, readMetadata: false));
                assignments.AddRange(inventory.Where(d => d.DiskType == "Physical").Select(d => (id, d)));
            }
            return RankCandidates(vmId, current.Generation, disks, optical, assignments);
        });

    internal static IReadOnlyList<VmPassthroughCandidate> RankCandidates(Guid vmId, int generation,
        IEnumerable<HostDiskInfo> disks, IEnumerable<HostOpticalDriveInfo> optical,
        IReadOnlyList<(Guid VmId, VmStorageAttachment Drive)> assignments)
    {
        var result = new List<VmPassthroughCandidate>();
        foreach (var disk in disks)
        {
            var owner = assignments.FirstOrDefault(a => a.Drive.DriveType == "HardDisk" && a.Drive.DiskNumber == disk.Number);
            Add(disk, null, owner.VmId, disk.CanPassthrough, disk.Status);
        }
        foreach (var device in optical)
        {
            var owner = assignments.FirstOrDefault(a => a.Drive.DriveType == "DvdDrive" &&
                string.Equals(a.Drive.PathOrDiskNumber, device.PnpDeviceId, StringComparison.OrdinalIgnoreCase));
            Add(null, device, owner.VmId, generation == 1, generation == 1 ? "可直通。" : "物理光驱仅支持第一代虚拟机。");
        }
        return result.OrderBy(r => r.Availability).ThenByDescending(r => r.OwnerVmId == vmId)
            .ThenBy(r => r.Disk?.Number ?? int.MaxValue).ThenBy(r => r.OpticalDrive?.PnpDeviceId, StringComparer.OrdinalIgnoreCase).ToArray();

        void Add(HostDiskInfo? disk, HostOpticalDriveInfo? dvd, Guid owner, bool available, string message)
        {
            if (owner != Guid.Empty)
                result.Add(new(disk, dvd, VmPassthroughAvailability.Assigned, owner, owner == vmId ? "已分配给当前虚拟机。" : $"已分配给虚拟机 {owner:D}。"));
            else result.Add(new(disk, dvd, available ? VmPassthroughAvailability.Available : VmPassthroughAvailability.Unavailable, null, message));
        }
    }
}
