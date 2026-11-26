using System.Runtime.InteropServices;
using ExHyperV.Tools;

namespace ExHyperV.Services;

// IMAPI2FS ABI (imapi2fs.h).
internal static unsafe class ImapiImage
{
    private static readonly Guid DirectoryInterface = new("2C941FDC-975B-59BE-A960-9A2A262853A5");

    internal static NativeComInterface Create(string volume, bool udfOnly)
    {
        var image = NativeComInterface.Create(new("2C941FC5-975B-59BE-A960-9A2A262853A5"),
            new("2C941FE1-975B-59BE-A960-9A2A262853A5"));
        try
        {
            nint label = Marshal.StringToBSTR(volume);
            try
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)image.Slot(15))(image.Pointer, label));
            }
            finally { Marshal.FreeBSTR(label); }
            image.SetInt32(11, 0); // Unlimited FreeMediaBlocks.
            image.SetInt32(29, udfOnly ? 4 : 1 | 4); // FsiFileSystemISO9660 / FsiFileSystemUDF.
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    internal static NativeComInterface OpenOrCreateDirectory(NativeComInterface parent, string name)
    {
        nint path = Marshal.StringToBSTR(name);
        try
        {
            nint item = 0;
            var getItem = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)parent.Slot(20);
            int hr = getItem(parent.Pointer, path, &item);
            if (hr is unchecked((int)0xC0AAB118) or unchecked((int)0x80070002))
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)parent.Slot(23))(parent.Pointer, path));
                hr = getItem(parent.Pointer, path, &item);
            }
            Marshal.ThrowExceptionForHR(hr);
            using var child = new NativeComInterface(item);
            return NativeComInterface.Query(item, DirectoryInterface);
        }
        finally { Marshal.FreeBSTR(path); }
    }

    internal static void AddTree(NativeComInterface parent, string directory)
    {
        nint path = Marshal.StringToBSTR(directory);
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, short, int>)parent.Slot(25))(parent.Pointer, path, 0));
        }
        finally { Marshal.FreeBSTR(path); }
    }
}
