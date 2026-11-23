using System.Management;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class VmStorageService
{
    private const string VmManagement = "SELECT * FROM Msvm_VirtualSystemManagementService";
    // The dependency inventory spans all local VMs. Acquire this after the image
    // and physical-device identities, including in add, detach and media edits.
    private const string StorageConfigurationLock = "vm:storage-configuration";
    private static ApiResponse Denied(string message) => ApiResponse.Fail(
        string.IsNullOrWhiteSpace(message) ? "当前设备不支持此维护操作。" : message, kind: ApiErrorKind.InvalidState);
    private static ApiResponse Conflict(string message) => ApiResponse.Fail(message, kind: ApiErrorKind.Conflict);
    private static ApiResponse Invalid(string message) => ApiResponse.Fail(message, kind: ApiErrorKind.InvalidArgument);
    private static ApiResponse Partial(string message, ApiResponse failure) => ApiResponse.Fail(
        message + " " + failure.Error, failure.Code, failure.ErrorSource, failure.Exception,
        failure.ErrorKind == ApiErrorKind.OutcomeUnknown ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired);

    private sealed record MaintenanceTarget(VmStorageAttachment Drive, VmDiskInspection Access, VmDiskChainInspection? Chain);

    private static async Task<ApiResponse> WithStorageConfigurationLock(Func<Task<ApiResponse>> action)
    {
        try { return await HostResourceLock.RunAsync(StorageConfigurationLock, action); }
        catch (Exception ex) { return Failure(ex); }
    }

    private static async Task<ApiResponse> WithMaintenance(Guid vmId, VmStorageAttachment drive,
        Func<MaintenanceTarget, Task<ApiResponse>> execute, bool includeChain = false,
        string? replacement = null, bool ignoreMismatch = false, bool slotOnly = false)
    {
        try
        {
            string? token = drive?.SelectionToken;
            string? fileToken = drive?.MediaFileToken;
            var selected = await CurrentDrive(vmId, token).ConfigureAwait(false);
            if (!slotOnly && selected.DiskType != "Virtual") return Denied("此操作仅适用于文件介质。");
            string path = selected.PathOrDiskNumber;
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var keys = new List<string> { StorageConfigurationLock };
            if (!slotOnly)
            {
                var chain = includeChain ? await ReadChain(path, replacement, ignoreMismatch) : null;
                if (chain is { IsComplete: false }) return Denied(chain.Message);
                foreach (string file in chain?.Nodes.Select(n => n.Path) ?? [path])
                {
                    VmPurgeFileSafety.EnsureNoReparse(file);
                    paths[file] = HostDiskResource.Image(file);
                }
                if (fileToken != null && paths[path] != fileToken) return Conflict("介质文件自查询后已被替换，请重新选择。");
                keys.AddRange(paths.Values);
            }
            else if (selected.DiskType == "Physical" && selected.DriveType == "HardDisk" && selected.DiskNumber >= 0)
                keys.Add(HostDiskResource.Disk(selected.DiskNumber));

            return await HostResourceLock.RunAsync(keys, async () =>
            {
                var current = await CurrentDrive(vmId, token);
                foreach (var pair in paths)
                    if (HostDiskResource.Image(pair.Key) != pair.Value) return Conflict("等待期间磁盘文件已替换，请重新检测。");
                var access = await InspectCurrent(vmId, current);
                var chain = includeChain ? await ReadChain(path, replacement, ignoreMismatch) : null;
                if (chain != null && (!chain.IsComplete || chain.Nodes.Count != paths.Count ||
                    chain.Nodes.Any(n => !paths.TryGetValue(n.Path, out var id) || HostDiskResource.Image(n.Path) != id)))
                    return Conflict("检查期间父链发生变化。" + chain.Message);
                return await execute(new(current, access, chain));
            });
        }
        catch (Exception ex) { return Failure(ex); }
    }

    /// <summary>只读检查可调整容量的范围。无法读取完整分区表时仍返回扩展条件。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="drive">存储查询返回的挂接快照，使用原始选择令牌核验身份。</param>
    /// <returns>磁盘访问条件、当前容量和已核实的调整边界。</returns>
    public static Task<ApiResponse<VmResizeSafety>> InspectResizeSafetyAsync(Guid vmId, VmStorageAttachment drive)
    {
        string? token = drive?.SelectionToken;
        return InspectAsync(async () =>
        {
            var current = await CurrentDrive(vmId, token);
            var access = await InspectCurrent(vmId, current);
            if (!access.CanShrink) return CalculateResize(current, access, null, null);
            return await HostDiskService.WithImageLockAsync(current.PathOrDiskNumber, () =>
                HostResourceLock.RunAsync(StorageConfigurationLock, async () =>
            {
                current = await CurrentDrive(vmId, token);
                access = await InspectCurrent(vmId, current);
                return ProbeResize(current, access);
            }));
        });
    }

    private static VmResizeSafety ProbeResize(VmStorageAttachment drive, VmDiskInspection access)
    {
        VirtualDiskLayout? layout = null;
        long? minimum = null;
        if (access.CanShrink && drive.VirtualCapacityBytes is long size)
        {
            try
            {
                using var pin = HostDiskResource.PinImage(drive.PathOrDiskNumber);
                HostResourceLock.AssertHeld(HostDiskResource.Image(pin));
                layout = VirtualDiskPartitionProbe.Read(drive.PathOrDiskNumber, size);
                if (layout is { Mode: VmPartitionLayoutMode.Empty, LastEnd: null } && drive.VhdType == "Dynamic")
                {
                    using var file = new FileStream(drive.PathOrDiskNumber, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (VirtualDiskLinkage.HasEmptyAllocationTable(file))
                        layout = new(VmPartitionLayoutMode.Empty, 0, "已确认动态磁盘尚未分配任何数据块。");
                }
                using var reader = new VirtualDiskMetadataReader();
                minimum = reader.ReadMinimumSize(drive.PathOrDiskNumber);
            }
            catch (Exception ex) when (ex is not CoreOperationException)
            {
                string error = "无法可靠读取收缩下限，已禁止收缩。" + ex.Message;
                layout = layout == null ? new(VmPartitionLayoutMode.Unavailable, null, error)
                    : layout with { Message = layout.Message + error };
            }
        }
        return CalculateResize(drive, access, minimum, layout);
    }

    internal static VmResizeSafety CalculateResize(VmStorageAttachment drive, VmDiskInspection access, long? minimum,
        VirtualDiskLayout? layout)
    {
        long? size = drive.VirtualCapacityBytes;
        long max = drive.VhdFormat == ".vhd" ? 2040 : 65536;
        var mode = layout?.Mode ?? VmPartitionLayoutMode.Unavailable;
        string message = layout?.Message ?? (access.Message.Length > 0 ? access.Message :
            drive.VhdType == "Differencing" ? "差异盘需先导出或合并才能调整容量。" : "当前设备不支持收缩。");
        long? floor = null;
        if (access.CanShrink && size is > 0 && minimum is >= 0 && minimum <= size &&
            layout?.LastEnd is >= 0 && layout.LastEnd <= size && mode is VmPartitionLayoutMode.Regular or VmPartitionLayoutMode.Conservative or VmPartitionLayoutMode.Empty)
        {
            long bytes = Math.Max(4 * GiB, Math.Max(minimum.Value, layout.LastEnd.Value));
            floor = 1 + (bytes - 1) / GiB;
            if (floor >= 1 + (size.Value - 1) / GiB)
            {
                mode = VmPartitionLayoutMode.NoTailSpace;
                message += " 当前没有可按整 GiB 收缩的尾部空间。";
            }
        }
        return new(access, size, max, minimum, layout?.LastEnd, floor, mode, message);
    }

    internal static ApiResponse ValidateResize(VmResizeSafety safety, long newSizeGiB)
    {
        if (newSizeGiB <= 0 || newSizeGiB > safety.MaximumGiB)
            return Invalid($"容量应在 1 至 {safety.MaximumGiB} GiB 范围内。");
        if (safety.CurrentCapacityBytes is not > 0) return Denied("无法确认磁盘当前容量。");
        long bytes = newSizeGiB * GiB;
        if (bytes == safety.CurrentCapacityBytes) return Invalid("目标容量与当前容量相同。");
        if (bytes > safety.CurrentCapacityBytes) return safety.CanExpand ? ApiResponse.Ok() : Denied(safety.Message);
        if (!safety.CanShrink) return Denied(safety.Message);
        return newSizeGiB >= safety.SafeMinimumGiB ? ApiResponse.Ok() : Invalid($"不能小于安全下限 {safety.SafeMinimumGiB} GiB。");
    }

    /// <summary>按整数 GiB 调整容量。收缩在锁内重新探测分区末尾和磁盘提供程序下限。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="drive">存储查询返回的挂接快照，使用原始选择令牌核验身份。</param>
    /// <param name="newSizeGiB">目标虚拟容量，单位 GiB。</param>
    /// <returns>容量调整结果，含提交或复核失败原因。</returns>
    public static Task<ApiResponse> ResizeVirtualDiskAsync(Guid vmId, VmStorageAttachment drive, long newSizeGiB)
        => WithMaintenance(vmId, drive, async t =>
        {
            var d = t.Drive;
            using var pin = HostDiskResource.PinImage(d.PathOrDiskNumber);
            HostResourceLock.AssertHeld(HostDiskResource.Image(pin));
            bool shrinking = newSizeGiB is > 0 and <= 65536 && newSizeGiB * GiB < d.VirtualCapacityBytes;
            var safety = shrinking ? ProbeResize(d, t.Access) : CalculateResize(d, t.Access, null, null);
            var valid = ValidateResize(safety, newSizeGiB);
            if (!valid.Success) return valid;
            // A scan can take time. Revalidate live state and geometry after releasing its temporary mount.
            var current = await CurrentDrive(vmId, d.SelectionToken);
            var access = await InspectCurrent(vmId, current);
            if (current.VirtualCapacityBytes != d.VirtualCapacityBytes || !(shrinking ? access.CanShrink : access.CanExpand))
                return Conflict("检查期间虚拟机或磁盘状态已变化，请重新检测。");
            if (shrinking)
            {
                using var reader = new VirtualDiskMetadataReader();
                if (newSizeGiB * GiB < reader.ReadMinimumSize(d.PathOrDiskNumber))
                    return Conflict("磁盘安全下限已变化，请重新检查分区。");
            }
            var resized = await ImageMethod("ResizeVirtualHardDisk", input =>
            {
                input["Path"] = d.PathOrDiskNumber;
                input["MaxInternalSize"] = (ulong)(newSizeGiB * GiB);
            });
            if (!resized.Success) return resized;
            try
            {
                using var reader = new VirtualDiskMetadataReader();
                if (reader.ReadGeometry(d.PathOrDiskNumber).Capacity == newSizeGiB * GiB) return ApiResponse.Ok();
                return ApiResponse.Fail("调整请求已完成，但读取的容量与目标不符，请检查磁盘。", kind: ApiErrorKind.OutcomeUnknown);
            }
            catch (Exception ex)
            {
                return ApiResponse.Fail("调整请求已完成，无法复核磁盘容量。" + ex.Message, exception: ex, kind: ApiErrorKind.OutcomeUnknown);
            }
        });

    private static async Task<ApiResponse> ImageMethod(string method, Action<ManagementBaseObject> configure, IProgress<int>? progress = null)
    {
        using var service = WmiApi.GetImageManagementService();
        if (service == null) return Denied("未找到 Hyper-V 映像管理服务。");
        return StorageWriteResult(await WmiApi.InvokeOnObjectAsync(service, method, configure, progress: progress));
    }

    private static ApiResponse StorageWriteResult(ApiResponse result) =>
        !result.Success && result.ErrorSource == ApiErrorSource.Wmi && result.Code is 32771 or 32772
            ? ApiResponse.Fail("Hyper-V 未能确认操作结果，请先重新检查。" + result.Error,
                result.Code, result.ErrorSource, result.Exception, ApiErrorKind.OutcomeUnknown)
            : result;

    private sealed class DiskProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) { try { report(Math.Clamp(value, 0, 100)); } catch { /* Observers cannot undo a completed write. */ } }
    }

    /// <summary>转换到同目录的新文件并切换原槽位。原文件保留，目标已存在时停止。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="drive">存储查询返回的挂接快照，使用原始选择令牌核验身份。</param>
    /// <param name="targetVhdType">目标分配类型，取 Fixed 或 Dynamic。</param>
    /// <returns>转换并恢复挂接后的结果。</returns>
    public static Task<ApiResponse> ConvertVirtualDiskTypeAsync(Guid vmId, VmStorageAttachment drive, string targetVhdType)
        => WithMaintenance(vmId, drive, t =>
        {
            if (!t.Access.CanChangeType) return Task.FromResult(Denied(t.Access.Message));
            if (targetVhdType is not ("Fixed" or "Dynamic") || targetVhdType == t.Drive.VhdType)
                return Task.FromResult(Invalid("请选择不同于当前类型的 Fixed 或 Dynamic。"));
            string path = t.Drive.PathOrDiskNumber;
            string destination = Path.ChangeExtension(path, targetVhdType.ToLowerInvariant() + Extension(path));
            return ConvertAndAttach(vmId, t.Drive, destination, targetVhdType, null);
        });

    /// <summary>生成同名 .vhdx，验证后更新当前槽位。成功后保留原 .vhd。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="drive">存储查询返回的挂接快照，使用原始选择令牌核验身份。</param>
    /// <param name="progress">可选的进度观察器。</param>
    /// <returns>容器转换和挂接切换的结果。</returns>
    public static Task<ApiResponse> UpgradeVhdToVhdxAsync(Guid vmId, VmStorageAttachment drive, IProgress<int>? progress = null)
        => WithMaintenance(vmId, drive, t => t.Access.CanUpgradeVhdx
            ? ConvertAndAttach(vmId, t.Drive, Path.ChangeExtension(t.Drive.PathOrDiskNumber, ".vhdx"), t.Drive.VhdType!, progress)
            : Task.FromResult(Denied(t.Access.Message)));

    private static async Task<ApiResponse> ConvertAndAttach(Guid vmId, VmStorageAttachment drive, string destination,
        string type, IProgress<int>? progress)
    {
        string? publishedId = null;
        var converted = await ConvertToNew(drive, destination, type, new DiskProgress(p => progress?.Report(p * 9 / 10)), id => publishedId = id);
        if (!converted.Success) return converted;
        var updated = await FinishRetarget(destination, "磁盘已转换，原文件已保留。", () => Retarget(vmId, drive, destination, publishedId!));
        if (updated.Success) new DiskProgress(p => progress?.Report(p)).Report(100);
        return updated;
    }

    private static async Task<ApiResponse> ConvertToNew(VmStorageAttachment drive, string destination, string type, IProgress<int>? progress,
        Action<string>? published = null)
    {
        destination = FullPath(destination);
        string ext = Extension(destination);
        if (ext is not (".vhdx" or ".vhd")) return Invalid("目标文件需使用 .vhd 或 .vhdx 扩展名。");
        string directory = Path.GetDirectoryName(destination)!;
        VmPurgeFileSafety.EnsureNoReparse(directory);
        using var sourcePin = HostDiskResource.PinImage(drive.PathOrDiskNumber);
        HostResourceLock.AssertHeld(HostDiskResource.Image(sourcePin));
        if (File.Exists(destination) || Directory.Exists(destination)) return Conflict("目标路径已存在，请选择新文件。");
        // The provider creates its own file. Publish by verified handle, without replacement,
        // so a competing writer cannot lose its destination during a long conversion.
        string stage = Path.Combine(directory, ".exhyperv-" + Guid.NewGuid().ToString("N") + ext);
        using var scope = WmiApi.GetVirtualSystemManagementService();
        using var cls = new ManagementClass(scope.Scope, new ManagementPath("Msvm_VirtualHardDiskSettingData"), null);
        using var settings = cls.CreateInstance();
        settings["Path"] = stage;
        settings["Type"] = (ushort)(type == "Fixed" ? 2 : 3);
        settings["Format"] = (ushort)(ext == ".vhd" ? 2 : 3);
        settings["BlockSize"] = 0u;
        settings["LogicalSectorSize"] = 0u;
        settings["ParentPath"] = null;
        string xml = settings.GetText(TextFormat.CimDtd20);
        var result = await ImageMethod("ConvertVirtualHardDisk", input =>
        {
            input["SourcePath"] = drive.PathOrDiskNumber;
            input["VirtualDiskSettingData"] = xml;
        }, progress);
        if (!result.Success)
        {
            if (result.ErrorKind == ApiErrorKind.OutcomeUnknown || File.Exists(stage))
                return Partial($"请核对转换作业和临时文件 {stage}。", result);
            return result;
        }
        try
        {
            string id = HostDiskResource.Image(stage);
            using (var pin = OwnedFileOperations.PinImage(stage, id))
            using (var reader = new VirtualDiskMetadataReader())
            {
                var geometry = reader.ReadGeometry(stage);
                if (geometry.Format != ext || geometry.Type != type || geometry.Capacity != drive.VirtualCapacityBytes)
                    throw new IOException("目标磁盘的格式、类型或容量与预期不符。");
            }
            OwnedFileOperations.MoveOrDelete(stage, id, destination);
            published?.Invoke(id);
            return ApiResponse.Ok();
        }
        catch (Exception ex) { return Partial($"转换已完成，请核对临时文件 {stage} 和目标 {destination}。", Failure(ex)); }
    }

    /// <summary>替换直接父盘，提交前验证整个候选链。忽略 ID 只针对本次替换的直接父盘。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="drive">存储查询返回的挂接快照，使用原始选择令牌核验身份。</param>
    /// <param name="newParentPath">候选父盘的完整路径。</param>
    /// <param name="ignoreIdMismatch">是否明确允许候选父盘标识不匹配。</param>
    /// <returns>差分盘父路径重连结果。</returns>
    public static Task<ApiResponse> ReconnectParentDiskAsync(Guid vmId, VmStorageAttachment drive, string newParentPath, bool ignoreIdMismatch)
    {
        if (!Path.IsPathFullyQualified(newParentPath ?? "")) return Task.FromResult(Invalid("请选择完整的父盘路径。"));
        return WithMaintenance(vmId, drive, async t =>
        {
            if (!t.Access.CanEditParentPath) return Denied(t.Access.Message);
            string parent = t.Chain!.Nodes[1].Path;
            using var sourcePin = HostDiskResource.PinImage(t.Drive.PathOrDiskNumber);
            using var parentPin = HostDiskResource.PinImage(parent);
            HostResourceLock.AssertHeld(HostDiskResource.Image(sourcePin));
            HostResourceLock.AssertHeld(HostDiskResource.Image(parentPin));
            var result = await ImageMethod("SetParentVirtualHardDisk", p =>
            {
                p["ChildPath"] = t.Drive.PathOrDiskNumber;
                p["ParentPath"] = parent;
                p["LeafPath"] = null;
                p["IgnoreIDMismatch"] = ignoreIdMismatch;
            });
            if (!result.Success) return result;
            try
            {
                using var reader = new VirtualDiskMetadataReader();
                if (HostDiskResource.Image(reader.ReadParentLocator(t.Drive.PathOrDiskNumber)) == HostDiskResource.Image(parentPin))
                    return ApiResponse.Ok();
            }
            catch (Exception ex) { return Partial($"父盘已提交变更，无法核实 {parent}。", Failure(ex)); }
            return ApiResponse.Fail($"父盘已提交变更，请重新核对目标 {parent}。", kind: ApiErrorKind.CleanupRequired);
        }, includeChain: true, replacement: newParentPath, ignoreMismatch: ignoreIdMismatch);
    }

    /// <summary>将验证后的整条差分链导出为独立动态磁盘。保留原挂接和原链。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="drive">存储查询返回的挂接快照，使用原始选择令牌核验身份。</param>
    /// <param name="destinationPath">导出的独立磁盘文件路径。</param>
    /// <param name="progress">可选的进度观察器。</param>
    /// <returns>将链内容导出到独立磁盘的结果。</returns>
    public static Task<ApiResponse> ExportDifferencingDiskAsync(Guid vmId, VmStorageAttachment drive, string destinationPath,
        IProgress<(int StepIndex, int Percent, string Message)>? progress = null)
        => WithMaintenance(vmId, drive, async t =>
        {
            if (!t.Access.CanExport) return Denied(t.Access.Message);
            new DiskProgress(p => progress?.Report((0, p, "已验证导出源链。"))).Report(100);
            var reporter = new DiskProgress(p => progress?.Report((1, p, "正在导出独立动态磁盘。")));
            reporter.Report(0);
            var result = await ConvertToNew(t.Drive, destinationPath, "Dynamic", reporter);
            if (result.Success) new DiskProgress(p => progress?.Report((2, p, "已验证导出文件。"))).Report(100);
            return result;
        }, includeChain: true);

    internal static int MergeDepth(VmDiskChainInspection chain, string target)
        => !chain.IsComplete ? -1 : chain.Nodes.Skip(1).FirstOrDefault(n =>
            string.Equals(n.Path, target, StringComparison.OrdinalIgnoreCase))?.Depth ?? -1;

    /// <summary>合并到已验证的祖先，随后更新原槽位并核实挂接。部分完成时返回恢复路径。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="drive">存储查询返回的挂接快照，使用原始选择令牌核验身份。</param>
    /// <param name="targetAncestorPath">要将叶盘合并到的祖先磁盘完整路径。</param>
    /// <param name="progress">可选的进度观察器。</param>
    /// <returns>合并到指定祖先并处理挂接的结果。</returns>
    public static Task<ApiResponse> MergeDifferencingDiskAsync(Guid vmId, VmStorageAttachment drive, string targetAncestorPath,
        IProgress<(int StepIndex, int Percent, string Message)>? progress = null)
        => WithMaintenance(vmId, drive, async t =>
        {
            if (!t.Access.CanMerge) return Denied(t.Access.Message);
            int depth = MergeDepth(t.Chain!, FullPath(targetAncestorPath));
            if (depth < 1) return Invalid("目标必须是当前完整差分链上的祖先。");
            var nodes = t.Chain!.Nodes;
            string target = nodes[depth].Path;
            string targetId = HostDiskResource.Image(target);
            using var targetPin = OwnedFileOperations.PinImage(target, targetId);
            HostResourceLock.AssertHeld(targetId);
            var dependency = await FindDependency(nodes.Take(depth + 1).Select(n => n.Path), t.Drive);
            if (dependency.Kind != VmDiskAccessKind.Normal) return Denied(dependency.Detail);
            var consumed = nodes.Take(depth).Select(n => new VmPurgeDiskFile(n.Path, HostDiskResource.Image(n.Path))).ToArray();
            foreach (var disk in consumed)
            {
                HostResourceLock.AssertHeld(disk.Identity);
                using var pin = OwnedFileOperations.PinImage(disk.Path, disk.Identity);
            }
            new DiskProgress(p => progress?.Report((0, p, "已验证差异链和目标祖先。"))).Report(100);
            var reporter = new DiskProgress(p => progress?.Report((1, p, "正在合并到所选祖先。")));
            var switched = await MergeAndRetarget(t.Drive.PathOrDiskNumber, target, (source, ancestor) => ImageMethod("MergeVirtualHardDisk", p =>
            {
                p["SourcePath"] = source;
                p["DestinationPath"] = ancestor;
            }, reporter), async ancestor =>
            {
                new DiskProgress(p => progress?.Report((2, p, "正在更新原控制器槽位。"))).Report(0);
                var updated = await Retarget(vmId, t.Drive, ancestor, targetId);
                if (updated.Success) new DiskProgress(p => progress?.Report((2, p, "已验证目标父盘挂接。"))).Report(100);
                return updated;
            });
            if (!switched.Success) return switched;
            var cleanup = await CleanMergedFiles(consumed, VmPurgeDiskProtection.IdentifyFile, async paths =>
            {
                var use = await FindDependency(paths, new VmStorageAttachment());
                return use.Kind == VmDiskAccessKind.Normal ? ApiResponse.Ok() : Denied(use.Detail);
            }, file => OwnedFileOperations.MoveOrDelete(file.Path, file.Identity, null));
            if (cleanup.Success) new DiskProgress(p => progress?.Report((3, p, "已核实合并文件清理完成。"))).Report(100);
            return cleanup.Success ? cleanup : Partial($"原槽位已切换到 {target}，请核对残留文件。", cleanup);
        }, includeChain: true);

    // Some provider versions leave the invalidated child and intermediate files.
    // Only clean the exact pre-merge identities after confirming the VM's new attachment.
    internal static async Task<ApiResponse> CleanMergedFiles(IReadOnlyList<VmPurgeDiskFile> consumed,
        Func<string, ApiResponse<string>> identify, Func<string[], Task<ApiResponse>> checkReferences,
        Action<VmPurgeDiskFile> delete)
    {
        try
        {
            var remaining = new List<VmPurgeDiskFile>();
            foreach (var disk in consumed)
            {
                var current = identify(disk.Path);
                if (!current.Success) return Partial(disk.Path, ApiResponse.FromFailure(current));
                if (!current.HasData) continue;
                if (current.Data != disk.Identity) return Partial(disk.Path, Conflict("文件已替换，已保留。"));
                remaining.Add(disk);
            }
            if (remaining.Count == 0) return ApiResponse.Ok();
            var references = await checkReferences(remaining.Select(d => d.Path).ToArray());
            if (!references.Success) return Partial(string.Join("，", remaining.Select(f => f.Path)), references);
            foreach (var disk in remaining)
            {
                try { delete(disk); }
                catch (Exception ex) { return Partial(disk.Path, Failure(ex)); }
            }
            return ApiResponse.Ok();
        }
        catch (Exception ex) { return Partial("请复核已并入的源盘与中间盘。", Failure(ex)); }
    }

    internal static async Task<ApiResponse> MergeAndRetarget(string source, string target,
        Func<string, string, Task<ApiResponse>> merge, Func<string, Task<ApiResponse>> retarget)
    {
        var result = await merge(source, target);
        if (!result.Success) return result.ErrorKind == ApiErrorKind.OutcomeUnknown
            ? Partial($"合并结果待确认，请检查源盘与目标父盘 {target}。", result) : result;
        return await FinishRetarget(target, "源盘已并入目标父盘。", () => retarget(target));
    }

    internal static async Task<ApiResponse> FinishRetarget(string target, string completed, Func<Task<ApiResponse>> retarget)
    {
        ApiResponse result;
        try { result = await retarget(); }
        catch (Exception ex) { result = Failure(ex); }
        return result.Success ? result : Partial($"{completed} 请手动核对原槽位并定位到 {target}。", result);
    }

    private static async Task<ApiResponse> Retarget(Guid vmId, VmStorageAttachment drive, string target, string identity)
    {
        using var pin = OwnedFileOperations.PinImage(target, identity);
        async Task<VmStorageAttachment> Refresh() => Require(VmStorageRemoval.SelectDevice(vmId, drive.DeviceToken, await GetVmStorageItemsAsync(vmId)));
        bool IsTarget(VmStorageAttachment d) => d.DiskType == "Virtual" && string.Equals(d.PathOrDiskNumber, target, StringComparison.OrdinalIgnoreCase);
        var current = await Refresh();
        if (IsTarget(current)) return ApiResponse.Ok();
        if (current.SelectionToken != drive.SelectionToken || !Require(await VmConfigurationState.ReadAsync(vmId)).IsPoweredOff)
            return Conflict("挂接或虚拟机状态发生变化。");
        var media = current.Selection!.Media.Single();
        using var obj = new ManagementObject(media.ObjectPath);
        obj.Get();
        if (obj["HostResource"] is not string[] { Length: 1 } paths ||
            !string.Equals(paths[0].Trim('"'), drive.PathOrDiskNumber, StringComparison.OrdinalIgnoreCase)) return Conflict("当前介质已变化。");
        obj["HostResource"] = new[] { target };
        string xml = obj.GetText(TextFormat.CimDtd20);
        var write = await WmiApi.InvokeAsync(VmManagement, "ModifyResourceSettings", p => p["ResourceSettings"] = new[] { xml });
        if (!write.Success) return StorageWriteResult(write);
        return IsTarget(await Refresh()) ? ApiResponse.Ok() : Conflict("未确认原槽位已经引用目标磁盘。");
    }

    /// <summary>先移除已核实的挂接，再按文件身份删除介质。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="drive">存储查询返回的挂接快照，使用原始选择令牌核验身份。</param>
    /// <returns>解除挂接和删除已确认归属文件的结果。</returns>
    public static Task<ApiResponse> DeleteAttachedMediaFileAsync(Guid vmId, VmStorageAttachment drive)
        => WithMaintenance(vmId, drive, async t =>
        {
            if (!t.Access.CanDeleteFile) return Denied(t.Access.Message);
            string path = t.Drive.PathOrDiskNumber;
            string id = HostDiskResource.Image(path);
            HostResourceLock.AssertHeld(id);
            using (var pin = OwnedFileOperations.PinImage(path, id)) { }
            return await DetachAndDelete(path, () => RemoveSelectedDriveAsync(vmId, t.Drive), async () =>
            {
                // The former attachment is gone. Include every remaining reference in this last check.
                var dep = await FindDependency([path], new VmStorageAttachment());
                if (dep.Kind != VmDiskAccessKind.Normal) throw new IOException(dep.Detail);
                if (await IsHostMounted(path)) throw new IOException("文件已被宿主挂载。");
                OwnedFileOperations.MoveOrDelete(path, id, null);
            });
        });

    internal static async Task<ApiResponse> DetachAndDelete(string path, Func<Task<ApiResponse>> detach, Func<Task> delete)
    {
        var result = await detach();
        if (!result.Success) return result;
        try { await delete(); return ApiResponse.Ok(); }
        catch (Exception ex) { return Partial($"挂接已移除，介质文件仍需核对：{path}。", Failure(ex)); }
    }
}
