namespace ExHyperV.Tools;

// 在内部组合流程中保留未知结果，防止普通异常处理触发回滚；不向外暴露原生作业对象。
internal sealed class OperationOutcomeUnknownException(ApiResponse result) : CoreOperationException(result)
{
    internal static void ThrowIfUnknown(ApiResponse result)
    {
        if (result.ErrorKind == ApiErrorKind.OutcomeUnknown)
            throw new OperationOutcomeUnknownException(result);
    }

    internal static void ThrowIfUnknown<T>(ApiResponse<T> result)
    {
        if (result.ErrorKind == ApiErrorKind.OutcomeUnknown)
            throw new OperationOutcomeUnknownException(ApiResponse.Fail(result.Error, result.Code,
                result.ErrorSource, result.Exception, result.ErrorKind));
    }
}
