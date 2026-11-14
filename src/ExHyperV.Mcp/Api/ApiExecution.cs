using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using ExHyperV.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace ExHyperV.Mcp.Api;

public sealed class ApiOperation
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
    public Task<CallToolResult> Task { get; set; } = null!;
    internal ConcurrentDictionary<Guid, ApiCallback> Decisions { get; } = new();
}

internal sealed record ApiCallback(Type ReturnType, TaskCompletionSource<object?> Completion, string Name, object?[] Arguments);

public sealed class ApiFeed(ApiCodec codec) : IDisposable
{
    private sealed record Packet(long Sequence, DateTimeOffset Time, string Name, object?[] Arguments, Guid? RequestId);
    private readonly object gate = new();
    private readonly Queue<Packet> packets = [];
    private long sequence, dropped;
    private bool disposed;
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
        }
    }

    public object Read(int limit)
    {
        if (limit is < 1 or > 128) throw new ArgumentException("事件条数为 1 至 128。");
        lock (gate)
        {
            List<object> result = [];
            while (packets.Count > 0 && result.Count < limit)
            {
                var packet = packets.Peek();
                if (packet.Arguments.Any(item => item is ConsoleFrame) && !codec.CanRetain) break;
                // Ownership of each retained frame now moves to the handle table.
                result.Add(new { packet.Sequence, packet.Time, packet.Name, packet.RequestId,
                    arguments = packet.Arguments.Select(item => item is ConsoleFrame image ? codec.Pack(image) : item).ToArray() });
                packets.Dequeue();
            }
            return new { events = result, dropped, remaining = packets.Count, resourceLimitReached = !codec.CanRetain };
        }
    }

    private static void DisposePacket(Packet packet) { foreach (var frame in packet.Arguments.OfType<ConsoleFrame>()) frame.Dispose(); }
    public void Dispose() { lock (gate) { disposed = true; while (packets.TryDequeue(out var packet)) DisposePacket(packet); } }
}

public sealed class ApiExecution(ApiCatalog catalog, ApiObjects objects, ApiCodec codec, ILogger<ApiExecution> logger) : IHostedService
{
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
            if (stopping) throw new InvalidOperationException("MCP 正在退出。");
            objects.RequireCapacity();
            if (operations.Count >= 256) throw new InvalidOperationException("已保留 256 个操作，请用 core_operation_release 清理已读取的结束记录。");
            var operation = new ApiOperation { Feed = new(codec), RetainResult = retainResult, ApiId = apiId };
            operations[operation.Id] = operation;
            operation.Task = Task.Run(async () =>
            {
                var response = await ToolReply.Run(async () =>
                {
                    bool entered = false;
                    try
                    {
                        await concurrency.WaitAsync(operation.Cancellation.Token);
                        entered = true;
                        operation.Started = true;
                        operation.Cancellation.Token.ThrowIfCancellationRequested();
                        object? result = await action(operation);
                        operation.FinalState = ApiCodec.Failed(result) ? "Failed" : "Completed";
                        return ToolReply.Result(new { success = !ApiCodec.Failed(result), result = codec.Pack(result, retain: operation.RetainResult) }, ApiCodec.Failed(result));
                    }
                    catch (Exception error) when (error is OperationCanceledException || error is TargetInvocationException { InnerException: OperationCanceledException })
                    {
                        operation.FinalState = "Cancelled";
                        return ToolReply.Error("Core 已响应取消；请结合当前资源状态和进度记录核对已完成的步骤。", ExHyperV.Tools.ApiErrorKind.Cancelled);
                    }
                    catch (TargetInvocationException error) when (error.InnerException != null)
                    { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
                    finally
                    {
                        lock (operation) objects.Return(operation.Held);
                        if (entered) concurrency.Release();
                        operation.CompletedAt = DateTimeOffset.UtcNow;
                    }
                    });
                if (response.StructuredContent is { } body)
                {
                    string? kind = ToolReply.FailureField(body, "errorKind");
                    operation.FinalState = kind == "OutcomeUnknown" ? "RequiresVerification" : kind == "Cancelled" ? "Cancelled"
                        : response.IsError == true ? "Failed" : "Completed";
                }
                return response;
            });
            return operation;
        }
    }

    public ApiOperation Get(Guid id) => operations.TryGetValue(id, out var operation) ? operation : throw new ArgumentException("操作编号已失效。");

    public async Task<object?> InvokeAsync(string apiId, JsonElement arguments, Guid? targetId, string[]? typeArguments, string[]? methodArguments, ApiOperation operation)
    {
        object? target = targetId.HasValue ? Hold(targetId.Value, operation) : null;
        using var cancellation = target is GpuDeploymentSession gpu ? operation.Cancellation.Token.Register(gpu.RequestCancellation) : default;
        var member = catalog.Close(catalog.Find(apiId), typeArguments, methodArguments, target);
        if (member is not MethodBase callable) throw new ArgumentException("此标识是属性、字段或事件，请选择相应的读取、写入或订阅工具。");
        if (callable is MethodInfo { IsStatic: false } && target == null) throw new ArgumentException("实例方法需要 target 对象编号。");
        var callGate = targetId.HasValue ? objects.Calls(targetId.Value) : null;
        if (callGate != null) await callGate.WaitAsync(operation.Cancellation.Token);
        try
        {
        if (targetId.HasValue && callable.Name is "Dispose" or "DisposeAsync") objects.RequireSoleUse(targetId.Value);
        var inputs = arguments.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
            : arguments.EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var parameters = callable.GetParameters();
        foreach (string name in inputs.Keys)
            if (!parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException($"参数名无效：{name}。");
        object?[] values = parameters.Select(parameter => Argument(parameter, inputs, operation)).ToArray();
        object? result;
        if (member.DeclaringType == typeof(ExHyperV.Vmcx.VmcxValue) && member.Name == "Raw")
            result = ExHyperV.Vmcx.VmcxValue.Raw((byte[])values[0]!);
        else result = member is ConstructorInfo ctor ? ctor.Invoke(values) : ((MethodInfo)member).Invoke(target, values);
        result = await AwaitResult(result);
        var outputs = parameters.Select((parameter, index) => (parameter, index)).Where(item => item.parameter.ParameterType.IsByRef)
            .ToDictionary(item => item.parameter.Name!, item => codec.Pack(values[item.index]));
        return outputs.Count == 0 ? result : new { result = codec.Pack(result), refArguments = outputs };
        }
        finally { callGate?.Release(); }
    }

    private object Hold(Guid id, ApiOperation operation) { objects.Acquire(id, operation.Held); return objects.Get(id); }

    private object? Argument(ParameterInfo parameter, Dictionary<string, JsonElement> inputs, ApiOperation operation)
    {
        Type type = parameter.ParameterType;
        if (type == typeof(CancellationToken)) return operation.Cancellation.Token;
        if (parameter.IsOut) return type.GetElementType()!.IsValueType ? Activator.CreateInstance(type.GetElementType()!) : null;
        if (ApiCatalog.IsCallback(type))
        {
            if (inputs.TryGetValue(parameter.Name!, out var callback) && callback.ValueKind == JsonValueKind.Null) return null;
            if (inputs.TryGetValue(parameter.Name!, out callback) && callback.ValueKind == JsonValueKind.Object && callback.TryGetProperty("$handle", out _))
                return codec.Decode(callback, type, operation);
            if (!inputs.ContainsKey(parameter.Name!) && parameter.HasDefaultValue && typeof(Delegate).IsAssignableFrom(type)
                && type.GetMethod("Invoke")!.ReturnType != typeof(void)) return parameter.DefaultValue;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IProgress<>))
                return Activator.CreateInstance(typeof(ProgressSink<>).MakeGenericType(type.GetGenericArguments()), operation.Feed, parameter.Name!);
            return DelegateFor(type, args => Callback(operation, parameter.Name!, type.GetMethod("Invoke")!.ReturnType, args));
        }
        if (inputs.TryGetValue(parameter.Name!, out var value))
            return codec.Decode(value, type == typeof(ReadOnlySpan<byte>) ? typeof(byte[]) : type, operation);
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

    private object? Callback(ApiOperation operation, string name, Type returnType, object?[] arguments)
    {
        if (returnType == typeof(void)) { operation.Feed.Publish(name, arguments); return null; }
        Type resultType = returnType.IsGenericType && (returnType.GetGenericTypeDefinition() == typeof(Task<>) || returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ? returnType.GetGenericArguments()[0] : returnType == typeof(Task) || returnType == typeof(ValueTask) ? typeof(object) : returnType;
        Guid id = Guid.NewGuid();
        if (operation.Decisions.Count >= 64) throw new InvalidOperationException("待回答事项已达到 64 项，请先处理当前事项。");
        var decision = new ApiCallback(resultType, new(TaskCreationOptions.RunContinuationsAsynchronously), name, arguments.Select(value => codec.Snapshot(value)).ToArray());
        operation.Decisions[id] = decision;
        operation.Feed.Publish(name, arguments, id);
        async Task<object?> Wait()
        {
            try { return await decision.Completion.Task.WaitAsync(operation.Cancellation.Token); }
            finally { operation.Decisions.TryRemove(id, out _); }
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
            object? response = codec.Decode(value, decision.ReturnType, operation);
            if (!decision.Completion.TrySetResult(response)) throw new InvalidOperationException("此确认已经提交。");
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

    public object Status(Guid id, int eventLimit)
    {
        var operation = Get(id);
        return new
        {
            operationId = id, state = operation.Task.IsCompleted ? operation.FinalState ?? "Failed"
                : !operation.Started ? "Queued" : operation.Decisions.IsEmpty ? "Running" : "AwaitingInput",
            createdAt = operation.CreatedAt, completedAt = operation.CompletedAt,
            cancellationRequested = operation.Cancellation.IsCancellationRequested,
            result = operation.Task.IsCompletedSuccessfully ? operation.Task.Result.StructuredContent : null,
            events = operation.Feed.Read(eventLimit),
            pendingCallbacks = operation.Decisions.Select(pair => new { requestId = pair.Key, pair.Value.Name, pair.Value.Arguments, returnType = ApiCatalog.TypeName(pair.Value.ReturnType) }).ToArray()
        };
    }

    public object List() => operations.Values.OrderBy(operation => operation.CreatedAt).Select(operation => new
    {
        operationId = operation.Id, apiId = operation.ApiId, createdAt = operation.CreatedAt,
        completedAt = operation.CompletedAt, cancellationRequested = operation.Cancellation.IsCancellationRequested,
        state = operation.Task.IsCompleted ? operation.FinalState ?? "Failed" : !operation.Started ? "Queued" : operation.Decisions.IsEmpty ? "Running" : "AwaitingInput"
    }).ToArray();

    public void Forget(Guid id)
    {
        var operation = Get(id);
        if (!operation.Task.IsCompleted) throw new InvalidOperationException("请先等待操作结束。");
        operations.TryRemove(id, out _); operation.Feed.Dispose(); operation.Cancellation.Dispose();
    }

    public Guid Subscribe(EventInfo evt, Guid? target)
    {
        lock (lifecycle)
        {
            if (stopping) throw new InvalidOperationException("MCP 正在退出。");
            if (subscriptions.Count >= 64) throw new InvalidOperationException("已建立 64 个事件订阅，请先结束已使用完的订阅。");
            ApiObjects.Lease? lease = target.HasValue ? objects.Use(target.Value) : null;
            var feed = new ApiFeed(codec);
            Delegate handler = DelegateFor(evt.EventHandlerType!, args => { feed.Publish(evt.Name, args); return null; });
            try { evt.AddEventHandler(lease?.Value, handler); }
            catch { lease?.Dispose(); feed.Dispose(); throw; }
            Guid id = Guid.NewGuid(); subscriptions[id] = new(evt, lease, handler, feed); return id;
        }
    }
    public object Events(Guid id, int limit) => subscriptions.TryGetValue(id, out var subscription) ? subscription.Feed.Read(limit) : throw new ArgumentException("事件订阅编号已失效。");
    public void Unsubscribe(Guid id)
    {
        if (!subscriptions.TryGetValue(id, out var subscription)) throw new ArgumentException("事件订阅编号已失效。");
        subscription.Event.RemoveEventHandler(subscription.Lease?.Value, subscription.Handler);
        subscriptions.TryRemove(id, out _); subscription.Feed.Dispose(); subscription.Lease?.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (lifecycle) stopping = true;
        objects.RequestStop();
        foreach (var operation in operations.Values)
            try { operation.Cancellation.Cancel(); } catch (Exception error) { logger.LogError(error, "操作 {OperationId} 取消回调失败", operation.Id); }
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
