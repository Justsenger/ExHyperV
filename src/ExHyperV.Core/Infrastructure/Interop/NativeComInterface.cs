using System.Runtime.InteropServices;

namespace ExHyperV.Tools;

// An owned interface reference. Keep it on the calling COM apartment and dispose
// it there. Slots in the typed callers follow the Windows SDK C interface layouts.
internal sealed unsafe class NativeComInterface(nint pointer) : IDisposable
{
    internal nint Pointer { get; private set; } = pointer != 0
        ? pointer : throw new COMException("The COM server returned a null interface.", unchecked((int)0x80004003));

    internal nint Slot(int index) => (*(nint**)Pointer)[index];

    internal static NativeComInterface Create(Guid clsid, Guid iid)
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, 1, in iid, out var instance));
        return new(instance);
    }

    internal static NativeComInterface Query(nint source, Guid iid)
    {
        nint result = 0;
        var query = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)source)[0];
        Marshal.ThrowExceptionForHR(query(source, &iid, &result));
        return new(result);
    }

    internal NativeComInterface GetInterface(int slot)
    {
        nint result = 0;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(slot))(Pointer, &result));
        return new(result);
    }

    internal NativeComInterface GetInterface(int slot, nint argument)
    {
        nint result = 0;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(slot))(Pointer, argument, &result));
        return new(result);
    }

    internal int GetInt32(int slot)
    {
        int value = 0;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(slot))(Pointer, &value));
        return value;
    }

    internal void SetInt32(int slot, int value) =>
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(slot))(Pointer, value));

    public void Dispose()
    {
        if (Pointer == 0) return;
        ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(2))(Pointer);
        Pointer = 0;
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
}

internal readonly ref struct NativeComApartment
{
    private readonly bool initialized;

    internal NativeComApartment(bool sta)
    {
        int hr = CoInitializeEx(0, sta ? 2u : 0u);
        // ISO writing also supports a caller that already initialized the other apartment.
        if (hr != unchecked((int)0x80010106) || sta) Marshal.ThrowExceptionForHR(hr);
        initialized = hr >= 0;
    }

    public void Dispose()
    {
        if (initialized) CoUninitialize();
    }

    [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
}
