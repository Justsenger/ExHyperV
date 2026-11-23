using System.Globalization;
using System.Runtime.InteropServices;

namespace System.Management;

internal enum CimType
{
    None = 0, SInt16 = 2, SInt32 = 3, Real32 = 4, Real64 = 5, String = 8,
    Boolean = 11, Object = 13, SInt8 = 16, UInt8 = 17, UInt16 = 18,
    UInt32 = 19, SInt64 = 20, UInt64 = 21, DateTime = 101, Reference = 102, Char16 = 103
}

// VARIANT is 24 bytes on both supported (64-bit) architectures.
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal unsafe struct WbemVariant
{
    [FieldOffset(0)] internal ushort Type;
    [FieldOffset(8)] internal long Data;
    private const ushort ArrayFlag = 0x2000;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal void Clear()
    {
        fixed (WbemVariant* self = &this) WbemNative.Check(VariantClear(self));
    }

    internal static WbemVariant From(CimType type, bool array, object? value)
    {
        if (value is null || value == DBNull.Value) return new() { Type = 1 };
        ushort vt = VariantType(type);
        WbemVariant result = new() { Type = vt };
        if (!array)
        {
            Write(type, &result.Data, value);
            return result;
        }

        var input = (Array)value;
        if (input.Rank != 1) throw new ArgumentException("WMI requires a one-dimensional array.");
        nint buffer = SafeArrayCreateVector(vt, 0, checked((uint)input.Length));
        if (buffer == 0) throw new OutOfMemoryException();
        result.Type |= ArrayFlag;
        result.Data = buffer;
        try
        {
            WbemNative.Check(SafeArrayAccessData(buffer, out var raw));
            try
            {
                uint stride = SafeArrayGetElemsize(buffer);
                for (int i = 0; i < input.Length; i++)
                    Write(type, (byte*)raw + i * stride, input.GetValue(i + input.GetLowerBound(0)));
            }
            finally { WbemNative.Check(SafeArrayUnaccessData(buffer)); }
            return result;
        }
        catch { result.Clear(); throw; }
    }

    internal object? Read(CimType type)
    {
        if (Type is 0 or 1) return null;
        if ((Type & ~ArrayFlag) != VariantType(type))
            throw new InvalidDataException($"Unexpected VARIANT 0x{Type:X} for CIM {type}.");
        if ((Type & ArrayFlag) != 0) return ReadArray((nint)Data, type);
        fixed (long* value = &Data) return ReadScalar(type, value);
    }

    internal static Array ReadArray(nint buffer, CimType type)
    {
        if (SafeArrayGetDim(buffer) != 1) throw new InvalidDataException("Invalid WMI array dimension.");
        WbemNative.Check(SafeArrayGetLBound(buffer, 1, out int first));
        WbemNative.Check(SafeArrayGetUBound(buffer, 1, out int last));
        int size = checked(last - first + 1);
        Array result = type switch
        {
            CimType.Boolean => new bool[size], CimType.Real32 => new float[size], CimType.Real64 => new double[size],
            CimType.SInt8 => new sbyte[size], CimType.UInt8 => new byte[size], CimType.SInt16 => new short[size],
            CimType.UInt16 => new ushort[size], CimType.SInt32 => new int[size], CimType.UInt32 => new uint[size],
            CimType.SInt64 => new long[size], CimType.UInt64 => new ulong[size], CimType.Char16 => new char[size],
            CimType.Object => new ManagementBaseObject[size], _ => new string[size]
        };
        WbemNative.Check(SafeArrayAccessData(buffer, out var raw));
        try
        {
            // The CLR also accepts sbyte[] in a byte[] type test. WMI stores
            // CIM_SINT8 as 16-bit elements, so only CIM_UINT8 can bulk-copy.
            if (type == CimType.UInt8) new ReadOnlySpan<byte>((void*)raw, size).CopyTo((byte[])result);
            else
            {
                uint stride = SafeArrayGetElemsize(buffer);
                for (int i = 0; i < size; i++) result.SetValue(ReadScalar(type, (byte*)raw + i * stride), i);
            }
        }
        finally { WbemNative.Check(SafeArrayUnaccessData(buffer)); }
        return result;
    }

    private static ushort VariantType(CimType type) => type switch
    {
        CimType.SInt8 or CimType.SInt16 or CimType.Char16 => 2,
        CimType.UInt16 or CimType.SInt32 or CimType.UInt32 => 3,
        CimType.Real32 => 4, CimType.Real64 => 5,
        CimType.SInt64 or CimType.UInt64 or CimType.String or CimType.DateTime or CimType.Reference => 8,
        CimType.Boolean => 11, CimType.Object => 13, CimType.UInt8 => 17,
        _ => throw new InvalidDataException($"Unknown CIM type {type}.")
    };

    private static object? ReadScalar(CimType type, void* p) => type switch
    {
        CimType.SInt8 => (sbyte)*(short*)p, CimType.UInt8 => *(byte*)p,
        CimType.SInt16 => *(short*)p, CimType.UInt16 => (ushort)*(int*)p,
        CimType.SInt32 => *(int*)p, CimType.UInt32 => *(uint*)p,
        CimType.SInt64 => long.Parse(Marshal.PtrToStringBSTR(*(nint*)p), Invariant),
        CimType.UInt64 => ulong.Parse(Marshal.PtrToStringBSTR(*(nint*)p), Invariant),
        CimType.Char16 => *(char*)p, CimType.Real32 => *(float*)p, CimType.Real64 => *(double*)p,
        CimType.Boolean => *(short*)p != 0,
        CimType.Object => *(nint*)p == 0 ? null : new ManagementBaseObject(WbemNative.Query(*(nint*)p, WbemNative.ClassObjectId)),
        _ => *(nint*)p == 0 ? null : Marshal.PtrToStringBSTR(*(nint*)p)
    };

    private static void Write(CimType type, void* p, object? value)
    {
        switch (type)
        {
            case CimType.SInt8: *(short*)p = Convert.ToSByte(value, Invariant); break;
            case CimType.UInt8: *(byte*)p = Convert.ToByte(value, Invariant); break;
            case CimType.SInt16: *(short*)p = Convert.ToInt16(value, Invariant); break;
            case CimType.UInt16: *(int*)p = Convert.ToUInt16(value, Invariant); break;
            case CimType.SInt32: *(int*)p = Convert.ToInt32(value, Invariant); break;
            case CimType.UInt32: *(uint*)p = Convert.ToUInt32(value, Invariant); break;
            case CimType.Char16: *(char*)p = Convert.ToChar(value, Invariant); break;
            case CimType.Real32: *(float*)p = Convert.ToSingle(value, Invariant); break;
            case CimType.Real64: *(double*)p = Convert.ToDouble(value, Invariant); break;
            case CimType.Boolean: *(short*)p = Convert.ToBoolean(value, Invariant) ? (short)-1 : (short)0; break;
            case CimType.Object:
                *(nint*)p = value is null ? 0 : ((ManagementBaseObject)value).Native.Use(obj =>
                {
                    ((delegate* unmanaged[Stdcall]<nint, uint>)WbemNative.Slot(obj, 1))(obj);
                    return obj;
                });
                break;
            default:
                string? text = type switch
                {
                    CimType.SInt64 => Convert.ToInt64(value, Invariant).ToString(Invariant),
                    CimType.UInt64 => Convert.ToUInt64(value, Invariant).ToString(Invariant),
                    _ => value?.ToString()
                };
                *(nint*)p = text is null ? 0 : Marshal.StringToBSTR(text);
                break;
        }
    }

    [DllImport("oleaut32", ExactSpelling = true)] private static extern int VariantClear(WbemVariant* variant);
    [DllImport("oleaut32", ExactSpelling = true)] private static extern nint SafeArrayCreateVector(ushort vt, int lowerBound, uint count);
    [DllImport("oleaut32", ExactSpelling = true)] private static extern uint SafeArrayGetDim(nint array);
    [DllImport("oleaut32", ExactSpelling = true)] private static extern uint SafeArrayGetElemsize(nint array);
    [DllImport("oleaut32", ExactSpelling = true)] private static extern int SafeArrayAccessData(nint array, out nint data);
    [DllImport("oleaut32", ExactSpelling = true)] private static extern int SafeArrayUnaccessData(nint array);
    [DllImport("oleaut32", ExactSpelling = true)] private static extern int SafeArrayGetLBound(nint array, uint dimension, out int bound);
    [DllImport("oleaut32", ExactSpelling = true)] private static extern int SafeArrayGetUBound(nint array, uint dimension, out int bound);
    [DllImport("oleaut32", ExactSpelling = true)] internal static extern int SafeArrayDestroy(nint array);
}
