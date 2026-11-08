using System.IO;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>提供保留磁盘与恢复配置的删除，以及注销后清理专属文件的彻底删除。</summary>
/// <remarks>执行前会按需要使虚拟机停止；部分失败可能发生在注销或文件操作已经完成后，应读取具体操作返回的状态，不能将 Success=false 解释为没有发生修改。</remarks>
public static class VmDeleteService
{
    /// <summary>彻底删除预览。这里只返回可证明属于该 VM 的文件；共享根目录永远不会被当成 VM 私有目录。</summary>
    /// <param name="ConfigDirectory">可确定的虚拟机配置目录；成功查询但无法确定目录时为 null。预览失败由外层响应表达。</param>
    /// <param name="DeletesConfigDirectory">是否能够证明整个配置目录归该虚拟机独占并计划删除。</param>
    /// <param name="ConfigFiles">明确识别为该虚拟机所有的配置文件路径。</param>
    /// <param name="DiskFiles">计划删除的专属磁盘文件路径。</param>
    public sealed record PurgePreview(
        string? ConfigDirectory,
        bool DeletesConfigDirectory,
        List<string> ConfigFiles,
        List<string> DiskFiles)
    {
        /// <summary>因共享依赖、身份或依赖不明而保留的磁盘文件。</summary>
        public IReadOnlyList<string> RetainedDiskFiles { get; init; } = Array.Empty<string>();
        /// <summary>未能完成磁盘依赖核对的原因；执行时仍注销虚拟机并仅清理已确认专属的配置。</summary>
        public IReadOnlyList<string> DiskDependencyIssues { get; init; } = Array.Empty<string>();
    }

    private sealed record PurgeTargets(
        string? ConfigDirectory,
        bool DeletesConfigDirectory,
        List<string> ConfigFiles,
        List<string> ConfigDirectories,
        List<string> DiskFiles,
        VmPurgeDiskPlan? DiskPlan = null,
        VmPurgeConfigFiles? SafeConfig = null);

    /// <summary>只读收集当前可证明属于该虚拟机的配置和磁盘清理目标。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>当前清理目标的查询响应；空 GUID 返回 InvalidArgument，虚拟机不存在返回 NotFound，读取失败保留错误。预览不预留或批准固定文件范围，彻底删除以执行时重新核对的最新归属清单为准。</returns>
    public static async Task<ApiResponse<PurgePreview>> PreviewPurgeAsync(Guid vmId)
    {
        if (vmId == Guid.Empty)
            return ApiResponse<PurgePreview>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
        try
        {
            var identity = await WmiApi.QueryFirstAsync(
                $"SELECT Name FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => obj["Name"]?.ToString());
            if (!identity.Success)
                return ApiResponse<PurgePreview>.Fail(identity.Error, identity.Code, identity.ErrorSource,
                    identity.Exception, identity.ErrorKind);
            if (!identity.HasData)
                return ApiResponse<PurgePreview>.Fail("找不到指定虚拟机。", kind: ApiErrorKind.NotFound);
            PurgeTargets targets = await CollectPurgeTargetsAsync(vmId.ToString("D"), forPurge: true);
            return ApiResponse<PurgePreview>.Ok(new(
                targets.ConfigDirectory, targets.DeletesConfigDirectory,
                targets.ConfigFiles, targets.DiskFiles)
            {
                RetainedDiskFiles = targets.DiskPlan!.Shared.Concat(targets.DiskPlan.Uncertain).ToArray(),
                DiskDependencyIssues = targets.DiskPlan.Issues
            });
        }
        catch (Exception ex) { return ApiResponse<PurgePreview>.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OperationFailed); }
    }

    /// <summary>保存恢复配置后注销一台虚拟机，保留其磁盘文件。</summary>
    /// <param name="vmName">仅用于错误说明的显示名称；目标定位始终使用 vmId。</param>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>流程结果分别报告注册状态、恢复位置与清理错误；失败可能发生在已注销后，Unknown 不表示仍在册。</returns>
    /// <instanceentry label="删除虚拟机" category="删除" mode="仅注销" order="10">
    /// <step>准备目标虚拟机的 GUID 和显示名称；GUID 用于确定操作对象。</step>
    /// <step>调用 <c>DeleteVmAsync</c>。Core 先保存不含磁盘副本的恢复配置，再注销虚拟机并将配置放回原目录。</step>
    /// <step>读取 <c>VmDeletionResult</c>，分别确认注销状态和配置保留情况。处理多台虚拟机时，由调用方逐次提交并记录每台结果。</step>
    /// </instanceentry>
    public static async Task<VmDeletionResult> DeleteVmAsync(string vmName, Guid vmId)
    {
        string recoveryRoot = Path.Combine(Path.GetTempPath(), "ExHyperV", "DeleteRecovery", Guid.NewGuid().ToString("N"));
        string? recoveryPackage = null;
        bool destroyAttempted = false;
        bool exportStateUnknown = false;
        bool restored = false;
        VmRegistrationState state = VmRegistrationState.Unknown;
        VmDeletionResult result;
        try
        {
            if (vmId == Guid.Empty)
                return new(false, state, null, [], Properties.Resources.Error_Net_VmNotFound);
            PurgeTargets targets = await CollectPurgeTargetsAsync(vmId.ToString("D"));
            if (string.IsNullOrWhiteSpace(targets.ConfigDirectory))
                return new(false, state, null, [], string.Format(Properties.Resources.VmDelete_ConfigRootMissing, vmName));

            Directory.CreateDirectory(recoveryRoot);
            var export = await VmExportService.ExportAsync(vmId, recoveryRoot,
                includeVirtualHardDisks: false,
                excludedVirtualHardDisks: Array.Empty<VmExportService.VirtualHardDiskInfo>(),
                checkpointMode: VmExportCheckpointMode.All, selectedCheckpoint: null, includeRuntimeState: true);
            exportStateUnknown = export.OperationStateUnknown;
            if (!export.Success || string.IsNullOrWhiteSpace(export.OutputPath))
                result = new(false, state, null, export.RetainedPaths, export.Error,
                    exportStateUnknown ? "恢复导出任务状态未确认，已保留其工作目录。" : null);
            else
            {
                recoveryPackage = export.OutputPath;
                state = VmRegistrationState.Registered;
                var off = await EnsureOffAsync(vmName, vmId);
                if (!off.Success)
                    result = new(false, VmRegistrationState.Unknown, null, [], off.Message);
                else
                {
                    destroyAttempted = true;
                    var destroy = await DestroyAsync(vmName, vmId);
                    state = destroy.RegistrationState;
                    if (!destroy.Success)
                        result = destroy with { RecoveryPath = recoveryPackage, RetainedPaths = [recoveryPackage] };
                    else
                    {
                        await RestoreConfigurationPackageAsync(recoveryPackage, targets.ConfigDirectory, vmId,
                            targets.ConfigFiles, targets.ConfigDirectories);
                        restored = true;
                        result = new(true, state, targets.ConfigDirectory, []);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            result = new(false, state, destroyAttempted ? recoveryPackage : null,
                destroyAttempted && recoveryPackage != null ? [recoveryPackage] : [],
                state == VmRegistrationState.Unregistered ? "虚拟机已注销，但恢复配置尚未完整还原。" : ex.Message,
                state == VmRegistrationState.Unregistered ? ex.Message : null) { Failure = ApiFailure.From(ex), CleanupFailure = state == VmRegistrationState.Unregistered ? ApiFailure.From(ex) : null };
        }
        if ((!destroyAttempted || restored) && !exportStateUnknown)
        {
            if (!await TryDeleteDirAsync(recoveryRoot))
                result = result with
                {
                    RetainedPaths = result.RetainedPaths.Append(recoveryRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    CleanupError = string.Join(Environment.NewLine, new[] { result.CleanupError, "未能清理临时恢复目录：" + recoveryRoot }
                        .Where(value => !string.IsNullOrEmpty(value)))
                };
        }
        return result;
    }

    /// <summary>仅执行 VMMS 销毁；供创建事务回滚使用，不生成用户恢复包。</summary>
    internal static async Task<VmDeletionResult> DestroyVmAsync(string vmName, Guid vmId)
    {
        try
        {
            var off = await EnsureOffAsync(vmName, vmId);
            if (!off.Success) return new(false, VmRegistrationState.Unknown, null, [], off.Message);
            return await DestroyAsync(vmName, vmId);
        }
        catch (Exception ex) { return new(false, VmRegistrationState.Unknown, null, [], ex.Message) { Failure = ApiFailure.From(ex) }; }
    }

    /// <summary>注销一台虚拟机，并删除已确认由它独占的配置和磁盘文件。</summary>
    /// <param name="vmName">仅用于错误说明的显示名称；目标定位始终使用 vmId。</param>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <returns>注销状态、未清理路径和错误分别返回。执行时重新核对最新归属，不绑定此前预览；注销结果未知时不继续删除文件。磁盘依赖无法核对时仍注销并清理专属配置，保留无法排除依赖的磁盘，返回 Success=false 及 CleanupError。</returns>
    /// <remarks>依赖保护覆盖当前宿主已登记虚拟机及检查点的磁盘父链，不扫描未登记的离线磁盘。父链只用于保护，不扩大删除范围。文件身份与引用在删除前复查，外部工具不遵循 Core 锁时仍不存在原子比较删除保证。</remarks>
    /// <instanceentry label="删除虚拟机" category="删除" mode="彻底删除" order="20">
    /// <step>如需展示待删除范围，先调用 <c>PreviewPurgeAsync</c>；执行时会重新读取文件归属。</step>
    /// <step>确认使用彻底删除模式后调用 <c>PurgeVmAsync</c>，传入目标 GUID 和显示名称。处理多台虚拟机时，由调用方逐次提交并决定遇到失败是否继续。</step>
    /// <step>读取删除结果及保留路径。共享磁盘、依赖无法确认的磁盘和清理失败的文件会保留并报告。</step>
    /// </instanceentry>
    public static async Task<VmDeletionResult> PurgeVmAsync(string vmName, Guid vmId)
    {
        VmRegistrationState state = VmRegistrationState.Unknown;
        var pendingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (vmId == Guid.Empty)
                return new(false, state, null, [], string.Format(Properties.Resources.Error_Net_VmNotFound, vmName));

            // 预览不批准固定范围，执行重新读取；依赖未知只阻止磁盘删除，不阻止注销。
            PurgeTargets targets = await CollectPurgeTargetsAsync(vmId.ToString("D"), forPurge: true);
            var off = await EnsureOffAsync(vmName, vmId);
            if (!off.Success) return new(false, state, null, [], off.Message);
            var destroy = await DestroyAsync(vmName, vmId);
            state = destroy.RegistrationState;
            if (!destroy.Success) return destroy;

            var plan = targets.DiskPlan!;
            var safeConfig = targets.SafeConfig!;
            pendingPaths.UnionWith(targets.DiskFiles.Concat(plan.Shared).Concat(plan.Uncertain)
                .Concat(targets.ConfigFiles).Concat(safeConfig.Retained.Keys));
            var retained = new HashSet<string>(plan.Shared.Concat(plan.Uncertain), StringComparer.OrdinalIgnoreCase);
            var cleanupErrors = new List<string>(plan.Issues);
            foreach (var candidate in plan.Deletable)
            {
                try
                {
                    var result = await HostResourceLock.RunAsync(candidate.Identity, async () =>
                    {
                        // 注销后所有仍在册配置都是外部使用者；重新检查可发现等待期间的新引用。
                        var foreign = await ReadRegisteredDiskPathsAsync();
                        if (!foreign.HasData)
                            return ApiResponse.Fail(foreign.Error, kind: ApiErrorKind.CleanupRequired);
                        var latest = await VmPurgeDiskProtection.EvaluateAsync(new[] { candidate.Path }, foreign.Data,
                            HostVirtualDiskMetadata.ReadParentPathAsync, VmPurgeDiskProtection.IdentifyFile);
                        if (latest.Uncertain.Count != 0 || latest.Issues.Count != 0)
                            return ApiResponse.Fail(string.Join(Environment.NewLine, latest.Issues.DefaultIfEmpty("磁盘依赖无法确认，已保留。")),
                                kind: ApiErrorKind.CleanupRequired);
                        if (latest.Shared.Count != 0)
                        {
                            retained.Add(candidate.Path);
                            return ApiResponse.Ok();
                        }
                        if (latest.Deletable.Count == 0) return ApiResponse.Ok(); // 文件已不存在。
                        if (latest.Deletable[0].Identity != candidate.Identity)
                            return ApiResponse.Fail("待删除磁盘文件已被替换，已保留。", kind: ApiErrorKind.Conflict);
                        return await VmPurgeFileSafety.DeleteFileAsync(candidate);
                    });
                    if (!result.Success)
                    {
                        retained.Add(candidate.Path);
                        cleanupErrors.Add(candidate.Path + "：" + result.Error);
                    }
                }
                catch (Exception error)
                {
                    retained.Add(candidate.Path);
                    cleanupErrors.Add(candidate.Path + "：" + error.Message);
                }
            }

            // 磁盘不得经配置文件或递归目录清理绕过依赖保护。
            var diskNames = new HashSet<string>(targets.DiskFiles.Concat(plan.Shared).Concat(plan.Uncertain),
                StringComparer.OrdinalIgnoreCase);
            var diskIdentities = plan.Deletable.Select(file => file.Identity).ToHashSet(StringComparer.Ordinal);
            foreach (string disk in plan.Shared.Concat(plan.Uncertain))
            {
                var identity = VmPurgeDiskProtection.IdentifyFile(disk);
                if (identity.HasData) diskIdentities.Add(identity.Data);
            }
            foreach (var file in safeConfig.Files)
            {
                if (diskNames.Contains(file.Path) || diskIdentities.Contains(file.Identity))
                { if (!IsPathMissing(file.Path)) retained.Add(file.Path); continue; }
                var result = await VmPurgeFileSafety.DeleteFileAsync(file);
                if (!result.Success)
                {
                    retained.Add(file.Path);
                    cleanupErrors.Add(file.Path + "：" + result.Error);
                }
            }
            foreach (var item in safeConfig.Retained)
            {
                if (IsPathMissing(item.Key)) continue;
                retained.Add(item.Key);
                // 已由磁盘规则说明的保留不再次当成配置格式错误。
                if (!diskNames.Contains(item.Key)) cleanupErrors.Add(item.Key + "：" + item.Value);
            }
            void RecordDirectoryResult(string directory, ApiResponse<IReadOnlyList<string>> result)
            {
                if (!result.Success) { retained.Add(directory); cleanupErrors.Add(directory + "：" + result.Error); return; }
                foreach (string path in result.Data ?? Array.Empty<string>())
                    if (retained.Add(path)) cleanupErrors.Add(path + "：清理期间发现未批准删除的内容，已保留。");
            }
            foreach (string directory in safeConfig.Directories.OrderByDescending(path => path.Length))
            {
                var result = VmPurgeFileSafety.DeleteEmptyDirectory(directory);
                RecordDirectoryResult(directory, result);
            }
            // 根目录也只能删空目录，不能依据较早的目录快照递归清理。
            if (targets.DeletesConfigDirectory && targets.ConfigDirectory != null)
            {
                var result = VmPurgeFileSafety.DeleteEmptyDirectory(targets.ConfigDirectory);
                RecordDirectoryResult(targets.ConfigDirectory, result);
            }
            retained.RemoveWhere(IsPathMissing);
            string[] remaining = retained.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            return cleanupErrors.Count == 0
                ? new(true, state, null, remaining)
                : new(false, state, null, remaining, "虚拟机已注销，但部分文件因依赖不明或清理失败而保留。",
                    string.Join(Environment.NewLine, cleanupErrors.Distinct()));
        }
        catch (Exception ex)
        {
            return new(false, state, null, pendingPaths.Where(path => !IsPathMissing(path)).ToArray(), ex.Message) { Failure = ApiFailure.From(ex) };
        }
    }

    private static async Task<ApiResponse<List<string>>> ReadRegisteredDiskPathsAsync()
    {
        var response = await WmiApi.QueryAsync(
            "SELECT HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceSubType = 'Microsoft:Hyper-V:Virtual Hard Disk'",
            obj => obj["HostResource"] as string[] ?? (obj["HostResource"] is string path ? new[] { path } : Array.Empty<string>()),
            WmiScope.HyperV);
        return response.Success
            ? ApiResponse<List<string>>.Ok((response.Data ?? []).SelectMany(paths => paths)
                .Where(IsPotentialFileBackedDiskPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            : ApiResponse<List<string>>.Fail(response.Error, response.Code, response.ErrorSource,
                response.Exception, response.ErrorKind);
    }

    // 预览与执行共用归属规则；执行重新收集，允许两次查询间的合法配置变化。
    private static async Task<PurgeTargets> CollectPurgeTargetsAsync(string vmGuid, bool forPurge = false)
    {
        // 仅收集 ResourceSubType 标识为虚拟硬盘的文件。
        // 关键:ISO 与 VHD 在 Msvm_StorageAllocationSettingData 里 ResourceType 都是 31,只有 ResourceSubType 不同——
        //   硬盘 = "Microsoft:Hyper-V:Virtual Hard Disk"、ISO = "Microsoft:Hyper-V:Virtual CD/DVD Disk"。
        //   按 ResourceType 根本区分不了,彻底删除会连用户挂的 ISO 一起删。ResourceSubType 是固定英文标识、不本地化,可等值过滤。
        // 路径在 HostResource 数组里（不是 "Path" 属性），且 VM GUID 出现在 InstanceID 或 Parent 中部（非固定前缀）。
        var diskResp = await WmiApi.QueryAsync(
            "SELECT InstanceID, Parent, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceSubType = 'Microsoft:Hyper-V:Virtual Hard Disk'",
            obj => (
                Id: obj["InstanceID"]?.ToString() ?? string.Empty,
                Parent: obj["Parent"]?.ToString() ?? string.Empty,
                Host: obj["HostResource"] as string[] ?? (obj["HostResource"] is string s ? new[] { s } : Array.Empty<string>())
            ),
            WmiScope.HyperV);
        if (!diskResp.Success)
            throw new InvalidOperationException(diskResp.Error);

        List<(string Id, string Parent, string[] Host)> diskAllocations = diskResp.Data ?? [];

        var configResp = await WmiApi.QueryAsync(
            $"SELECT VirtualSystemType, ConfigurationID, ConfigurationDataRoot, SnapshotDataRoot, SwapFileDataRoot " +
            $"FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmGuid}'",
            obj => (
                Type: obj["VirtualSystemType"]?.ToString() ?? string.Empty,
                Id: obj["ConfigurationID"]?.ToString() ?? string.Empty,
                ConfigRoot: obj["ConfigurationDataRoot"]?.ToString() ?? string.Empty,
                SnapshotRoot: obj["SnapshotDataRoot"]?.ToString() ?? string.Empty,
                SwapRoot: obj["SwapFileDataRoot"]?.ToString() ?? string.Empty),
            WmiScope.HyperV);
        if (!configResp.Success)
            throw new InvalidOperationException(configResp.Error);

        List<(string Type, string Id, string ConfigRoot, string SnapshotRoot, string SwapRoot)> settings =
            configResp.Data ?? [];
        var realized = settings.FirstOrDefault(item => string.Equals(
            item.Type,
            "Microsoft:Hyper-V:System:Realized",
            StringComparison.OrdinalIgnoreCase));
        string? configDirectory = string.IsNullOrWhiteSpace(realized.ConfigRoot)
            ? null
            : NormalizePath(realized.ConfigRoot);

        HashSet<string> roots = settings
            .SelectMany(item => new[] { item.ConfigRoot, item.SnapshotRoot, item.SwapRoot })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (configDirectory != null) roots.Add(configDirectory);

        HashSet<string> configurationIds = settings
            .Select(item => item.Id)
            .Append(vmGuid)
            .Where(value => Guid.TryParse(value, out _))
            .Select(value => Guid.Parse(value).ToString("D"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 检查点的磁盘分配以检查点 ConfigurationID 开头，而不是根 VM GUID。
        // 用上面从 VSSD 得到的完整 ID 集合，才能同时覆盖当前链和所有检查点。
        bool BelongsToVm((string Id, string Parent, string[] Host) allocation)
            => configurationIds.Any(id =>
                allocation.Id.Contains(id, StringComparison.OrdinalIgnoreCase)
                || allocation.Parent.Contains(id, StringComparison.OrdinalIgnoreCase));

        var foreignDiskPaths = diskAllocations.Where(allocation => !BelongsToVm(allocation))
            .SelectMany(allocation => allocation.Host).Where(IsPotentialFileBackedDiskPath).ToArray();
        var candidates = diskAllocations.Where(BelongsToVm)
            .SelectMany(allocation => allocation.Host).Where(IsPotentialFileBackedDiskPath)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        VmPurgeDiskPlan? diskPlan = forPurge
            ? await VmPurgeDiskProtection.EvaluateAsync(candidates, foreignDiskPaths,
                HostVirtualDiskMetadata.ReadParentPathAsync, VmPurgeDiskProtection.IdentifyFile)
            : null;
        var diskPaths = forPurge ? diskPlan!.Deletable.Select(file => file.Path).ToList()
            : candidates.Where(IsFileBackedDiskPath).Select(NormalizePath)
                .Except(foreignDiskPaths.Where(IsFileBackedDiskPath).Select(NormalizePath), StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();

        var configFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        VmPurgeConfigFiles? safeConfig = null;
        if (forPurge)
        {
            safeConfig = VmPurgeFileSafety.Collect(roots, configurationIds, VmPurgeDiskProtection.IdentifyFile);
            configFiles.UnionWith(safeConfig.Files.Select(file => file.Path));
            configDirectories.UnionWith(safeConfig.Directories);
        }
        else CollectOwnedConfigArtifacts(roots, configurationIds, configFiles, configDirectories);

        bool deletesConfigDirectory = await CanDeleteConfigDirectoryAsync(
            configDirectory, vmGuid, configFiles.Concat(diskPaths), safeEnumeration: forPurge);
        return new PurgeTargets(configDirectory, deletesConfigDirectory,
            configFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
            configDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
            diskPaths, diskPlan, safeConfig);
    }

    // 取 VM 路径并 DestroySystem（含回查确认真注销）。
    private static async Task<VmDeletionResult> DestroyAsync(string vmName, Guid vmId)
    {
        var vm = await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'",
            obj => obj.Path.Path, WmiScope.HyperV);
        if (!vm.Success) return new(false, VmRegistrationState.Unknown, null, [], vm.Error) { Failure = ApiFailure.From(vm) };
        if (!vm.HasData || string.IsNullOrEmpty(vm.Data))
            return new(false, VmRegistrationState.Unregistered, null, [], $"VM '{vmName}' not found");

        var request = await WmiApi.InvokeAsync("SELECT * FROM Msvm_VirtualSystemManagementService",
            "DestroySystem", args => args["AffectedSystem"] = vm.Data, WmiScope.HyperV);
        var remaining = await WmiApi.QueryFirstAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'",
            obj => obj["Name"]?.ToString(), WmiScope.HyperV);
        return ResolveDestroyResult(request, remaining, vmName);
    }

    internal static VmDeletionResult ResolveDestroyResult(ApiResponse request, ApiResponse<string?> remaining, string vmName)
    {
        // 注册记录暂时消失不证明 DestroySystem 作业已结束，不能据此开始文件删除或回迁。
        if (request.ErrorKind == ApiErrorKind.OutcomeUnknown)
            return new(false, VmRegistrationState.Unknown, null, [], request.Error,
                "注销操作的最终结果尚未确认，未继续处理相关文件。" + (remaining.Success ? "" : remaining.Error)) { Failure = ApiFailure.From(request), CleanupFailure = ApiFailure.From(remaining) };
        // 查询失败或原生操作未确认成功时，仍可能有后台作业；不能把当下在册推断为最终失败。
        if (!remaining.Success)
            return new(false, VmRegistrationState.Unknown, null, [], request.Success ? remaining.Error : request.Error,
                request.Success ? null : remaining.Error) { Failure = request.Success ? ApiFailure.From(remaining) : ApiFailure.From(request), CleanupFailure = request.Success ? null : ApiFailure.From(remaining) };
        if (!remaining.HasData)
            return new(request.Success, VmRegistrationState.Unregistered, null, [], request.Success ? null : request.Error) { Failure = ApiFailure.From(request) };
        return new(false, request.Success ? VmRegistrationState.Registered : VmRegistrationState.Unknown,
            null, [], request.Success ? string.Format(Properties.Resources.VmDelete_DestroyVerifyFail, vmName) : request.Error) { Failure = request.Success ? new(ApiErrorKind.Conflict, -1, ApiErrorSource.None, "注销后仍可查询到虚拟机。") : ApiFailure.From(request) };
    }

    private static async Task RestoreConfigurationPackageAsync(
        string packageDirectory,
        string configurationDirectory,
        Guid vmId,
        IEnumerable<string> previousConfigFiles,
        IEnumerable<string> previousConfigDirectories)
    {
        string sourceRoot = NormalizePath(packageDirectory);
        string targetRoot = NormalizePath(configurationDirectory);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException(sourceRoot);

        // DestroySystem 可能删除、保留或改写旧状态文件。先只清理销毁前已证明属于该 VM
        // 的精确文件和 ID 目录，再复制标准导出包；不清理共享配置根。
        foreach (string file in previousConfigFiles)
        {
            if (!await TryDeleteFileAsync(file))
                throw new IOException($"Cannot replace configuration file: {file}");
        }
        foreach (string directory in previousConfigDirectories.OrderByDescending(path => path.Length))
        {
            if (!await TryDeleteDirAsync(directory))
                throw new IOException($"Cannot replace configuration directory: {directory}");
        }

        Directory.CreateDirectory(targetRoot);
        foreach (string sourceFile in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceRoot, sourceFile);
            string targetFile = Path.GetFullPath(Path.Combine(targetRoot, relativePath));
            if (!IsPathWithinOrEqual(targetFile, targetRoot))
                throw new InvalidDataException($"Configuration package path is outside the target directory: {relativePath}");

            string? targetDirectory = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
                Directory.CreateDirectory(targetDirectory);
            await CopyFileWithRetryAsync(sourceFile, targetFile);
        }

        string expectedName = vmId.ToString("D") + ".vmcx";
        bool hasMainConfiguration = Directory
            .EnumerateFiles(targetRoot, "*.vmcx", SearchOption.AllDirectories)
            .Any(path => string.Equals(
                Path.GetFileName(path),
                expectedName,
                StringComparison.OrdinalIgnoreCase));
        if (!hasMainConfiguration)
            throw new InvalidDataException($"The restored package does not contain {expectedName}.");
    }

    private static async Task CopyFileWithRetryAsync(string sourcePath, string targetPath)
    {
        Exception? lastError = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                File.Copy(sourcePath, targetPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                await Task.Delay(250);
            }
        }

        throw new IOException($"Cannot restore configuration file: {targetPath}", lastError);
    }

    // 销毁前确保 VM 已关机：保存态/运行态不先关，DestroySystem 可能残留配置/状态文件。
    // 放行 EnabledState 3(已关) 和 6(Enabled but Offline，配置丢失的坏机)：6 的 TurnOff 关不掉但 DestroySystem 能直接注销，其余非关机态先 TurnOff 再回查。
    // Caption 会被本地化，不能用于等值过滤虚拟机对象。
    private static bool IsOffOrOrphan(int enabledState) => enabledState == 3 || enabledState == 6;

    private static async Task<(bool Success, string Message)> EnsureOffAsync(string vmName, Guid vmId)
    {
        if (vmId == Guid.Empty)
            return (false, string.Format(Properties.Resources.Error_Net_VmNotFound, vmName));

        var state = await WmiApi.QueryFirstAsync(
            $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'",
            obj => Convert.ToInt32(obj["EnabledState"] ?? (ushort)0), WmiScope.HyperV);
        if (!state.Success) return (false, state.Error);
        if (!state.HasData) return (true, string.Empty);   // 查不到 = 已不存在
        if (IsOffOrOrphan(state.Data)) return (true, string.Empty);

        var off = await VmPowerService.ExecuteControlActionAsync(vmId, "TurnOff");   // RequestStateChange(3)：保存态会丢弃保存状态
        var after = await WmiApi.QueryFirstAsync(
            $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'",
            obj => Convert.ToInt32(obj["EnabledState"] ?? (ushort)0), WmiScope.HyperV);
        if (!after.Success) return (false, after.Error);
        if (after.HasData && !IsOffOrOrphan(after.Data))
            return (false, off.Success ? string.Format(Properties.Resources.VmDelete_TurnOffFail, vmName) : off.Error);
        return (true, string.Empty);
    }

    private static bool IsPotentialFileBackedDiskPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && path.IndexOf("Msvm_DiskDrive", StringComparison.OrdinalIgnoreCase) < 0;

    private static bool IsFileBackedDiskPath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && path.IndexOf("Msvm_DiskDrive", StringComparison.OrdinalIgnoreCase) < 0
           && Path.IsPathFullyQualified(path);

    private static string NormalizePath(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // 配置和状态文件没有一条可直接枚举“本 VM 所有文件”的 WMI 关系。
    // 但当前 VM 与各检查点的 ConfigurationID 都是平台分配的 GUID；只在 WMI 返回的
    // 数据根下匹配这些精确 ID，既能覆盖 vmcx/vmgs/vmrs，也不会认领共享根里的其它 VM。
    private static void CollectOwnedConfigArtifacts(
        IEnumerable<string> roots,
        IEnumerable<string> configurationIds,
        HashSet<string> files,
        HashSet<string> directories)
    {
        string[] ids = configurationIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string containerName in new[] { "Virtual Machines", "Snapshots" })
            {
                string container = Path.Combine(root, containerName);
                if (!Directory.Exists(container)) continue;
                foreach (string id in ids)
                {
                    try
                    {
                        foreach (string file in Directory.EnumerateFiles(
                                     container,
                                     id + ".*",
                                     SearchOption.TopDirectoryOnly))
                            files.Add(NormalizePath(file));

                        string idDirectory = Path.Combine(container, id);
                        if (!Directory.Exists(idDirectory)) continue;
                        directories.Add(NormalizePath(idDirectory));
                        foreach (string file in Directory.EnumerateFiles(
                                     idDirectory,
                                     "*",
                                     SearchOption.AllDirectories))
                            files.Add(NormalizePath(file));
                    }
                    catch
                    {
                        // 单个根无法枚举时不扩大范围；目标计算保持 fail-closed。
                    }
                }
            }
        }
    }

    private static async Task<bool> CanDeleteConfigDirectoryAsync(
        string? rawConfigDirectory,
        string vmGuid,
        IEnumerable<string> ownedFiles, bool safeEnumeration = false)
    {
        if (string.IsNullOrWhiteSpace(rawConfigDirectory)) return false;
        string configDirectory;
        try { configDirectory = NormalizePath(rawConfigDirectory); }
        catch { return false; }
        if (!Directory.Exists(configDirectory)) return false;

        string? volumeRoot = Path.GetPathRoot(configDirectory)?.TrimEnd('\\', '/');
        if (string.Equals(volumeRoot, configDirectory, StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (string hostRoot in await GetHostDefaultRootsAsync())
            if (string.Equals(NormalizePath(hostRoot), configDirectory, StringComparison.OrdinalIgnoreCase))
                return false;

        // 其它 VM 的任一数据根等于或位于本目录内，说明本目录包含别的 VM，不能删除。
        // 本目录位于另一个共享父根内并不构成冲突；后面的逐文件归属检查仍会兜底。
        var otherRootsResponse = await WmiApi.QueryAsync(
            "SELECT VirtualSystemIdentifier, ConfigurationDataRoot, SnapshotDataRoot, SwapFileDataRoot " +
            "FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
            obj => (
                VmId: obj["VirtualSystemIdentifier"]?.ToString() ?? string.Empty,
                Roots: new[]
                {
                    obj["ConfigurationDataRoot"]?.ToString() ?? string.Empty,
                    obj["SnapshotDataRoot"]?.ToString() ?? string.Empty,
                    obj["SwapFileDataRoot"]?.ToString() ?? string.Empty
                }),
            WmiScope.HyperV);
        if (!otherRootsResponse.Success) return false;

        foreach (var other in otherRootsResponse.Data ?? [])
        {
            if (string.Equals(other.VmId, vmGuid, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string otherRootValue in other.Roots.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                string otherRoot;
                try { otherRoot = NormalizePath(otherRootValue); }
                catch { return false; }
                if (IsPathWithinOrEqual(otherRoot, configDirectory)) return false;
            }
        }

        var owned = ownedFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (safeEnumeration) return VmPurgeFileSafety.ContainsOnlyFiles(configDirectory, owned);
        try
        {
            // 目录里只要出现一个无法归属到当前 VM 的文件，就只删精确文件，不删目录。
            return Directory.EnumerateFiles(configDirectory, "*", SearchOption.AllDirectories)
                .Select(NormalizePath)
                .All(owned.Contains);
        }
        catch { return false; }
    }

    private static bool IsPathWithinOrEqual(string path, string root)
    {
        string normalizedPath = NormalizePath(path);
        string normalizedRoot = NormalizePath(root);
        if (string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        return normalizedPath.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    // 删配置目录：零硬编码、可证明安全。
    // 仅删除递归检查后不含文件的目录。
    //       并动态护住盘符根与主机默认根目录（DefaultExternalDataRoot / DefaultVirtualHardDiskPath，连空也不删）。
    private static async Task<bool> DeleteConfigDirAsync(string? rawConfigDir, bool isProvenDedicated)
    {
        if (!isProvenDedicated || string.IsNullOrEmpty(rawConfigDir)) return true;
        string configDir = rawConfigDir.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(configDir)) return true;

        // 盘符根（如 "C:"）不删
        if (Path.GetPathRoot(configDir)?.TrimEnd('\\', '/')
                .Equals(configDir, StringComparison.OrdinalIgnoreCase) ?? false)
            return true;

        // 主机默认根目录不删（动态查，无硬编码）
        foreach (var root in await GetHostDefaultRootsAsync())
            if (string.Equals(root, configDir, StringComparison.OrdinalIgnoreCase))
                return true;

        // 仅当目录下递归无任何文件时才删（共享目录还装着别的 VM → 非空 → 保留）
        try
        {
            if (!Directory.EnumerateFiles(configDir, "*", SearchOption.AllDirectories).Any())
                return await TryDeleteDirAsync(configDir);
            return true; // 新出现的未知文件必须保留；已知文件清理失败由调用方单独报告。
        }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; }
    }

    // 主机默认 VM / VHD 根目录（动态，替代写死的 C:\... denylist）。
    private static async Task<List<string>> GetHostDefaultRootsAsync()
    {
        try
        {
            var resp = await WmiApi.QueryFirstAsync(
                "SELECT * FROM Msvm_VirtualSystemManagementServiceSettingData",
                obj => new List<string?>
                {
                    obj.TryGetString("DefaultExternalDataRoot"),
                    obj.TryGetString("DefaultVirtualHardDiskPath")
                },
                WmiScope.HyperV);
            return (resp.Data ?? new List<string?>())
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!.TrimEnd('\\', '/'))
                .ToList();
        }
        catch { return new List<string>(); }
    }

    // 删文件/目录带重试：DestroySystem 注销后 vmwp/VMMS 可能短暂仍占着 .VMRS/.vmcx/VHD → 重试几次。
    private static async Task<bool> TryDeleteFileAsync(string path)
    {
        for (int i = 0; i < 6; i++)
        {
            try { File.Delete(path); return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch { await Task.Delay(250); }
        }
        return false;
    }

    private static async Task<bool> TryDeleteDirAsync(string path)
    {
        for (int i = 0; i < 6; i++)
        {
            try { Directory.Delete(path, recursive: true); return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch { await Task.Delay(250); }
        }
        return false;
    }

    private static bool IsPathMissing(string path)
    {
        try { File.GetAttributes(path); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; } // 无法访问不能视为已经删除。
    }
}
