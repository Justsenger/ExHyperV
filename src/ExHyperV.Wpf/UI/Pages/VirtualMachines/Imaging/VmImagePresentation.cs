using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExHyperV.Models;

namespace ExHyperV.UI.Pages.VirtualMachines.Imaging;

internal static class VmImagePresentation
{
    public static BitmapSource? ToBitmap(VmScreenshotImage? image)
    {
        if (image == null) return null;
        try
        {
            BitmapSource bitmap;
            if (image.IsEncoded)
            {
                using var stream = new MemoryStream(image.CopyBytes(), writable: false);
                var decoded = new BitmapImage();
                decoded.BeginInit();
                decoded.StreamSource = stream;
                decoded.CacheOption = BitmapCacheOption.OnLoad;
                decoded.EndInit();
                bitmap = decoded;
            }
            else bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgr565, null, image.CopyBytes(), image.Stride);
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    public static byte[] EncodeJpeg(VmScreenshotImage image)
    {
        using var stream = new MemoryStream();
        var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
        encoder.Frames.Add(BitmapFrame.Create(ToBitmap(image) ?? throw new InvalidDataException("Invalid thumbnail.")));
        encoder.Save(stream);
        return stream.ToArray();
    }
}
