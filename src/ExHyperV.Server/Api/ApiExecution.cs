using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using ExHyperV.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ExHyperV.Server.Api;

internal sealed class ApiOperation
{
    public Guid Id { get; } = Guid.NewGuid();
    public CancellationTokenSource Cancellation { get; } = new();
    internal HashSet<Guid> Held { get; } = [];
    public required ApiFeed Feed { get; init; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; internal set; }
    public string? FinalState { get; internal set; }
    public string? ApiId { get; init; }
    public bool Started { get; internal set; }
    public bool RetainResult { get; init; }
    public Task<OperationResult> Task { get; set; } = null!;
    internal ConcurrentDictionary<Guid, ApiCallback> Decisions { get; } = new();
}

internal sealed record ApiCallback(Type ReturnType, TaskCompletionSource<object?> Completion, string Name, object?[] Arguments);

internal sealed class ApiFeed(ApiCodec codec) : IDisposable
{
    private sealed record Packet(long Sequence, DateTimeOffset Time, string Name, object?[] Arguments, Guid? RequestId);
    private readonly object gate = new();
    private readonly Queue<Packet> packets = [];
    private long sequence, dropped;
    private bool disposed;
    private TaskCompletionSource? changed;
    internal Task NextChange()
    {
        lock (gate)
        {
            if (disposed) throw new ArgumentException("订阅已结束。");
            return (changed ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    internal void Signal()
    {
        lock (gate) { changed?.TrySetResult(); changed = null; }
    }
    public void Publish(string name, object?[] args, Guid? requestId = null)
    {
        lock (gate)
        {
            if (disposed) return;
            // A video event keeps only the latest lease. Pixel pools must remain available to Core.
            bool frame = args.Any(item => item is ConsoleFrame);
            while (packets.Count >= (frame ? 1 : 128)) { DisposePacket(packets.Dequeue()); dropped++; }
            var retained = new List<object?>();
            try
            {
                foreach (var item in args) retained.Add(item is ConsoleFrame image ? image.Retain() : codec.Snapshot(item));
            }
            catch { foreach (var image in retained.OfType<ConsoleFrame>()) image.Dispose(); throw; }
            packets.Enqueue(new(++sequence, DateTimeOffset.UtcNow, name, retained.ToArray(), requestId));
            Signal();
        }
    }

    public object Read(int limit, FrameScope? scope = null)
    {
        if (limit is < 1 or > 128) throw new ArgumentException("事件条数为 1 至 128。");
        lock (gate)
        {
            List<object> result = [];
            bool limited = false;
            while (packets.Count > 0 && result.Count < limit)
            {
                var packet = packets.Peek();
                if (!codec.TryTransferPacket(packet.Arguments, out var arguments, scope)) { limited = true; break; }
                result.Add(new { packet.Sequence, packet.Time, packet.Name, packet.RequestId, arguments });
                packets.Dequeue();
            }
            return new { events = result, dropped, remaining = packets.Count, resourceLimitReached = limited || !codec.CanRetain };
        }
    }

    private static void DisposePacket(Packet packet) { foreach (var frame in packet.Arguments.OfType<ConsoleFrame>()) frame.Dispose(); }
    public void Dispose() { lock (gate) { disposed = true; Signal(); while (packets.TryDequeue(out var packet)) DisposePacket(packet); } }
}

internal sealed class ApiExecution(ApiCatalog catalog, ApiObjects objects, ApiCodec codec, ILogger<ApiExecution> logger) : IHostedService
{
    private sealed record ByRefResult(object? Value, Dictionary<string, object?> Arguments);
    private readonly ConcurrentDictionary<Guid, ApiOperation> operations = new();
    private readonly ConcurrentDictionary<Guid, Subscription> subscriptions = new();
    private readonly object lifecycle = new();
    private readonly SemaphoreSlim concurrency = new(8);
    private bool stopping;
    private sealed record Subscription(EventInfo Event, ApiObjects.Lease? Lease, Delegate Handler, ApiFeed Feed);

    public ApiOperation Start(Func<ApiOperation, Task<object?>> action, bool retainResult = false, string? apiId = null)
    {
        lock (lifecycle)
        {
            if (stopping) throw new InvalidOperationException("服务器正在退出。");
            objects.RequireCapacity();
            if (operations.Count >= 256) throw new InvalidOperationException("已保留 256 个操作，请用 DELETE /api/v1/operations/{operationId} 清理已读取的结束记录。");
            var operation = new ApiOperation { Feed = new(codec), RetainResult = retainResult, ApiId = apiId };
            operation.Task = Task.Run(() => RunAsync(operation, action));
            operations[operation.Id] = operation;
            _ = operation.Task.ContinueWith(_ => operation.Feed.Signal(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return operation;
        }
    }

    private async Task<OperationResult> RunAsync(ApiOperation operation, Func<ApiOperation, Task<object?>> action)
    {
        bool entered = false;
        OperationResult response;
        try
        {
            await concurrency.WaitAsync(operation.Cancellation.Token);
            entered = true;
            operation.Started = true;
            operation.Feed.Signal();
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            object? result = await action(operation);
            var byRef = result as ByRefResult;
            object? value = byRef == null ? result : byRef.Value;
            response = OperationResult.FromCore(value, codec.Pack(value, retain: operation.RetainResult));
            if (byRef != null)
                response = response with { Result = new { result = response.Result,
                    refArguments = byRef.Arguments.ToDictionary(pair => pair.Key, pair => codec.Pack(pair.Value)) } };
        }
        catch (Exception error)
        {
            response = OperationResult.FromException(error, codec);
        }
        finally
        {
            lock (operation) objects.Return(operation.Held);
            if (entered) concurrency.Release();
        }
        operation.FinalState = response.ErrorKind switch
        {
            "OutcomeUnknown" => "RequiresVerification",
            "Cancelled" => "Cancelled",
            _ => response.Success ? "Completed" : "Failed"
        };
        operation.CompletedAt = DateTimeOffset.UtcNow;
        return response;
    }

    public ApiOperation Get(Guid id) => operations.TryGetValue(id, out var operation) ? operation : throw new ArgumentException("操作编号已失效。");

    public async Task<object?> InvokeAsync(string apiId, JsonElement arguments, Guid? targetId, string[]? typeArguments, string[]? methodArguments, ApiOperation operation)
    {
        var definition = catalog.Find(apiId);
        if (targetId is Guid handle && definition is MethodInfo { IsStatic: false, Name: "Dispose" or "DisposeAsync" } cleanup
            && cleanup.GetParameters().Length == 0 && cleanup.DeclaringType!.IsInstanceOfType(objects.Get(handle)))
            return await objects.ReleaseAsync(handle);
        object? target = targetId.HasValue ? Hold(targetId.Value, operation) : null;
        using var cancellation = target is GpuDeploymentSession gpu ? operation.Cancellation.Token.Register(gpu.RequestCancellation) : default;
        var member = catalog.Close(definition, typeArguments, methodArguments, target);
        if (member is not MethodBase callable) throw new ArgumentException("此标识是属性、字段或事件，请选择相应的读取、写入或订阅工具。");
        if (callable is MethodInfo { IsStatic: false } && target == null) throw new ArgumentException("实例方法需要 target 对象编号。");
        var callGate = targetId.HasValue ? objects.Calls(targetId.Value) : null;
        if (callGate != null) await callGate.WaitAsync(operation.Cancellation.Token);
        try
        {
            var inputs = arguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
                : arguments.EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);
            var parameters = callable.GetParameters();
            foreach (string name in inputs.Keys)
                if (!parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"参数名无效：{name}。");
            object?[] values = parameters.Select(parameter => Argument(parameter, inputs, operation)).ToArray();
            object? result = InvokeMember(callable, target, values);
            result = await AwaitResult(result);
            var outputs = parameters.Select((parameter, index) => (parameter, index)).Where(item => item.parameter.ParameterType.IsByRef)
                .ToDictionary(item => item.parameter.Name!, item => values[item.index]);
            return outputs.Count == 0 ? result : new ByRefResult(result, outputs);
        }
        finally { callGate?.Release(); }
    }

    // MethodBase.Invoke cannot box byref-like spans. Bridge the Core byte-span contracts explicitly.
    private static readonly Dictionary<MethodBase, Func<object?, object?[], object?>> SpanCalls = new()
    {
        [typeof(ExHyperV.Vmcx.VmcxValue).GetMethod(nameof(ExHyperV.Vmcx.VmcxValue.Raw), [typeof(ReadOnlySpan<byte>)])!] =
            (_, args) => ExHyperV.Vmcx.VmcxValue.Raw((byte[])args[0]!),
        [typeof(ConsoleAudioPacket).GetConstructor([typeof(ConsoleAudioFormat), typeof(ReadOnlySpan<byte>), typeof(long), typeof(TimeSpan)])!] =
            (_, args) => new ConsoleAudioPacket((ConsoleAudioFormat)args[0]!, (byte[])args[1]!, (long)args[2]!, (TimeSpan)args[3]!),
        [typeof(IConsoleAudioInput).GetMethod(nameof(IConsoleAudioInput.SendAudio), [typeof(ConsoleAudioFormat), typeof(ReadOnlySpan<byte>)])!] =
            (target, args) => ((IConsoleAudioInput)target!).SendAudio((ConsoleAudioFormat)args[0]!, (byte[])args[1]!)
    };

    internal static bool SupportsSpanCall(MethodBase member) => SpanCalls.ContainsKey(member);

    internal static object? InvokeMember(MethodBase member, object? target, object?[] values)
    {
        if (SpanCalls.TryGetValue(member, out var call)) return call(target, values);
        return member is ConstructorInfo ctor ? ctor.Invoke(values) : ((MethodInfo)member).Invoke(target, values);
    }

    private object Hold(Guid id, ApiOperation operation) { objects.Acquire(id, operation.Held); return objects.Get(id); }

    private object? Argument(ParameterInfo parameter, Dictionary<string, JsonElement> inputs, ApiOperation operation)
    {
        Type type = parameter.ParameterType;
        if (type == typeof(CancellationToken)) return operation.Cancellation.Token;
        if (parameter.IsOut) return type.GetElementType()!.IsValueType ? Activator.CreateInstance(type.GetElementType()!) : null;
        if (ApiCatalog.IsCallback(type))
        {
            if (inputs.TryGetValue(parameter.Name!, out var callback))
            {
                if (callback.ValueKind == JsonValueKind.Null) return null;
                if (callback.ValueKind != JsonValueKind.Object || callback.GetPropertyCount() != 1)
                    throw new ArgumentException($"回调 {parameter.Name} 需要 null、$handle 或显式的 $callback 标记。");
                if (callback.TryGetProperty("$handle", out _)) return codec.Decode(callback, type, operation.Held);
                if (!callback.TryGetProperty("$callback", out var enabled) || enabled.ValueKind != JsonValueKind.True)
                    throw new ArgumentException($"回调 {parameter.Name} 的 $callback 必须为 true。");
            }
            else if (parameter.HasDefaultValue && typeof(Delegate).IsAssignableFrom(type)
                && type.GetMethod("Invoke")!.ReturnType != typeof(void)) return parameter.DefaultValue;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IProgress<>))
                return Activator.CreateInstance(typeof(ProgressSink<>).MakeGenericType(type.GetGenericArguments()), operation.Feed, parameter.Name!);
            return DelegateFor(type, args => Callback(operation, parameter.Name!, type.GetMethod("Invoke")!.ReturnType, args));
        }
        if (inputs.TryGetValue(parameter.Name!, out var value))
            return codec.Decode(value, type == typeof(ReadOnlySpan<byte>) ? typeof(byte[]) : type, operation.Held);
        if (parameter.HasDefaultValue) return parameter.DefaultValue;
        throw new ArgumentException($"缺少参数：{parameter.Name}。");
    }

    private sealed class ProgressSink<T>(ApiFeed feed, string name) : IProgress<T>
    { public void Report(T value) => feed.Publish(name, [value]); }

    internal static Delegate DelegateFor(Type type, Func<object?[], object?> callback)
    {
        var invoke = type.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name)).ToArray();
        var call = Expression.Invoke(Expression.Constant(callback), Expression.NewArrayInit(typeof(object), parameters.Select(parameter => Expression.Convert(parameter, typeof(object)))));
        Expression body = invoke.ReturnType == typeof(void) ? Expression.Block(call, Expression.Empty()) : Expression.Convert(call, invoke.ReturnType);
        return Expression.Lambda(type, body, parameters).Compile();
    }

    internal object? Callback(ApiOperation operation, string name, Type returnType, object?[] arguments)
    {
        if (returnType == typeof(void)) { operation.Feed.Publish(name, arguments); return null; }
        Type resultType = returnType.IsGenericType && (returnType.GetGenericTypeDefinition() == typeof(Task<>) || returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ? returnType.GetGenericArguments()[0] : returnType == typeof(Task) || returnType == typeof(ValueTask) ? typeof(object) : returnType;
        Guid id = Guid.NewGuid();
        var decision = new ApiCallback(resultType, new(TaskCreationOptions.RunContinuationsAsynchronously), name, arguments.Select(value => codec.Snapshot(value)).ToArray());
        lock (operation)
        {
            if (operation.Decisions.Count >= 64) throw new InvalidOperationException("待回答事项已达到 64 项，请先处理当前事项。");
            operation.Decisions[id] = decision;
            try { operation.Feed.Publish(name, arguments, id); }
            catch { operation.Decisions.TryRemove(id, out _); throw; }
        }
        async Task<object?> Wait()
        {
            try { return await decision.Completion.Task.WaitAsync(operation.Cancellation.Token); }
            finally { operation.Decisions.TryRemove(id, out _); operation.Feed.Signal(); }
        }
        Task<object?> waiting = Wait();
        if (returnType == typeof(Task)) return (Task)waiting;
        if (returnType == typeof(ValueTask)) return new ValueTask(waiting);
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() is var generic && (generic == typeof(Task<>) || generic == typeof(ValueTask<>)))
        {
            object typed = typeof(ApiExecution).GetMethod(nameof(TypedCallback), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(resultType).Invoke(null, [waiting])!;
            return generic == typeof(Task<>) ? typed : Activator.CreateInstance(returnType, [typed]);
        }
        return waiting.GetAwaiter().GetResult();
    }

    private static async Task<T> TypedCallback<T>(Task<object?> task) => (T)(await task)!;
    public void Reply(Guid operationId, Guid requestId, JsonElement value)
    {
        var operation = Get(operationId);
        lock (operation)
        {
            if (operation.Task.IsCompleted || operation.Cancellation.IsCancellationRequested || !operation.Decisions.TryGetValue(requestId, out var decision))
                throw new ArgumentException("确认编号已过期，请读取当前操作状态。");
            if (decision.Completion.Task.IsCompleted) throw new InvalidOperationException("此确认已经提交。");
            var previous = operation.Held.ToHashSet();
            try
            {
                object? response = codec.Decode(value, decision.ReturnType, operation.Held);
                decision.Completion.SetResult(response);
            }
            catch
            {
                var acquired = operation.Held.Except(previous).ToHashSet();
                operation.Held.ExceptWith(acquired);
                objects.Return(acquired);
                throw;
            }
        }
    }

    public static async Task<object?> AwaitResult(object? value)
    {
        if (value is ValueTask nonGeneric) { await nonGeneric; return null; }
        if (value != null && value.GetType().IsGenericType && value.GetType().GetGenericTypeDefinition() == typeof(ValueTask<>))
            value = value.GetType().GetMethod("AsTask")!.Invoke(value, null);
        if (value is not Task task) return value;
        await task;
        var property = task.GetType().GetProperty("Result");
        return property?.PropertyType.FullName == "System.Threading.Tasks.VoidTaskResult" ? null : property?.GetValue(task);
    }

    public object Status(Guid id, int eventLimit, FrameScope? scope = null)
    {
        var operation = Get(id);
        return new
        {
            operationId = id, state = operation.Task.IsCompleted ? operation.FinalState ?? "Failed"
                : !operation.Started ? "Queued" : operation.Decisions.IsEmpty ? "Running" : "AwaitingInput",
            createdAt = operation.CreatedAt, completedAt = operation.Task.IsCompleted ? operation.CompletedAt : null,
            cancellationRequested = operation.Cancellation.IsCancellationRequested,
            result = operation.Task.IsCompletedSuccessfully ? operation.Task.Result : null,
            events = operation.Feed.Read(eventLimit, scope),
            pendingCallbacks = operation.Decisions.Select(pair => new { requestId = pair.Key, pair.Value.Name, pair.Value.Arguments, returnType = ApiCatalog.TypeName(pair.Value.ReturnType) }).ToArray()
        };
    }

    public object List() => operations.Values.OrderBy(operation => operation.CreatedAt).Select(operation => new
    {
        operationId = operation.Id, apiId = operation.ApiId, createdAt = operation.CreatedAt,
        completedAt = operation.Task.IsCompleted ? operation.CompletedAt : null, cancellationRequested = operation.Cancellation.IsCancellationRequested,
        state = operation.Task.IsCompleted ? operation.FinalState ?? "Failed" : !operation.Started ? "Queued" : operation.Decisions.IsEmpty ? "Running" : "AwaitingInput"
    }).ToArray();

    public void Cancel(Guid id)
    {
        lock (lifecycle) { var op = Get(id); op.Cancellation.Cancel(); op.Feed.Signal(); }
    }

    internal void CancelIfRunning(Guid id)
    {
        lock (lifecycle)
            if (operations.TryGetValue(id, out var operation) && !operation.Task.IsCompleted) operation.Cancellation.Cancel();
    }

    public void Forget(Guid id)
    {
        var operation = Get(id);
        if (!operation.Task.IsCompleted) throw new InvalidOperationException("请先等待操作结束。");
        lock (lifecycle)
        {
            if (operations.TryRemove(id, out _)) { operation.Feed.Dispose(); operation.Cancellation.Dispose(); }
        }
    }

    public Guid Subscribe(EventInfo evt, Guid? target)
    {
        lock (lifecycle)
        {
            if (stopping) throw new InvalidOperationException("服务器正在退出。");
            if (subscriptions.Count >= 64) throw new InvalidOperationException("已建立 64 个事件订阅，请先结束已使用完的订阅。");
            ApiObjects.Lease? lease = target.HasValue ? objects.Use(target.Value) : null;
            var feed = new ApiFeed(codec);
            Delegate handler;
            try
            {
                handler = DelegateFor(evt.EventHandlerType!, args => { feed.Publish(evt.Name, args); return null; });
                evt.AddEventHandler(lease?.Value, handler);
            }
            catch { lease?.Dispose(); feed.Dispose(); throw; }
            Guid id = Guid.NewGuid(); subscriptions[id] = new(evt, lease, handler, feed); return id;
        }
    }
    public object Events(Guid id, int limit) => subscriptions.TryGetValue(id, out var subscription) ? subscription.Feed.Read(limit) : throw new ArgumentException("事件订阅编号已失效。");
    private readonly HashSet<Guid> streaming = [];
    internal ApiFeed ClaimSubscription(Guid id)
    {
        lock (lifecycle)
        {
            if (!subscriptions.TryGetValue(id, out var sub)) throw new ArgumentException("事件订阅编号已失效。");
            if (!streaming.Add(id)) throw new InvalidOperationException("此事件订阅已连接。");
            return sub.Feed;
        }
    }

    internal void ReleaseSubscription(Guid id)
    {
        lock (lifecycle) if (streaming.Contains(id)) Unsubscribe(id);
    }

    public void Unsubscribe(Guid id)
    {
        lock (lifecycle)
        {
            if (!subscriptions.TryGetValue(id, out var subscription)) throw new ArgumentException("事件订阅编号已失效。");
            subscription.Event.RemoveEventHandler(subscription.Lease?.Value, subscription.Handler);
            streaming.Remove(id);
            subscriptions.TryRemove(id, out _); subscription.Feed.Dispose(); subscription.Lease?.Dispose();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    internal void BeginStop()
    {
        lock (lifecycle)
        {
            if (stopping) return;
            stopping = true;
        }
        objects.RequestStop();
        foreach (Guid id in operations.Keys)
            try { CancelIfRunning(id); } catch (Exception error) { logger.LogError(error, "操作 {OperationId} 取消回调失败", id); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        BeginStop();
        try { await Task.WhenAll(operations.Values.Select(item => item.Task)).WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("退出等待已到期，仍在执行的操作及其资源保留到进程结束：{Operations}",
                operations.Values.Where(item => !item.Task.IsCompleted).Select(item => item.Id).ToArray());
            return;
        }
        foreach (Guid id in subscriptions.Keys) try { Unsubscribe(id); } catch (Exception error) { logger.LogError(error, "事件订阅清理失败"); }
        try { await objects.WaitForIdleAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { logger.LogWarning("退出等待已到期，仍在使用的 Core 资源保持有效，等待实际结果核对"); return; }
        foreach (Guid id in objects.ReverseOrder())
        {
            if (cancellationToken.IsCancellationRequested) break;
            try { await Task.Run(() => objects.ReleaseAsync(id)).WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { logger.LogWarning("资源 {Handle} 的退出清理仍在执行，结果需要核对", id); break; }
            catch (Exception error) { logger.LogError(error, "Core 对象 {Handle} 释放失败", id); }
        }
        foreach (var operation in operations.Values) { operation.Feed.Dispose(); operation.Cancellation.Dispose(); }
    }
}
