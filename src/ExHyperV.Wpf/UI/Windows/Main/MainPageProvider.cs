using Wpf.Ui.Abstractions;

namespace ExHyperV.UI.Windows.Main;

/// <summary>预热和实际导航共用页面实例；未启用缓存的页面仍按次创建。</summary>
internal sealed class MainPageProvider(IEnumerable<Type> cachedPageTypes) : INavigationViewPageProvider
{
    private readonly HashSet<Type> _cachedTypes = new(cachedPageTypes);
    private readonly Dictionary<Type, object> _pages = new();

    public object GetPage(Type pageType)
    {
        if (_cachedTypes.Contains(pageType) && _pages.TryGetValue(pageType, out var existing))
            return existing;

        var page = Wpf.Ui.Designer.DesignerHelper.IsInDesignMode
            ? new System.Windows.Controls.Page()
            : Activator.CreateInstance(pageType)
            ?? throw new InvalidOperationException($"Unable to create page {pageType}.");
        if (_cachedTypes.Contains(pageType)) _pages.Add(pageType, page);
        return page;
    }
}
