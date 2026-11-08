using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExHyperV.Tools;

/// <summary>读取 DXGI DedicatedVideoMemory；D3DKMT 仅用于把适配器 LUID 精确关联到 PnP 实例。</summary>
internal static class DxgiGpuMemory
{
    internal static unsafe IReadOnlyDictionary<string, ulong?> ReadAll()
    {
        var readings = new List<(string InstanceId, ulong Bytes)>();
        nint factory = 0;
        try
        {
            var iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
            Marshal.ThrowExceptionForHR(Native.CreateDXGIFactory1(ref iid, out factory));
            if (factory == 0) throw new InvalidDataException("DXGI 未返回有效工厂对象。");
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            for (uint index = 0; index < 256; index++)
            {
                nint adapter = 0;
                try
                {
                    int result = enumerate(factory, index, &adapter);
                    if (result == unchecked((int)0x887A0002)) return MergeReadings(readings); // DXGI_ERROR_NOT_FOUND
                    Marshal.ThrowExceptionForHR(result);
                    if (adapter == 0) throw new InvalidDataException("DXGI 未返回有效适配器。");
                    var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)(*(nint**)adapter)[10];
                    AdapterDescription description = default;
                    int descriptionResult = getDescription(adapter, &description);
                    if (descriptionResult < 0)
                    {
                        Debug.WriteLine($"[DXGI GPU memory] GetDesc1: 0x{descriptionResult:X8}");
                        continue;
                    }
                    if ((description.Flags & 3) != 0) continue; // REMOTE / SOFTWARE，不冒充物理显卡
                    string? instanceId = ReadInstanceId(description.AdapterLuid);
                    if (instanceId != null)
                        readings.Add((instanceId, (ulong)description.DedicatedVideoMemory));
                }
                finally { Release(adapter); }
            }
            throw new InvalidDataException("DXGI 适配器枚举超过合理上限。");
        }
        catch (Exception error)
        {
            Debug.WriteLine($"[DXGI GPU memory] {error.Message}");
            // 枚举未完成时不发布可能漏掉冲突项的部分结果；不回退注册表或按名称猜测。
            return new Dictionary<string, ulong?>(StringComparer.OrdinalIgnoreCase);
        }
        finally { Release(factory); }
    }

    internal static IReadOnlyDictionary<string, ulong?> MergeReadings(IEnumerable<(string InstanceId, ulong Bytes)> readings)
    {
        var result = new Dictionary<string, ulong?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, bytes) in readings)
        {
            if (string.IsNullOrWhiteSpace(id)) continue;
            // 同一 PnP 实例可以有多个 DXGI LUID；一致值归并，冲突保持未知，不能相加或选首项。
            if (result.TryGetValue(id, out ulong? previous))
                result[id] = previous == bytes ? previous : null;
            else result.Add(id, bytes);
        }
        return result;
    }

    private static unsafe string? ReadInstanceId(Luid luid)
    {
        var open = new OpenAdapter { AdapterLuid = luid };
        int status = Native.D3DKMTOpenAdapterFromLuid(ref open);
        if (status < 0) return null;
        try
        {
            uint count = 0;
            var query = new QueryAdapter { Handle = open.Handle, Type = 30 /* PHYSICALADAPTERCOUNT */,
                Data = (nint)(&count), Size = sizeof(uint) };
            if (Native.D3DKMTQueryAdapterInfo(ref query) < 0 || count != 1)
                return null; // 联合适配器的容量不能直接当作其中某张物理卡的容量。

            const int capacity = 1024;
            char* buffer = stackalloc char[capacity];
            new Span<char>(buffer, capacity).Clear();
            uint length = capacity;
            var pnp = new PhysicalAdapterPnpKey { KeyType = 1 /* HARDWARE */,
                Destination = (nint)buffer, CharacterCount = (nint)(&length) };
            query.Type = 41; // PHYSICALADAPTERPNPKEY；公开 WDDM 2.2 查询，不读取注册表容量。
            query.Data = (nint)(&pnp);
            query.Size = (uint)sizeof(PhysicalAdapterPnpKey);
            status = Native.D3DKMTQueryAdapterInfo(ref query);
            if (status < 0 || length < 2 || length > capacity || buffer[length - 1] != '\0') return null;
            var key = new ReadOnlySpan<char>(buffer, (int)length - 1);
            if (key.Contains('\0')) return null;
            return InstanceIdFromHardwareKey(key.ToString());
        }
        finally
        {
            uint handle = open.Handle;
            int close = Native.D3DKMTCloseAdapter(ref handle);
            if (close < 0) Debug.WriteLine($"[DXGI GPU memory] D3DKMTCloseAdapter: 0x{close:X8}");
        }
    }

    internal static string? InstanceIdFromHardwareKey(string key)
    {
        const string prefix = @"\REGISTRY\MACHINE\SYSTEM\";
        const string suffix = @"\Device Parameters";
        if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return null;
        string relative = key[prefix.Length..^suffix.Length];
        int separator = relative.IndexOf('\\');
        if (separator < 0) return null;
        string controlSet = relative[..separator];
        if (!controlSet.Equals("CurrentControlSet", StringComparison.OrdinalIgnoreCase) &&
            !(controlSet.Length == 13 && controlSet.StartsWith("ControlSet", StringComparison.OrdinalIgnoreCase) &&
              controlSet.AsSpan(10).IndexOfAnyExceptInRange('0', '9') < 0)) return null;
        relative = relative[(separator + 1)..];
        if (!relative.StartsWith(@"Enum\", StringComparison.OrdinalIgnoreCase)) return null;
        string id = relative[5..];
        var parts = id.Split('\\');
        return parts.Length == 3 && parts.All(part => !string.IsNullOrWhiteSpace(part)) ? id : null;
    }

    private static unsafe void Release(nint instance)
    {
        if (instance != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)instance)[2])(instance);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid { internal uint Low; internal int High; }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterDescription
    {
        internal fixed char Description[128];
        internal uint VendorId, DeviceId, SubsystemId, Revision;
        internal nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        internal Luid AdapterLuid;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenAdapter { internal Luid AdapterLuid; internal uint Handle; }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapter { internal uint Handle, Type; internal nint Data; internal uint Size; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PhysicalAdapterPnpKey { internal uint Index, KeyType; internal nint Destination, CharacterCount; }

    private static class Native
    {
        [DllImport("dxgi.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int CreateDXGIFactory1(ref Guid iid, out nint factory);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapter adapter);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int D3DKMTQueryAdapterInfo(ref QueryAdapter query);
        [DllImport("gdi32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int D3DKMTCloseAdapter(ref uint handle);
    }
}
