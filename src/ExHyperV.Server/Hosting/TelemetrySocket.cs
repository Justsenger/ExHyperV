using System.Text.Json;
using ExHyperV.Server.Api;

namespace ExHyperV.Server.Hosting;

internal interface ITelemetrySampler : IAsyncDisposable
{
    Task<object?> HostAsync(string metric, CancellationToken token);
    Task<IReadOnlyDictionary<Guid, object?>> VirtualMachinesAsync(string metric, Guid[] ids, CancellationToken token);
}

internal sealed record TelemetryRequest(bool Host, Guid[] VmIds, string[] Metrics, int IntervalMs = 1000)
{
    internal static readonly string[] Supported = ["cpu", "memory", "disk", "network", "gpu", "hostThreads"];
    internal TelemetryRequest Validate(LaunchOptions options)
    {
        if (VmIds == null || VmIds.Length > 256 || VmIds.Contains(Guid.Empty) || !Host && VmIds.Length == 0)
            throw new ArgumentException("请选择宿主机或最多 256 个有效虚拟机编号。");
        if (Metrics is not { Length: > 0 } || Metrics.Except(Supported).Any()
            || !Host && Metrics.Contains("hostThreads"))
            throw new ArgumentException("遥测指标无效，hostThreads 需要订阅宿主机。");
        if (Host && !options.Toolsets.Contains("host")) throw new UnauthorizedAccessException("宿主遥测需要 host 工具组。");
        if (VmIds.Length > 0 && !options.Toolsets.Contains("vm")) throw new UnauthorizedAccessException("虚拟机遥测需要 vm 工具组。");
        return this with { IntervalMs = Math.Clamp(IntervalMs, 500, 10000), VmIds = VmIds.Distinct().ToArray(), Metrics = Metrics.Distinct().ToArray() };
    }
}

internal static class TelemetrySocket
{
    internal static async Task Run(SocketPeer peer, LaunchOptions options, Func<ITelemetrySampler> create)
    {
        SocketWorker? worker = null;
        try
        {
            await peer.Receive(async body =>
            {
                var type = body.GetProperty("type").GetString();
                if (type == "ping") { await peer.Send(new { type = "pong" }); return; }
                if (type is not ("subscribe" or "unsubscribe")) throw new ArgumentException("未知的遥测指令。");
                var request = type == "subscribe" ? body.Deserialize<TelemetryRequest>(SocketPeer.Json)!.Validate(options) : null;
                if (worker != null) { await worker.DisposeAsync(); worker = null; }
                if (request == null) { await peer.Send(new { type = "unsubscribed" }); return; }
                await peer.Send(new { type = "subscribed", request.IntervalMs });
                worker = new(peer, async token =>
                {
                    token.ThrowIfCancellationRequested();
                    await using var sampler = create();
                    while (!token.IsCancellationRequested)
                    {
                        var sample = await Sample(sampler, request, token);
                        token.ThrowIfCancellationRequested();
                        await peer.Send(sample);
                        await Task.Delay(request.IntervalMs, token);
                    }
                });
            });
        }
        finally { if (worker != null) await worker.DisposeAsync(); }
    }

    private static async Task<object> Sample(ITelemetrySampler sampler, TelemetryRequest request, CancellationToken token)
    {
        var host = request.Host ? new Dictionary<string, object?>() : null;
        var vms = request.VmIds.ToDictionary(id => id, _ => new Dictionary<string, object?>());
        foreach (var metric in request.Metrics)
        {
            token.ThrowIfCancellationRequested();
            if (host != null)
            {
                try { host[metric] = Reading(await sampler.HostAsync(metric, token), "本次没有有效样本。"); }
                catch (Exception error) when (!token.IsCancellationRequested) { host[metric] = Issue(error.Message); }
            }
            if (vms.Count == 0 || metric == "hostThreads") continue;
            token.ThrowIfCancellationRequested();
            try
            {
                var readings = await sampler.VirtualMachinesAsync(metric, request.VmIds, token);
                foreach (var (id, values) in vms) values[metric] = Reading(readings.GetValueOrDefault(id), "未取得该虚拟机的样本。");
            }
            catch (Exception error) when (!token.IsCancellationRequested)
            { foreach (var values in vms.Values) values[metric] = Issue(error.Message); }
        }
        return new { type = "telemetry", timestamp = DateTimeOffset.UtcNow, host, vms };
    }

    private static object Reading(object? value, string missing)
    {
        if (value == null) return Issue(missing);
        // Finish each metric's serialization here so a bad counter cannot end the subscription.
        try { return JsonSerializer.SerializeToElement(value, SocketPeer.Json); }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException)
        { return Issue("此指标无法编码为 JSON。"); }
    }

    internal static object Issue(string message) => new { state = "Unavailable", issue = DisplayText.Clean(message) };
}
