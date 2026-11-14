namespace ExHyperV.Tools;

/// <summary>Core 操作抛出的失败异常；Result 保留错误类别和执行结果。</summary>
/// <remarks>OutcomeUnknown 表示操作结果待确认；重试或清理前先核实原操作状态。</remarks>
public class CoreOperationException : InvalidOperationException
{
    internal CoreOperationException(ApiResponse result) : base(result.Error, result.Exception) => Result = result;

    /// <summary>保留错误类别、来源、错误码与原始异常的失败响应。</summary>
    public ApiResponse Result { get; }
}
