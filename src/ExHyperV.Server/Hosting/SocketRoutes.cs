using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using ExHyperV.Server.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ExHyperV.Server.Hosting;

internal static class SocketRoutes
{
    internal static void Map(WebApplication app)
    {
        app.MapGet("/ws/v1/events", (HttpContext ctx, ApiExecution execution, ApiObjects objects) =>
            Run(ctx, peer => EventSocket.Run(peer, execution, objects)));
        app.MapGet("/ws/v1/telemetry", (HttpContext ctx, LaunchOptions options, Func<ITelemetrySampler> create) =>
            Run(ctx, peer => TelemetrySocket.Run(peer, options, create)));
        app.MapGet("/ws/v1/console/{vmId}", (HttpContext ctx, string vmId) => ConsoleRoute(ctx, vmId, false));
        app.MapGet("/ws/v2/console/{vmId}", (HttpContext ctx, string vmId) => ConsoleRoute(ctx, vmId, true));
    }

    private static async Task ConsoleRoute(HttpContext ctx, string vmId, bool v2)
    {
        var services = ctx.RequestServices;
        var options = services.GetRequiredService<LaunchOptions>();
        var grant = (SocketAuthorization)ctx.Items[HttpAccess.SessionKey]!;
        if (!options.Toolsets.Contains("console") || !v2 && options.ReadOnly || v2 && !grant.Scoped)
            throw new UnauthorizedAccessException("控制台需要相应的观看或控制授权。");
        if (v2 && ctx.Request.Query.TryGetValue("requiredFeatures", out var required) && required.ToString() != "0")
            throw new ArgumentException("控制台必需特性不受支持。");
        var request = ConsoleRequest.Parse(vmId, ctx.Request.Query, v2);
        var directory = services.GetRequiredService<ConsoleDirectory>();
        var target = await directory.InspectVm(request.Options.VmId, ctx.RequestAborted);
        ExtensionAccess? access = null;
        if (v2)
        {
            var allowed = directory.Allowed(request.Backend, request.Options.VmId, grant, options, target, HttpAccess.SecureConsole(ctx), out access);
            if ((grant.Rights & allowed) != grant.Rights || grant.ExtensionVersion != access?.Snapshot.Version) throw new UnauthorizedAccessException("控制台授权已变化。");
            request = request with { Options = request.Options with { ViewerContextId = grant.ViewerContextId } };
        }
        else
        {
            var legacy = request.Backend == "RdpBasic" ? target.Basic : target.Enhanced;
            if (!legacy.HasFlag(ConsoleRights.Watch)) throw new UnauthorizedAccessException("虚拟机控制台策略未授权观看。");
            grant = grant with { Rights = legacy };
        }
        var features = ConsoleDirectory.Builtin(request.Backend)
            ? request.Backend == "RdpBasic" ? target.Basic : target.Enhanced
            : ConsoleDirectory.Rights(access!.Snapshot.Granted & access.Snapshot.Supported);
        request = request with { Options = request.Options with
        {
            RedirectClipboard = request.Options.RedirectClipboard && features.HasFlag(ConsoleRights.Clipboard),
            RedirectAudioOutput = request.Options.RedirectAudioOutput && features.HasFlag(ConsoleRights.AudioOutput),
            RedirectAudioInput = request.Options.RedirectAudioInput && features.HasFlag(ConsoleRights.AudioInput)
        }};
        var slots = services.GetRequiredService<ConsoleSlots>();
        if (!slots.Available.Wait(0))
        {
            ctx.Response.StatusCode = 429;
            await ctx.Response.WriteAsJsonAsync(new { error = "并发控制台连接已达 16 条。" });
            return;
        }
        try
        {
            await Run(ctx, peer => ConsoleSocket.Run(peer, request, services.GetRequiredService<ConsoleSources>(), grant, access),
                access?.Revoked ?? default);
        }
        finally { slots.Available.Release(); }
    }

    private static async Task Run(HttpContext context, Func<SocketPeer, Task> session, CancellationToken upstream = default)
    {
        using var registration = context.RequestServices.GetRequiredService<SocketSessions>().Enter();
        if (registration == null)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsJsonAsync(new { error = "服务正在停止。" });
            return;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,
            context.RequestServices.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().ApplicationStopping);
        var auth = context.RequestServices.GetRequiredService<Authentication>();
        var grant = (SocketAuthorization)context.Items[HttpAccess.SessionKey]!;
        var active = new ActiveSocket(() => { context.Abort(); stop.Cancel(); });
        auth.Register(grant, active);
        try
        {
            using var expiry = new CancellationTokenSource();
            if (grant.LoginExpiresAt is { } until) expiry.CancelAfter(TimeSpan.FromTicks(Math.Max(0, auth.Remaining(until).Ticks)));
            using var expired = expiry.Token.Register(() => _ = active.Revoke());
            using var revoked = upstream.Register(() => _ = active.Revoke());
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var writer = context.RequestServices.GetRequiredService<ConsoleTransport>();
            var peer = new SocketPeer(socket, stop, active, (data, type, token) => writer.Send(grant, socket, data, type, token));
            try { await session(peer); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (WebSocketException) { }
            catch (InvalidDataException error)
            {
                await peer.Close(WebSocketCloseStatus.MessageTooBig, error.Message);
            }
            finally { await peer.Close(WebSocketCloseStatus.NormalClosure, "连接结束。"); active.Dispose(); }
        }
        finally { auth.Unregister(active); }
    }
}

internal class ConsoleTransport
{
    internal virtual ValueTask Send(SocketAuthorization grant, WebSocket socket, ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken token)
        => socket.SendAsync(data, type, true, token);
}

internal sealed class SocketPeer(WebSocket socket, CancellationTokenSource lifetime, ActiveSocket? active = null,
    Func<ReadOnlyMemory<byte>, WebSocketMessageType, CancellationToken, ValueTask>? send = null)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim writer = new(1);
    internal CancellationToken Token => lifetime.Token;
    internal void Stop() => lifetime.Cancel();
    internal Task Send(object message) => Write(JsonSerializer.SerializeToUtf8Bytes(message, Json), WebSocketMessageType.Text);
    internal Task Error(string message) => Send(new { type = "error", error = DisplayText.Clean(message) });

    internal async Task Write(ReadOnlyMemory<byte> data, WebSocketMessageType type)
    {
        await writer.WaitAsync(Token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            if (active == null) await socket.SendAsync(data, type, true, timeout.Token);
            else await active.Deliver(() => send!(data, type, timeout.Token),
                type == WebSocketMessageType.Binary && data.Length > 2 && data.Span[2] is 1 or 3 or 4);
        }
        catch { Stop(); throw; }
        finally { writer.Release(); }
    }

    internal async Task Close(WebSocketCloseStatus status, string message)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await socket.CloseOutputAsync(status, message, timeout.Token); }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException) { }
    }

    internal async Task Receive(Func<JsonElement, Task> command, Action<ReadOnlyMemory<byte>>? binary = null)
    {
        byte[] chunk = new byte[16384];
        while (!Token.IsCancellationRequested)
        {
            using var body = new ConsolePacket(1024);
            ValueWebSocketReceiveResult part;
            do
            {
                part = await socket.ReceiveAsync(chunk.AsMemory(), Token);
                if (part.MessageType == WebSocketMessageType.Close) return;
                int limit = binary == null ? 65536 : part.MessageType == WebSocketMessageType.Binary
                    ? ExHyperV.Services.ConsolePayload.MaxAudioPacketBytes + 34 : 46 * 1024 * 1024;
                if (body.Length + part.Count > limit) throw new InvalidDataException("消息超过大小限制。");
                body.Write(chunk, 0, part.Count);
            } while (!part.EndOfMessage);
            if (socket.State == WebSocketState.CloseSent) continue;
            var data = body.Data;
            try
            {
                if (part.MessageType == WebSocketMessageType.Binary)
                {
                    if (binary == null) throw new ArgumentException("此连接仅接受 JSON 消息。");
                    binary(data);
                    continue;
                }
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.GetProperty("type").GetString() == "ping") await Send(new { type = "pong" });
                else await command(root);
            }
            catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or FormatException
                or KeyNotFoundException or OverflowException or UnauthorizedAccessException)
            { await Error(error.Message); }
        }
    }
}

// A subscription owns its producer until the previous sample or send has drained.
internal sealed class SocketWorker : IAsyncDisposable
{
    private readonly CancellationTokenSource stop;
    private readonly Task producer;
    internal SocketWorker(SocketPeer peer, Func<CancellationToken, Task> run)
    {
        stop = CancellationTokenSource.CreateLinkedTokenSource(peer.Token);
        producer = Task.Run(async () =>
        {
            try { await run(stop.Token); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (WebSocketException) { peer.Stop(); }
            catch (Exception error) when (!peer.Token.IsCancellationRequested) { await peer.Error(error.Message); }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        try { await producer; }
        finally { stop.Dispose(); }
    }
}
