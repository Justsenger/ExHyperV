
using ExHyperV.App.Localization;
using ExHyperV.Tools;

namespace ExHyperV.App.UI.Shared;

internal static class CoreResultExtensions
{
    internal static T RequireData<T>(this ApiResponse<T> result)
    {
        if (!result.Success || !result.HasData)
            throw new InvalidOperationException(CoreMessageResolver.ResolveError(result));
        return result.Data!;
    }
}
