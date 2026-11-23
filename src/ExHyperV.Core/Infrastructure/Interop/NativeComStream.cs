using System.Runtime.InteropServices;

namespace ExHyperV.Tools;

internal static unsafe class NativeComStream
{
    internal static NativeComInterface Create()
    {
        Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(0, 1, out var pointer));
        return new(pointer);
    }

    internal static int Read(this NativeComInterface stream, byte[] buffer, int requested)
    {
        if ((uint)requested > (uint)buffer.Length) throw new ArgumentOutOfRangeException(nameof(requested));
        int count = 0;
        fixed (byte* data = buffer)
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, byte*, int, int*, int>)stream.Slot(3))(
                stream.Pointer, data, requested, &count));
        }
        return count;
    }

    internal static void Write(this NativeComInterface stream, byte[] buffer)
    {
        int count = 0;
        fixed (byte* data = buffer)
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, byte*, int, int*, int>)stream.Slot(4))(
                stream.Pointer, data, buffer.Length, &count));
        }
        if (count != buffer.Length) throw new IOException("Incomplete write to the image stream.");
    }

    internal static long Seek(this NativeComInterface stream, SeekOrigin origin)
    {
        long position = 0;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, long, uint, long*, int>)stream.Slot(5))(
            stream.Pointer, 0, (uint)origin, &position));
        return position;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32), DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CreateStreamOnHGlobal(nint memory, int deleteOnRelease, out nint stream);
}
