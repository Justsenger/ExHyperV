using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace System.Management;

// Keep COM proxies in one apartment. The UI may call from an STA and dispose a
// previously returned object there, while most service operations run on the pool.
internal static unsafe class WbemNative
{
    internal static readonly Guid ClassObjectId = new("dc12a681-737f-11cf-884d-00aa004b2e24");
    private static readonly MtaUsage Apartment = new();

    private sealed class MtaUsage : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal MtaUsage() : base(true)
        {
            Marshal.ThrowExceptionForHR(CoIncrementMTAUsage(out var cookie));
            SetHandle(cookie);
        }
        protected override bool ReleaseHandle() => CoDecrementMTAUsage(handle) >= 0;
    }

    internal static T InApartment<T>(Func<T> action)
    {
        GC.KeepAlive(Apartment);
        int hr = CoGetApartmentType(out int apartment, out _);
        Marshal.ThrowExceptionForHR(hr);
        if (apartment == 1) return action(); // APTTYPE_MTA
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { result.SetResult(action()); }
            catch (Exception ex) { result.SetException(ex); }
        });
        return result.Task.GetAwaiter().GetResult();
    }

    internal static nint Slot(nint self, int index) => (*(nint**)self)[index];
    internal static void Release(nint self) => ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(self, 2))(self);
    internal static WbemHandle Create(Guid clsid, Guid iid)
    {
        Check(CoCreateInstance(&clsid, 0, 1, &iid, out var result));
        return new(result);
    }

    internal static WbemHandle Query(nint source, Guid iid)
    {
        nint result = 0;
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(source, 0))(source, &iid, &result));
        return new(result);
    }

    internal static void Check(int hr)
    {
        if (hr >= 0) return;
        ManagementBaseObject? details = null;
        string? description = null;
        if (GetErrorInfo(0, out var info) == 0 && info != 0)
        {
            try
            {
                nint text = 0;
                if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(info, 5))(info, &text) >= 0)
                    description = TakeBstr(text);
                var id = ClassObjectId;
                nint obj = 0;
                if (((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(info, 0))(info, &id, &obj) >= 0)
                    details = new ManagementBaseObject(new WbemHandle(obj));
            }
            finally { Release(info); }
        }
        if (((uint)hr & 0xfffff000u) == 0x80041000u)
            throw new ManagementException((ManagementStatus)hr, description, details);
        details?.Dispose();
        if (hr == unchecked((int)0x80070005)) throw new UnauthorizedAccessException(description);
        throw new COMException(description ?? Marshal.GetPInvokeErrorMessage(hr), hr);
    }

    internal static string TakeBstr(nint value)
    {
        if (value == 0) return string.Empty;
        try { return Marshal.PtrToStringBSTR(value); }
        finally { Marshal.FreeBSTR(value); }
    }

    [DllImport("ole32", ExactSpelling = true)] private static extern int CoIncrementMTAUsage(out nint cookie);
    [DllImport("ole32", ExactSpelling = true)] private static extern int CoDecrementMTAUsage(nint cookie);
    [DllImport("ole32", ExactSpelling = true)] private static extern int CoGetApartmentType(out int type, out int qualifier);
    [DllImport("ole32", ExactSpelling = true)] private static extern int CoCreateInstance(Guid* clsid, nint outer, int context, Guid* iid, out nint obj);
    [DllImport("oleaut32", ExactSpelling = true)] private static extern int GetErrorInfo(uint reserved, out nint info);
}

internal sealed class WbemHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal WbemHandle(nint owned) : base(true) => SetHandle(owned);

    internal T Use<T>(Func<nint, T> action)
    {
        bool acquired = false;
        try
        {
            DangerousAddRef(ref acquired);
            return WbemNative.InApartment(() => action(handle));
        }
        finally { if (acquired) DangerousRelease(); }
    }

    protected override bool ReleaseHandle() =>
        WbemNative.InApartment(() => { WbemNative.Release(handle); return true; });
}

internal readonly ref struct WbemBstr(string? value)
{
    internal nint Pointer { get; } = value is null ? 0 : Marshal.StringToBSTR(value);
    public void Dispose()
    {
        if (Pointer != 0) Marshal.ZeroFreeBSTR(Pointer);
    }
}
