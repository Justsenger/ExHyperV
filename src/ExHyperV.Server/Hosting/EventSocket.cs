using System.Text.Json;
using ExHyperV.Server.Api;

namespace ExHyperV.Server.Hosting;

internal static class EventSocket
{
    internal static async Task Run(SocketPeer peer, ApiExecution execution, ApiObjects objects)
    {
        var watches = new Dictionary<(bool Operation, Guid Id), SocketWorker>();
        try { await peer.Receive(Command); }
        finally { await Task.WhenAll(watches.Values.Select(worker => worker.DisposeAsync().AsTask())); }

        async Task Command(JsonElement command)
        {
            string? type = command.GetProperty("type").GetString();
            bool operation = type == "subscribeOperation" || type == "unsubscribe" && command.TryGetProperty("operationId", out _);
            var id = command.GetProperty(operation ? "operationId" : "subscriptionId").GetGuid();
            var key = (operation, id);
            if (type == "unsubscribe")
            {
                if (watches.Remove(key, out var worker)) await worker.DisposeAsync();
                await peer.Send(new { type = "unsubscribed", id });
                return;
            }
            if (type is not ("subscribeOperation" or "subscribeEvent")) throw new ArgumentException("未知的事件指令。");
            if (watches.ContainsKey(key)) return;
            if (watches.Count >= 64) throw new ArgumentException("每条连接最多监听 64 个订阅。");
            var feed = operation ? execution.Get(id).Feed : execution.ClaimSubscription(id);
            watches.Add(key, new(peer, async token =>
            {
                var scope = new FrameScope();
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        // Arm the notification before taking the snapshot to avoid losing a concurrent publish.
                        var changed = feed.NextChange();
                        scope.CapacityAvailable = null;
                        if (operation)
                            await peer.Send(new { type = "operation", operationId = id, status = execution.Status(id, 128, scope) });
                        else
                            await peer.Send(new { type = "event", subscriptionId = id, batch = feed.Read(128, scope) });
                        if (scope.CapacityAvailable is { } capacity) changed = Task.WhenAny(changed, capacity);
                        await changed.WaitAsync(token);
                    }
                }
                finally
                {
                    try { if (!operation) execution.ReleaseSubscription(id); }
                    finally { objects.CloseFrameScope(scope); }
                }
            }));
        }
    }
}
