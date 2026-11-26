using System.Runtime.InteropServices;
using ExHyperV.Models;

namespace ExHyperV.Services;

/// <summary>在宿主上迁移一代虚拟机的配置、存储挂接和设备，完成后二代虚拟机保持关机。</summary>
public static class VmGenerationUpgradeService
{
    /// <summary>使用自动转换 VHD 和关闭安全启动的默认设置升级虚拟机。</summary>
    /// <param name="vmId">原虚拟机的稳定 GUID。中断后仍使用此 GUID 重入。</param>
    /// <param name="cancellationToken">在提交前取消并回滚。已提交的事务继续清理。</param>
    /// <returns>注册身份、正式路径、事务结果以及需要保留的恢复路径。</returns>
    /// <remarks>需要本机管理员权限与 x64 Hyper-V。运行中的原机会先正常关机。成功后新机处于关机状态。</remarks>
    /// <instanceentry label="直接升级为二代" category="升级" order="10">
    /// <step>传入原虚拟机 GUID，Core 迁移虚拟机配置和所有磁盘挂接。</step>
    /// <step>读取状态与新 GUID。遇到清理待办或操作中断时，使用原 GUID 重新调用以对账。</step>
    /// </instanceentry>
    public static Task<VmGenerationUpgradeResult> UpgradeToGeneration2Async(Guid vmId, CancellationToken cancellationToken = default)
        => UpgradeToGeneration2Async(vmId, null, cancellationToken);

    /// <summary>按指定的磁盘格式和安全启动选项升级虚拟机，核验配置后提交。</summary>
    /// <param name="vmId">一代虚拟机 GUID，重入仍使用原 GUID。</param>
    /// <param name="options">存储与固件设置。传入 null 使用默认值，重入时沿用日志中的设置。</param>
    /// <param name="cancellationToken">在提交前取消并回滚。已提交的事务继续清理。</param>
    /// <returns>注册身份、正式路径、事务状态和保留的恢复材料。</returns>
    public static async Task<VmGenerationUpgradeResult> UpgradeToGeneration2Async(Guid vmId, VmGenerationUpgradeOptions? options,
        CancellationToken cancellationToken = default)
    {
        if (vmId == Guid.Empty || !VmCreateService.SupportsGeneration1 || RuntimeInformation.OSArchitecture != Architecture.X64)
            return new VmGenerationUpgradeResult(VmGenerationUpgradeStatus.Rejected, vmId, null,
                null, null, "Preflight", Properties.Resources.ResourceManager.GetString("GenerationUpgradePrerequisites"), []);
        using var host = new WindowsUpgradeHost();
        return await GenerationUpgradeTransaction.RunAsync(vmId, UpgradeJournal.DefaultRoot, host, cancellationToken, options);
    }
}
