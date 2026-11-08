using System.Diagnostics;

namespace ExHyperV.UI.Windows.Console;

/// <summary>
/// Counts received frames and completed bitmap updates.
/// All timestamps use Stopwatch.GetTimestamp. Call Snapshot about every 500 ms; its FPS
/// uses the actual time since the previous snapshot/reset, including a shorter first sample.
/// Completed update gaps are retained in 100 ms buckets for five seconds (up to 100 ms
/// expiry tolerance). The ongoing gap since the last update is included once, even if it
/// exceeds five seconds. Static images also accumulate time in the ongoing update gap.
/// </summary>
internal sealed class ConsoleFrameStatistics
{
    private const int HistoryBucketCount = 51;
    private static readonly long BucketTicks = Math.Max(1, (Stopwatch.Frequency + 9) / 10);
    private static readonly long LongGapTicks = Math.Max(1, (Stopwatch.Frequency + 19) / 20);
    private readonly object _gate = new();
    private readonly GapBucket[] _gaps = new GapBucket[HistoryBucketCount];
    private long _resetTicks, _sampleTicks, _lastReceivedTicks, _lastPresentedTicks;
    private long _received, _presented;
    private bool _hasReceived, _hasPresented;
    private ConsoleFrameStatisticsSnapshot _lastSnapshot;

    internal ConsoleFrameStatistics(long ticks) => Reset(ticks);

    internal void Reset(long ticks)
    {
        lock (_gate)
        {
            _resetTicks = _sampleTicks = ticks;
            _lastReceivedTicks = _lastPresentedTicks = ticks;
            _received = _presented = 0;
            _hasReceived = _hasPresented = false;
            _lastSnapshot = default;
            Array.Clear(_gaps);
        }
    }

    // Timestamps older than reset or the latest event in their stream are stale.
    // Equal timestamps are allowed: Stopwatch resolution does not identify individual frames.
    internal void RecordReceived(long ticks)
    {
        lock (_gate)
        {
            if (ticks < _resetTicks || (_hasReceived && ticks < _lastReceivedTicks)) return;
            _lastReceivedTicks = ticks;
            _hasReceived = true;
            _received++;
        }
    }

    internal void RecordPresented(long ticks)
    {
        lock (_gate)
        {
            if (ticks < _resetTicks || (_hasPresented && ticks < _lastPresentedTicks)) return;
            _presented++;
            if (_hasPresented)
            {
                long gap = ticks - _lastPresentedTicks;
                long bucketId = ticks / BucketTicks;
                ref var bucket = ref _gaps[(int)(bucketId % HistoryBucketCount)];
                if (bucket.Id != bucketId) bucket = new GapBucket { Id = bucketId };
                bucket.MaxGapTicks = Math.Max(bucket.MaxGapTicks, gap);
                if (gap >= LongGapTicks) bucket.LongGapCount++;
            }
            _lastPresentedTicks = ticks;
            _hasPresented = true;
        }
    }

    internal ConsoleFrameStatisticsSnapshot Snapshot(long ticks)
    {
        lock (_gate)
        {
            // A repeated/backward sample cannot consume counters or create a negative FPS.
            if (ticks <= _sampleTicks) return _lastSnapshot;
            double seconds = (ticks - _sampleTicks) / (double)Stopwatch.Frequency;
            long maxGap = 0;
            int longGaps = 0;
            long currentBucket = ticks / BucketTicks;
            foreach (ref readonly var bucket in _gaps.AsSpan())
            {
                long age = currentBucket - bucket.Id;
                if (age < 0 || age >= HistoryBucketCount) continue;
                maxGap = Math.Max(maxGap, bucket.MaxGapTicks);
                longGaps += bucket.LongGapCount;
            }
            if (_hasPresented && ticks >= _lastPresentedTicks)
            {
                long currentGap = ticks - _lastPresentedTicks;
                maxGap = Math.Max(maxGap, currentGap);
                if (currentGap >= LongGapTicks) longGaps++;
            }
            _lastSnapshot = new(_received / seconds, _presented / seconds,
                maxGap * 1000.0 / Stopwatch.Frequency, longGaps);
            _sampleTicks = ticks;
            _received = _presented = 0;
            return _lastSnapshot;
        }
    }

    private struct GapBucket
    {
        internal long Id, MaxGapTicks;
        internal int LongGapCount;
    }
}

internal readonly record struct ConsoleFrameStatisticsSnapshot(
    double ReceivedFps, double PresentedFps, double MaxGapMilliseconds, int LongGapCount);
