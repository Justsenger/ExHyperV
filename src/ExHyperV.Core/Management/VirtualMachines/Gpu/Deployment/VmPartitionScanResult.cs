using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>GPU 分区扫描结果，区分扫描和恢复失败。</summary>
/// <param name="Partitions">已读取分区；失败时可能不完整。</param>
/// <param name="Errors">扫描失败说明。</param>
/// <param name="CleanupErrors">恢复原始磁盘状态或卸载临时映像失败的说明。</param>
/// <param name="IsCancelled">是否在扫描边界收到取消；已完成的分区和恢复结果仍保留。</param>
public sealed record VmPartitionScanResult(IReadOnlyList<ExHyperV.Models.DiskPartitionInfo> Partitions,
    IReadOnlyList<string> Errors, IReadOnlyList<string> CleanupErrors, bool IsCancelled = false)
{
    /// <summary>底层提交或清理结果尚未确认；不得据此重试写入或卸载，须先核对实际状态。</summary>
    public bool OutcomeUnknown { get; init; }
    /// <summary>全部扫描和恢复均成功。</summary>
    public bool Success => !OutcomeUnknown && !IsCancelled && Errors.Count == 0 && CleanupErrors.Count == 0;
    /// <summary>存在仍需处理的磁盘状态恢复。</summary>
    public bool CleanupRequired => CleanupErrors.Count > 0;
}
