using Microsoft.UI;
using Microsoft.UI.Xaml;

namespace ExHyperV.WinUI3.Composition;

public sealed partial class ProductConfiguration
{
    /// <summary>附加在宿主页后面的产品页面，按需创建并由壳缓存。</summary>
    public IReadOnlyList<ProductPageDefinition> Pages { get; init; } = [];
}

public sealed record ProductPageContext(WindowId OwnerWindowId);
public sealed record ProductPageDefinition(string Id, string Title, string Glyph,
    Func<ProductPageContext, FrameworkElement> Create);

/// <summary>离开页面仅触发 Unloaded；关闭产品时等待 StopAsync，再 Dispose。</summary>
public interface IProductPageLifetime : IDisposable
{
    Task StopAsync();
}
