using ExHyperV.Tools;
using ExHyperV.WinUI3.Localization;

namespace ExHyperV.WinUI3.UI.Shared;

internal static class CoreResultExtensions
{
    internal static T RequireData<T>(this ApiResponse<T> result)
    {
        if (!result.Success || !result.HasData)
            throw new InvalidOperationException(CoreMessageResolver.ResolveError(result));
        return result.Data!;
    }
}
