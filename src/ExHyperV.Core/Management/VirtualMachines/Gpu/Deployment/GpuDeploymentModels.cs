namespace ExHyperV.Models;

/// <summary>一次 GPU 部署要执行的工作范围。</summary>
public enum GpuDeploymentMode
{
    /// <summary>配置并分配 GPU，不部署来宾驱动。</summary>
    AddWithoutDrivers,
    /// <summary>配置并分配 GPU，再按确认流程部署来宾驱动。</summary>
    AddWithDrivers,
    /// <summary>针对已有分配更新来宾驱动，不新增 GPU 适配器。</summary>
    UpdateDrivers
}
/// <summary>一次 GPU 部署的状态；调用方应以状态和 AllowedActions 决定下一次调用。</summary>
public enum GpuDeploymentState
{
    /// <summary>已创建，尚未执行部署，可调用 StartAsync。</summary>
    Created,
    /// <summary>正在执行当前步骤。</summary>
    Running,
    /// <summary>已暂停并等待匹配 PendingDecision.Id 的用户决定。</summary>
    WaitingForDecision,
    /// <summary>部署已完成。</summary>
    Completed,
    /// <summary>部署失败，相关错误保存在快照中。</summary>
    Failed,
    /// <summary>部署取消流程已结束，已完成的配置修改予以保留。</summary>
    Cancelled,
    /// <summary>取消或失败后的任务结果、临时磁盘恢复或新增适配器清理尚未解决；按允许动作核实或重试。</summary>
    CleanupRequired
}
/// <summary>GPU 部署暂停时需要用户处理的具体事项。</summary>
public enum GpuDecisionKind
{
    /// <summary>确认强制关闭虚拟机（TurnOff）。</summary>
    PowerOff,
    /// <summary>选择目标系统分区。</summary>
    SelectPartition,
    /// <summary>确认向离线 Windows 系统分区部署驱动文件。</summary>
    InstallWindows,
    /// <summary>确认启动 Linux 来宾以继续在线部署。</summary>
    StartLinux,
    /// <summary>提交部署脚本和 SSH 凭据。</summary>
    LinuxCredentials
}
/// <summary>一个待确认事项所接受的输入结构。</summary>
public enum GpuDecisionInput
{
    /// <summary>使用 GpuConfirmation 接受或拒绝。</summary>
    Confirmation,
    /// <summary>使用 GpuPartitionSelection 提交分区 ID。</summary>
    Partition,
    /// <summary>使用 GpuLinuxInput 提交脚本和 SSH 凭据。</summary>
    LinuxCredentials
}
/// <summary>当前部署快照允许调用方发起的动作。</summary>
public enum GpuDeploymentAction
{
    /// <summary>启动尚未开始的部署。</summary>
    Start,
    /// <summary>提交当前待确认事项的决定。</summary>
    SubmitDecision,
    /// <summary>返回分区选择，需使用当前请求 ID。</summary>
    BackToPartitions,
    /// <summary>请求取消并等待可执行的清理。</summary>
    Cancel,
    /// <summary>恢复临时磁盘资源并清理本次新增的 GPU 适配器，不继续部署。</summary>
    RetryCleanup,
    /// <summary>只读核实本次部署和清理的原任务及资源状态，不重新执行写入。</summary>
    Reconcile
}

/// <summary>GPU 部署流程中的步骤类别。</summary>
public enum GpuDeploymentStepKind
{
    /// <summary>准备宿主 GPU 分配环境。</summary>
    Prepare,        // 环境准备
    /// <summary>检查虚拟机的 GPU 所需设置。</summary>
    ConfigCheck,    // 检查虚拟机设置
    /// <summary>检查并按用户决定处理虚拟机电源状态。</summary>
    PowerCheck,     // 电源检查
    /// <summary>调整 GPU 所需的缓存和 MMIO 设置。</summary>
    Optimization,   // 系统优化
    /// <summary>新增虚拟 GPU 分区适配器。</summary>
    Assign,         // 分配显卡
    /// <summary>安装或更新来宾 GPU 驱动。</summary>
    Driver          // 驱动安装
}

/// <summary>单个 GPU 部署步骤的执行状态。</summary>
public enum GpuDeploymentStepStatus
{
    /// <summary>尚未执行该步骤。</summary>
    Pending,
    /// <summary>步骤正在执行或等待其所需决定。</summary>
    Running,
    /// <summary>步骤已成功结束。</summary>
    Success,
    /// <summary>步骤执行失败。</summary>
    Failed
}

/// <summary>部署步骤的显示状态快照。名称和说明用于展示，程序分支应使用 Type 和 Status。</summary>
/// <param name="Type">步骤类别。</param>
/// <param name="Name">步骤显示名称。</param>
/// <param name="Description">步骤当前进展说明。</param>
/// <param name="Status">步骤执行状态。</param>
public sealed record GpuDeploymentStep(GpuDeploymentStepKind Type, string Name, string Description, GpuDeploymentStepStatus Status);

// Snapshot values are immutable; a caller cannot change the disk used by a pending operation.
/// <summary>供用户选择的系统分区快照。会话以 Id 重新匹配内部记录，不接受调用方改写的磁盘路径。</summary>
/// <param name="Id">本次分区列表中的选择标识，只应用于当前部署。</param>
/// <param name="PartitionNumber">分区编号。</param>
/// <param name="SizeInBytes">分区容量，单位为字节。</param>
/// <param name="OsType">识别的系统类型。</param>
/// <param name="TypeDescription">分区类型显示说明。</param>
/// <param name="DiskPath">虚拟磁盘文件路径或物理磁盘编号字符串。</param>
/// <param name="DiskDisplayName">所属磁盘的显示名称。</param>
/// <param name="IsPhysicalDisk">是否来自物理直通磁盘。</param>
/// <param name="IsBitLocker">是否检测到 BitLocker 签名；加密分区不能直接用于此部署。</param>
public sealed record GpuDeploymentPartition(string Id, int PartitionNumber, ulong SizeInBytes,
    OperatingSystemType OsType, string TypeDescription, string DiskPath, string DiskDisplayName,
    bool IsPhysicalDisk, bool IsBitLocker)
{
    /// <summary>按 1024³ 字节换算的分区容量，单位为 GiB。</summary>
    public double SizeGiB => SizeInBytes / (1024.0 * 1024 * 1024);
    /// <summary>磁盘名称、分区编号和容量组合的本地化文本。</summary>
    public string DisplayName => string.Format(Properties.Resources.Format_PartitionDesc, DiskDisplayName, PartitionNumber, SizeGiB);
}

/// <summary>部署暂停时的待确认事项；提交决定时须携带本次请求的 Id。</summary>
/// <param name="Id">提交决定时必须原样携带的请求 ID。</param>
/// <param name="Kind">待确认事项的种类。</param>
/// <param name="Title">本地化标题。</param>
/// <param name="Message">本地化操作说明。</param>
/// <param name="Partitions">可供选择的分区快照；会话发布的是只读列表。</param>
/// <param name="SuggestedHost">建议的 Linux IPv4 地址；空字符串表示未取得。</param>
/// <param name="CanGoBack">是否允许从当前事项返回分区选择。</param>
public sealed record GpuDecisionRequest(Guid Id, GpuDecisionKind Kind, string Title, string Message,
    IReadOnlyList<GpuDeploymentPartition> Partitions, string SuggestedHost = "", bool CanGoBack = false)
{
    /// <summary>由 Kind 推导出的输入结构类别。</summary>
    public GpuDecisionInput Input => Kind switch
    {
        GpuDecisionKind.SelectPartition => GpuDecisionInput.Partition,
        GpuDecisionKind.LinuxCredentials => GpuDecisionInput.LinuxCredentials,
        _ => GpuDecisionInput.Confirmation
    };
    /// <summary>是否为直接关闭虚拟机或写入离线 Windows 驱动的确认；其他步骤仍可能有配置副作用。</summary>
    public bool IsDestructive => Kind is GpuDecisionKind.PowerOff or GpuDecisionKind.InstallWindows;
}

/// <summary>部署会话发布的状态快照。步骤、动作和分区集合为只读；手动构造时应传入不可变集合。</summary>
/// <param name="SessionId">部署会话的稳定标识。</param>
/// <param name="Revision">同一会话中递增的快照修订号。</param>
/// <param name="State">部署当前状态。</param>
/// <param name="Steps">当前各步骤的状态快照。</param>
/// <param name="PendingDecision">当前待确认事项；不在等待决定时为 null。</param>
/// <param name="AllowedActions">当前状态允许的调用动作。</param>
/// <param name="HasAddedAdapter">本次部署是否新增并识别到适配器。</param>
/// <param name="HasPendingCleanup">是否有已识别且尚待清理的适配器；false 时仍应检查 State 和 CleanupError。</param>
/// <param name="Error">部署错误说明；无错误时为 null。</param>
/// <param name="CleanupError">清理失败说明；没有清理错误时为 null。</param>
/// <param name="LastLog">最近一条步骤日志；尚无日志时为 null。</param>
public sealed record GpuDeploymentSnapshot(Guid SessionId, long Revision, GpuDeploymentState State,
    IReadOnlyList<GpuDeploymentStep> Steps, GpuDecisionRequest? PendingDecision,
    IReadOnlyList<GpuDeploymentAction> AllowedActions, bool HasAddedAdapter, bool HasPendingCleanup,
    string? Error = null, string? CleanupError = null, string? LastLog = null)
{
    /// <summary>底层操作结果尚未确认；此状态不允许重试清理。AllowedActions 包含 Reconcile 时可只读核实原任务和资源。</summary>
    public bool OutcomeUnknown { get; init; }
    /// <summary>本次原生回执返回且已核对属于目标 VM 的 GPU 适配器身份；未确认时为 null，不通过清单差异推断。</summary>
    public string? AddedAdapterId { get; init; }
    /// <summary>已确认归属且待清理的适配器标识；清理时机由 AllowedActions 指示。</summary>
    public string? PendingCleanupAdapterId { get; init; }
    /// <summary>本次部署与恢复的任务、资源、原状态及核实结果；无记录时为 null，可保存用于排查。</summary>
    public GpuDeploymentRecovery? Recovery { get; init; }

    internal GpuDeploymentSnapshot(Guid sessionId, long revision, GpuDeploymentState state,
        IReadOnlyList<GpuDeploymentStep> steps, GpuDecisionRequest? pendingDecision,
        IReadOnlyList<GpuDeploymentAction> allowedActions, string? addedAdapterId, string? pendingCleanupAdapterId,
        string? error = null, string? cleanupError = null, string? lastLog = null)
        : this(sessionId, revision, state, steps, pendingDecision, allowedActions,
            addedAdapterId != null, pendingCleanupAdapterId != null, error, cleanupError, lastLog)
    {
        AddedAdapterId = addedAdapterId;
        PendingCleanupAdapterId = pendingCleanupAdapterId;
    }
}

/// <summary>提交给部署会话的决定基类，决定必须对应当前尚未消费的请求 ID。</summary>
/// <param name="RequestId">当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。</param>
public abstract record GpuDeploymentDecision(Guid RequestId);
/// <summary>接受或拒绝一项部署确认；拒绝会进入取消流程。</summary>
/// <param name="RequestId">当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。</param>
/// <param name="Accepted">true 继续确认的步骤；false 取消部署。</param>
public sealed record GpuConfirmation(Guid RequestId, bool Accepted) : GpuDeploymentDecision(RequestId);
/// <summary>通过 PartitionId 从当前候选列表中选择分区。</summary>
/// <param name="RequestId">当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。</param>
/// <param name="PartitionId">当前 PendingDecision.Partitions 中的分区 Id。</param>
public sealed record GpuPartitionSelection(Guid RequestId, string PartitionId) : GpuDeploymentDecision(RequestId);
/// <summary>为 Linux 部署提供脚本和 SSH 凭据；会话在处理前复制二者，避免后续调用方修改影响待执行操作。</summary>
/// <param name="RequestId">当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。</param>
/// <param name="Script">选定的部署脚本；类型允许 null，但提交 Linux 决定时不能为空。</param>
/// <param name="Credentials">SSH 凭据；类型允许 null，但提交 Linux 决定时不能为空，端口及代理需有效。</param>
public sealed record GpuLinuxInput(Guid RequestId, LinuxGpuDeploymentScript? Script, SshCredentials? Credentials) : GpuDeploymentDecision(RequestId);
/// <summary>请求从允许返回的待确认事项回到系统分区选择。</summary>
/// <param name="RequestId">当前 PendingDecision.Id；过期或不匹配的 ID 会被拒绝。</param>
public sealed record GpuBackToPartitions(Guid RequestId) : GpuDeploymentDecision(RequestId);
