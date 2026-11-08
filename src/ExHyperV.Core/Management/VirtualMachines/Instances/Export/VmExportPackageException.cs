using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>打包失败的诊断附件，保存在失败 ApiResponse 的 Exception 属性中，不要求解析错误文本。</summary>
public sealed class VmExportPackageException : IOException
{
    internal VmExportPackageException(string message, Exception cause, IReadOnlyList<string> retainedPaths, string? cleanupError = null)
        : base(message, cause)
    { Failure = ApiFailure.From(cause); RetainedPaths = Array.AsReadOnly(retainedPaths.ToArray()); CleanupError = cleanupError; }
    /// <summary>主失败的结构化信息。</summary>
    public ApiFailure Failure { get; }
    /// <summary>本次创建但未成功清理的临时归档路径；不包含仍保留的源目录。</summary>
    public IReadOnlyList<string> RetainedPaths { get; }
    /// <summary>临时归档清理失败原因；没有遗留时为 null。</summary>
    public string? CleanupError { get; }
}
