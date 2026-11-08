namespace ExHyperV.Models;

/// <summary>部署结果不明时的核对材料；这些标识只用于诊断，不是执行任意底层命令的入口。</summary>
/// <param name="OperationId">Core 为本次部署恢复记录生成的编号，各原生调用另有 Operations.Id。</param>
/// <param name="VmId">此次操作固定的虚拟机 GUID。</param>
/// <param name="StartedAtUtc">创建此恢复记录的时间。</param>
/// <param name="JobReference">GPU 适配器创建的原生作业引用；未取得时为 null，其他阶段见 Operations。</param>
/// <param name="ResourceReferences">GPU 适配器创建回执的资源引用；只有 AddedAdapterId 表示已验证属于目标 VM 的 GPU 设备。其他临时资源见 Resources。</param>
public sealed record GpuDeploymentRecovery(Guid OperationId, Guid VmId, DateTimeOffset StartedAtUtc,
    string? JobReference, IReadOnlyList<string> ResourceReferences)
{
    /// <summary>电源、MMIO、磁盘和适配器移除等步骤的原生操作；适配器创建使用 JobReference 和 ResourceReferences。IsSettled 为 false 表示尚未确认结束。</summary>
    public IReadOnlyList<GpuRecoveryOperation> Operations { get; init; } = [];
    /// <summary>临时资源及原状态；Restored 为 true 表示最近核实已经恢复，尚未恢复的项由 RetryCleanupAsync 处理。</summary>
    public IReadOnlyList<GpuRecoveryResource> Resources { get; init; } = [];
    /// <summary>最近核实的当前值：Power.State 为电源状态，Mmio.LowSizeMiB、Mmio.HighSizeMiB、Mmio.HighBaseMiB 为 MMIO 大小和基址，单位 MiB；未提供的值为 Unavailable。这里只包含本次涉及的步骤，不作为新的写入请求。</summary>
    public IReadOnlyDictionary<string, string> ObservedValues { get; init; } = new Dictionary<string, string>();
}

/// <summary>部署发起的一个原生操作及核实结果；原生引用只用于诊断。</summary>
/// <param name="Id">本次原生调用的 Core 编号。</param>
/// <param name="Stage">部署步骤标识，例如 Power:Start、Mmio、Partitions、WindowsDrivers、RemoveAdapter、Recovery:资源类型。</param>
/// <param name="Method">实际调用的原生方法名。</param>
/// <param name="TargetReference">由 Core 查询取得的目标引用。</param>
/// <param name="JobReference">原生作业引用；同步调用或未取得时为 null。</param>
/// <param name="StartedAtUtc">调用开始时间。</param>
/// <param name="IsSettled">是否已确认结束；不表示成功，失败详情见 Failure。</param>
/// <param name="Failure">最新失败详情；正在执行且未取得结果时也可能为 null，需结合 IsSettled。</param>
public sealed record GpuRecoveryOperation(Guid Id, string Stage, string Method, string TargetReference,
    string? JobReference, DateTimeOffset StartedAtUtc, bool IsSettled, ExHyperV.Tools.ApiFailure? Failure);

/// <summary>本次部署需要恢复的一个临时资源，不包含驱动文件回滚。</summary>
/// <param name="Kind">资源种类：DiskState、ImageMount、PhysicalAttachment、DriveLetter。</param>
/// <param name="Resource">原磁盘、映像、挂接或分区的身份说明。</param>
/// <param name="OriginalValues">原状态与恢复目标。DiskState 含 DiskNumber/IsOffline/IsReadOnly；ImageMount 含 ImagePath/OriginallyMounted；PhysicalAttachment 含 VmId/ControllerId/ControllerType/ControllerNumber/Location；DriveLetter 含 AccessPath/OriginallyPresent。布尔值使用 True/False。</param>
/// <param name="Restored">最近核实是否已经恢复。</param>
/// <param name="Failure">最近恢复或核实失败的详情。</param>
public sealed record GpuRecoveryResource(string Kind, string Resource,
    IReadOnlyDictionary<string, string> OriginalValues, bool Restored, ExHyperV.Tools.ApiFailure? Failure);
