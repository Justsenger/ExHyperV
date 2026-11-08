using ExHyperV.Tools;

namespace ExHyperV.WinUI3.UI.Shared;

internal static class CoreResultExtensions
{
    internal static T RequireData<T>(this ApiResponse<T> result)
    {
        if (!result.Success || !result.HasData)
            throw new InvalidOperationException(result.Error ?? "未取得有效数据。");
        return result.Data!;
    }
}
