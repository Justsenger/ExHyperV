using System.Diagnostics;
using System.Threading.Channels;
using ExHyperV.Services;

namespace ExHyperV.Server.Hosting;

internal sealed class ConsoleMediaGroup
{
    internal string Encoding { get; }
    internal bool Regions { get; }
    private readonly IConsoleVideoSource? video;
    private readonly object gate = new();
    private readonly HashSet<ConsoleSocket> viewers = [];
    private readonly Channel<bool> wake = Channel.CreateBounded<bool>(1);
    private readonly CancellationTokenSource stop = new();
    private ConsoleFrame? pending;
    private bool refresh, closed, software, failed;
    private Task? worker;
    private ConsoleH264Encoder? encoder;
    private readonly long startedAt = Stopwatch.GetTimestamp();
    private long previous, timestamp;
    private long MediaTime() => timestamp = Math.Max(timestamp + 1, (long)Stopwatch.GetElapsedTime(startedAt).TotalMicroseconds);
    private int width, height;
    private bool idlePending, idleFull;
    private ConsoleFrameRegion idleRegion;
    private IConsoleGpuDelivery? gpu;
    private bool gpuReady;
    private ulong? activeEpoch;
    private long? activeAdapter;
    private bool cpuFallback;
    private void GpuAvailable()
    {
        lock (gate) { if (closed || cpuFallback) return; gpuReady = true; wake.Writer.TryWrite(true); }
    }

    internal ConsoleMediaGroup(IConsoleVideoSource? video, string encoding, bool regions)
    {
        this.video = video;
        Encoding = encoding;
        Regions = regions;
        if (encoding == "h264" && video is IConsoleGpuSource provider)
        {
            try { gpu = provider.OpenDelivery(); gpu.Available += GpuAvailable; }
            catch (Exception) { gpu?.Dispose(); gpu = null; cpuFallback = true; }
        }
        if (video != null) video.FrameReceived += Frame;
    }

    internal void Join(ConsoleSocket viewer)
    {
        lock (gate)
        {
            viewers.Add(viewer);
            if (failed) viewer.EncodingChanged(this, "deflate");
            if (worker != null) Refresh();
        }
    }
    internal void Start() { lock (gate) worker ??= Task.Run(Pump); }
    internal bool Leave(ConsoleSocket viewer) { lock (gate) { viewers.Remove(viewer); return viewers.Count == 0; } }
    internal async Task Stop()
    {
        lock (gate) { closed = true; pending?.Dispose(); pending = null; }
        if (video != null) video.FrameReceived -= Frame;
        stop.Cancel();
        try { if (worker != null) await worker; }
        finally { if (gpu != null) { gpu.Available -= GpuAvailable; gpu.Dispose(); } encoder?.Dispose(); stop.Dispose(); }
    }
    private void Frame(ConsoleFrame frame)
    {
        lock (gate)
        {
            if (closed || gpu != null && !cpuFallback) return;
            if (pending != null) { refresh = true; pending.Dispose(); }
            pending = frame.Retain();
            wake.Writer.TryWrite(true);
        }
    }
    internal bool Refresh()
    {
        lock (gate)
        {
            if (gpu != null && !cpuFallback) { refresh = true; GpuAvailable(); return true; }
            using var captured = video?.CaptureFrame();
            if (captured == null) return false;
            refresh = true;
            Frame(captured);
            return true;
        }
    }
    private async Task Pump()
    {
        var token = stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                if (idlePending) timeout.CancelAfter(ConsoleSocket.IdleLosslessCompensationMs);
                bool idle = false;
                try { await wake.Reader.ReadAsync(timeout.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { idle = true; }
                ConsoleSocket[] targets;
                ConsoleFrame? frame;
                bool force, takeGpu;
                lock (gate)
                {
                    if (closed) return;
                    targets = viewers.ToArray();
                    takeGpu = gpuReady; gpuReady = false;
                    frame = pending;
                    pending = null;
                    force = refresh;
                    refresh = false;
                }
                using (frame)
                {
                    if (takeGpu && !cpuFallback) EncodeGpu(targets, force);
                    else if (frame != null) Encode(frame, targets, force);
                    else if (idle && idlePending)
                    {
                        idlePending = false;
                        using var capture = video?.CaptureFrame();
                        if (capture != null) Lossless(capture, targets, idleFull || capture.Sequence != previous, true);
                        idleFull = false;
                        idleRegion = default;
                        if (targets.Any(v => v.NeedsKey)) Refresh();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            lock (gate) foreach (var viewer in viewers) viewer.MediaFailed();
        }
    }

    private void EncodeGpu(ConsoleSocket[] targets, bool force)
    {
        try
        {
            using var frame = gpu!.TakeLatest();
            if (frame == null) return;
            if (ConsolePayload.IsDesktopSize(frame.Width, frame.Height))
            {
                if (width != frame.Width || height != frame.Height || activeAdapter != frame.AdapterLuid)
                { encoder?.Dispose(); encoder = null; width = frame.Width; height = frame.Height; }
                activeAdapter = frame.AdapterLuid;
                encoder ??= ConsoleH264Encoder.TryCreate(width, height, true, out _, frame.AdapterLuid);
                bool changed = activeEpoch != frame.Epoch;
                if (encoder?.IsHardware == true && encoder.EncodePacket([], 0, MediaTime(), force || changed || targets.Any(v => v.NeedsKey),
                    frame, out var annexB, out bool key))
                {
                    using (annexB)
                    {
                        if (annexB == null) return;
                        using var packet = ConsoleWire.H264(annexB.Bytes, frame.Sequence, timestamp, width, height, key, true);
                        foreach (var viewer in targets)
                        {
                            if (changed) viewer.Transport(this, "GpuShared", frame.Epoch);
                            viewer.Offer(this, packet, frame.Sequence, key, true);
                        }
                        previous = frame.Sequence;
                        activeEpoch = frame.Epoch;
                        idlePending = idleFull = true;
                    }
                    return;
                }
            }
        }
        catch (Exception) { /* This delivery alone switches to its CPU path. */ }
        lock (gate) { cpuFallback = true; refresh = true; }
        encoder?.Dispose(); encoder = null;
        ulong epoch = gpu!.RequestCpu();
        foreach (var viewer in targets) viewer.Transport(this, "CpuShared", epoch);
        activeEpoch = epoch;
        Refresh();
    }

    private void Encode(ConsoleFrame frame, ConsoleSocket[] targets, bool force)
    {
        force |= frame.Width != width || frame.Height != height || frame.BaseSequence != previous;
        width = frame.Width; height = frame.Height; previous = frame.Sequence;
        if (Encoding == "h264" && !failed && width >= 64 && height >= 64 && ((width | height) & 1) == 0)
        {
            if (encoder != null && (encoder.Width != width || encoder.Height != height)) { encoder.Dispose(); encoder = null; }
            encoder ??= ConsoleH264Encoder.TryCreate(width, height, !software);
            bool needKey = force || targets.Any(v => v.NeedsKey);
            MediaTime();
            ConsolePacket? bytes = null;
            bool key = false;
            bool ok = encoder != null && encoder.EncodePacket(frame.Pixels, frame.Stride, timestamp, needKey, null, out bytes, out key);
            if (!ok && encoder?.IsHardware == true)
            {
                encoder.Dispose();
                software = true;
                encoder = ConsoleH264Encoder.TryCreate(width, height, false);
                ok = encoder != null && encoder.EncodePacket(frame.Pixels, frame.Stride, timestamp, true, null, out bytes, out key);
                force = true;
            }
            using var output = bytes;
            if (ok && bytes != null)
            {
                using var packet = ConsoleWire.H264(bytes.Bytes, frame.Sequence, timestamp, width, height, key, encoder!.IsHardware);
                foreach (var viewer in targets) viewer.Offer(this, packet, frame.Sequence, key, true);
                if (idlePending || force || frame.HasFullDamage || frame.Damage.IsEmpty) idleFull = true;
                idleRegion = frame.Damage;
                idlePending = true;
                return;
            }
            if (ok) return;
            failed = true;
            encoder?.Dispose(); encoder = null;
            foreach (var viewer in targets) viewer.EncodingChanged(this, "deflate");
            force = true;
        }
        idlePending = false;
        Lossless(frame, targets, force, false);
    }

    private void Lossless(ConsoleFrame frame, ConsoleSocket[] targets, bool force, bool idle)
    {
        ConsolePacket? full = null, delta = null;
        try
        {
            foreach (var viewer in targets)
            {
                if (idle && viewer.NeedsKey) continue;
                bool complete = force || frame.HasFullDamage || !viewer.CanApply(this, idle ? frame.Sequence : frame.BaseSequence, idle)
                    || idle && (idleRegion.IsEmpty || (long)idleRegion.Width * idleRegion.Height * 100 >= (long)frame.Width * frame.Height * 65);
                var packet = complete ? full ??= Build(true) : delta ??= Build(false);
                viewer.Offer(this, packet, frame.Sequence, complete, false, idle);
            }
        }
        finally { full?.Dispose(); delta?.Dispose(); }
        ConsolePacket Build(bool complete) => Regions && !idle && (Encoding == "bgra" || !complete && (frame.DirtyRegions.Count > 1 || frame.MoveRegions.Count > 0)) ? ConsoleWire.Regions(frame, complete)
            : ConsoleWire.Video(frame, idle ? "bgra" : Encoding == "h264" ? "deflate" : Encoding,
                frame.BaseSequence, frame.Width, frame.Height, complete, idle && !complete ? idleRegion : null);
    }
}
