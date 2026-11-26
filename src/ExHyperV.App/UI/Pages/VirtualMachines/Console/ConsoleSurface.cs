using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

internal sealed partial class ConsoleSurface : UserControl
{
    private static readonly Guid InputCursorStaticsInteropIid = new("ac6f5065-90c4-46ce-beb7-05e138e54117");
    private readonly InputCursor viewerCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private InputCursor? blankCursor;
    private sealed class CursorHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly bool icon;
        internal CursorHandle(nint cursor, bool icon) : base(true) { this.icon = icon; SetHandle(cursor); }
        protected override bool ReleaseHandle() => icon ? DestroyIcon(handle) : DestroyCursor(handle);
    }

    private CursorHandle? blankHandle, remoteCursorHandle;

    public ConsoleSurface()
    {
        Unloaded += (_, _) =>
        {
            ResetCursor();
            blankCursor = null;
            blankHandle?.Dispose();
            blankHandle = null;
        };
    }

    public void SetBlankCursor(bool enabled)
    {
        var target = enabled ? (blankCursor ??= CreateBlankCursor()) : viewerCursor;
        if (!ReferenceEquals(ProtectedCursor, target))
            ProtectedCursor = target;
        DestroyRemoteHandle();
    }

    public void SetRemoteCursor(int width, int height, int hotX, int hotY, byte[]? bgraPixels)
    {
        if (bgraPixels == null || width <= 0 || height <= 0 || width > 384 || height > 384
            || hotX < 0 || hotY < 0 || hotX >= width || hotY >= height
            || bgraPixels.Length != width * height * 4)
        {
            SetBlankCursor(true);
            return;
        }

        var (handle, cursor) = CreateRemoteCursor(width, height, hotX, hotY, bgraPixels);
        if (cursor == null || handle == 0)
        {
            if (handle != 0) DestroyIcon(handle);
            ResetCursor();
            return;
        }

        var owner = new CursorHandle(handle, true);
        try { ProtectedCursor = cursor; }
        catch { owner.Dispose(); throw; }
        DestroyRemoteHandle();
        remoteCursorHandle = owner;
    }

    public void ResetCursor()
    {
        ProtectedCursor = viewerCursor;
        DestroyRemoteHandle();
    }

    private void DestroyRemoteHandle()
    {
        remoteCursorHandle?.Dispose();
        remoteCursorHandle = null;
    }

    private InputCursor? CreateBlankCursor()
    {
        try
        {
            int width = Math.Max(1, GetSystemMetrics(13));
            int height = Math.Max(1, GetSystemMetrics(14));
            int stride = ((width + 15) / 16) * 2;
            byte[] andMask = new byte[stride * height];
            byte[] xorMask = new byte[stride * height];
            Array.Fill(andMask, (byte)0xFF);

            var owner = new CursorHandle(CreateCursor(0, 0, 0, width, height, andMask, xorMask), false);
            var cursor = CreateInputCursor(owner.DangerousGetHandle());
            if (cursor == null) owner.Dispose();
            else blankHandle = owner;
            return cursor;
        }
        catch
        {
            return null;
        }
    }

    private static unsafe (nint Handle, InputCursor? Cursor) CreateRemoteCursor(int width, int height, int hotX, int hotY, byte[] bgraPixels)
    {
        nint colorBitmap = 0, maskBitmap = 0, cursorHandle = 0;
        try
        {
            var header = new BitmapInfoHeader
            {
                Size = (uint)sizeof(BitmapInfoHeader),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0
            };
            colorBitmap = CreateDIBSection(0, ref header, 0, out nint bitsPtr, 0, 0);
            if (colorBitmap == 0 || bitsPtr == 0) return (0, null);

            int maskStride = ((width + 15) / 16) * 2;
            byte[] andMask = new byte[maskStride * height];
            byte* dest = (byte*)bitsPtr;
            for (int y = 0; y < height; y++)
            {
                int maskRow = y * maskStride;
                int pixelRow = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int i = pixelRow + x * 4;
                    byte a = bgraPixels[i + 3];
                    if (a == 0)
                    {
                        dest[i] = dest[i + 1] = dest[i + 2] = dest[i + 3] = 0;
                        andMask[maskRow + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                    }
                    else if (a == 255)
                    {
                        dest[i] = bgraPixels[i];
                        dest[i + 1] = bgraPixels[i + 1];
                        dest[i + 2] = bgraPixels[i + 2];
                        dest[i + 3] = 255;
                    }
                    else
                    {
                        dest[i] = (byte)((bgraPixels[i] * a + 127) / 255);
                        dest[i + 1] = (byte)((bgraPixels[i + 1] * a + 127) / 255);
                        dest[i + 2] = (byte)((bgraPixels[i + 2] * a + 127) / 255);
                        dest[i + 3] = a;
                    }
                }
            }

            maskBitmap = CreateBitmap(width, height, 1, 1, andMask);
            if (maskBitmap == 0) return (0, null);

            var info = new IconInfo { IconFlag = 0, HotX = hotX, HotY = hotY, Mask = maskBitmap, Color = colorBitmap };
            cursorHandle = CreateIconIndirect(ref info);
            if (cursorHandle == 0) return (0, null);

            var inputCursor = CreateInputCursor(cursorHandle);
            if (inputCursor == null)
            {
                DestroyIcon(cursorHandle);
                return (0, null);
            }
            return (cursorHandle, inputCursor);
        }
        catch
        {
            if (cursorHandle != 0) DestroyIcon(cursorHandle);
            return (0, null);
        }
        finally
        {
            if (maskBitmap != 0) DeleteObject(maskBitmap);
            if (colorBitmap != 0) DeleteObject(colorBitmap);
        }
    }

    private static unsafe InputCursor? CreateInputCursor(nint handle)
    {
        if (handle == 0) return null;
        try
        {
            using var factory = ActivationFactory.Get("Microsoft.UI.Input.InputCursor", InputCursorStaticsInteropIid);
            nint factoryPtr = factory.ThisPtr;
            nint cursorAbi = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(nint**)factoryPtr)[6])(
                factoryPtr, handle, &cursorAbi);
            if (hr < 0 || cursorAbi == 0) return null;
            try
            {
                return InputCursor.FromAbi(cursorAbi);
            }
            finally
            {
                Marshal.Release(cursorAbi);
            }
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int IconFlag, HotX, HotY;
        public nint Mask, Color;
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    [LibraryImport("user32.dll")]
    private static partial nint CreateCursor(
        nint hInst, int xHotSpot, int yHotSpot, int nWidth, int nHeight,
        ReadOnlySpan<byte> pvAndPlane, ReadOnlySpan<byte> pvXorPlane);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconIndirect(ref IconInfo piconinfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint hIcon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyCursor(nint cursor);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(
        nint hdc, ref BitmapInfoHeader pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(
        int nWidth, int nHeight, uint nPlanes, uint nBitCount, ReadOnlySpan<byte> lpBits);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint ho);
}
