
using ExHyperV.Tools;
using ExHyperV.Services;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Dashboard;

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

    public static string? ReadName(VmQueryRow row)
    {
        var metadata = row.GetCollection(VmQueryCollection.Metadata);
        if (metadata.State != VmQueryValueState.Available) return null;
        var os = metadata.Rows.FirstOrDefault(m => m.Get(VmMetadataFields.Key).Value == VmMetadataService.OsTypeKey)?.Get(VmMetadataFields.Value);
        return GetResourceName(os is { HasValue: true } ? os.Value : null);
    }

    public static string GetResourceName(string? osType) =>
        OsImages.SupportedTypes.FirstOrDefault(name => name.Equals(osType, StringComparison.OrdinalIgnoreCase)) ?? "Windows";
}
