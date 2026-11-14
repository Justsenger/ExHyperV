using ExHyperV.Tools;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

/// <summary>读取与 WPF 同源的操作系统矢量资源；在界面线程使用。</summary>
internal static class OsIconSource
{
    private static readonly Dictionary<string, ImageSource> Images = new(StringComparer.Ordinal);

    public static ImageSource Get(string? osType)
    {
        var name = GetResourceName(osType);
        if (!Images.TryGetValue(name, out var image))
        {
            image = new SvgImageSource(new Uri($"ms-appx:///Assets/OperatingSystems/{name}.svg"));
            Images.Add(name, image);
        }
        return image;
    }

    public static string GetResourceName(string? osType) =>
        OsImages.SupportedTypes.FirstOrDefault(name => name.Equals(osType, StringComparison.OrdinalIgnoreCase)) ?? "Windows";
}
