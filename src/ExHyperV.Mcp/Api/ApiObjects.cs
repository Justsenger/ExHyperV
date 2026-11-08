using ExHyperV.Services;

namespace ExHyperV.Mcp.Api;

public sealed class ApiObjects
{
    internal sealed class Entry(object value, bool owned, long sequence)
    {
        public object Value { get; } = value;
        public bool Owned { get; set; } = owned;
        public long Sequence { get; } = sequence;
        public int Uses;
        public bool Releasing;
        public HashSet<Guid> Parents { get; } = [];
        public SemaphoreSlim Calls { get; } = new(1, 1);
    }

    private readonly object gate = new();
    private readonly Dictionary<Guid, Entry> entries = [];
    private readonly Dictionary<object, Guid> identities = new(ReferenceEqualityComparer.Instance);
    private long sequence;
    private bool stopping;

    public int Count { get { lock (gate) return entries.Count; } }
    internal int CountOf<T>() { lock (gate) return entries.Values.Count(entry => entry.Value is T); }

    // Stop admitting new work at the budget; always register resources already returned by Core
    // so cleanup receipts remain reachable even when a single result exceeds the budget.
    public void RequireCapacity(long additionalBytes = 0)
    {
        lock (gate)
        {
            if (stopping) throw new InvalidOperationException("MCP 正在退出。");
            if (entries.Count >= 4096) throw new InvalidOperationException("已保留 4096 个对象，请先释放已使用完的资源。");
            long bytes = entries.Values.Sum(entry => RetainedBytes(entry.Value));
            if (bytes + additionalBytes > 128L * 1024 * 1024) throw new InvalidOperationException("二进制缓冲已达到 128 MiB，请先释放已使用完的数据。");
        }
    }

    private static long RetainedBytes(object value)
    {
        try { return value switch { byte[] buffer => buffer.LongLength, MemoryStream stream => stream.Length, _ => 0L }; }
        catch (ObjectDisposedException) { return 0; }
    }

    internal void RequestStop()
    {
        GpuDeploymentSession[] gpu;
        lock (gate) { stopping = true; gpu = entries.Values.Select(entry => entry.Value).OfType<GpuDeploymentSession>().ToArray(); }
        foreach (var session in gpu) session.RequestCancellation();
    }

    internal async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (gate) if (entries.Values.All(entry => entry.Uses == 0)) return;
            await Task.Delay(20, cancellationToken);
        }
    }

    public Guid Add(object value, bool owned = true, Guid? parent = null)
    {
        lock (gate)
        {
            if (identities.TryGetValue(value, out var existing))
            {
                // Borrowing an existing object never transfers ownership to the caller.
                if (parent.HasValue && parent != existing) entries[existing].Parents.Add(parent.Value);
                return existing;
            }
            Guid id = value is GpuDeploymentSession gpu ? gpu.GetStatus().SessionId : Guid.NewGuid();
            var entry = new Entry(value, owned, ++sequence);
            if (parent.HasValue) entry.Parents.Add(parent.Value);
            entries.Add(id, entry);
            identities.Add(value, id);
            return id;
        }
    }

    public object Get(Guid id)
    {
        lock (gate)
            return entries.TryGetValue(id, out var entry) && !entry.Releasing ? entry.Value
                : throw new ArgumentException("对象编号已失效，请重新取得 Core 对象。");
    }

    internal void Acquire(Guid id, HashSet<Guid> held)
    {
        lock (gate)
        {
            _ = Get(id);
            var previous = held.ToHashSet();
            try { AcquireTree(id, held); }
            catch
            {
                foreach (Guid added in held.Except(previous).ToArray()) { entries[added].Uses--; held.Remove(added); }
                throw;
            }
        }
    }

    private void AcquireTree(Guid id, HashSet<Guid> held)
    {
        _ = Get(id);
        if (!held.Add(id)) return;
        entries[id].Uses++;
        foreach (Guid parent in entries[id].Parents) AcquireTree(parent, held);
    }

    internal void Return(HashSet<Guid> held)
    {
        lock (gate)
        {
            foreach (Guid id in held) if (entries.TryGetValue(id, out var entry)) entry.Uses--;
            held.Clear();
        }
    }

    public sealed class Lease : IDisposable
    {
        private readonly ApiObjects owner;
        private readonly HashSet<Guid> held = [];
        public object Value { get; }
        internal Lease(ApiObjects owner, Guid id) { this.owner = owner; owner.Acquire(id, held); Value = owner.Get(id); }
        public void Dispose() { owner.Return(held); held.Clear(); }
    }
    public Lease Use(Guid id) => new(this, id);

    internal SemaphoreSlim Calls(Guid id) { lock (gate) return entries[id].Calls; }
    internal void RequireSoleUse(Guid id)
    {
        lock (gate) if (entries[id].Uses > 1) throw new InvalidOperationException("对象仍有其他调用或事件订阅，请先结束它们再释放。");
    }

    public object[] List()
    {
        lock (gate) return entries.Select(pair => (object)new
        { handle = pair.Key, type = ApiCatalog.TypeName(pair.Value.Value.GetType()), owned = pair.Value.Owned, activeCalls = pair.Value.Uses }).ToArray();
    }

    public async Task<object> ReleaseAsync(Guid id)
    {
        Entry entry;
        lock (gate)
        {
            _ = Get(id);
            entry = entries[id];
            if (entry.Uses != 0) throw new InvalidOperationException("对象仍有执行中的调用，请先等待操作结束或请求取消。");
            entry.Releasing = true;
        }
        try
        {
            if (entry.Owned)
            {
                if (entry.Value is GpuDeploymentSession gpu)
                {
                    var result = await gpu.CancelAsync();
                    if (result.HasPendingCleanup || result.OutcomeUnknown)
                        throw new InvalidOperationException($"GPU 流程仍需核对清理状态：{result.CleanupError ?? result.Error}");
                }
                if (entry.Value is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                else if (entry.Value is IDisposable disposable) disposable.Dispose();
                if (entry.Value is VmImportSession import && import.CleanupState.ToString() is "Failed" or "Deferred")
                    throw new InvalidOperationException($"导入工作资源仍需清理：{import.CleanupError}");
            }
            lock (gate)
            {
                entries.Remove(id); identities.Remove(entry.Value);
                // Borrowed children are invalid once their owning parent has been released.
                InvalidateChildren(id);
            }
            return new { success = true, handle = id };
        }
        catch { lock (gate) entry.Releasing = false; throw; }
    }

    private void InvalidateChildren(Guid parent)
    {
        foreach (var pair in entries.Where(pair => !pair.Value.Owned && pair.Value.Parents.Contains(parent)).ToArray())
        {
            entries.Remove(pair.Key); identities.Remove(pair.Value.Value);
            InvalidateChildren(pair.Key);
        }
        foreach (var entry in entries.Values) entry.Parents.Remove(parent);
    }

    internal Guid[] ReverseOrder()
    {
        lock (gate) return entries.OrderByDescending(pair => pair.Value.Sequence).Select(pair => pair.Key).ToArray();
    }
}
