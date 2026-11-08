using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ExHyperV.Models;

/// <summary>一张独立保存的截图，包含尺寸和图像字节，可转换为 JPEG。</summary>
/// <remarks>CaptureAsync 返回的图像保存 RGB565 原始像素；FromEncoded 创建的图像保存已有的图片文件字节。
/// 用 IsEncoded 区分两者，再用 CopyBytes 取得副本。对象不需要 Dispose，也不依赖控制台连接的生命周期。</remarks>
public sealed class VmScreenshotImage
{
    private readonly byte[] bytes;
    private VmScreenshotImage(ReadOnlySpan<byte> data, int width, int height, int stride, bool encoded)
    { bytes = data.ToArray(); Width = width; Height = height; Stride = stride; IsEncoded = encoded; }
    /// <summary>截图的宽度。</summary>
    /// <fielddoc>
    /// <meaning>截图的宽度。</meaning>
    /// <values kind="format">正整数，单位为像素。</values>
    /// </fielddoc>
    public int Width { get; }
    /// <summary>截图的高度。</summary>
    /// <fielddoc>
    /// <meaning>截图的高度。</meaning>
    /// <values kind="format">正整数，单位为像素。</values>
    /// </fielddoc>
    public int Height { get; }
    /// <summary>原始像素图每行占用的字节数。</summary>
    /// <fielddoc>
    /// <meaning>原始像素图每行占用的字节数。</meaning>
    /// <values kind="format">RGB565 图像为 Width × 2；编码图片为 0。</values>
    /// </fielddoc>
    public int Stride { get; }
    /// <summary>图像字节是否保存为编码图片。</summary>
    /// <fielddoc>
    /// <meaning>图像字节是否保存为编码图片。</meaning>
    /// <values kind="options">true：图片文件字节；false：从上到下排列的小端 RGB565 像素，每像素 2 字节。</values>
    /// </fielddoc>
    public bool IsEncoded { get; }
    /// <summary>取得图像内容的一份独立字节副本。</summary>
    /// <returns>IsEncoded 为 false 时是 RGB565 像素，为 true 时是原来的编码图片字节。修改返回数组不会改变此图像。</returns>
    public byte[] CopyBytes() => (byte[])bytes.Clone();

    /// <summary>从 RGB565 原始像素创建图像，并复制输入数组。</summary>
    /// <param name="data">从上到下排列的小端 RGB565 数据，每个像素 2 字节、每行无填充；长度至少为 width × height × 2。</param>
    /// <param name="width">图像宽度，正整数，单位为像素。</param>
    /// <param name="height">图像高度，正整数，单位为像素。</param>
    /// <returns>拥有独立字节副本的图像，IsEncoded 为 false。</returns>
    /// <remarks>构造后可复用原数组。整个数组都会被复制，多余的尾部字节也会由 CopyBytes 原样返回。</remarks>
    /// <exception cref="ArgumentException">宽高不是正数，或数组长度不足。</exception>
    /// <exception cref="OverflowException">计算行长度或图像长度时超出整数范围。</exception>
    public static VmScreenshotImage FromRgb565(byte[] data, int width, int height)
    {
        int stride = checked(width * 2);
        if (width <= 0 || height <= 0 || data.Length < checked(stride * height)) throw new ArgumentException("Invalid thumbnail dimensions or buffer.");
        return new(data, width, height, stride, false);
    }

    internal static VmScreenshotImage? FromHyperVThumbnail(byte[] data, int width, int height)
    {
        int pixelBytes = checked(width * height * 2);
        ReadOnlySpan<byte> pixels = data;
        if (pixels.Length != pixelBytes)
        {
            // Some VMMS builds prefix RGB565 data with the total byte count in network byte order.
            if (pixels.Length - pixelBytes != sizeof(int) ||
                BinaryPrimitives.ReadInt32BigEndian(pixels) != pixels.Length) return null;
            pixels = pixels[sizeof(int)..];
        }
        return new(pixels, width, height, width * 2, false);
    }

    /// <summary>从已有图片数据创建图像，保留原来的编码格式。</summary>
    /// <param name="data">Windows 图像解码器可读取的图片字节，例如 JPEG 或 PNG；本方法复制输入数组。</param>
    /// <returns>保存原编码字节和图片尺寸的图像，IsEncoded 为 true。</returns>
    /// <remarks>读取图片取得宽高，但不会把对象保存的内容替换为原始像素。不支持或损坏的图片会由解码器抛出异常。</remarks>
    public static VmScreenshotImage FromEncoded(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var image = Image.FromStream(stream);
        return new(data, image.Width, image.Height, 0, true);
    }

    // Headless consumers have a Windows JPEG codec. WPF supplies its existing WIC
    // encoder when saving checkpoints, preserving the original output exactly.
    /// <summary>将这张图像转换为 JPEG，便于保存或发送给网页。</summary>
    /// <returns>新建的 JPEG 字节数组。</returns>
    /// <remarks>使用 Windows JPEG 编码器，质量参数固定为 80。JPEG 有损且不保留透明度；
    /// 即使输入已经是 JPEG，此方法也会重新编码。想保留原文件字节时使用 CopyBytes。</remarks>
    public byte[] EncodeJpeg()
    {
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
        if (IsEncoded)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var image = Image.FromStream(stream);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.DrawImageUnscaled(image, 0, 0);
        }
        else
        {
            var bits = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[Width * 3];
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int pixel = bytes[y * Stride + x * 2] | (bytes[y * Stride + x * 2 + 1] << 8);
                        int b = pixel & 31, g = (pixel >> 5) & 63, r = (pixel >> 11) & 31;
                        row[x * 3] = (byte)((b << 3) | (b >> 2));
                        row[x * 3 + 1] = (byte)((g << 2) | (g >> 4));
                        row[x * 3 + 2] = (byte)((r << 3) | (r >> 2));
                    }
                    Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, row.Length);
                }
            }
            finally { bitmap.UnlockBits(bits); }
        }
        using var output = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
        bitmap.Save(output, ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid), parameters);
        return output.ToArray();
    }
}
