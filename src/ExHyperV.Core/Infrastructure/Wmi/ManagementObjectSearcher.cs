using System.Collections;

namespace System.Management;

internal sealed class ManagementObjectSearcher(ManagementScope scope, ObjectQuery query, EnumerationOptions? options = null) : IDisposable
{
    internal ManagementObjectSearcher(string scope, string query) : this(new ManagementScope(scope), new ObjectQuery(query)) { }
    internal ManagementObjectSearcher(string query) : this(@"\\.\root\cimv2", query) { }
    internal ManagementObjectCollection Get()
    {
        var config = options ?? new EnumerationOptions();
        return new ManagementObjectCollection(scope, scope.Query(query.QueryString, config), config.Timeout, config.Rewindable);
    }
    public void Dispose() { }
}

internal sealed class ManagementObjectCollection(ManagementScope scope, WbemHandle rows, TimeSpan timeout, bool rewindable) : IEnumerable<ManagementObject>, IDisposable
{
    private unsafe WbemHandle Clone() => rows.Use(p =>
    {
        if (!rewindable)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)WbemNative.Slot(p, 1))(p);
            return new WbemHandle(p);
        }
        nint copy = 0;
        WbemNative.Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)WbemNative.Slot(p, 6))(p, &copy));
        return scope.SecureEnumerator(copy);
    });
    private unsafe ManagementObject? Next(WbemHandle enumerator) => enumerator.Use(p =>
    {
        uint count = 0;
        nint obj = 0;
        int wait = timeout == TimeSpan.MaxValue ? -1 : (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        int hr = ((delegate* unmanaged[Stdcall]<nint, int, uint, nint*, uint*, int>)WbemNative.Slot(p, 4))(p, wait, 1, &obj, &count);
        WbemNative.Check(hr);
        if (count == 0 && hr == (int)ManagementStatus.Timedout) throw new ManagementException(ManagementStatus.Timedout);
        return count == 0 ? null : new ManagementObject(scope, new WbemHandle(obj));
    });
    public IEnumerator<ManagementObject> GetEnumerator()
    {
        using var enumerator = Clone();
        while (Next(enumerator) is { } obj) yield return obj;
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    internal int Count
    {
        get
        {
            int count = 0;
            foreach (var obj in this) { obj.Dispose(); count++; }
            return count;
        }
    }
    public void Dispose() => rows.Dispose();
}
