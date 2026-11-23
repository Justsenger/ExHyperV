
using Microsoft.UI.Xaml.Media;

namespace ExHyperV.App.UI.Shared.Appearance;

internal static class AppFonts
{
    internal const string SegoeName = "Segoe Fluent Icons";
    internal const string FluentSystemName = "ms-appx:///Assets/Fonts/FluentSystemIcons-Regular.ttf#FluentSystemIcons-Regular";
    internal static readonly FontFamily SegoeFluent = new(SegoeName);
    internal static readonly FontFamily FluentSystem = new(FluentSystemName);
    internal static readonly FontFamily Monospace = new("Consolas");

    internal static FontFamily Resolve(string name) => name switch
    {
        SegoeName => SegoeFluent,
        FluentSystemName => FluentSystem,
        "Consolas" => Monospace,
        _ => new FontFamily(name)
    };
}
