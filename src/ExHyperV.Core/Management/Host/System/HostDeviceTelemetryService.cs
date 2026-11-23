using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ExHyperV.Tools;
using Microsoft.Win32;

namespace ExHyperV.Services;

/// <summary>一个采样间隔内的物理磁盘负载与吞吐量。</summary>
/// <param name="ActivePercent">物理磁盘活动时间百分比。</param>
/// <param name="ReadBytesPerSec">最近采样窗口的每秒读取字节数。</param>
/// <param name="WriteBytesPerSec">最近采样窗口的每秒写入字节数。</param>
public sealed record HostDiskActivity(double ActivePercent, double ReadBytesPerSec, double WriteBytesPerSec);

/// <summary>与同一 PnP 设备精确关联的 GPU 引擎占用率，缺测引擎保留空值。</summary>
/// <param name="Pct3D">最忙 3D 引擎的占用百分比，缺测时为 null。</param>
/// <param name="PctCopy">最忙复制引擎的占用百分比，缺测时为 null。</param>
/// <param name="PctEnc">最忙视频编码引擎的占用百分比，缺测时为 null。</param>
/// <param name="PctDec">最忙视频解码引擎的占用百分比，缺测时为 null。</param>
public sealed record HostGpuActivity(double? Pct3D, double? PctCopy, double? PctEnc, double? PctDec);

/// <summary>本机网卡可读取的 IPv4 和物理地址。</summary>
/// <param name="IPv4">接口报告的首个非回环 IPv4 地址，缺失时为空字符串。</param>
/// <param name="Mac">接口报告的物理地址文本。</param>
public sealed record HostAdapterAddress(string IPv4, string Mac);

/// <summary>持有一段可见采样期的性能计数器。顺序调用，结束该采样期时释放实例。</summary>
public sealed class HostDeviceTelemetryService : IDisposable
{
    private readonly Dictionary<(string Category, string Instance, string Counter), PerformanceCounter> counters = new();
    private readonly HashSet<(string Category, string Instance, string Counter)> used = new();
    private bool disposed;

    /// <summary>按真实实例读取频率、磁盘和 GPU。预热、失败和无法归属的读数为空或缺项。</summary>
    /// <param name="diskNumbers">要返回活动读数的物理磁盘编号集合。</param>
    /// <returns>当前频率（GHz）、按物理盘号归属的磁盘读数和按设备实例归属的 GPU 读数。</returns>
    public (double? CpuGHz, IReadOnlyDictionary<int, HostDiskActivity> Disks, IReadOnlyDictionary<string, HostGpuActivity> Gpus)
        Read(IReadOnlyCollection<int> diskNumbers)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        used.Clear();
        double? frequency = ReadCounter("Processor Information", "_Total", "Processor Frequency", prime: false);
        var disks = new Dictionary<int, HostDiskActivity>();
        foreach (string name in Instances("PhysicalDisk"))
        {
            if (!int.TryParse(name.Split(' ')[0], out int number) || !diskNumbers.Contains(number)) continue;
            var active = ReadCounter("PhysicalDisk", name, "% Disk Time");
            var read = ReadCounter("PhysicalDisk", name, "Disk Read Bytes/sec");
            var write = ReadCounter("PhysicalDisk", name, "Disk Write Bytes/sec");
            if (active.HasValue && read.HasValue && write.HasValue)
                disks[number] = new(Math.Min(100, active.Value), read.Value, write.Value);
        }

        var gpuReadings = new List<(string Device, int Engine, int Kind, double? Percent)>();
        var identities = new Dictionary<(uint High, uint Low), string?>();
        foreach (string name in Instances("GPU Engine"))
        {
            int kind = name[(name.LastIndexOf('_') + 1)..].ToUpperInvariant() switch
            {
                "3D" => 0, "COPY" => 1, "VIDEOENCODE" => 2, "VIDEODECODE" => 3, _ => -1
            };
            if (kind < 0 || !TryParseLuid(name, out var luid)) continue;
            if (!identities.TryGetValue(luid, out var id))
                identities[luid] = id = DxgiGpuMemory.ReadInstanceId(luid.High, luid.Low);
            if (id == null) continue;
            var parts = name.Split('_');
            int eng = Array.FindIndex(parts, s => s.Equals("eng", StringComparison.OrdinalIgnoreCase));
            if (eng < 0 || eng + 1 >= parts.Length || !int.TryParse(parts[eng + 1], out int engine)) continue;
            gpuReadings.Add((id, engine, kind, ReadCounter("GPU Engine", name, "Utilization Percentage")));
        }

        foreach (var key in counters.Keys.Where(k => !used.Contains(k)).ToArray())
        {
            counters[key].Dispose();
            counters.Remove(key);
        }
        return (frequency is > 0 ? frequency / 1000 : null, disks, GroupGpuReadings(gpuReadings));
    }

    internal static Dictionary<string, HostGpuActivity> GroupGpuReadings(
        IEnumerable<(string Device, int Engine, int Kind, double? Percent)> readings)
    {
        var result = new Dictionary<string, HostGpuActivity>(StringComparer.OrdinalIgnoreCase);
        foreach (var gpu in readings.GroupBy(r => r.Device, StringComparer.OrdinalIgnoreCase))
        {
            var loads = new double?[4];
            foreach (var kind in gpu.GroupBy(r => r.Kind))
            {
                if (kind.Any(r => !r.Percent.HasValue)) continue;
                // Instances are per process. Sum a physical engine's users, then show the busiest engine of this kind.
                loads[kind.Key] = kind.GroupBy(r => r.Engine).Max(engine => Math.Min(100, engine.Sum(r => r.Percent!.Value)));
            }
            if (loads.Any(v => v.HasValue)) result[gpu.Key] = new(loads[0], loads[1], loads[2], loads[3]);
        }
        return result;
    }

    internal static bool TryParseLuid(string instance, out (uint High, uint Low) luid)
    {
        luid = default;
        var parts = instance.Split('_');
        int i = Array.FindIndex(parts, part => part.Equals("luid", StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 2 >= parts.Length || !Hex(parts[i + 1], out var high) || !Hex(parts[i + 2], out var low)) return false;
        luid = (high, low);
        return true;

        static bool Hex(string text, out uint value)
        {
            value = 0;
            return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
        }
    }

    private static string[] Instances(string category)
    {
        try { return new PerformanceCounterCategory(category).GetInstanceNames(); }
        catch (Exception ex) when (IsCounterError(ex)) { return []; }
    }

    private double? ReadCounter(string category, string instance, string counter, bool prime = true)
    {
        var key = (category, instance, counter);
        used.Add(key);
        try
        {
            bool exists = counters.TryGetValue(key, out var handle);
            if (!exists) counters[key] = handle = new(category, counter, instance, true);
            var value = handle!.NextValue();
            return (exists || !prime) && float.IsFinite(value) && value >= 0 ? value : null;
        }
        catch (Exception ex) when (IsCounterError(ex))
        {
            if (counters.Remove(key, out var handle)) handle.Dispose();
            return null;
        }
    }

    private static bool IsCounterError(Exception ex) => ex is InvalidOperationException or System.ComponentModel.Win32Exception
        or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or IOException;

    /// <summary>按接口 GUID 返回本地 IPv4 与 MAC 地址，缺失的接口不产生记录。</summary>
    /// <returns>以接口 GUID 为键的地址快照。</returns>
    public static IReadOnlyDictionary<Guid, HostAdapterAddress> ReadAdapterAddresses()
    {
        var result = new Dictionary<Guid, HostAdapterAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!Guid.TryParse(adapter.Id, out var id)) continue;
            try
            {
                var ip = adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a.Address));
                result[id] = new(ip?.Address.ToString() ?? "", adapter.GetPhysicalAddress().ToString());
            }
            catch (NetworkInformationException) { }
        }
        return result;
    }

    /// <summary>探测本机 usbipd 安装，无法访问服务注册信息时返回未知。</summary>
    /// <returns>安装证据存在时为 true，完成探测且未找到时为 false，访问失败时为 null。</returns>
    public static bool? IsUsbipdInstalled()
    {
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\usbipd");
            if (service != null) return true;
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (File.Exists(Path.Combine(programFiles, "usbipd-win", "usbipd.exe"))) return true;
            return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(dir => File.Exists(Path.Combine(dir, "usbipd.exe")));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    /// <summary>释放全部计数器及采样基线。</summary>
    public void Dispose()
    {
        disposed = true;
        foreach (var handle in counters.Values) handle.Dispose();
        counters.Clear();
    }
}
