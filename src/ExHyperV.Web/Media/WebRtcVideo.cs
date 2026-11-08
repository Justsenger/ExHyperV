using System.Diagnostics;
using System.Threading.Channels;

namespace ExHyperV.Web;

// One owner calls the native pipeline. WebSocket input queues bounded signalling only.
internal sealed class WebRtcVideo(VideoSettings settings, Func<object, CancellationToken, Task> send)
{
    private readonly Channel<ClientMessage> commands = Channel.CreateBounded<ClientMessage>(
        new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });

    public void Signal(ClientMessage message)
    {
        if (!commands.Writer.TryWrite(message)) throw new InvalidDataException("Too many media signalling messages.");
    }

    public async Task RunAsync(FrameMailbox frames, CancellationToken token)
    {
        GStreamerVideo? media = null;
        PendingFrame? latest = null;
        int epoch = 0, candidates = 0;
        bool connected = false, answered = false, softwareFallback = false, dirty = false, refresh = false;
        var clock = Stopwatch.StartNew();
        double created = 0, due = 0, lastPush = 0, lastStats = 0;
        double lastSourceChange = 0, lastEncodedAt = 0;
        long previousReceived = 0;
        ulong previousEncoded = 0;

        async Task CreateAsync()
        {
            if (media is not null) { var retired = media; media = null; await retired.DisposeAsync(); }
            connected = answered = false; candidates = 0; epoch++;
            previousEncoded = 0;
            var frame = latest!.Frame;
            var selected = softwareFallback ? settings with { Encoder = 2 } : settings;
            try { media = new(frame.Width, frame.Height, selected); }
            catch when (settings.Encoder == 0 && !softwareFallback)
            {
                softwareFallback = true;
                media = new(frame.Width, frame.Height, settings with { Encoder = 2 });
            }
            var info = media.Statistics;
            created = clock.Elapsed.TotalSeconds; due = created; lastPush = -1; dirty = true;
            lastEncodedAt = created;
            await send(new { type = "media", epoch, width = frame.Width, height = frame.Height,
                videoWidth = info.OutputWidth, videoHeight = info.OutputHeight,
                encoder = info.Hardware != 0 ? "硬件 H.264" : "x264 · 低延迟", settings.Fps,
                bitrate = settings.Bitrate }, token);
        }

        try
        {
            // Frame arrivals wake the consumer immediately. A periodic poll introduces an
            // unrelated clock and can merge two source updates before encoding either.
            using var waits = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task frameReady = frames.WaitForFrameAsync(waits.Token);
            Task signalReady = commands.Reader.WaitToReadAsync(waits.Token).AsTask();
            Task poll = Task.Delay(10, waits.Token);
            try
            {
                while (true)
                {
                    await Task.WhenAny(frameReady, signalReady, poll);
                    token.ThrowIfCancellationRequested();
                    double now = clock.Elapsed.TotalSeconds;
                    if (frameReady.IsCompleted)
                    {
                        await frameReady;
                        var next = frames.TryTake();
                        if (next is not null) { latest?.Dispose(); latest = next; dirty = true; lastSourceChange = now; }
                        frameReady = frames.WaitForFrameAsync(waits.Token);
                    }
                    if (poll.IsCompleted) poll = Task.Delay(10, waits.Token);
                    if (latest is null)
                    {
                        if (signalReady.IsCompleted) throw new InvalidDataException("Signalling arrived before the first frame.");
                        if (now > 20) throw new TimeoutException("Core 未在限定时间内提供控制台画面。");
                        continue;
                    }
                    if (media is null || media.SourceWidth != latest.Frame.Width || media.SourceHeight != latest.Frame.Height)
                        await CreateAsync();

                    while (commands.Reader.TryRead(out var command))
                    {
                        if (command.Epoch != epoch) continue;
                        if (command.Type == "answer")
                        {
                            if (answered) throw new InvalidDataException("Duplicate WebRTC answer.");
                            media!.Answer(command.Sdp ?? ""); answered = true;
                        }
                        else if (command.Type == "ice")
                        {
                            if (!answered || ++candidates > 64) throw new InvalidDataException("Unexpected ICE candidate.");
                            media!.AddIce(command.Line, command.Candidate ?? "");
                        }
                    }
                    if (signalReady.IsCompleted) signalReady = commands.Reader.WaitToReadAsync(waits.Token).AsTask();
                    bool restart = false;
                    for (int count = 0; count < 64 && media!.Poll() is { } signal; count++)
                    {
                        switch (signal.Kind)
                        {
                            case 1: await send(new { type = "offer", epoch, sdp = signal.Text }, token); break;
                            case 2: await send(new { type = "ice", epoch, line = signal.Value, candidate = signal.Text }, token); break;
                            case 3:
                                if (settings.Encoder == 0 && !softwareFallback && !connected && media.Statistics.Hardware != 0)
                                { softwareFallback = true; restart = true; }
                                else throw new InvalidOperationException("视频传输失败：" + signal.Text);
                                break;
                            case 4:
                                if (signal.Value == 2) { connected = true; refresh = true; }
                                if (signal.Value is 4 or 5) throw new IOException("WebRTC 连接已结束，请重新连接。");
                                break;
                            case 6: refresh = true; break;
                        }
                        if (restart) break;
                    }
                    if (restart) { await CreateAsync(); continue; }
                    if (!connected && now - created > 5 && settings.Encoder == 0 && !softwareFallback
                        && media!.Statistics is { Hardware: not 0, Encoded: 0 })
                    { softwareFallback = true; await CreateAsync(); continue; }
                    if (!connected && now - created > 15) throw new TimeoutException("浏览器视频连接超时，请检查媒体连接是否可用。");

                    if (now >= due && (connected || lastPush < 0 || now - lastPush >= .5))
                    {
                        bool repeat = refresh && now - lastPush >= .25 || !connected && now - lastPush >= .5;
                        if (dirty || repeat)
                        {
                            if (repeat) media!.RequestKeyFrame();
                            var frame = latest.Frame;
                            if (media!.Push(frame.Pixels, frame.Width, frame.Height, frame.Stride))
                            {
                                dirty = false; if (repeat) refresh = false; lastPush = now;
                                // Only a submitted frame consumes a slot. Advancing this on
                                // empty polls makes slower sources wait for an unrelated clock.
                                double step = 1.0 / settings.Fps;
                                due += (Math.Floor(Math.Max(0, now - due) / step) + 1) * step;
                            }
                        }
                    }
                    if (now - lastStats >= 1)
                    {
                        var info = media!.Statistics;
                        double seconds = now - lastStats;
                        if (info.Encoded != previousEncoded) lastEncodedAt = now;
                        if (connected && now - created > 5 && now - lastEncodedAt > 3 && now - lastSourceChange < 1)
                            throw new TimeoutException("视频编码停止输出，已结束本次控制台连接。请重新连接。");

                        double sourceFps = (latest.Received - previousReceived) / seconds;


                        await send(new { type = "mediaStats", epoch,
                            sourceFps,
                            encodedFps = (info.Encoded - previousEncoded) / seconds,
                            encodeMs = info.EncodeMilliseconds, bitrate = info.Bitrate,
                            receiverBitrate = info.ReceiverBitrate, dropped = info.Dropped, replaced = latest.Replaced }, token);
                        previousReceived = latest.Received; previousEncoded = info.Encoded; lastStats = now;
                    }
                }
            }
            finally { waits.Cancel(); }
        }
        finally
        {
            commands.Writer.TryComplete();
            latest?.Dispose();
            if (media is not null) await media.DisposeAsync();
        }
    }
}
