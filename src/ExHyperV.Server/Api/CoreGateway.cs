using System.Reflection;
using System.Text.Json;
using ExHyperV.Infrastructure.Interop;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.AspNetCore.Http;

namespace ExHyperV.Server.Api;

internal sealed record CallRequest(string ApiId, Guid? TargetObjectId, JsonElement Arguments, bool Background = false,
    bool RetainResult = false, string[]? TypeArguments = null, string[]? MethodArguments = null);
internal sealed record AccessRequest(string ApiId, Guid? TargetObjectId, JsonElement Arguments, JsonElement Value, string[]? TypeArguments);
internal sealed record SubscribeRequest(string ApiId, Guid? TargetObjectId);
internal sealed record ReplyRequest(Guid RequestId, JsonElement Value);

internal sealed class CoreGateway(ApiCatalog catalog, ApiPolicy policy, ApiObjects objects, ApiCodec codec, ApiExecution execution)
{
    private sealed record Entry(string Group, string Kind, bool Auxiliary, bool ReadOnly, string Search, JsonElement Summary);
    private readonly Lazy<Entry[]> entries = new(() => catalog.Members.Values.Where(policy.Visible).Select(member =>
    {
        var summary = JsonSerializer.SerializeToElement(catalog.Describe(member, detail: false));
        string text = string.Join("\n", new[] { "apiId", "category", "description" }.Select(key => summary.GetProperty(key).GetString()));
        return new Entry(catalog.Group(member), member is Type ? "Type" : member.MemberType.ToString(),
            ApiValuePolicy.IsHelper(member), catalog.IsReadOnly(member), text, summary);
    }).ToArray());

    internal object Catalog(string? search, string? group, string? kind, bool includeAuxiliary, bool readOnlyOnly, int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentException("offset 从 0 开始，limit 为 1 至 100。");
        if (group != null && !Hosting.LaunchOptions.Groups.Contains(group)) throw new ArgumentException("工具组名称无效。");
        string[] kinds = ["All", "Type", "Constructor", "Method", "Field", "Property", "Event"];
        if (kind != null && !kinds.Contains(kind, StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("成员种类无效。");
        var matches = entries.Value.Where(e => (group == null || e.Group == group) && (includeAuxiliary || !e.Auxiliary)
            && (kind == null || kind.Equals("All", StringComparison.OrdinalIgnoreCase) || kind.Equals(e.Kind, StringComparison.OrdinalIgnoreCase))
            && (!readOnlyOnly || catalog.ContractReviewed && e.ReadOnly)
            && (string.IsNullOrWhiteSpace(search) || e.Search.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        return new { total = matches.Length, offset, limit, entries = matches.Skip(offset).Take(limit).Select(e => e.Summary) };
    }

    internal object Describe(string apiId)
    {
        var member = catalog.Find(apiId);
        if (!policy.Visible(member)) throw new UnauthorizedAccessException("当前配置未启用此工具组。");
        return catalog.Describe(member);
    }

    internal async Task<IResult> Call(CallRequest request, CancellationToken abort)
    {
        var member = catalog.Find(request.ApiId);
        policy.Demand(member);
        if (member is not MethodBase) throw new ArgumentException("调用需要方法或构造函数标识。");
        if (request.Arguments.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
            throw new ArgumentException("arguments 需要按参数名填写 JSON 对象。");
        var operation = execution.Start(current => execution.InvokeAsync(request.ApiId, request.Arguments, request.TargetObjectId,
            request.TypeArguments, request.MethodArguments, current), request.RetainResult || member is ConstructorInfo, request.ApiId);
        if (request.Background) return Results.Accepted($"/api/v1/operations/{operation.Id}", new { operationId = operation.Id });
        using var registration = abort.Register(() => execution.CancelIfRunning(operation.Id));
        var result = await operation.Task;
        return Results.Json(new { operationId = operation.Id, result.Success, result.Result, result.ErrorKind, result.Error },
            statusCode: result.ErrorKind switch { "InvalidArgument" => 400, "AccessDenied" => 403, "NotFound" => 404,
                "InvalidState" or "Conflict" => 409, _ => 200 });
    }

    internal async Task<object> Access(AccessRequest request, bool write, CancellationToken cancellation)
    {
        var definition = catalog.Find(request.ApiId);
        policy.Demand(definition, write);
        objects.RequireCapacity();
        using var lease = request.TargetObjectId is Guid id ? objects.Use(id) : null;
        object? target = lease?.Value;
        var member = catalog.Close(definition, request.TypeArguments, null, target);
        var gate = request.TargetObjectId is Guid targetId ? objects.Calls(targetId) : null;
        if (gate != null) await gate.WaitAsync(cancellation);
        var held = new HashSet<Guid>();
        try
        {
            object?[] Indices(PropertyInfo property) => property.GetIndexParameters().Select(p =>
                request.Arguments.ValueKind == JsonValueKind.Object && request.Arguments.TryGetProperty(p.Name!, out var input)
                    ? codec.Decode(input, p.ParameterType, held) : throw new ArgumentException($"缺少索引参数 {p.Name}。")).ToArray();
            if (write)
            {
                if (request.Value.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("请提供 value。");
                switch (member)
                {
                    case PropertyInfo p when ApiCatalog.Writable(p): p.SetValue(target, codec.Decode(request.Value, p.PropertyType, held), Indices(p)); break;
                    case FieldInfo { IsInitOnly: false, IsLiteral: false } f: f.SetValue(target, codec.Decode(request.Value, f.FieldType, held)); break;
                    default: throw new ArgumentException("此成员没有公开写入入口。");
                }
                return new { success = true };
            }
            object? value = member switch
            {
                PropertyInfo { DeclaringType: var type, Name: nameof(ConsoleFrame.Pixels) } when type == typeof(ConsoleFrame) && target is ConsoleFrame f => f.CopyPixels(),
                PropertyInfo { DeclaringType: var type, Name: nameof(ConsoleAudioPacket.Samples) } when type == typeof(ConsoleAudioPacket) && target is ConsoleAudioPacket p => p.CopySamples(),
                PropertyInfo { GetMethod.IsPublic: true } p => p.GetValue(target, Indices(p)),
                FieldInfo f => f.GetValue(target),
                _ => throw new ArgumentException("请使用可读取的属性或字段标识。")
            };
            return new { value = codec.Pack(value, owned: false, parent: request.TargetObjectId) };
        }
        finally { objects.Return(held); gate?.Release(); }
    }

    internal object Subscribe(SubscribeRequest request)
    {
        if (catalog.Find(request.ApiId) is not EventInfo evt) throw new ArgumentException("请提供事件标识。");
        policy.Demand(evt);
        return new { subscriptionId = execution.Subscribe(evt, request.TargetObjectId) };
    }

    internal async Task SendBinary(Guid id, HttpContext context)
    {
        using var lease = objects.Use(id);
        var gate = objects.Calls(id);
        await gate.WaitAsync(context.RequestAborted);
        try
        {
            if (lease.Value is Stream { CanRead: true, CanSeek: true } stream)
            {
                long previous = stream.Position;
                try
                {
                    stream.Position = 0;
                    context.Response.ContentType = "application/octet-stream";
                    context.Response.ContentLength = stream.Length;
                    await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
                }
                finally { stream.Position = previous; }
                return;
            }
            (byte[] bytes, string mediaType) = lease.Value switch
            {
                ConsoleFrame frame => (WindowsImageCodec.PngFromBgra32(frame.CopyPixels(), frame.Width, frame.Height, frame.Stride), "image/png"),
                VmScreenshotImage screenshot => (screenshot.EncodeJpeg(), "image/jpeg"),
                byte[] data => (data, "application/octet-stream"),
                _ => throw new ArgumentException("请提供二进制缓冲、可定位的流、画面或截图的编号。")
            };
            context.Response.ContentType = mediaType;
            context.Response.ContentLength = bytes.LongLength;
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        }
        finally { gate.Release(); }
    }
}
