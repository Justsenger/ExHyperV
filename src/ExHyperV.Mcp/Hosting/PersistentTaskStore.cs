using System.Collections.Immutable;
using System.Text.Json;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace ExHyperV.Mcp.Hosting;

// Protocol task state is durable; Windows/Core execution remains in the creating process.
// A restarted process reports interrupted work for reconciliation instead of replaying writes.
public sealed class PersistentTaskStore : IMcpTaskStore, IDisposable
{
    private readonly string directory;
    private readonly string instance = Guid.NewGuid().ToString("N");
    private readonly object gate = new();
    private readonly Dictionary<string, McpTaskInfo> tasks = [];
    private readonly FileStream ownerLock;
    private int disposed;
    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived;

    public PersistentTaskStore() : this(Path.Combine(InvocationJournal.StateDirectory, "tasks")) { }
    public PersistentTaskStore(string directory)
    {
        this.directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        ownerLock = new FileStream(Path.Combine(directory, instance + ".lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }

    public Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            Sweep();
            if (tasks.Count >= 256) throw new InvalidOperationException("已保留 256 个协议任务，请用 server_task_release 释放已读取的结束记录。");
            var now = DateTimeOffset.UtcNow;
            var info = new McpTaskInfo(instance + "-" + Guid.NewGuid().ToString("N"), McpTaskStatus.Working, now, now, TimeToLive: TimeSpan.FromHours(24), PollIntervalMs: 1000);
            Save(info); tasks.Add(info.TaskId, info);
            return Task.FromResult(info);
        }
    }

    public Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ValidateId(taskId);
            if (tasks.TryGetValue(taskId, out var info)) return Task.FromResult<McpTaskInfo?>(info);
            string file = FileName(taskId);
            if (!File.Exists(file)) return Task.FromResult<McpTaskInfo?>(null);
            info = JsonSerializer.Deserialize<McpTaskInfo>(File.ReadAllText(file))!;
            // Another live server owns its execution and input waiters.
            if (OwnerAlive(taskId[..32])) throw new InvalidOperationException("任务由另一个运行中的 MCP 进程持有，请连接原服务查询。");
            if (!Terminal(info.Status)) info = info with
            {
                Status = McpTaskStatus.Failed, InputRequests = null,
                Error = JsonSerializer.SerializeToElement(new { code = -32603, message = "原执行进程已结束。请依据 server_requests 核对资源状态，操作保持原样，等待核实。" })
            };
            return Task.FromResult<McpTaskInfo?>(info);
        }
    }

    public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken = default)
    { Update(taskId, info => info with { Status = McpTaskStatus.Completed, Result = result.Clone(), InputRequests = null }); return Task.CompletedTask; }
    public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken = default)
    { Update(taskId, info => info with { Status = McpTaskStatus.Failed, Error = error.Clone(), InputRequests = null }); return Task.CompletedTask; }
    public Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken = default)
    {
        // SDK cancellation terminates the protocol request. Core operation IDs remain queryable.
        return Task.FromResult(Update(taskId, info => info with { Status = McpTaskStatus.Cancelled, InputRequests = null,
            StatusMessage = "已请求取消。底层操作及资源清理由 core_operation_status 核对。" }));
    }

    public Task SetInputRequestsAsync(string taskId, IDictionary<string, InputRequest> inputRequests, CancellationToken cancellationToken = default)
    {
        Update(taskId, info => info with { Status = McpTaskStatus.InputRequired,
            InputRequests = (info.InputRequests ?? ImmutableDictionary<string, InputRequest>.Empty).ToImmutableDictionary().SetItems(inputRequests) });
        return Task.CompletedTask;
    }

    public Task ResolveInputRequestsAsync(string taskId, IDictionary<string, InputResponse> inputResponses, CancellationToken cancellationToken = default)
    {
        List<KeyValuePair<string, InputResponse>> accepted = [];
        Update(taskId, info =>
        {
            var pending = (info.InputRequests ?? ImmutableDictionary<string, InputRequest>.Empty).ToImmutableDictionary();
            foreach (var pair in inputResponses)
                if (pending.ContainsKey(pair.Key)) { pending = pending.Remove(pair.Key); accepted.Add(pair); }
            return info with { InputRequests = pending, Status = pending.Count == 0 ? McpTaskStatus.Working : McpTaskStatus.InputRequired };
        });
        foreach (var pair in accepted) InputResponseReceived?.Invoke(new() { TaskId = taskId, RequestId = pair.Key, Response = pair.Value });
        return Task.CompletedTask;
    }

    public void Release(string taskId)
    {
        lock (gate)
        {
            ValidateId(taskId);
            if (tasks.TryGetValue(taskId, out var info))
            {
                if (!Terminal(info.Status)) throw new InvalidOperationException("此协议任务仍在执行或等待输入。");
                File.Delete(FileName(taskId)); tasks.Remove(taskId);
                return;
            }
            if (!File.Exists(FileName(taskId))) return;
            if (OwnerAlive(taskId[..32])) throw new InvalidOperationException("请连接持有此任务的 MCP 进程进行清理。");
            info = JsonSerializer.Deserialize<McpTaskInfo>(File.ReadAllText(FileName(taskId)))!;
            if (!Terminal(info.Status)) throw new InvalidOperationException("原任务执行中断，请保留记录并核对 server_requests 中的资源状态。");
            File.Delete(FileName(taskId));
        }
    }

    private bool Update(string id, Func<McpTaskInfo, McpTaskInfo> update)
    {
        lock (gate)
        {
            ValidateId(id);
            if (!tasks.TryGetValue(id, out var current) || Terminal(current.Status)) return false;
            var next = update(current) with { LastUpdatedAt = DateTimeOffset.UtcNow };
            Save(next); tasks[id] = next; return true;
        }
    }
    private void Save(McpTaskInfo info)
    {
        string file = FileName(info.TaskId), temporary = file + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, info); stream.Flush(flushToDisk: true); }
        File.Move(temporary, file, overwrite: true);
    }
    private void Sweep()
    {
        foreach (var info in tasks.Values.Where(info => Terminal(info.Status) && DateTimeOffset.UtcNow - info.LastUpdatedAt > TimeSpan.FromHours(24)).ToArray())
        { File.Delete(FileName(info.TaskId)); tasks.Remove(info.TaskId); }
        foreach (string file in Directory.EnumerateFiles(directory, "*.json").Where(file => DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromDays(7)))
        {
            var info = JsonSerializer.Deserialize<McpTaskInfo>(File.ReadAllText(file))!;
            if (Terminal(info.Status) && !OwnerAlive(info.TaskId[..32])) File.Delete(file);
        }
    }
    private bool OwnerAlive(string id)
    {
        string file = Path.Combine(directory, id + ".lock");
        if (!File.Exists(file)) return false;
        try { using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }
    private static bool Terminal(McpTaskStatus status) => status is McpTaskStatus.Completed or McpTaskStatus.Failed or McpTaskStatus.Cancelled;
    private string FileName(string id) => Path.Combine(directory, id + ".json");
    private static void ValidateId(string id)
    {
        if (id.Length != 65 || id[32] != '-' || !Guid.TryParseExact(id[..32], "N", out _) || !Guid.TryParseExact(id[33..], "N", out _))
            throw new ArgumentException("协议任务编号无效。");
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        ownerLock.Dispose(); File.Delete(Path.Combine(directory, instance + ".lock"));
    }
}
