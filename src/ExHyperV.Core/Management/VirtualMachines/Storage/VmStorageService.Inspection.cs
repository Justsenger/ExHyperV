using System.ComponentModel;
using System.Management;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class VmStorageService
{
    private const long GiB = 1L << 30;

    internal static void ReadAttachmentMetadata(VmStorageAttachment drive)
    {
        try
        {
            string path = drive.PathOrDiskNumber;
            drive.FileSizeBytes = new FileInfo(path).Length;
            drive.DiskSizeGiB = drive.FileSizeBytes.Value / (double)GiB;
            drive.SetMediaFileToken(HostDiskResource.Image(path));
            if (drive.DriveType == "HardDisk")
            {
                using var reader = new VirtualDiskMetadataReader();
                (drive.VhdFormat, drive.VirtualCapacityBytes, drive.VhdType) = reader.ReadGeometry(path);
                if (drive.VhdType == "Differencing") drive.ParentPath = reader.ReadParentLocator(path);
            }
            drive.MetadataState = VmDiskMetadataState.Available;
        }
        catch (Exception ex)
        {
            drive.MetadataState = IsMissing(ex) ? VmDiskMetadataState.Missing :
                IsLocked(ex) ? VmDiskMetadataState.Locked : VmDiskMetadataState.Unavailable;
            drive.MetadataMessage = drive.MetadataState switch
            {
                VmDiskMetadataState.Missing => "介质文件不存在，请重新定位。",
                VmDiskMetadataState.Locked => "介质文件正被占用，请稍后重新检测。",
                _ => "无法完整读取介质元数据。" + ex.Message
            };
        }
    }

    private static bool IsMissing(Exception e) => e is FileNotFoundException or DirectoryNotFoundException
        or Win32Exception { NativeErrorCode: 2 or 3 };
    private static bool IsLocked(Exception e) => e is Win32Exception { NativeErrorCode: 32 or 33 }
        || e is IOException && (e.HResult & 0xffff) is 32 or 33;
    private static string Extension(string path) => Path.GetExtension(path).ToLowerInvariant();
    private static string ContainerExtension(string path) => Extension(path) switch { ".avhd" => ".vhd", ".avhdx" => ".vhdx", var ext => ext };
    private static string FullPath(string path) => Path.IsPathFullyQualified(path)
        ? Path.GetFullPath(path) : throw new ArgumentException("请选择完整的文件路径。");

    private static T Require<T>(ApiResponse<T> result) => result.HasData ? result.Data
        : throw new CoreOperationException(result.Success ? ApiResponse.Fail("未找到所请求的资源。", kind: ApiErrorKind.NotFound)
            : ApiResponse.FromFailure(result));

    private static ApiResponse Failure(Exception ex) => ex switch
    {
        CoreOperationException known => known.Result,
        ArgumentException => ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.InvalidArgument),
        _ => HostDiskService.LockFailure(ex)
    };

    private static async Task<ApiResponse<T>> InspectAsync<T>(Func<Task<T>> inspect)
    {
        try { return ApiResponse<T>.Ok(await Task.Run(inspect).ConfigureAwait(false)); }
        catch (Exception ex)
        {
            var f = Failure(ex);
            return ApiResponse<T>.Fail(f.Error, f.Code, f.ErrorSource, f.Exception, f.ErrorKind);
        }
    }

    private static async Task<VmStorageAttachment> CurrentDrive(Guid vmId, string? token)
    {
        if (vmId == Guid.Empty || string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("请提供虚拟机及查询返回的存储挂接。");
        return Require(VmStorageRemoval.Select(vmId, token, await GetVmStorageItemsAsync(vmId)));
    }

    /// <summary>重新核对所选挂接、虚拟机状态、宿主挂载和本机登记的磁盘依赖。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="drive">存储查询返回的挂接快照，使用原始选择令牌核验身份。</param>
    /// <returns>挂接访问状态、限制原因和当前可执行的维护操作。</returns>
    public static Task<ApiResponse<VmDiskInspection>> InspectDiskAsync(Guid vmId, VmStorageAttachment drive)
    {
        string? token = drive?.SelectionToken;
        return InspectAsync(async () => await InspectCurrent(vmId, await CurrentDrive(vmId, token)));
    }

    internal sealed record MediaReference(string InstanceId, string Path, bool IsDisk);

    private static Task<List<MediaReference>> RegisteredMedia() => Task.Run(() =>
    {
        var result = new List<MediaReference>();
        var scope = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
        // The general inventory mapper may skip unreadable rows. A destructive
        // operation must instead stop when a registered reference can't be read.
        Read("Msvm_StorageAllocationSettingData WHERE ResourceSubType = 'Microsoft:Hyper-V:Virtual Hard Disk' " +
            "OR ResourceSubType = 'Microsoft:Hyper-V:Virtual CD/DVD Disk'", slot: false);
        Read("Msvm_ResourceAllocationSettingData WHERE ResourceType = 16 OR ResourceType = 17", slot: true);
        return result;

        void Read(string source, bool slot)
        {
            using var search = new ManagementObjectSearcher(scope, new ObjectQuery(
                "SELECT InstanceID, ResourceType, ResourceSubType, HostResource FROM " + source));
            using var rows = search.Get();
            foreach (ManagementObject row in rows)
            using (row)
            {
                var subtype = row["ResourceSubType"] as string;
                if (slot && subtype == "Microsoft:Hyper-V:Physical Disk Drive") continue;
                bool disk = slot ? Convert.ToInt32(row["ResourceType"]) == 17 : subtype == "Microsoft:Hyper-V:Virtual Hard Disk";
                string[] paths = row["HostResource"] switch
                {
                    string[] values => values, string value => [value], null => [],
                    _ => throw new InvalidDataException("无法解析已登记的存储引用。")
                };
                foreach (string value in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
                {
                    string path = value.Trim('"');
                    if (slot && !Path.IsPathFullyQualified(path)) continue;
                    if (!disk && (!Path.IsPathFullyQualified(path) || Extension(path) != ".iso")) continue;
                    string id = row["InstanceID"] as string ?? throw new InvalidDataException("存储引用缺少设备身份。");
                    result.Add(new(id, path, disk));
                }
            }
        }
    });

    private static async Task<(VmDiskAccessKind Kind, string Detail)> FindDependency(
        IEnumerable<string> targets, VmStorageAttachment selected)
    {
        var ids = targets.Select(HostDiskResource.Image).ToHashSet(StringComparer.Ordinal);
        string? ownId = selected.Selection?.Media.SingleOrDefault()?.InstanceId ?? selected.Selection?.Slot.InstanceId;
        using var reader = new VirtualDiskMetadataReader();
        return EvaluateDependencies(ids, ownId, await RegisteredMedia(), HostDiskResource.Image,
            path => reader.ReadGeometry(path).Type == "Differencing" ? reader.ReadParentLocator(path) : null);
    }

    internal static (VmDiskAccessKind Kind, string Detail) EvaluateDependencies(IReadOnlySet<string> ids, string? ownId,
        IEnumerable<MediaReference> references, Func<string, string> identify, Func<string, string?> parentOf)
    {
        string? uncertain = null;
        var complete = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            if (string.Equals(ownId, reference.InstanceId, StringComparison.OrdinalIgnoreCase)) continue;
            string path = reference.Path;
            var visited = new HashSet<string>();
            try
            {
                for (int depth = 0; ; depth++)
                {
                    string id;
                    try { id = identify(FullPath(path)); }
                    catch (Exception ex) when (IsMissing(ex))
                    {
                        // Stale WMI or deleted snapshot references whose backing file is absent on disk cannot lock or link to existing media.
                        complete.UnionWith(visited);
                        break;
                    }
                    if (ids.Contains(id))
                        return depth == 0 ? (VmDiskAccessKind.LockedExternal, "另一个虚拟机挂接或检查点引用此文件，请先解除引用。")
                            : (VmDiskAccessKind.ParentInUse, $"磁盘 {reference.Path} 的父链正在引用此文件。");
                    if (!reference.IsDisk || complete.Contains(id)) break;
                    if (depth >= 256 || !visited.Add(id)) throw new InvalidDataException("磁盘父链循环或超过检查上限。");
                    string? parent = parentOf(path);
                    if (identify(path) != id) throw new IOException("读取父链期间磁盘身份已变化。");
                    if (parent == null) break;
                    path = parent;
                }
                complete.UnionWith(visited);
            }
            catch (Exception ex) { uncertain ??= $"无法排除 {reference.Path} 的依赖。{ex.Message}"; }
        }
        return uncertain == null ? (VmDiskAccessKind.Normal, "") : (VmDiskAccessKind.Unavailable, uncertain);
    }

    private static async Task<bool> IsHostMounted(string path)
    {
        var state = await HostDiskService.ReadImageAsync(path);
        if (state.HasData) return state.Data.Attached;
        // Storage may refuse a broken chain before returning the image instance.
        // The unloaded state on the file itself still rules out a host attachment.
        using var reader = new VirtualDiskMetadataReader();
        if (!reader.IsLoaded(path)) return false;
        return Require(state).Attached;
    }

    private static async Task<VmDiskInspection> InspectCurrent(Guid vmId, VmStorageAttachment drive)
    {
        var state = Require(await VmConfigurationState.ReadAsync(vmId)).State;
        VmDiskAccessKind kind = VmDiskAccessKind.Normal;
        string message = "";
        if (drive.MetadataState == VmDiskMetadataState.Missing) kind = VmDiskAccessKind.Missing;
        else if (drive.DiskType == "Virtual" && state is VmState.PoweredOff or VmState.Running)
        {
            try
            {
                (kind, message) = await FindDependency([drive.PathOrDiskNumber], drive);
                if (kind == VmDiskAccessKind.Normal && await IsHostMounted(drive.PathOrDiskNumber))
                    kind = VmDiskAccessKind.HostMounted;
                if (kind == VmDiskAccessKind.Normal && state == VmState.PoweredOff)
                    ProbeExclusiveRead(drive.PathOrDiskNumber);
            }
            catch (Exception ex)
            {
                kind = IsMissing(ex) ? VmDiskAccessKind.Missing : IsLocked(ex) ? VmDiskAccessKind.LockedExternal : VmDiskAccessKind.Unavailable;
                message = ex.Message;
            }
        }
        return EvaluateAccess(drive, state, kind, message);
    }

    private static void ProbeExclusiveRead(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.None);
    }

    internal static VmDiskInspection EvaluateAccess(VmStorageAttachment d, VmState state, VmDiskAccessKind kind, string detail = "")
    {
        bool off = state == VmState.PoweredOff, running = state == VmState.Running;
        bool editable = d.DiskType != "Physical";
        bool dvd = d.DriveType == "DvdDrive";
        bool file = d.DiskType == "Virtual";
        bool knownDisk = file && !dvd && d.VhdFormat == ContainerExtension(d.PathOrDiskNumber)
            && d.VhdFormat is ".vhd" or ".vhdx" && d.VhdType is "Dynamic" or "Fixed" or "Differencing";
        bool diff = knownDisk && d.VhdType == "Differencing";
        if (kind != VmDiskAccessKind.Missing)
        {
            if (state is VmState.Saved or VmState.FastSaved or VmState.Suspended) kind = VmDiskAccessKind.VmSavedOrPaused;
            else if (!off && !running) kind = VmDiskAccessKind.Unavailable;
            else if (kind == VmDiskAccessKind.Normal && running) kind = VmDiskAccessKind.VmRunning;
            else if (kind == VmDiskAccessKind.Normal && file && !dvd && !knownDisk) kind = VmDiskAccessKind.Unavailable;
        }
        bool normal = off && kind == VmDiskAccessKind.Normal;
        bool online = kind == VmDiskAccessKind.VmRunning && running && d.ControllerType == "SCSI" && d.VhdFormat == ".vhdx";
        string message = !string.IsNullOrEmpty(detail) ? detail : kind switch
        {
            VmDiskAccessKind.Missing => "介质文件不存在，请重新定位。",
            VmDiskAccessKind.LockedExternal => "介质正被其他虚拟机或进程占用，请解除占用后重新检测。",
            VmDiskAccessKind.HostMounted => "介质已挂载到宿主机，可卸载后重新检测。",
            VmDiskAccessKind.VmRunning => dvd ? "运行中可以更换或弹出 ISO。" : "运行中仅支持扩展 SCSI 上的独立 VHDX。",
            VmDiskAccessKind.VmSavedOrPaused => "虚拟机已保存或暂停，请彻底关机后再编辑。",
            VmDiskAccessKind.ParentInUse => "另一块磁盘的父链正在引用此文件。",
            VmDiskAccessKind.Unavailable => "无法确认存储维护条件，请重新检测。",
            _ => ""
        };
        return new(kind, message, state)
        {
            CanOpenFile = file && off && (normal || kind == VmDiskAccessKind.ParentInUse),
            CanExpand = knownDisk && !diff && (normal || online),
            CanShrink = normal && knownDisk && !diff && d.VhdFormat == ".vhdx",
            CanChangeType = normal && knownDisk && !diff,
            CanCompact = knownDisk && d.VhdType != "Fixed" && off && (normal || kind == VmDiskAccessKind.HostMounted),
            CanUpgradeVhdx = normal && knownDisk && !diff && d.VhdFormat == ".vhd",
            CanExport = off && diff && (normal || kind == VmDiskAccessKind.ParentInUse),
            CanMerge = normal && diff, CanEditParentPath = normal && diff,
            CanDeleteFile = normal && file && (knownDisk || dvd && Extension(d.PathOrDiskNumber) == ".iso"),
            CanEditSlot = off && !d.IsPassthroughStale, CanEditMedia = editable && (off || running && dvd)
        };
    }

    /// <summary>读取完整祖先链。指定替代父盘只影响第一条边，忽略身份不匹配也只作用于第一条边。</summary>
    /// <param name="diskPath">要从叶盘开始检查的完整磁盘路径。</param>
    /// <param name="overrideParentPath">可选的首级候选父盘路径，仅用于本次检查。</param>
    /// <param name="ignoreParentIdMismatch">是否允许首级候选父盘的标识不匹配。</param>
    /// <returns>链状态、已解析节点及终止原因。</returns>
    public static Task<ApiResponse<VmDiskChainInspection>> InspectDifferencingChainAsync(string diskPath,
        string? overrideParentPath = null, bool ignoreParentIdMismatch = false)
        => InspectAsync(() => ReadChain(FullPath(diskPath), overrideParentPath == null ? null : FullPath(overrideParentPath), ignoreParentIdMismatch));

    internal sealed record ChainFacts(string Identity, string Format, string Type, Guid Linkage, Guid? Expected,
        string? Parent, VmDiskChainStatus Status = VmDiskChainStatus.Complete);

    private static Task<VmDiskChainInspection> ReadChain(string path, string? replacement = null, bool ignoreMismatch = false)
        => WalkChain(path, replacement, ignoreMismatch, async (current, overridden) =>
        {
            string id = HostDiskResource.Image(current);
            if (await IsHostMounted(current)) return new(id, "", "", default, null, null, VmDiskChainStatus.HostMounted);
            ProbeExclusiveRead(current);
            using var reader = new VirtualDiskMetadataReader();
            var g = reader.ReadGeometry(current);
            var linkage = g.Format == ".vhdx" ? VirtualDiskLinkage.ReadDataWriteGuid(current) : reader.ReadLinkageId(current, false);
            return new(id, g.Format, g.Type, linkage,
                g.Type == "Differencing" ? reader.ReadLinkageId(current, true) : null,
                g.Type == "Differencing" && !overridden ? reader.ReadParentLocator(current) : null);
        });

    internal static async Task<VmDiskChainInspection> WalkChain(string path, string? replacement, bool ignoreMismatch,
        Func<string, bool, Task<ChainFacts>> read)
    {
        var nodes = new List<VmDiskChainNode>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Guid? expected = null;
        string? format = null;
        VmDiskChainInspection End(VmDiskChainStatus status, string reason) => new(status, nodes.AsReadOnly(), reason);
        for (int depth = 0; depth < 256; depth++)
        {
            nodes.Add(new(depth, path));
            try
            {
                var facts = await read(path, depth == 0 && replacement != null);
                if (facts.Status != VmDiskChainStatus.Complete) return End(facts.Status, $"链中的磁盘正在使用：{path}");
                bool mismatch = expected.HasValue && expected != facts.Linkage;
                bool ignored = mismatch && depth == 1 && ignoreMismatch;
                nodes[^1] = new(depth, path, facts.Format, facts.Type, facts.Linkage, facts.Expected, ignored);
                if (!ids.Add(facts.Identity)) return End(VmDiskChainStatus.Cycle, "父链包含重复的磁盘文件。");
                string formatByName = ContainerExtension(path);
                if (formatByName != facts.Format) return End(VmDiskChainStatus.FormatMismatch, "磁盘扩展名与容器格式不一致。");
                if (format != null && facts.Format != format) return End(VmDiskChainStatus.FormatMismatch, "父盘与子盘的容器格式不一致。");
                if (facts.Linkage == Guid.Empty || facts.Type == "Differencing" && (facts.Expected == null || facts.Expected == Guid.Empty))
                    return End(VmDiskChainStatus.Unavailable, "缺少磁盘链身份，无法验证连接。");
                if (mismatch && !ignored) return End(VmDiskChainStatus.IdMismatch, $"父盘身份不匹配：{path}");
                if (facts.Type != "Differencing")
                {
                    if (depth == 0 && replacement != null) return End(VmDiskChainStatus.Unavailable, "独立磁盘无法重连父盘。");
                    return End(VmDiskChainStatus.Complete, nodes.Any(n => n.IdMismatchIgnored) ? "已明确允许忽略直接父盘的身份差异。" : "已验证磁盘链。");
                }
                format = facts.Format;
                expected = facts.Expected;
                path = depth == 0 && replacement != null ? replacement : facts.Parent ?? throw new InvalidDataException("无法解析父盘位置。");
            }
            catch (Exception ex)
            {
                return End(IsMissing(ex) ? VmDiskChainStatus.Missing : IsLocked(ex) ? VmDiskChainStatus.Locked : VmDiskChainStatus.Unavailable,
                    $"无法读取磁盘链中的文件 {path}。{ex.Message}");
            }
        }
        return End(VmDiskChainStatus.Unavailable, "磁盘父链超过检查上限。");
    }
}
