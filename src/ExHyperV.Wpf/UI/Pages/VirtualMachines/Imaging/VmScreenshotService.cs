using ExHyperV.Services;
using System.Windows.Media.Imaging;
namespace ExHyperV.UI.Pages.VirtualMachines.Imaging;

public static class VmScreenshotService
{
    public static async Task<BitmapSource?> CaptureAsync(Guid vmId, int desiredWidth, int desiredHeight)
        => VmImagePresentation.ToBitmap(await VmScreenshotSource.CaptureAsync(vmId, desiredWidth, desiredHeight));
}
