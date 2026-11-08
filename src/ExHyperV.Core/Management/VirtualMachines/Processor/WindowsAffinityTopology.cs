using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExHyperV.Services;

// 只采集编号的拓扑含义。忽略缓存、实时负载、保留字节和记录返回顺序。
internal static class WindowsAffinityTopology
{
    internal static string ReadFingerprint()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException("亲和性环境核实需要 64 位进程。");
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint length = 0;
            GetLogicalProcessorInformationEx(0xffff, IntPtr.Zero, ref length);
            int error = Marshal.GetLastWin32Error();
            if (error != 122) throw new Win32Exception(error, "无法查询处理器拓扑长度。");
            if (length is < 8 or > 4 * 1024 * 1024) throw new InvalidOperationException("处理器拓扑长度无效。");
            uint allocated = length;
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)length));
            try
            {
                if (!GetLogicalProcessorInformationEx(0xffff, buffer, ref length))
                {
                    error = Marshal.GetLastWin32Error();
                    if (error == 122) continue;
                    throw new Win32Exception(error, "无法读取处理器拓扑。");
                }
                if (length > allocated) throw new InvalidOperationException("处理器拓扑超出读取缓冲区。");
                byte[] bytes = new byte[length];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                return Fingerprint(bytes);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidOperationException("处理器拓扑持续变化，无法确认保存环境。");
    }

    internal static string Fingerprint(ReadOnlySpan<byte> bytes)
    {
        var active = new Dictionary<ushort, ulong>();
        var coreCoverage = new HashSet<(ushort, int)>();
        var packageCoverage = new HashSet<(ushort, int)>();
        var nodeCoverage = new HashSet<(ushort, int)>();
        var records = new List<string>();
        var nodes = new Dictionary<uint, string>();
        for (int offset = 0; offset < bytes.Length;)
        {
            if (bytes.Length - offset < 8) throw Invalid();
            uint relation = U32(bytes, offset), size = U32(bytes, offset + 4);
            if (size < 8 || size > bytes.Length - offset) throw Invalid();
            var row = bytes.Slice(offset, (int)size);
            if (relation == 4)
            {
                if (row.Length < 32 || active.Count != 0) throw Invalid();
                int count = U16(row, 10);
                if (count == 0 || U16(row, 8) < count || row.Length != 32 + count * 48) throw Invalid();
                for (ushort group = 0; group < count; group++)
                {
                    int start = 32 + group * 48;
                    ulong mask = U64(row, start + 40);
                    if (mask == 0 || System.Numerics.BitOperations.PopCount(mask) != row[start + 1]) throw Invalid();
                    active.Add(group, mask);
                    records.Add(FormattableString.Invariant($"active:{group}:{mask:X16}"));
                }
            }
            else if (relation is 0 or 3 or 1 or 6)
            {
                if (row.Length < 48) throw Invalid();
                int count = U16(row, 30);
                if (relation is 1 or 6 && count == 0 && row.Length == 48) count = 1; // 旧 NUMA 结构的保留位为零。
                if (count == 0 || row.Length != 32 + count * 16) throw Invalid();
                var masks = new List<(ushort Group, ulong Mask)>();
                for (int i = 0; i < count; i++)
                {
                    ulong mask = U64(row, 32 + i * 16);
                    ushort group = U16(row, 40 + i * 16);
                    if (mask == 0 || masks.Any(m => m.Group == group)) throw Invalid();
                    masks.Add((group, mask));
                }
                string mapping = string.Join(",", masks.OrderBy(m => m.Group).Select(m => FormattableString.Invariant($"{m.Group}:{m.Mask:X16}")));
                if (relation is 1 or 6)
                {
                    uint node = U32(row, 8);
                    // RelationAll 在不同系统返回旧或扩展 NUMA 记录；相同关系只计一次。
                    if (nodes.TryGetValue(node, out var prior))
                    {
                        if (prior != mapping) throw Invalid();
                    }
                    else { nodes.Add(node, mapping); AddCoverage(nodeCoverage, masks); }
                }
                else
                {
                    records.Add(relation == 0 ? FormattableString.Invariant($"core:{row[8] & 1}:{row[9]}:{mapping}") : "package:" + mapping);
                    AddCoverage(relation == 0 ? coreCoverage : packageCoverage, masks);
                }
            }
            offset += (int)size;
        }
        var all = new HashSet<(ushort, int)>();
        AddCoverage(all, active.Select(p => (p.Key, p.Value)));
        if (all.Count == 0 || !all.SetEquals(coreCoverage) || !all.SetEquals(packageCoverage) || !all.SetEquals(nodeCoverage)) throw Invalid();
        records.AddRange(nodes.Select(n => FormattableString.Invariant($"numa:{n.Key}:{n.Value}")));
        records.Sort(StringComparer.Ordinal);
        return AffinityEnvironmentReader.Hash("windows-topology-v1|" + RuntimeInformation.OSArchitecture + "|" + string.Join(";", records));
    }

    private static void AddCoverage(HashSet<(ushort, int)> seen, IEnumerable<(ushort Group, ulong Mask)> masks)
    {
        foreach (var (group, mask) in masks)
            for (int bit = 0; bit < 64; bit++)
                if ((mask & (1UL << bit)) != 0 && !seen.Add((group, bit))) throw Invalid();
    }
    private static InvalidOperationException Invalid() => new("处理器拓扑记录不完整、重复或不一致，无法核实编号映射。");
    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    private static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(uint relationship, IntPtr buffer, ref uint length);
}
