namespace ExHyperV.Models;

/// <summary>代际升级完成后，宿主注册与磁盘事务的状态。</summary>
public enum VmGenerationUpgradeStatus
{
    /// <summary>二代虚拟机已验证并提交，恢复包与已转换的原 VHD 均已清理。</summary>
    Succeeded,
    /// <summary>未进入原机替换阶段，原注册和原磁盘保留。</summary>
    Rejected,
    /// <summary>已确认原 GUID 的一代虚拟机及其原磁盘，替换机已撤销。</summary>
    RolledBack,
    /// <summary>二代虚拟机已提交，再次以原 GUID 调用可继续清理。</summary>
    CommittedCleanupPending,
    /// <summary>注册、磁盘或原生作业状态无法确认，已保留日志及恢复材料。</summary>
    OutcomeUnknown
}

/// <summary>一次直接升级或事务对账的结果。</summary>
/// <param name="Status">升级最终状态。</param>
/// <param name="OldVmId">调用时提供的一代虚拟机 GUID，也是重入事务的键。</param>
/// <param name="NewVmId">已取得创建回执的二代虚拟机 GUID。尚未创建时为空。</param>
/// <param name="ConfigurationDirectory">清单中记录的正式配置根目录。预检前可能为空。</param>
/// <param name="SystemDiskPath">首选硬盘的正式路径。无硬盘时为空，未探测来宾系统。</param>
/// <param name="FailedStage">首次失败或待核实的事务阶段。成功时为空。</param>
/// <param name="Error">本次失败或需要继续清理的原因。</param>
/// <param name="RetainedPaths">用于对账、查阅日志或恢复的路径。</param>
public sealed record VmGenerationUpgradeResult(VmGenerationUpgradeStatus Status, Guid OldVmId, Guid? NewVmId,
    string? ConfigurationDirectory, string? SystemDiskPath, string? FailedStage, string? Error,
    IReadOnlyList<string> RetainedPaths)
{
    /// <summary>已核验的二代虚拟机正式 VMCX 文件路径，取得最终配置回执前为空。</summary>
    public string? ConfigurationPath { get; internal init; }

    /// <summary>可选来宾升级日志目录。当前虚拟机层面的升级返回空值。</summary>
    public string? LogDirectory { get; internal init; }

    /// <summary>正式二代虚拟机的虚拟硬盘路径，首选硬盘位于首项。无硬盘或尚未完成回读时为空。</summary>
    public IReadOnlyList<string> DiskPaths { get; internal init; } = [];
}
