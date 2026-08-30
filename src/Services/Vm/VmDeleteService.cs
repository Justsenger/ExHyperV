using System.IO;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>
/// 虚拟机删除 / 彻底删除。从 VirtualMachinesPageViewModel 抽出的 inline WMI（DestroySystem + 文件清理），
/// 使 VM 层只调服务、删除逻辑可复用可测。删除前先关机（DestroySystem 要求 VM 已关）。
/// </summary>
public static class VmDeleteService
{
    /// <summary>彻底删除预览。这里只返回可证明属于该 VM 的文件；共享根目录永远不会被当成 VM 私有目录。</summary>
    public sealed record PurgePreview(
        string? ConfigDirectory,
        bool DeletesConfigDirectory,
        List<string> ConfigFiles,
        List<string> DiskFiles);

    private sealed record PurgeTargets(
        string? ConfigDirectory,
        bool DeletesConfigDirectory,
        List<string> ConfigFiles,
        List<string> ConfigDirectories,
        List<string> DiskFiles);

    /// <summary>预先算出彻底删除会动到的目录与文件（只读，用于确认弹窗清单）。</summary>
    public static async Task<PurgePreview> PreviewPurgeAsync(Guid vmId)
    {
        try
        {
            PurgeTargets targets = await CollectPurgeTargetsAsync(vmId.ToString());
            return new PurgePreview(
                targets.ConfigDirectory,
                targets.DeletesConfigDirectory,
                targets.ConfigFiles,
                targets.DiskFiles);
        }
        catch { return new PurgePreview(null, false, new List<string>(), new List<string>()); }
    }

    /// <summary>
    /// 可恢复删除：先导出一份不复制虚拟硬盘的完整配置定义，再从 VMMS 注销虚拟机，
    /// 最后把可重新导入的配置放回原配置目录。虚拟硬盘和检查点磁盘始终留在原处。
    /// </summary>
    public static async Task<(bool Success, string Message)> DeleteVmAsync(string vmName, Guid vmId)
    {
        string recoveryRoot = Path.Combine(
            Path.GetTempPath(),
            "ExHyperV",
            "DeleteRecovery",
            Guid.NewGuid().ToString("N"));
        string? recoveryPackage = null;
        bool destroyed = false;
        bool restored = false;
        try
        {
            if (vmId == Guid.Empty)
                return (false, string.Format(Properties.Resources.Error_Net_VmNotFound, vmName));

            // DestroySystem 的文件保留行为不是可重新导入契约。必须在 VM 仍注册时让 VMMS
            // 生成标准导出定义；CopyVmStorage=false 只保存配置、检查点和运行状态文件，
            // 不复制 VHD。导出放在临时目录，避免与仍在使用的原配置文件发生冲突。
            PurgeTargets targets = await CollectPurgeTargetsAsync(vmId.ToString("D"));
            if (string.IsNullOrWhiteSpace(targets.ConfigDirectory))
                return (false, string.Format(
                    Properties.Resources.VmDelete_ConfigRootMissing,
                    vmName));

            Directory.CreateDirectory(recoveryRoot);
            var export = await VmExportService.ExportAsync(
                vmId,
                vmName,
                recoveryRoot,
                includeVirtualHardDisks: false,
                excludedVirtualHardDiskIds: Array.Empty<string>(),
                checkpointMode: VmExportCheckpointMode.All,
                selectedCheckpointPath: null,
                includeRuntimeState: true);
            if (!export.Success || string.IsNullOrWhiteSpace(export.Data))
                return (false, export.Error);
            recoveryPackage = export.Data;

            // 导出完成后再关机。这样运行态/保存态也已进入恢复包，而 DestroySystem 仍在
            // Hyper-V 要求的关机状态执行。
            var off = await EnsureOffAsync(vmName);
            if (!off.Success) return off;

            var destroy = await DestroyAsync(vmName);
            if (!destroy.Success) return destroy;
            destroyed = true;

            await RestoreConfigurationPackageAsync(
                recoveryPackage,
                targets.ConfigDirectory,
                vmId,
                targets.ConfigFiles,
                targets.ConfigDirectories);
            restored = true;
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            if (destroyed && !string.IsNullOrWhiteSpace(recoveryPackage))
            {
                return (false, string.Format(
                    Properties.Resources.VmDelete_RestoreFailed,
                    recoveryPackage,
                    ex.Message));
            }
            return (false, ex.Message);
        }
        finally
        {
            // 注销后的恢复失败是唯一需要保留临时包的情况，便于用户手动找回；
            // 其它失败仍有在册 VM，临时导出可以安全清理。
            if (!destroyed || restored)
                await TryDeleteDirAsync(recoveryRoot);
        }
    }

    /// <summary>仅执行 VMMS 销毁；供创建事务回滚使用，不生成用户恢复包。</summary>
    internal static async Task<(bool Success, string Message)> DestroyVmAsync(string vmName)
    {
        try
        {
            var off = await EnsureOffAsync(vmName);
            if (!off.Success) return off;
            return await DestroyAsync(vmName);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>彻底删除：移除配置 + 删磁盘文件 + 删配置目录（带受保护路径防误删）。仅在 DestroySystem 成功后才动文件。</summary>
    public static async Task<(bool Success, string Message)> PurgeVmAsync(string vmName, Guid vmId)
    {
        try
        {
            // 删除前先收集要清理的路径（删完就查不到了）——与 Preview 同一逻辑，保证"显示=实删"。
            PurgeTargets targets = await CollectPurgeTargetsAsync(vmId.ToString());

            var off = await EnsureOffAsync(vmName);
            if (!off.Success) return off;
            var destroy = await DestroyAsync(vmName);
            if (!destroy.Success) return destroy;   // 删除失败就不动文件（VM 仍在用着盘）

            foreach (string diskPath in targets.DiskFiles)
                await TryDeleteFileAsync(diskPath);

            // DestroySystem 对保存态/TPM 机可能残留配置与状态文件。目标已在销毁前按
            // VM/检查点 ConfigurationID 精确收集；执行与确认弹窗使用同一份清单。
            foreach (string configFile in targets.ConfigFiles)
                await TryDeleteFileAsync(configFile);
            foreach (string configDirectory in targets.ConfigDirectories
                         .OrderByDescending(path => path.Length))
                await TryDeleteDirAsync(configDirectory);
            await DeleteConfigDirAsync(targets.ConfigDirectory, targets.DeletesConfigDirectory);

            return (true, string.Empty);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // 收集"将清理的目标"：VHD 文件路径 + 配置目录。Preview 与 Purge 共用，保证显示与实删一致。
    private static async Task<PurgeTargets> CollectPurgeTargetsAsync(string vmGuid)
    {
        // 只收【虚拟硬盘】的文件来删,靠 ResourceSubType 精确区分。
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

        HashSet<string> foreignDiskPaths = diskAllocations
            .Where(allocation => !BelongsToVm(allocation))
            .SelectMany(allocation => allocation.Host)
            .Where(IsFileBackedDiskPath)
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var diskPaths = diskAllocations
            .Where(BelongsToVm)
            .SelectMany(allocation => allocation.Host)
            .Where(IsFileBackedDiskPath)
            .Select(NormalizePath)
            // 同一个 VHD 被其他虚拟机引用时不归任何一台 VM 独占，彻底删除也必须保留。
            .Where(path => !foreignDiskPaths.Contains(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var configFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectOwnedConfigArtifacts(roots, configurationIds, configFiles, configDirectories);

        bool deletesConfigDirectory = await CanDeleteConfigDirectoryAsync(
            configDirectory,
            vmGuid,
            configFiles.Concat(diskPaths));

        return new PurgeTargets(
            configDirectory,
            deletesConfigDirectory,
            configFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
            configDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
            diskPaths);
    }

    // 取 VM 路径并 DestroySystem（含回查确认真注销）。
    private static async Task<(bool Success, string Message)> DestroyAsync(string vmName)
    {
        var vmPath = (await WmiApi.QueryFirstAsync(
            $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
            obj => obj.Path.Path, WmiScope.HyperV)).Data;
        if (string.IsNullOrEmpty(vmPath))
            return (false, $"VM '{vmName}' not found");

        var r = await WmiApi.InvokeAsync(
            "SELECT * FROM Msvm_VirtualSystemManagementService",
            "DestroySystem",
            p => p["AffectedSystem"] = vmPath,
            WmiScope.HyperV);
        if (!r.Success) return (false, r.Error);

        // 回查确认真的注销了——引擎对保存态/TPM 机可能报成功却没销毁干净。不回查就"假成功"：
        // 上层乐观地从列表移除，但 VM 还在册、文件还在 → 再建同名即撞 0x80070050。
        var still = await WmiApi.QueryFirstAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
            obj => obj["Name"]?.ToString(), WmiScope.HyperV);
        return still.HasData
            ? (false, string.Format(Properties.Resources.VmDelete_DestroyVerifyFail, vmName))
            : (true, string.Empty);
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
        // 的精确文件/ID 目录，再复制标准导出包；绝不清理整个共享配置根。
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
    // 绝不带 `Caption = 'Virtual Machine'` 过滤——中文系统上被翻译，等值匹配永远查不到。
    private static bool IsOffOrOrphan(int enabledState) => enabledState == 3 || enabledState == 6;

    private static async Task<(bool Success, string Message)> EnsureOffAsync(string vmName)
    {
        var state = await WmiApi.QueryFirstAsync(
            $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
            obj => Convert.ToInt32(obj["EnabledState"] ?? (ushort)0), WmiScope.HyperV);
        if (!state.HasData) return (true, string.Empty);   // 查不到 = 已不存在
        if (IsOffOrOrphan(state.Data)) return (true, string.Empty);

        var off = await VmPowerService.ExecuteControlActionAsync(vmName, "TurnOff");   // RequestStateChange(3)：保存态会丢弃保存状态
        var after = await WmiApi.QueryFirstAsync(
            $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
            obj => Convert.ToInt32(obj["EnabledState"] ?? (ushort)0), WmiScope.HyperV);
        if (after.HasData && !IsOffOrOrphan(after.Data))
            return (false, off.Success ? string.Format(Properties.Resources.VmDelete_TurnOffFail, vmName) : off.Error);
        return (true, string.Empty);
    }

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
        IEnumerable<string> ownedFiles)
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
    // 规则：只删"递归下已无任何文件"的目录（有文件就保留，绝不误删别的 VM）；
    //       并动态护住盘符根与主机默认根目录（DefaultExternalDataRoot / DefaultVirtualHardDiskPath，连空也不删）。
    private static async Task DeleteConfigDirAsync(string? rawConfigDir, bool isProvenDedicated)
    {
        if (!isProvenDedicated || string.IsNullOrEmpty(rawConfigDir)) return;
        string configDir = rawConfigDir.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(configDir) || !Directory.Exists(configDir)) return;

        // 盘符根（如 "C:"）不删
        if (Path.GetPathRoot(configDir)?.TrimEnd('\\', '/')
                .Equals(configDir, StringComparison.OrdinalIgnoreCase) ?? false)
            return;

        // 主机默认根目录不删（动态查，无硬编码）
        foreach (var root in await GetHostDefaultRootsAsync())
            if (string.Equals(root, configDir, StringComparison.OrdinalIgnoreCase))
                return;

        // 仅当目录下递归无任何文件时才删（共享目录还装着别的 VM → 非空 → 保留）
        try
        {
            if (!Directory.EnumerateFiles(configDir, "*", SearchOption.AllDirectories).Any())
                await TryDeleteDirAsync(configDir);
        }
        catch { }
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
            try { if (!File.Exists(path)) return true; File.Delete(path); return true; }
            catch { await Task.Delay(250); }
        }
        return false;
    }

    private static async Task<bool> TryDeleteDirAsync(string path)
    {
        for (int i = 0; i < 6; i++)
        {
            try { if (!Directory.Exists(path)) return true; Directory.Delete(path, recursive: true); return true; }
            catch { await Task.Delay(250); }
        }
        return false;
    }
}
