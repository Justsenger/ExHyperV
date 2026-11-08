namespace ExHyperV.Tools;

/// <summary>Core 操作抛出的失败异常；Result 保留错误类别和执行结果。</summary>
/// <remarks>OutcomeUnknown 表示已提交操作的结果尚未确认，不代表操作未发生或已停止，不应直接重试或清理。</remarks>
public class CoreOperationException : InvalidOperationException
{
    internal CoreOperationException(ApiResponse result) : base(result.Error, result.Exception) => Result = result;

    /// <summary>保留错误类别、来源、错误码与原始异常的失败响应。</summary>
    public ApiResponse Result { get; }
}
