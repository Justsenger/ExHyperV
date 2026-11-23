using System.Runtime.InteropServices;

namespace ExHyperV.Tools;

// The connection and configuration ABIs come from netcon.h. Keep configuration
// references on the STA until readback or rollback has completed.
internal sealed unsafe class NetSharingConnections : IDisposable
{
    private readonly List<Connection> connections = [];
    internal IReadOnlyList<Connection> Items => connections;

    internal static NetSharingConnections Open()
    {
        using var manager = NativeComInterface.Create(new("5C63C1AD-3956-4FF8-8486-40034758315B"),
            new("C08956B7-1CD3-11D1-B1C5-00805FC1270E"));
        using var collection = manager.GetInterface(11); // get_EnumEveryConnection
        using var unknown = collection.GetInterface(7);  // get__NewEnum returns IUnknown
        using var enumerator = NativeComInterface.Query(unknown.Pointer, new("00020404-0000-0000-C000-000000000046"));
        var next = (delegate* unmanaged[Stdcall]<nint, uint, Variant*, uint*, int>)enumerator.Slot(3);
        var result = new NetSharingConnections();
        try
        {
            while (true)
            {
                Variant value = default;
                uint fetched = 0;
                try
                {
                    int hr = next(enumerator.Pointer, 1, &value, &fetched);
                    Marshal.ThrowExceptionForHR(hr);
                    if (fetched == 0) break;
                    if (value.Type is not (9 or 13) || value.Pointer == 0)
                        throw new IcsException("ICS returned an invalid connection interface.");
                    using var connection = NativeComInterface.Query(value.Pointer, new("C08956A1-1CD3-11D1-B1C5-00805FC1270E"));
                    var props = manager.GetInterface(12, connection.Pointer);
                    try
                    {
                        Guid id = Guid.Parse(ReadString(props, 7));
                        var configuration = manager.GetInterface(10, connection.Pointer);
                        result.connections.Add(new(id, props, configuration));
                    }
                    catch { props.Dispose(); throw; }
                }
                finally { VariantClear(&value); }
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    public void Dispose()
    {
        foreach (var item in connections) item.Dispose();
        connections.Clear();
    }

    private static string ReadString(NativeComInterface props, int slot)
    {
        nint text = 0;
        try
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)props.Slot(slot))(props.Pointer, &text));
            return text == 0 ? string.Empty : Marshal.PtrToStringBSTR(text);
        }
        finally { Marshal.FreeBSTR(text); }
    }

    internal sealed class Connection(Guid id, NativeComInterface props, NativeComInterface config) : IDisposable
    {
        internal Guid Id => id;
        internal string Name => ReadString(props, 8);
        internal bool SharingEnabled
        {
            get
            {
                short value = 0; // VARIANT_BOOL
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, short*, int>)config.Slot(7))(config.Pointer, &value));
                return value != 0;
            }
        }
        internal int SharingConnectionType => config.GetInt32(8);
        internal void DisableSharing() =>
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)config.Slot(9))(config.Pointer));
        internal void EnableSharing(int type) => config.SetInt32(10, type);
        public void Dispose() { config.Dispose(); props.Dispose(); }
    }

    // The largest VARIANT union member contains two pointers (BRECORD).
    [StructLayout(LayoutKind.Sequential)]
    private struct Variant
    {
        internal ushort Type;
        private ushort reserved1, reserved2, reserved3;
        internal nint Pointer;
        private nint recordInfo;
    }
    [DllImport("oleaut32.dll", ExactSpelling = true)] private static extern int VariantClear(Variant* value);
}
