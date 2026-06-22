using System.IO;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>
/// 虚拟机删除 / 彻底删除。从 VirtualMachinesPageViewModel 抽出的 inline WMI（DestroySystem + 文件清理），
/// 使 VM 层只调服务、删除逻辑可复用可测。删除前先关机（DestroySystem 要求 VM 已关）。
/// </summary>
public static class VmDeleteService
{
    /// <summary>删除虚拟机（仅移除配置，保留磁盘文件）。等价 Remove-VM。</summary>
    public static async Task<(bool Success, string Message)> DeleteVmAsync(string vmName)
    {
        try
        {
            await VmPowerService.ExecuteControlActionAsync(vmName, "TurnOff");   // InvokeAsync 等 job 完成才返回，确保已关再 Destroy
            return await DestroyAsync(vmName);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>彻底删除：移除配置 + 删磁盘文件 + 删配置目录（带受保护路径防误删）。仅在 DestroySystem 成功后才动文件。</summary>
    public static async Task<(bool Success, string Message)> PurgeVmAsync(string vmName, Guid vmId)
    {
        try
        {
            string vmGuid = vmId.ToString();

            // 删除前先收集要清理的路径（删完就查不到了）
            var diskResp = await WmiApi.QueryAsync(
                $"SELECT Path FROM Msvm_StorageAllocationSettingData WHERE InstanceID LIKE 'Microsoft:{vmGuid}%' AND ResourceSubType = 'Microsoft:Hyper-V:Virtual Hard Disk'",
                obj => obj["Path"]?.ToString() ?? string.Empty,
                WmiScope.HyperV);
            var diskPaths = (diskResp.Data ?? new List<string>())
                .Where(p => !string.IsNullOrEmpty(p)).ToList();

            var configResp = await WmiApi.QueryFirstAsync(
                $"SELECT ConfigurationDataRoot FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmGuid}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                obj => obj["ConfigurationDataRoot"]?.ToString() ?? string.Empty,
                WmiScope.HyperV);
            string? rawConfigDir = configResp.HasData ? configResp.Data : null;

            await VmPowerService.ExecuteControlActionAsync(vmName, "TurnOff");
            var destroy = await DestroyAsync(vmName);
            if (!destroy.Success) return destroy;   // 删除失败就不动文件（VM 仍在用着盘）

            foreach (var diskPath in diskPaths)
            {
                try { if (File.Exists(diskPath)) File.Delete(diskPath); }
                catch { }
            }
            await DeleteConfigDirAsync(rawConfigDir);

            return (true, string.Empty);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // 取 VM 路径并 DestroySystem。
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
        return r.Success ? (true, string.Empty) : (false, r.Error);
    }

    // 删配置目录：零硬编码、可证明安全。
    // DestroySystem 已清掉 VM 自己的配置文件、VHD 也已删 → VM 专属目录此时应为空壳。
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
                Directory.Delete(configDir, recursive: true);
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
}
