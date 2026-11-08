using System.IO;
using System.Management;
using System.Text.RegularExpressions;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>本次导出包含哪些检查点。</summary>
public enum VmExportCheckpointMode
{
    /// <summary>仅导出当前虚拟机，不包含检查点树。</summary>
    None,
    /// <summary>导出全部检查点。</summary>
    All,
    /// <summary>导出选定检查点；执行时强制包含其磁盘和运行状态。</summary>
    Single
}

/// <summary>导出一台虚拟机的配置、磁盘、检查点和运行状态。</summary>
public static partial class VmExportService
{
    /// <summary>可供导出排除选择的虚拟硬盘；将查询返回的对象传回导出方法。</summary>
    public sealed class VirtualHardDiskInfo
    {
        internal VirtualHardDiskInfo(Guid virtualMachineId, string resourceId, string path)
        {
            VirtualMachineId = virtualMachineId;
            ResourceId = resourceId;
            Path = path;
        }

        internal Guid VirtualMachineId { get; }
        internal string ResourceId { get; }
        /// <summary>虚拟硬盘的实际文件路径。</summary>
        public string Path { get; }
    }

    /// <summary>检查点身份和父子关系；单检查点导出使用查询返回的此对象。</summary>
    public sealed class CheckpointInfo
    {
        internal CheckpointInfo(Guid virtualMachineId, string instanceId, string? parentInstanceId,
            string id, string name, DateTime createdDate, string path)
        {
            VirtualMachineId = virtualMachineId;
            InstanceId = instanceId;
            ParentInstanceId = parentInstanceId;
            Id = id;
            Name = name;
            CreatedDate = createdDate;
            Path = path;
        }

        internal Guid VirtualMachineId { get; }
        internal string InstanceId { get; }
        internal string? ParentInstanceId { get; }
        internal string Path { get; }
        /// <summary>检查点配置的 GUID 文本；主机未提供可识别的 GUID 时为空，仍可使用此对象导出。</summary>
        public string Id { get; }
        /// <summary>父检查点的配置 GUID 文本；根节点或无法识别父节点时为 null。</summary>
        public string? ParentId { get; internal set; }
        /// <summary>检查点的显示名称。</summary>
        public string Name { get; }
        /// <summary>检查点创建时间。</summary>
        public DateTime CreatedDate { get; }
    }

    private sealed record VirtualHardDiskAllocation(
        string InstanceId,
        string ResourceSubType,
        string[] HostResources);

    private sealed record CheckpointSelection(
        string VirtualSystemType,
        string VirtualSystemIdentifier,
        string? ConfigurationId,
        string Path);

    /// <summary>读取可用于导出排除选择的虚拟硬盘对象和文件路径。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>磁盘列表或查询错误；排除磁盘时原样传回所选对象，导出前会重新验证其所属虚拟机和当前挂接状态。</returns>
    public static async Task<ApiResponse<List<VirtualHardDiskInfo>>> GetVirtualHardDisksAsync(
        Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<List<VirtualHardDiskInfo>>.Fail(
                Properties.Resources.Error_Net_VmNotFound);

        string escapedVmId = WmiApi.Escape(vmId.ToString("D"));
        var outer = await WmiApi.WithFirstAsync(
            $"SELECT * FROM Msvm_VirtualSystemSettingData " +
            $"WHERE VirtualSystemIdentifier = '{escapedVmId}' " +
            $"AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
            settings => WmiApi.QueryRelatedCimAsync(
                settings,
                "Msvm_VirtualSystemSettingDataComponent",
                "Msvm_StorageAllocationSettingData",
                "GroupComponent",
                "PartComponent",
                obj => new VirtualHardDiskAllocation(
                    obj["InstanceID"]?.ToString() ?? string.Empty,
                    obj["ResourceSubType"]?.ToString() ?? string.Empty,
                    obj["HostResource"] as string[]
                        ?? (obj["HostResource"] is string path
                            ? new[] { path }
                            : Array.Empty<string>())),
                WmiScope.HyperV),
            WmiScope.HyperV);

        if (!outer.Success)
            return ApiResponse<List<VirtualHardDiskInfo>>.Fail(
                outer.Error, outer.Code, outer.ErrorSource, outer.Exception, outer.ErrorKind);

        if (!outer.HasData)
            return ApiResponse<List<VirtualHardDiskInfo>>.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);

        var inner = outer.Data!;
        if (!inner.Success)
            return ApiResponse<List<VirtualHardDiskInfo>>.Fail(
                inner.Error, inner.Code, inner.ErrorSource, inner.Exception, inner.ErrorKind);

        var disks = (inner.Data ?? new List<VirtualHardDiskAllocation>())
            .Where(item => string.Equals(
                item.ResourceSubType,
                "Microsoft:Hyper-V:Virtual Hard Disk",
                StringComparison.OrdinalIgnoreCase))
            .Select(item => new VirtualHardDiskInfo(
                vmId,
                item.InstanceId,
                item.HostResources.FirstOrDefault() ?? string.Empty))
            .Where(item => !string.IsNullOrWhiteSpace(item.ResourceId)
                        && !string.IsNullOrWhiteSpace(item.Path))
            .ToList();

        return ApiResponse<List<VirtualHardDiskInfo>>.Ok(disks);
    }

    /// <summary>读取指定虚拟机的检查点和父子关系。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>包含检查点 GUID、父子关系、名称和创建时间的列表；单检查点导出原样传回所选对象，失败通过响应返回。</returns>
    public static async Task<ApiResponse<List<CheckpointInfo>>> GetCheckpointsAsync(
        Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<List<CheckpointInfo>>.Fail(
                Properties.Resources.Error_Net_VmNotFound);

        var vm = await WmiApi.QueryFirstAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}", obj => obj["Name"]?.ToString());
        if (!vm.Success) return ApiResponse<List<CheckpointInfo>>.Fail(vm.Error, vm.Code, vm.ErrorSource, vm.Exception, vm.ErrorKind);
        if (!vm.HasData) return ApiResponse<List<CheckpointInfo>>.Fail(Properties.Resources.Error_Net_VmNotFound, kind: ApiErrorKind.NotFound);

        string escapedVmId = WmiApi.Escape(vmId.ToString("D"));
        var checkpointsResult = await WmiApi.QueryAsync(
            $"SELECT * FROM Msvm_VirtualSystemSettingData " +
            $"WHERE VirtualSystemIdentifier = '{escapedVmId}' " +
            "AND VirtualSystemType = 'Microsoft:Hyper-V:Snapshot:Realized'",
            obj => new CheckpointInfo(
                vmId,
                obj["InstanceID"]?.ToString() ?? string.Empty,
                ExtractInstanceId(obj["Parent"]?.ToString()),
                GetCheckpointId(obj["ConfigurationID"]?.ToString())
                    ?? GetCheckpointId(obj["InstanceID"]?.ToString()) ?? string.Empty,
                obj["ElementName"]?.ToString() ?? string.Empty,
                obj["CreationTime"] is string creationTime
                    ? ManagementDateTimeConverter.ToDateTime(creationTime)
                    : DateTime.MinValue,
                obj.Path.Path),
            WmiScope.HyperV);

        if (!checkpointsResult.Success)
            return ApiResponse<List<CheckpointInfo>>.Fail(
                checkpointsResult.Error,
                checkpointsResult.Code,
                checkpointsResult.ErrorSource, checkpointsResult.Exception, checkpointsResult.ErrorKind);

        var checkpoints = checkpointsResult.Data ?? new List<CheckpointInfo>();
        var checkpointsByInstanceId = checkpoints.ToLookup(item => item.InstanceId, StringComparer.OrdinalIgnoreCase);
        foreach (var checkpoint in checkpoints)
        {
            var parent = checkpoint.ParentInstanceId == null ? null
                : checkpointsByInstanceId[checkpoint.ParentInstanceId].FirstOrDefault();
            checkpoint.ParentId = string.IsNullOrEmpty(parent?.Id) ? null : parent.Id;
        }
        return ApiResponse<List<CheckpointInfo>>.Ok(checkpoints);
    }

    /// <summary>将一台虚拟机的配置及选定的磁盘、检查点和运行状态导出到目录。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="destinationRoot">已存在的导出根目录，服务在其中建立 GUID 命名的输出目录。</param>
    /// <param name="includeVirtualHardDisks">是否复制虚拟硬盘；单检查点模式会强制开启。</param>
    /// <param name="excludedVirtualHardDisks">由 GetVirtualHardDisksAsync 返回的待排除磁盘对象，可为空集合；Core 重新核对其所属虚拟机和挂接状态，不能与检查点模式组合。</param>
    /// <param name="checkpointMode">不含检查点、全部检查点或单个检查点的导出模式。</param>
    /// <param name="selectedCheckpoint">单检查点模式下由 GetCheckpointsAsync 返回的对象；Core 重新检查其仍存在且属于 vmId，其他模式可为 null。</param>
    /// <param name="includeRuntimeState">是否包含运行状态；单检查点模式会强制开启。</param>
    /// <param name="progress">可选进度接收器；null 时不通知。回调线程由具体 IProgress 实现决定。</param>
    /// <returns>成功时 OutputPath 为实际导出目录；失败保留已产生路径及未知操作状态。单检查点模式强制包含磁盘与运行状态，不能与磁盘排除选择组合。</returns>
    /// <remarks>
    /// <para>输出目录由 <c>ExportAsync</c> 完成。打包是调用方选择的后续步骤：成功后将结果的 <c>OutputPath</c> 交给 <see cref="VmExportPackagingService"/>，指定 ZIP 路径和 <see cref="VmExportPackageOptions"/>；可选择压缩级别、AES-256 密码以及保留源目录或校验后删除。Core 不自动命名归档或串联多台导出。</para>
    /// <para>选项重载默认均衡压缩、不加密、保留源目录。原 <c>VmExportPackageMode</c> 重载保持原行为：<c>Store</c> 不压缩，<c>Compress</c> 优先最小体积，两者都在校验成功后尝试清理专用导出暂存目录。归档结果分别报告 <c>SourceHandling</c>、<c>SourceDirectoryRemoved</c> 和清理错误。</para>
    /// <para>本入口不提供取消原生导出。提交后的失败如果未能确认终态会标记 OperationStateUnknown，此时不得删除保留目录或假定任务已停止；Core 不自动清理失败输出。</para>
    /// </remarks>
    /// <instanceentry label="导出虚拟机" category="导出" order="10">
    /// <step>指定目标 GUID 和已存在的输出根目录，决定是否包含磁盘、检查点和运行状态。</step>
    /// <step>需要排除磁盘或选择单个检查点时，先用 <c>GetVirtualHardDisksAsync</c> 或 <c>GetCheckpointsAsync</c> 取得对应对象。随后调用 <c>ExportAsync</c>。</step>
    /// <step>从结果的 <c>OutputPath</c> 取得导出目录；如需 ZIP，再执行可选打包步骤，并分别处理导出和归档结果。</step>
    /// </instanceentry>
    public static Task<VmExportResult> ExportAsync(
        Guid vmId,
        string destinationRoot,
        bool includeVirtualHardDisks,
        IReadOnlyCollection<VirtualHardDiskInfo> excludedVirtualHardDisks,
        VmExportCheckpointMode checkpointMode,
        CheckpointInfo? selectedCheckpoint,
        bool includeRuntimeState,
        IProgress<int>? progress = null)
        => Task.Run(async () =>
        {
            string? exportDirectory = null;
            string? stagingDirectory = null;
            bool ownsOutput = false;
            bool operationStateUnknown = false;
            VmExportResult Fail(string error, int code = -1, ApiErrorSource source = ApiErrorSource.None,
                Exception? exception = null, ApiErrorKind kind = ApiErrorKind.Unspecified)
            {
                var retained = ownsOutput ? new[] { exportDirectory, stagingDirectory }
                    .Where(path => path != null && (Directory.Exists(path) || File.Exists(path))).Select(path => path!).ToArray() : [];
                return new(false, vmId, null, operationStateUnknown, Array.AsReadOnly(retained), error, code, source, kind);
            }
            try
            {
                if (vmId == Guid.Empty)
                    return Fail(Properties.Resources.Error_Net_VmNotFound);

                if (!Directory.Exists(destinationRoot))
                    return Fail(Properties.Resources.VmExport_PathRequired);

                string vmDirectoryName = vmId.ToString("D").ToUpperInvariant();
                exportDirectory = Path.Combine(destinationRoot, vmDirectoryName);
                stagingDirectory = Path.Combine(destinationRoot, vmDirectoryName + ".partial");
                if (Directory.Exists(exportDirectory) || File.Exists(exportDirectory))
                    return Fail(
                        string.Format(Properties.Resources.VmExport_TargetExists, vmDirectoryName));
                if (Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory))
                    return Fail(
                        string.Format(Properties.Resources.VmExport_TargetExists, vmDirectoryName + ".partial"));

                if (checkpointMode != VmExportCheckpointMode.None
                    && excludedVirtualHardDisks.Count > 0)
                    return Fail(
                        Properties.Resources.VmExport_DiskSelectionCheckpointsConflict);

                if (!Enum.IsDefined(checkpointMode))
                    return Fail("检查点导出模式无效。", kind: ApiErrorKind.InvalidArgument);
                using var service = WmiApi.GetVirtualSystemManagementService();
                using var vm = WmiApi.GetVmComputerSystem(vmId);
                if (vm == null)
                    return Fail(Properties.Resources.Error_Net_VmNotFound);

                string vmName = vm["ElementName"]?.ToString() ?? vmId.ToString("D");
                Guid? configurationId = vmId;
                string? validatedCheckpointPath = null;
                if (checkpointMode == VmExportCheckpointMode.Single)
                {
                    if (selectedCheckpoint == null)
                        return Fail(
                            Properties.Resources.VmExport_CheckpointSelectionRequired);

                    if (selectedCheckpoint.VirtualMachineId != vmId
                        || string.IsNullOrWhiteSpace(selectedCheckpoint.InstanceId))
                        return Fail(Properties.Resources.VmExport_CheckpointUnavailable);

                    string escapedCheckpointId = WmiApi.Escape(selectedCheckpoint.InstanceId);
                    string escapedVmId = WmiApi.Escape(vmId.ToString("D"));
                    var checkpointResult = await WmiApi.QueryFirstAsync(
                        $"SELECT * FROM Msvm_VirtualSystemSettingData " +
                        $"WHERE InstanceID = '{escapedCheckpointId}' " +
                        $"AND VirtualSystemIdentifier = '{escapedVmId}' " +
                        "AND VirtualSystemType = 'Microsoft:Hyper-V:Snapshot:Realized'",
                        checkpoint => new CheckpointSelection(
                            checkpoint["VirtualSystemType"]?.ToString() ?? string.Empty,
                            checkpoint["VirtualSystemIdentifier"]?.ToString() ?? string.Empty,
                            GetCheckpointId(checkpoint["ConfigurationID"]?.ToString())
                                ?? GetCheckpointId(checkpoint["InstanceID"]?.ToString()),
                            checkpoint.Path.Path),
                        WmiScope.HyperV);

                    if (!checkpointResult.Success)
                        return Fail(
                            checkpointResult.Error,
                            checkpointResult.Code,
                            checkpointResult.ErrorSource, checkpointResult.Exception, checkpointResult.ErrorKind);

                    if (!checkpointResult.HasData)
                        return Fail(
                            Properties.Resources.VmExport_CheckpointUnavailable);

                    var checkpoint = checkpointResult.Data!;
                    string realizedVmId = vm["Name"]?.ToString() ?? string.Empty;
                    if (!string.Equals(
                            checkpoint.VirtualSystemType,
                            "Microsoft:Hyper-V:Snapshot:Realized",
                            StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(
                            checkpoint.VirtualSystemIdentifier,
                            realizedVmId,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return Fail(
                            Properties.Resources.VmExport_CheckpointUnavailable);
                    }

                    validatedCheckpointPath = checkpoint.Path;
                    // A single snapshot is exported as a standalone VM under the snapshot's identity.
                    configurationId = Guid.TryParse(checkpoint.ConfigurationId, out var checkpointGuid) ? checkpointGuid : null;
                }

                string[] validatedExcludedDiskIds = Array.Empty<string>();
                if (checkpointMode == VmExportCheckpointMode.None
                    && includeVirtualHardDisks && excludedVirtualHardDisks.Count > 0)
                {
                    var selectedDisks = excludedVirtualHardDisks.ToArray();
                    var disksResult = await GetVirtualHardDisksAsync(vmId);
                    if (!disksResult.Success)
                        return Fail(disksResult.Error, disksResult.Code, disksResult.ErrorSource, disksResult.Exception, disksResult.ErrorKind);

                    var currentDisks = disksResult.Data ?? new List<VirtualHardDiskInfo>();
                    var resolvedIds = new List<string>();
                    foreach (var selectedDisk in selectedDisks)
                    {
                        if (selectedDisk == null || selectedDisk.VirtualMachineId != vmId)
                            return Fail(Properties.Resources.Error_Storage_DiskNotFound);

                        var currentDisk = currentDisks.FirstOrDefault(disk =>
                            string.Equals(disk.ResourceId, selectedDisk.ResourceId, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(disk.Path, selectedDisk.Path, StringComparison.OrdinalIgnoreCase));
                        if (currentDisk == null)
                            return Fail(Properties.Resources.Error_Storage_DiskNotFound);
                        resolvedIds.Add(currentDisk.ResourceId);
                    }
                    validatedExcludedDiskIds = resolvedIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                }

                bool effectiveIncludeStorage = checkpointMode == VmExportCheckpointMode.Single
                    || includeVirtualHardDisks;
                bool effectiveIncludeRuntime = checkpointMode == VmExportCheckpointMode.Single
                    || includeRuntimeState;

                var settingsResult = await WmiApi.CreateInstanceTextAsync(
                    "Msvm_VirtualSystemExportSettingData",
                    settings =>
                    {
                        if (validatedCheckpointPath != null
                            && !settings.HasProperty("SnapshotVirtualSystem"))
                        {
                            return ApiResponse.Fail(
                                Properties.Resources.VmExport_CheckpointSelectionUnsupported);
                        }

                        settings["CopySnapshotConfiguration"] = checkpointMode switch
                        {
                            VmExportCheckpointMode.All => (byte)0,
                            VmExportCheckpointMode.None => (byte)1,
                            VmExportCheckpointMode.Single => (byte)2,
                            _ => (byte)1
                        };

                        if (validatedCheckpointPath != null)
                            settings["SnapshotVirtualSystem"] = validatedCheckpointPath;

                        settings["CopyVmStorage"] = effectiveIncludeStorage;
                        settings["CopyVmRuntimeInformation"] = effectiveIncludeRuntime;
                        settings["CreateVmExportSubdirectory"] = true;

                        if (checkpointMode == VmExportCheckpointMode.None
                            && includeVirtualHardDisks
                            && validatedExcludedDiskIds.Length > 0)
                        {
                            if (!settings.HasProperty("ExcludedVirtualHardDisks"))
                            {
                                return ApiResponse.Fail(
                                    Properties.Resources.VmExport_SelectDisksUnsupported);
                            }

                            settings["ExcludedVirtualHardDisks"] =
                                validatedExcludedDiskIds;

                            if (settings.HasProperty("DisableDifferentialOfIgnoredStorage"))
                                settings["DisableDifferentialOfIgnoredStorage"] = true;
                        }

                        // Running VMs can be exported either crash-consistently or with saved state.
                        if (settings.HasProperty("CaptureLiveState"))
                            settings["CaptureLiveState"] = effectiveIncludeRuntime ? (byte)1 : (byte)0;

                        return ApiResponse.Ok();
                    },
                    WmiScope.HyperV);

                if (!settingsResult.Success)
                    return Fail(
                        settingsResult.Error,
                        settingsResult.Code,
                        settingsResult.ErrorSource, settingsResult.Exception, settingsResult.ErrorKind);

                string settingsXml = settingsResult.Data!;
                Directory.CreateDirectory(stagingDirectory);
                ownsOutput = true;
                VmExportAccessService.EnsureCurrentUserCanModifyTree(stagingDirectory);
                operationStateUnknown = true;
                var result = await WmiApi.InvokeOnObjectAsync(
                    service,
                    "ExportSystemDefinition",
                    p =>
                    {
                        p["ComputerSystem"] = vm.Path.Path;
                        p["ExportDirectory"] = stagingDirectory;
                        p["ExportSettingData"] = settingsXml;
                    },
                    progress: progress,
                    timeout: TimeSpan.FromHours(24));

                // InvokeOnObjectAsync distinguishes a terminal failure from losing the job after submission.
                operationStateUnknown = result.ErrorKind == ApiErrorKind.OutcomeUnknown;
                if (!result.Success)
                    return Fail(
                        result.Error, result.Code, result.ErrorSource, result.Exception, result.ErrorKind);

                progress?.Report(100);

                string? stagedExportDirectory = FindExportDirectory(
                    stagingDirectory,
                    configurationId);
                if (stagedExportDirectory == null)
                    return Fail(string.Format(
                        Properties.Resources.VmExport_ConfigurationMissing,
                        vmName,
                        vmDirectoryName));

                if (string.Equals(
                        stagedExportDirectory,
                        stagingDirectory,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(stagingDirectory, exportDirectory);
                }
                else
                {
                    Directory.Move(stagedExportDirectory, exportDirectory);
                    if (!Directory.EnumerateFileSystemEntries(stagingDirectory).Any())
                        Directory.Delete(stagingDirectory);
                }

                bool hasConfiguration = FindCurrentConfigurationFile(
                    exportDirectory,
                    configurationId) != null;
                return hasConfiguration
                    ? new VmExportResult(true, vmId, exportDirectory, false, [])
                    : Fail(string.Format(
                        Properties.Resources.VmExport_ConfigurationMissing,
                        vmName,
                        vmDirectoryName));
            }
            catch (ManagementException ex)
            {
                return Fail(
                    ex.Message, (int)ex.ErrorCode, ApiErrorSource.Wmi, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Fail(
                    ex.Message, 5, ApiErrorSource.Win32, ex);
            }
            catch (Exception ex)
            {
                return Fail(ex.Message, -1, ApiErrorSource.None, ex);
            }
        });

    private static string? FindExportDirectory(string stagingDirectory, Guid? configurationId)
    {
        string? configurationPath = FindCurrentConfigurationFile(
            stagingDirectory,
            configurationId);

        if (configurationPath == null)
            return null;

        string relativePath = Path.GetRelativePath(stagingDirectory, configurationPath);
        int separatorIndex = relativePath.IndexOf(Path.DirectorySeparatorChar);
        if (separatorIndex < 0)
            return stagingDirectory;

        string firstSegment = relativePath[..separatorIndex];
        return string.Equals(
            firstSegment,
            "Virtual Machines",
            StringComparison.OrdinalIgnoreCase)
            ? stagingDirectory
            : Path.Combine(stagingDirectory, firstSegment);
    }

    internal static string? FindCurrentConfigurationFile(string directory, Guid? configurationId)
    {
        // Some providers expose no parseable snapshot GUID. Only an unambiguous primary
        // configuration in the new export may stand in for that missing identity.
        var candidates = Directory.EnumerateFiles(directory, "*.vmcx", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "Virtual Machines",
                    StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id) &&
                (!configurationId.HasValue || id == configurationId))
            .Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string? GetCheckpointId(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        const string prefix = "Microsoft:";
        string id = value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..] : value;
        return Guid.TryParse(id, out var guid) ? guid.ToString("D") : null;
    }

    private static string? ExtractInstanceId(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        Match match = Regex.Match(
            path,
            "InstanceID=\"([^\"]+)\"",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Replace("\\\\", "\\") : null;
    }
}
