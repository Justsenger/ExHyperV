using System.IO;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>
/// 虚拟机删除 / 彻底删除。从 VirtualMachinesPageViewModel 抽出的 inline WMI（DestroySystem + 文件清理），
/// 使 VM 层只调服务、删除逻辑可复用可测。删除前先关机（DestroySystem 要求 VM 已关）。
/// </summary>
public static class VmDeleteService
{
    /// <summary>彻底删除预览：将删除的「配置目录 + 目录内现有文件 + 目录外的虚拟硬盘」。供确认弹窗展示，不删任何东西。</summary>
    public sealed record PurgePreview(string? ConfigDir, List<string> ConfigDirFiles, List<string> ExternalDiskFiles);

    /// <summary>预先算出彻底删除会动到的目录与文件（只读，用于确认弹窗清单）。</summary>
    public static async Task<PurgePreview> PreviewPurgeAsync(Guid vmId)
    {
        try
        {
            var (diskPaths, configDir) = await CollectPurgeTargetsAsync(vmId.ToString());

            var dirFiles = new List<string>();
            if (!string.IsNullOrEmpty(configDir) && Directory.Exists(configDir))
            {
                try { dirFiles = Directory.EnumerateFiles(configDir!, "*", SearchOption.AllDirectories).ToList(); }
                catch { }
            }
            // 目录外的 VHD 单列（目录内的会随"删目录"一并清掉，已在 dirFiles 里体现）
            string? norm = configDir?.TrimEnd('\\', '/').ToUpperInvariant();
            var external = diskPaths
                .Where(d => norm == null || !d.ToUpperInvariant().StartsWith(norm))
                .ToList();
            return new PurgePreview(configDir, dirFiles, external);
        }
        catch { return new PurgePreview(null, new List<string>(), new List<string>()); }
    }

    /// <summary>删除虚拟机（仅移除配置，保留磁盘文件）。等价 Remove-VM。</summary>
    public static async Task<(bool Success, string Message)> DeleteVmAsync(string vmName)
    {
        try
        {
            var off = await EnsureOffAsync(vmName);   // 保存态/运行态不先关，DestroySystem 可能只注销却残留文件
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
            var (diskPaths, rawConfigDir) = await CollectPurgeTargetsAsync(vmId.ToString());

            var off = await EnsureOffAsync(vmName);
            if (!off.Success) return off;
            var destroy = await DestroyAsync(vmName);
            if (!destroy.Success) return destroy;   // 删除失败就不动文件（VM 仍在用着盘）

            foreach (var diskPath in diskPaths)
                await TryDeleteFileAsync(diskPath);

            // DestroySystem 对保存态/TPM 机可能残留 Virtual Machines\<guid>.vmcx/.vmgs/.VMRS → 先按 GUID 删本机文件，再收空壳目录。
            await CleanupOwnConfigFilesAsync(rawConfigDir, vmId.ToString());
            await DeleteConfigDirAsync(rawConfigDir);

            return (true, string.Empty);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // 收集"将清理的目标"：VHD 文件路径 + 配置目录。Preview 与 Purge 共用，保证显示与实删一致。
    private static async Task<(List<string> DiskPaths, string? ConfigDir)> CollectPurgeTargetsAsync(string vmGuid)
    {
        // 只收【虚拟硬盘】(ResourceType 31)的文件来删。
        // 绝不收 ResourceType 16(虚拟 DVD)——它的 HostResource 是用户挂载的 ISO 镜像,彻底删除 VM 不该动用户的 ISO。
        // 路径在 HostResource 数组里（不是 "Path" 属性），且 VM GUID 出现在 InstanceID 或 Parent 中部（非固定前缀）。
        var diskResp = await WmiApi.QueryAsync(
            "SELECT InstanceID, Parent, HostResource, ResourceType FROM Msvm_StorageAllocationSettingData WHERE ResourceType = 31",
            obj => (
                Id: obj["InstanceID"]?.ToString() ?? string.Empty,
                Parent: obj["Parent"]?.ToString() ?? string.Empty,
                Host: obj["HostResource"] as string[] ?? (obj["HostResource"] is string s ? new[] { s } : Array.Empty<string>())
            ),
            WmiScope.HyperV);
        string guidKey = vmGuid.ToUpperInvariant();
        var diskPaths = (diskResp.Data ?? new List<(string Id, string Parent, string[] Host)>())
            .Where(d => d.Id.ToUpperInvariant().Contains(guidKey) || d.Parent.ToUpperInvariant().Contains(guidKey))
            .SelectMany(d => d.Host)
            // 排除物理盘直通(HostResource 是 Msvm_DiskDrive 引用、非文件),防御性:虚拟硬盘 31 里正常不含它。
            .Where(p => !string.IsNullOrEmpty(p)
                     && p.IndexOf("Msvm_DiskDrive", StringComparison.OrdinalIgnoreCase) < 0)
            .Distinct()
            .ToList();

        var configResp = await WmiApi.QueryFirstAsync(
            $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmGuid}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
            obj => obj["ConfigurationDataRoot"]?.ToString() ?? string.Empty,
            WmiScope.HyperV);
        string? configDir = configResp.HasData ? configResp.Data : null;
        return (diskPaths, configDir);
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
            ? (false, $"引擎报成功但虚拟机 '{vmName}' 仍在册（可能是保存态/受 TPM 保护，未能彻底销毁）")
            : (true, string.Empty);
    }

    // 销毁前确保 VM 已关机：保存态(.VMRS)/运行态不先关，DestroySystem 可能只注销却残留配置/状态文件。
    // 只认 EnabledState==3（CIM 标准 Disabled=已关）；非 3 一律 TurnOff 再回确认。
    // 绝不带 `Caption = 'Virtual Machine'` 过滤——该 Caption 在中文等本地化系统上被翻译，等值匹配永远查不到（实测）。
    private static async Task<(bool Success, string Message)> EnsureOffAsync(string vmName)
    {
        var state = await WmiApi.QueryFirstAsync(
            $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
            obj => Convert.ToInt32(obj["EnabledState"] ?? (ushort)0), WmiScope.HyperV);
        if (!state.HasData) return (true, string.Empty);   // 查不到 = 已不存在，当作已关
        if (state.Data == 3) return (true, string.Empty);  // 已关机

        var off = await VmPowerService.ExecuteControlActionAsync(vmName, "TurnOff");   // RequestStateChange(3)：保存态会丢弃保存状态
        var after = await WmiApi.QueryFirstAsync(
            $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
            obj => Convert.ToInt32(obj["EnabledState"] ?? (ushort)0), WmiScope.HyperV);
        if (after.HasData && after.Data != 3)
            return (false, off.Success ? $"虚拟机 '{vmName}' 未能进入关机状态" : off.Error);
        return (true, string.Empty);
    }

    // 删本 VM 自己残留的配置/状态文件（DestroySystem 对保存态/TPM 机可能不删 Virtual Machines\<guid>.*）。
    // 严格按 GUID 匹配，只动这台 VM 的文件，绝不波及同目录的其它 VM（NTFS 大小写不敏感）。
    private static async Task CleanupOwnConfigFilesAsync(string? rawConfigDir, string vmGuid)
    {
        if (string.IsNullOrEmpty(rawConfigDir) || string.IsNullOrEmpty(vmGuid)) return;
        try
        {
            string vmSub = Path.Combine(rawConfigDir, "Virtual Machines");
            if (!Directory.Exists(vmSub)) return;
            foreach (var f in Directory.EnumerateFiles(vmSub, vmGuid + "*").ToList())   // <guid>.vmcx / .vmgs / .VMRS
                await TryDeleteFileAsync(f);
            string guidDir = Path.Combine(vmSub, vmGuid);
            if (Directory.Exists(guidDir))
                await TryDeleteDirAsync(guidDir);
        }
        catch { }
    }

    // 删配置目录：零硬编码、可证明安全。
    // 规则：只删"递归下已无任何文件"的目录（有文件就保留，绝不误删别的 VM）；
    //       并动态护住盘符根与宿主默认根目录（DefaultExternalDataRoot / DefaultVirtualHardDiskPath，连空也不删）。
    private static async Task DeleteConfigDirAsync(string? rawConfigDir)
    {
        if (string.IsNullOrEmpty(rawConfigDir)) return;
        string configDir = rawConfigDir.TrimEnd('\\', '/');
        if (string.IsNullOrEmpty(configDir) || !Directory.Exists(configDir)) return;

        // 盘符根（如 "C:"）不删
        if (Path.GetPathRoot(configDir)?.TrimEnd('\\', '/')
                .Equals(configDir, StringComparison.OrdinalIgnoreCase) ?? false)
            return;

        // 宿主默认根目录不删（动态查，无硬编码）
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

    // 宿主默认 VM / VHD 根目录（动态，替代写死的 C:\... denylist）。
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
