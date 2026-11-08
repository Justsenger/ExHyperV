using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>导入预览的准备结果，以及仍由调用方负责释放的批次。</summary>
/// <remarks>准备失败也可能留下未清理完的工作目录。此时 Batch 只用于检查清理结果和再次 DisposeAsync，不可继续导入；不能仅根据 Success 丢弃它。</remarks>
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
