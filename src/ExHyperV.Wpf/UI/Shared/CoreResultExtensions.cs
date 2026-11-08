using ExHyperV.Tools;

namespace ExHyperV.UI.Shared;

internal static class CoreResultExtensions
{
    /// <summary>读取当前操作必需的数据；失败或缺少数据时交由调用页面显示错误。</summary>
    internal static T RequireData<T>(this ApiResponse<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.HasData) return result.Data;
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(result.Error) ? Properties.Resources.Error_Unknown : result.Error,
            result.Exception);
    }

    internal static void ThrowIfFailed(this ApiResponse result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Success)
            throw new InvalidOperationException(result.Error, result.Exception);
    }
}
