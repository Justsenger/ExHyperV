using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using ExHyperV.Services;

namespace ExHyperV.Web;

internal sealed class ConsoleSession(WebSocket socket, Guid vmId, VideoSettings settings, ILogger<ConsoleSession> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim writeGate = new(1, 1);

    // The HTTP endpoint owns the connection; this bridge only consumes its capabilities.
    public async Task RunAsync(IConsoleConnection connection, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var frames = new FrameMailbox();
        var media = new WebRtcVideo(settings, SendJsonAsync);
        var video = connection.Video;
        var input = connection.Input is { } target ? new ConsoleInput(target) : null;
        var disconnected = new TaskCompletionSource<ConsoleDisconnectInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(ConsoleFrame frame) { input?.SetSize(frame.Width, frame.Height); frames.Offer(frame); }
        void OnDisconnected(ConsoleDisconnectInfo info) => disconnected.TrySetResult(info);
        Task send = Task.CompletedTask, receive = Task.CompletedTask;
        try
        {
            if (video is null)
            {
                await SendJsonAsync(new { type = "disconnected", message = "所选控制台来源不提供画面。" }, stop.Token);
                return;
            }
            video.FrameReceived += OnFrame;
            connection.Disconnected += OnDisconnected;
            await SendJsonAsync(new { type = "connecting", backendId = connection.BackendId,
                capabilities = new { input = input is not null } }, stop.Token);
            connection.Start();
            send = media.RunAsync(frames, stop.Token);
            receive = ReceiveInputAsync(input, media, stop.Token);
            var finished = await Task.WhenAny(send, receive, disconnected.Task);
            if (finished == disconnected.Task)
            {
                var info = await disconnected.Task;
                logger.LogInformation("Console {VmId} backend {Backend} disconnected: {Code:X8} {Message}", vmId, connection.BackendId, info.ErrorCode, info.Error);
                using var notice = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await SendJsonAsync(new { type = "disconnected", backendId = connection.BackendId, code = info.ErrorCode,
                    message = info.Error ?? "虚拟机控制台已断开。请检查所选来源和虚拟机的状态。" }, notice.Token);
            }
            else await finished;
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Console {VmId} ended", vmId);
            using var notice = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await SendJsonAsync(new { type = "disconnected", message = exception.Message }, notice.Token); }
            catch (Exception) { }
        }
        finally
        {
            stop.Cancel();
            try { await Task.WhenAll(send, receive); }
            catch (Exception exception) { logger.LogDebug(exception, "Console transport stopped"); }
            if (video is not null) video.FrameReceived -= OnFrame;
            connection.Disconnected -= OnDisconnected;
            frames.Dispose();
            try { input?.ReleaseAll(); }
            finally
            {
                try { await connection.DisconnectAsync(); }
                finally { socket.Abort(); writeGate.Dispose(); }
            }
        }
    }

    private async Task ReceiveInputAsync(ConsoleInput? input, WebRtcVideo media, CancellationToken token)
    {
        var buffer = new byte[96 * 1024];
        long interval = Stopwatch.GetTimestamp();
        int messages = 0;
        try
        {
            while (true)
            {
                int length = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer.AsMemory(length), token);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Expected JSON input.");
                    length += result.Count;
                    if (length == buffer.Length && !result.EndOfMessage) throw new InvalidDataException("Input too large.");
                } while (!result.EndOfMessage);
                if (Stopwatch.GetElapsedTime(interval).TotalSeconds >= 1) { interval = Stopwatch.GetTimestamp(); messages = 0; }
                if (++messages > 2000) throw new InvalidDataException("Input rate exceeded.");
                var message = JsonSerializer.Deserialize<ClientMessage>(buffer.AsSpan(0, length), Json)
                    ?? throw new InvalidDataException("Missing input.");
                if (message.Type is "answer" or "ice") media.Signal(message);
                else if (message.Type == "ping") await SendJsonAsync(new { type = "pong", time = message.Time }, token);
                else input?.Apply(message);
            }
        }
        finally { input?.ReleaseAll(); }
    }

    private async Task SendJsonAsync<T>(T data, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data, Json);
        await writeGate.WaitAsync(token);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token); }
        finally { writeGate.Release(); }
    }
}
