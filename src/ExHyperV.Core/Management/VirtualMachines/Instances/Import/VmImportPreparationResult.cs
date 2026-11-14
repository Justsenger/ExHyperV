using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>导入预览的准备结果，以及仍由调用方负责释放的批次。</summary>
/// <remarks>准备失败时，Batch 可能仍有待清理的工作目录。保留引用，检查清理结果并重试 DisposeAsync。</remarks>
public sealed class VmImportPreparationResult
{
    internal VmImportPreparationResult(ApiResponse result, VmImportBatchSession? batch)
    {
        Result = result;
        Batch = batch;
    }

    /// <summary>准备阶段的成功或失败信息；不包含清理错误，清理结果从 Batch 读取。</summary>
    public ApiResponse Result { get; }

    /// <summary>是否已成功生成完整预览；为 true 时 Batch 不为 null。</summary>
    public bool Success => Result.Success;

    /// <summary>本次创建的批次；尚未创建时为 null。失败时可能已释放但清理未完成，应保留引用并重试 DisposeAsync。</summary>
    public VmImportBatchSession? Batch { get; }
}
