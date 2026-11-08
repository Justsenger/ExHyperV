using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ExHyperV.Mcp.Hosting;

// Persist identifiers and outcomes, never request arguments, passwords or full result payloads.
public sealed class InvocationJournal
{
    public sealed record Receipt(string Id, string Fingerprint, string Tool, string State, DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt = null, Guid? OperationId = null, string? ApiId = null, IReadOnlyDictionary<string, string>? ResourceIds = null);
    private readonly string directory;
    private readonly object gate = new();
    private readonly Dictionary<string, (string Fingerprint, Lazy<Task<CallToolResult>> Result)> running = [];
    private readonly Dictionary<string, Task> operations = [];

    internal static string StateDirectory => Environment.GetEnvironmentVariable("EXHYPERV_MCP_STATE_DIRECTORY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExHyperV", "Mcp");
    public InvocationJournal() : this(Path.Combine(StateDirectory, "requests")) { }
    public InvocationJournal(string directory) { this.directory = Path.GetFullPath(directory); }

    public Task<CallToolResult> RunAsync(string? key, string tool, JsonElement arguments, Func<Task<CallToolResult>> action,
        Func<Guid, Task<CallToolResult>>? operationResult = null)
    {
        if (key?.Length > 128) throw new ArgumentException("requestKey 最多 128 个字符。");
        string id = key == null ? Guid.NewGuid().ToString("N") : Hash(key);
        string fingerprint = Hash(tool + "\n" + Canonical(arguments));
        lock (gate)
        {
            if (running.TryGetValue(id, out var previous))
            {
                if (previous.Fingerprint != fingerprint) throw new ArgumentException("此 requestKey 已用于其他参数，请核对原请求。");
                return previous.Result.Value;
            }
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, id + ".json");
            if (File.Exists(file))
                throw new InvalidOperationException("此 requestKey 已有执行记录。请用 server_requests 核对实际状态；原进程结果无法直接重放。");
            Prune();
            if (Directory.EnumerateFiles(directory, "*.json").Take(1024).Count() >= 1024)
                throw new InvalidOperationException("请求记录已达到 1024 项，请核对并清理 server_requests 中的结束记录。");
            string? apiId = arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty("apiId", out var api) ? api.GetString() : null;
            var receipt = new Receipt(id, fingerprint, tool, "Running", DateTimeOffset.UtcNow, ApiId: apiId, ResourceIds: ResourceIds(arguments));
            using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            { JsonSerializer.Serialize(stream, receipt); stream.Flush(flushToDisk: true); }
            var task = new Lazy<Task<CallToolResult>>(() => Execute(receipt, file, action, operationResult), LazyThreadSafetyMode.ExecutionAndPublication);
            running.Add(id, (fingerprint, task));
            return task.Value;
        }
    }

    private async Task<CallToolResult> Execute(Receipt receipt, string file, Func<Task<CallToolResult>> action, Func<Guid, Task<CallToolResult>>? operationResult)
    {
        try
        {
            var result = await action();
            var data = result.StructuredContent;
            Guid? operation = data is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty("operationId", out var id) && id.TryGetGuid(out var parsed) ? parsed : null;
            bool accepted = data is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("accepted", out var flag) && flag.ValueKind == JsonValueKind.True;
            Write(file, receipt with { State = accepted ? "Accepted" : result.IsError == true ? "RequiresVerification" : "Completed", EndedAt = DateTimeOffset.UtcNow, OperationId = operation });
            if (accepted && operation.HasValue && operationResult != null)
            {
                var pending = operationResult(operation.Value);
                lock (gate) operations[receipt.Id] = pending;
                _ = TrackOperation(receipt, file, operation.Value, pending);
            }
            return result;
        }
        catch
        {
            Write(file, receipt with { State = "RequiresVerification", EndedAt = DateTimeOffset.UtcNow });
            throw;
        }
    }

    private async Task TrackOperation(Receipt receipt, string file, Guid id, Task<CallToolResult> pending)
    {
        try
        {
            var result = await pending;
            lock (gate)
                if (File.Exists(file)) Write(file, receipt with { State = result.IsError == true ? "RequiresVerification" : "Completed",
                    EndedAt = DateTimeOffset.UtcNow, OperationId = id });
        }
        catch { /* The durable Accepted receipt remains available for reconciliation. */ }
    }

    public object List(int offset = 0, int limit = 50)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentException("offset 从 0 开始，limit 为 1 至 100。");
        lock (gate)
        {
            var records = ReadAll().OrderByDescending(record => record.StartedAt).ToArray();
            return new { total = records.Length, offset, entries = records.Skip(offset).Take(limit)
                .Select(record => new { record.Id, record.Tool, record.ApiId, record.ResourceIds, record.State, record.StartedAt, record.EndedAt, record.OperationId,
                    sameProcess = running.ContainsKey(record.Id), needsVerification = record.State is "Running" or "Accepted" or "RequiresVerification" }) };
        }
    }

    private static IReadOnlyDictionary<string, string> ResourceIds(JsonElement arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Read(JsonElement input)
        {
            if (input.ValueKind != JsonValueKind.Object) return;
            foreach (var property in input.EnumerateObject())
            {
                if (property.Name.Equals("arguments", StringComparison.OrdinalIgnoreCase)) { Read(property.Value); continue; }
                if (!new[] { "vmId", "sessionId", "handle", "target", "diskId", "gpuInstanceId", "operationId", "subscriptionId" }.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) continue;
                if (property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) continue;
                string value = property.Value.ToString();
                if (value.Length <= 512) result[property.Name] = value;
            }
        }
        Read(arguments);
        return result;
    }

    public void Forget(string id, bool verified = false)
    {
        if (id.Length is not (32 or 64) || !id.All(char.IsAsciiHexDigit)) throw new ArgumentException("请使用 server_requests 返回的记录 ID。");
        lock (gate)
        {
            if (running.TryGetValue(id, out var entry) && !entry.Result.Value.IsCompleted) throw new InvalidOperationException("此请求仍在执行。");
            if (operations.TryGetValue(id, out var pending) && !pending.IsCompleted) throw new InvalidOperationException("此请求的 Core 操作仍在执行。");
            string file = Path.Combine(directory, id + ".json");
            var receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(file))!;
            if (!verified && receipt.State is "Running" or "Accepted" or "RequiresVerification") throw new InvalidOperationException("请先核对实际资源状态，再用 verified=true 确认清理收据。");
            File.Delete(file); running.Remove(id); operations.Remove(id);
        }
    }

    private IEnumerable<Receipt> ReadAll() => Directory.Exists(directory)
        ? Directory.EnumerateFiles(directory, "*.json").Select(file => JsonSerializer.Deserialize<Receipt>(File.ReadAllText(file))!) : [];
    private void Prune()
    {
        foreach (var receipt in ReadAll().Where(record => record.State is "Completed" or "Failed" && DateTimeOffset.UtcNow - record.EndedAt > TimeSpan.FromDays(7)))
        { File.Delete(Path.Combine(directory, receipt.Id + ".json")); running.Remove(receipt.Id); operations.Remove(receipt.Id); }
    }
    private static void Write(string file, Receipt receipt)
    {
        string temporary = file + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, receipt); stream.Flush(flushToDisk: true); }
        File.Move(temporary, file, overwrite: true);
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => JsonSerializer.Serialize(property.Name) + ":" + Canonical(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.Undefined => "null", _ => value.GetRawText()
    };
}
