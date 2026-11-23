using System.Globalization;

namespace System.Management;

internal sealed unsafe class ManagementPath
{
    internal string Path { get; }
    internal string Server { get; }
    internal string NamespacePath { get; }
    internal string RelativePath { get; }
    internal string ClassName { get; }
    internal bool IsClass { get; }
    internal bool IsInstance { get; }

    internal ManagementPath(string? path)
    {
        (Path, Server, NamespacePath, RelativePath, ClassName, IsClass, IsInstance) =
            WbemNative.InApartment(() => Parse(path ?? ""));
    }

    private static (string, string, string, string, string, bool, bool) Parse(string path)
    {
        if (path.Length == 0) return ("", "", "", "", "", false, false);
        // The native WMI grammar handles complex keys, quoted backslashes and embedded colon characters.
        using var parser = WbemNative.Create(new("cf4cc405-e2c5-4ddd-b3ce-5e7582d8c9fa"), new("3bc15af2-736c-477e-9e51-238af8667dcc"));
        return parser.Use(p =>
        {
            uint flags = path.Equals("root", StringComparison.OrdinalIgnoreCase) ? 12u : 4u;
            fixed (char* input = path)
                WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, uint, char*, int>)WbemNative.Slot(p, 3))(p, flags, input));
            ulong info = 0;
            WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, uint, ulong*, int>)WbemNative.Slot(p, 5))(p, 0, &info));
            string ns = Read(p, 4, 16);
            string relative = Read(p, 4, 2);
            return (ns.Length == 0 ? relative : Read(p, 4, 4), Read(p, 7), ns, relative, Read(p, 21),
                (info & 4) != 0, (info & 8) != 0);
        });
    }

    private static string Read(nint parser, int slot, int flags = 0)
    {
        int Get(uint* count, char* chars) => slot == 4
            ? ((delegate* unmanaged[Stdcall]<nint, int, uint*, char*, int>)WbemNative.Slot(parser, slot))(parser, flags, count, chars)
            : ((delegate* unmanaged[Stdcall]<nint, uint*, char*, int>)WbemNative.Slot(parser, slot))(parser, count, chars);
        uint length = 0;
        int hr = Get(&length, null);
        // WBEM_E_INVALID_OBJECT_PATH here denotes an absent component of a
        // successfully parsed path (e.g. requesting the class of a namespace).
        if (hr == unchecked((int)0x8004103a) || hr == unchecked((int)0x80041008) || hr == (int)ManagementStatus.NotFound) return "";
        WbemNative.Check(hr);
        if (length < 2) return "";
        var buffer = new char[checked((int)length)];
        fixed (char* chars = buffer) WbemNative.Check(Get(&length, chars));
        return new string(buffer, 0, Array.IndexOf(buffer, '\0'));
    }

    public override string ToString() => Path;
}

internal enum ManagementStatus
{
    Timedout = 0x40004, NotFound = unchecked((int)0x80041002), AccessDenied = unchecked((int)0x80041003),
    ProviderFailure = unchecked((int)0x80041004), InvalidParameter = unchecked((int)0x80041008),
    NotSupported = unchecked((int)0x8004100c), InvalidNamespace = unchecked((int)0x8004100e),
    InvalidObject = unchecked((int)0x8004100f), InvalidClass = unchecked((int)0x80041010),
    ProviderLoadFailure = unchecked((int)0x80041013), InitializationFailure = unchecked((int)0x80041014),
    TransportFailure = unchecked((int)0x80041015), InvalidQuery = unchecked((int)0x80041017),
    ProviderNotCapable = unchecked((int)0x80041024), ValueOutOfRange = unchecked((int)0x8004102b),
    InvalidMethodParameters = unchecked((int)0x8004102f), ShuttingDown = unchecked((int)0x80041033),
    ServerTooBusy = unchecked((int)0x80041045), MethodNotImplemented = unchecked((int)0x80041055),
    PrivilegeNotHeld = unchecked((int)0x80041062)
}

internal sealed class ManagementException : Exception
{
    internal ManagementException(ManagementStatus code, string? description = null, ManagementBaseObject? information = null)
        : base(string.IsNullOrEmpty(description) ? $"{code} (0x{(uint)code:X8})" : description)
    {
        HResult = (int)code;
        ErrorCode = code;
        ErrorInformation = information;
    }
    internal ManagementStatus ErrorCode { get; }
    internal ManagementBaseObject? ErrorInformation { get; }
}

internal static class ManagementDateTimeConverter
{
    internal static DateTime ToDateTime(string dmtfDate)
    {
        if (dmtfDate is null || dmtfDate.Length != 25 || dmtfDate[14] != '.' ||
            (dmtfDate[21] is not ('+' or '-') && !dmtfDate.EndsWith("***", StringComparison.Ordinal)))
            throw new ArgumentOutOfRangeException(nameof(dmtfDate));
        int Part(int start, int length, int fallback)
        {
            var digits = dmtfDate.AsSpan(start, length);
            return digits.IndexOfAnyExcept('*') == -1 ? fallback : int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        }
        try
        {
            var wall = new DateTime(Part(0, 4, 1), Part(4, 2, 1), Part(6, 2, 1), Part(8, 2, 0),
                Part(10, 2, 0), Part(12, 2, 0), DateTimeKind.Local).AddTicks(Part(15, 6, 0) * 10);
            int localMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(wall).TotalMinutes;
            int minutes = dmtfDate.EndsWith("***", StringComparison.Ordinal) ? 0
                : Part(22, 3, 0) * (dmtfDate[21] == '-' ? -1 : 1);
            return wall.AddMinutes(localMinutes - minutes);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentOutOfRangeException)
        { throw new ArgumentOutOfRangeException(nameof(dmtfDate), ex.Message); }
    }
}
