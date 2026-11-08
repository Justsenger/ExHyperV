using System.Diagnostics;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

public sealed partial class VmDashboardView
{
    private static readonly TimeSpan MetricInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan UptimeFreshness = TimeSpan.FromSeconds(4);
    private DispatcherQueueTimer uptimeTimer = null!;
    private TimeSpan uptimeAnchor;
    private long uptimeTimestamp;
    private long uptimeExpires;
    private int runtimeVersion;

    private void InitializeUptimeClock()
    {
        uptimeTimer = DispatcherQueue.CreateTimer();
        uptimeTimer.Interval = TimeSpan.FromSeconds(1);
        uptimeTimer.Tick += (_, _) =>
        {
            if (cancellation?.IsCancellationRequested != false || !stateVerified || state != VmState.Running)
            {
                FreezeUptimeClock();
                return;
            }
            long now = Stopwatch.GetTimestamp();
            DisplayUptime(uptimeAnchor + Stopwatch.GetElapsedTime(uptimeTimestamp, Math.Min(now, uptimeExpires)), live: true);
            // A stalled provider must not leave an apparently live clock advancing indefinitely.
            if (now >= uptimeExpires) FreezeUptimeClock();
        };
    }

    private void FreezeUptimeClock() => uptimeTimer.Stop();

    private void UpdateUptimeClock(VmQueryRow row, bool live, TimeSpan queryElapsed)
    {
        var uptime = row.Get(VmIdentityFields.Uptime);
        var observedState = row.Get(VmIdentityFields.State);
        if (!uptime.HasValue || uptime.Value < TimeSpan.Zero)
        {
            FreezeUptimeClock();
            return;
        }
        if (!live || !observedState.HasValue || observedState.Value != VmState.Running)
        {
            FreezeUptimeClock();
            DisplayUptime(uptime.Value, live);
            return;
        }

        // Summary is read before the other providers finish. Bound UTC-derived delivery age
        // by the actual monotonic query duration so a host clock adjustment cannot add hours.
        TimeSpan age = DateTimeOffset.UtcNow - uptime.ObservedAtUtc;
        age = TimeSpan.FromTicks(Math.Clamp(age.Ticks, 0, Math.Max(0, queryElapsed.Ticks)));
        if (age >= UptimeFreshness)
        {
            FreezeUptimeClock();
            // Preserve the last displayed time rather than moving backwards to an expired sample.
            return;
        }
        uptimeAnchor = uptime.Value + age;
        uptimeTimestamp = Stopwatch.GetTimestamp();
        uptimeExpires = uptimeTimestamp + (long)((UptimeFreshness - age).TotalSeconds * Stopwatch.Frequency);
        DisplayUptime(uptimeAnchor, live: true);
        if (!uptimeTimer.IsRunning) uptimeTimer.Start();
    }

    private void DisplayUptime(TimeSpan value, bool live)
    {
        UptimeText.Text = value.ToString(value.Days > 0 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss");
        if (live && current != null) current.Uptime = UptimeText.Text;
    }

    private static async Task<long> WaitForNextPollAsync(long previousDeadline, TimeSpan interval, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long period = (long)(interval.TotalSeconds * Stopwatch.Frequency);
        long next = previousDeadline + period;
        long now = Stopwatch.GetTimestamp();
        // Slow queries skip elapsed time slots, with neither overlap nor queued catch-up work.
        if (next <= now) next += ((now - next) / period + 1) * period;
        await Task.Delay(Stopwatch.GetElapsedTime(now, next), token);
        return next;
    }

    private bool IsCurrentPreview(Guid id, CancellationTokenSource source, int version)
        => !source.IsCancellationRequested && ReferenceEquals(cancellation, source) && current?.Id == id
            && stateVerified && state == VmState.Running && runtimeVersion == version;

    private async Task RunPreviewAsync(Guid id, CancellationTokenSource source)
    {
        var token = source.Token;
        long nextPoll = Stopwatch.GetTimestamp();
        try
        {
            while (!token.IsCancellationRequested)
            {
                int version = runtimeVersion;
                if (IsCurrentPreview(id, source, version))
                {
                    try
                    {
                        // Capture has no cancellation parameter. Always await it before releasing
                        // this selection; a late frame must never appear after switching or pausing.
                        var image = await VmScreenshotSource.CaptureAsync(id, 240, 180);
                        token.ThrowIfCancellationRequested();
                        if (image != null && IsCurrentPreview(id, source, version))
                        {
                            var jpeg = await Task.Run(image.EncodeJpeg, token);
                            token.ThrowIfCancellationRequested();
                            using var stream = new InMemoryRandomAccessStream();
                            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                            {
                                writer.WriteBytes(jpeg);
                                await writer.StoreAsync();
                            }
                            token.ThrowIfCancellationRequested();
                            stream.Seek(0);
                            var bitmap = new BitmapImage();
                            await bitmap.SetSourceAsync(stream);
                            token.ThrowIfCancellationRequested();
                            if (IsCurrentPreview(id, source, version))
                            {
                                Preview.Source = bitmap;
                                PreviewBorder.Visibility = Visibility.Visible;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        if (IsCurrentPreview(id, source, version)) ShowError("虚拟机预览读取失败：" + error.Message);
                    }
                }
                nextPoll = await WaitForNextPollAsync(nextPoll, PreviewInterval, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
