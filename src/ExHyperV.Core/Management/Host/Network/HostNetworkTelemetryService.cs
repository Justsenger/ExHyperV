using System.Diagnostics;
using ExHyperV.Models;

namespace ExHyperV.Services;

/// <summary>网络接口或虚拟交换机的实时吞吐采样。</summary>
/// <param name="BytesSentPerSec">每秒发送字节数（Bytes/sec）。<fielddoc><meaning>最近采样周期内的每秒发送字节速率。</meaning><unit>Bytes/sec（字节每秒）。</unit></fielddoc></param>
/// <param name="BytesReceivedPerSec">每秒接收字节数（Bytes/sec）。<fielddoc><meaning>最近采样周期内的每秒接收字节速率。</meaning><unit>Bytes/sec（字节每秒）。</unit></fielddoc></param>
/// <param name="TotalBytesPerSec">每秒总字节数（Bytes/sec）。<fielddoc><meaning>发送与接收字节速率之和。</meaning><unit>Bytes/sec（字节每秒）。</unit></fielddoc></param>
public sealed record NetworkThroughputSample(double BytesSentPerSec, double BytesReceivedPerSec, double TotalBytesPerSec);

/// <summary>吞吐观测及其时间。仅有效观测含有速率值。</summary>
/// <param name="Sample">采样状态和 UTC 观测时间。</param>
/// <param name="Throughput">有效观测的收发速率，预热或缺测时为 null。</param>
public sealed record NetworkThroughputReading(PerformanceSampleInfo Sample, NetworkThroughputSample? Throughput);

/// <summary>按实例维护宿主网络计数器的采样基线。</summary>
public sealed class HostNetworkTelemetryService : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, CounterPair> counters = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    private sealed class CounterPair : IDisposable
    {
        private readonly PerformanceCounter tx;
        private readonly PerformanceCounter rx;
        private bool primed;

        public CounterPair(string category, string instance)
        {
            tx = new(category, "Bytes Sent/sec", instance, true);
            try { rx = new(category, "Bytes Received/sec", instance, true); }
            catch { tx.Dispose(); throw; }
        }

        public NetworkThroughputReading Read()
        {
            float sent = tx.NextValue();
            float received = rx.NextValue();
            var reading = ClassifySample(primed, sent, received, DateTimeOffset.UtcNow);
            primed = true;
            return reading;
        }

        public void Dispose()
        {
            tx.Dispose();
            rx.Dispose();
        }
    }

    internal static NetworkThroughputReading ClassifySample(bool primed, float sent, float received, DateTimeOffset now)
    {
        if (!primed) return new(new(PerformanceSampleState.WarmingUp, now), null);
        if (!float.IsFinite(sent) || !float.IsFinite(received) || sent < 0 || received < 0)
            return new(new(PerformanceSampleState.Unavailable, now), null);
        return new(new(PerformanceSampleState.Valid, now), new(sent, received, (double)sent + received));
    }

    internal static string NormalizePerfInstanceName(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        return name
            .Replace('(', '[')
            .Replace(')', ']')
            .Replace('#', '_')
            .Replace('/', '_')
            .Replace('\\', '_');
    }

    /// <summary>按交换机显示名读取唯一对应的背板计数器。</summary>
    /// <param name="switchName">交换机显示名，必须能唯一匹配计数器实例。</param>
    /// <returns>采样状态、时间和有效时的每秒收发字节数。</returns>
    public NetworkThroughputReading GetSwitchThroughput(string switchName) =>
        Sample("Hyper-V Virtual Switch", switchName, normalize: false);

    /// <summary>按接口描述读取唯一对应的适配器计数器。</summary>
    /// <param name="interfaceDescription">宿主网卡的接口描述。</param>
    /// <returns>采样状态、时间和有效时的每秒收发字节数。</returns>
    public NetworkThroughputReading GetAdapterThroughput(string interfaceDescription) =>
        Sample("Network Interface", interfaceDescription, normalize: true);

    private NetworkThroughputReading Sample(string category, string name, bool normalize)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            string key = category + "/" + name;
            try
            {
                if (!PerformanceCounterCategory.Exists(category))
                    return Missing(PerformanceSampleState.Unsupported);
                var instances = new PerformanceCounterCategory(category).GetInstanceNames();
                string expected = normalize ? NormalizePerfInstanceName(name) : name;
                var matches = instances.Where(instance => instance.Equals(expected, StringComparison.OrdinalIgnoreCase)
                    || instance.StartsWith(expected + "#", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(instance.AsSpan(expected.Length + 1), out _)).ToArray();
                if (matches.Length != 1 || !matches[0].Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return Missing(matches.Length == 0 ? PerformanceSampleState.Unavailable : PerformanceSampleState.Ambiguous);
                if (!counters.TryGetValue(key, out var pair))
                    counters.Add(key, pair = new CounterPair(category, matches[0]));
                return pair.Read();
            }
            catch (Exception error)
            {
                return Missing(PerformanceSampleState.Failed, error.Message);
            }

            NetworkThroughputReading Missing(PerformanceSampleState state, string? error = null)
            {
                if (counters.Remove(key, out var old)) old.Dispose();
                return new(new(state, DateTimeOffset.UtcNow, error), null);
            }
        }
    }

    /// <summary>等待正在读取的样本完成，释放本实例的计数器并重新建立采样基线。</summary>
    public void Reset()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            Clear();
        }
    }

    private void Clear()
    {
        foreach (var pair in counters.Values) pair.Dispose();
        counters.Clear();
    }

    /// <summary>等待正在读取的样本完成，释放本实例的计数器。</summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            Clear();
        }
    }
}
