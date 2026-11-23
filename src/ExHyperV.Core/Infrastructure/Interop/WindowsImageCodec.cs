using System.Runtime.InteropServices;
using ExHyperV.Tools;

namespace ExHyperV.Infrastructure.Interop;

/// <summary>Windows GDI+ codecs used by screenshots and console frames.</summary>
internal static class WindowsImageCodec
{
    private const int Bgr24 = 0x21808;
    private const int Bgra32 = 0x26200A;
    private static readonly Guid Jpeg = new("557cf401-1a04-11d3-9a73-0000f81ef32e");
    private static readonly Guid Png = new("557cf406-1a04-11d3-9a73-0000f81ef32e");
    private static readonly Guid Quality = new("1d5be4b5-fa4a-452d-9cdd-5db35105e7eb");

    internal static (int Width, int Height) Dimensions(byte[] encoded)
        => WithImage(encoded, image =>
        {
            Check(GdipGetImageWidth(image, out int width));
            Check(GdipGetImageHeight(image, out int height));
            return (width, height);
        });

    internal static byte[] JpegFromEncoded(byte[] encoded, int width, int height)
        => WithImage(encoded, source =>
        {
            Check(GdipCreateBitmapFromScan0(width, height, 0, Bgr24, 0, out var bitmap));
            try
            {
                Check(GdipGetImageGraphicsContext(bitmap, out var graphics));
                // Graphics.DrawImageUnscaled(image, x, y) uses this overload, including its DPI semantics.
                try { Check(GdipDrawImageI(graphics, source, 0, 0)); }
                finally { GdipDeleteGraphics(graphics); }
                return Save(bitmap, Jpeg);
            }
            finally { GdipDisposeImage(bitmap); }
        });

    internal static byte[] JpegFromBgr24(byte[] pixels, int width, int height, int stride)
        => EncodePixels(pixels, width, height, stride, Bgr24, Jpeg);

    internal static byte[] PngFromBgra32(byte[] pixels, int width, int height, int stride)
        => EncodePixels(pixels, width, height, stride, Bgra32, Png);

    private static unsafe byte[] EncodePixels(byte[] pixels, int width, int height, int stride, int format, Guid encoder)
    {
        int rowBytes = checked(width * (format == Bgr24 ? 3 : 4));
        if (width <= 0 || height <= 0 || stride < rowBytes || pixels.Length < checked((height - 1) * stride + rowBytes))
            throw new ArgumentException("Invalid pixel buffer.");
        using var session = new Session();
        // GDI+ requires DWORD-aligned scanlines. A ConsoleFrame can have arbitrary row padding.
        if ((stride & 3) != 0)
        {
            int aligned = checked((rowBytes + 3) & ~3);
            var copy = new byte[checked(aligned * height)];
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(pixels, y * stride, copy, y * aligned, rowBytes);
            pixels = copy;
            stride = aligned;
        }
        fixed (byte* scan = pixels)
        {
            Check(GdipCreateBitmapFromScan0(width, height, stride, format, (nint)scan, out var bitmap));
            try { return Save(bitmap, encoder); }
            finally { GdipDisposeImage(bitmap); }
        }
    }

    private static T WithImage<T>(byte[] data, Func<nint, T> read)
    {
        ArgumentNullException.ThrowIfNull(data);
        using var session = new Session();
        using var input = NativeComStream.Create();
        input.Write(data);
        input.Seek(SeekOrigin.Begin);
        Check(GdipLoadImageFromStream(input.Pointer, out var image));
        try { Check(GdipImageForceValidation(image)); return read(image); }
        finally { GdipDisposeImage(image); }
    }

    private static unsafe byte[] Save(nint bitmap, Guid codec)
    {
        using var output = NativeComStream.Create();
        uint quality = 80;
        var parameters = new EncoderParameters { Count = 1, Parameter = new Parameter { Id = Quality, Values = 1, Type = 4, Value = (nint)(&quality) } };
        Check(GdipSaveImageToStream(bitmap, output.Pointer, in codec, codec == Jpeg ? (nint)(&parameters) : 0));
        var bytes = new byte[checked((int)output.Seek(SeekOrigin.End))];
        output.Seek(SeekOrigin.Begin);
        if (output.Read(bytes, bytes.Length) != bytes.Length) throw new EndOfStreamException();
        return bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput { internal uint Version; internal nint Callback; internal int SuppressThread, SuppressCodecs; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Parameter { internal Guid Id; internal uint Values, Type; internal nint Value; }
    [StructLayout(LayoutKind.Sequential)]
    private struct EncoderParameters { internal uint Count; internal Parameter Parameter; }

    private sealed class Session : IDisposable
    {
        private readonly nuint token;
        internal Session() { var input = new StartupInput { Version = 1 }; Check(GdiplusStartup(out token, in input, 0)); }
        public void Dispose() => GdiplusShutdown(token);
    }
    private static void Check(int status)
    {
        if (status == 0) return;
        if (status == 3) throw new OutOfMemoryException();
        if (status is 2 or 13) throw new ArgumentException($"Invalid image or GDI+ parameter ({status}).");
        throw new ExternalException($"Windows image codec failed ({status}).");
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("gdiplus.dll")] private static extern int GdiplusStartup(out nuint token, in StartupInput input, nint output);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("gdiplus.dll")] private static extern void GdiplusShutdown(nuint token);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("gdiplus.dll")] private static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, nint pixels, out nint bitmap);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("gdiplus.dll")] private static extern int GdipGetImageGraphicsContext(nint bitmap, out nint graphics);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("gdiplus.dll")] private static extern int GdipDrawImageI(nint graphics, nint image, int x, int y);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("gdiplus.dll")] private static extern int GdipDeleteGraphics(nint graphics);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("gdiplus.dll")] private static extern int GdipLoadImageFromStream(nint input, out nint image);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("gdiplus.dll")] private static extern int GdipImageForceValidation(nint image);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("gdiplus.dll")] private static extern int GdipGetImageWidth(nint image, out int width);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("gdiplus.dll")] private static extern int GdipGetImageHeight(nint image, out int height);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("gdiplus.dll")] private static extern int GdipSaveImageToStream(nint image, nint output, in Guid clsid, nint parameters);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("gdiplus.dll")] private static extern int GdipDisposeImage(nint image);
}
