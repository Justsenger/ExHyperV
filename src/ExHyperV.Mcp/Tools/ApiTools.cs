using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ExHyperV.Mcp.Api;
using ExHyperV.Models;
using ExHyperV.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExHyperV.Mcp.Tools;

[McpServerToolType]
public sealed class ApiTools(ApiCatalog catalog, ApiObjects objects, ApiCodec codec, ApiExecution execution, ApiPolicy policy)
{
    [McpServerTool(Name = "core_api_coverage", Title = "查看完整 API 接入范围", ReadOnly = true, OpenWorld = false)]
    [Description("返回当前 Core 构建标识、公开类型和成员数量，以及传输签名检查发现的缺项。该清单表示接口接入范围；实际功能条件和成功结果由 Core 判定。")]
    public CallToolResult Coverage() => ToolReply.Result(catalog.Coverage());

    [McpServerTool(Name = "core_api_list", Title = "搜索完整 Core API", ReadOnly = true, OpenWorld = false)]
    [Description("按中文说明、分类、类型名或方法名检索当前 Core 全部公开 API，覆盖宿主、虚拟机、网络、PCIe、GPU、导入导出、控制台、监控和工具。默认返回可调用方法及构造函数；kind 可选 All、Type、Method、Constructor、Property、Field、Event。取 apiId 后调用 core_api_describe。")]
    public CallToolResult List(string? search = null, string kind = "Method", int offset = 0, int limit = 30,
        [Description("包含编译器生成的比较、克隆等辅助成员。")] bool includeAuxiliary = false,
        [Description("host、vm、devices、console、utilities 或 core。")] string? group = null)
    {
        if (offset < 0 || limit is < 1 or > 100) return ToolReply.Error("offset 从 0 开始，limit 为 1 至 100。");
        if (!new[] { "All", "Type", "Method", "Constructor", "Property", "Field", "Event" }.Contains(kind, StringComparer.OrdinalIgnoreCase)) return ToolReply.Error("kind 使用 All、Type、Method、Constructor、Property、Field 或 Event。");
        var items = catalog.Members.Values.Where(member => policy.Visible(member) && (includeAuxiliary || !ApiValuePolicy.IsHelper(member))
            && (group == null || catalog.Group(member) == group)).Where(member => kind.Equals("All", StringComparison.OrdinalIgnoreCase)
            || kind.Equals("Type", StringComparison.OrdinalIgnoreCase) && member is Type
            || kind.Equals("Method", StringComparison.OrdinalIgnoreCase) && member is MethodBase
            || member.MemberType.ToString().Equals(kind, StringComparison.OrdinalIgnoreCase))
            .Select(member => catalog.Describe(member, detail: false))
            .Where(item => string.IsNullOrWhiteSpace(search) || JsonSerializer.Serialize(item, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        return ToolReply.Result(new { total = items.Length, offset, limit, coreModuleId = ApiCatalog.Core.ManifestModule.ModuleVersionId, entries = items.Skip(offset).Take(limit) });
    }

    [McpServerTool(Name = "core_api_describe", Title = "读取 API 参数和用法", ReadOnly = true, OpenWorld = false)]
    [Description("读取准确签名、中文说明、参数、返回值、泛型参数和读写能力。对象参数可用 JSON 构造，或传 {\"$handle\":\"对象编号\"} 保留 Core 原始身份；抽象模型可加 $type 指定目录中的具体类型。")]
    public Task<CallToolResult> Describe(string apiId) => ToolReply.Run(() => Task.FromResult(ToolReply.Result(catalog.Describe(catalog.Find(apiId)))));

    [McpServerTool(Name = "core_api_call", Title = "调用 Core API 或构造对象", Destructive = true, OpenWorld = true)]
    [Description("执行目录中的任意公开方法或构造函数。arguments 按参数名称填写；实例方法提供 target 对象编号，静态方法省略。泛型类型与泛型方法分别用 typeArguments、methodArguments。默认后台执行并返回 operationId，用 core_operation_status 查看进度。CancellationToken、进度由 MCP 接管；可选 Func 回调省略时保留 Core 默认行为，填写 {\"$callback\":true} 时转为待回答事项，由 core_callback_reply 提交。执行真实管理操作，按 Core 文档核对范围。")]
    public Task<CallToolResult> Call(string apiId, JsonElement? arguments = null, Guid? target = null,
        string[]? typeArguments = null, string[]? methodArguments = null, bool background = true,
        [Description("保留返回数据的原对象编号，供后续实例方法或属性赋值使用。构造函数自动保留；普通查询默认直接返回数据。")] bool retainResult = false,
        CancellationToken cancellationToken = default) => ToolReply.Run(async () =>
    {
        var member = catalog.Find(apiId);
        policy.Demand(member);
        if (member is MethodInfo && catalog.Group(member) is "host" or "vm" or "devices" or "console") ToolReply.RequireAdministrator();
        JsonElement inputs = (arguments ?? default).CloneOrDefault();
        var operation = execution.Start(current => execution.InvokeAsync(apiId, inputs, target, typeArguments, methodArguments, current), retainResult || member is ConstructorInfo, apiId);
        if (background) return ToolReply.Result(new { accepted = true, operationId = operation.Id });
        using var cancellation = cancellationToken.Register(operation.Cancellation.Cancel);
        CallToolResult result = await operation.Task;
        // Keep the operation ID even for a synchronous call: progress and callbacks remain inspectable.
        return ToolReply.Result(new { operationId = operation.Id, response = result.StructuredContent }, result.IsError == true);
    });

    [McpServerTool(Name = "core_api_query", Title = "调用只读 Core API", ReadOnly = true, OpenWorld = true)]
    [Description("调用目录中 readOnly 为 true 的方法。参数结构由 core_api_describe.inputSchema 给出；读写分类在服务端校验。默认等待返回，需要长时间查询时可设置 background。")]
    public Task<CallToolResult> Query(string apiId, JsonElement? arguments = null, Guid? target = null,
        string[]? typeArguments = null, string[]? methodArguments = null, bool background = false, CancellationToken cancellationToken = default) => ToolReply.Run(async () =>
    {
        if (!catalog.ContractReviewed || !catalog.IsReadOnly(catalog.Find(apiId))) throw new UnauthorizedAccessException("此 API 尚未登记为只读，或 Core 契约已更新待核对。");
        return await Call(apiId, arguments, target, typeArguments, methodArguments, background, cancellationToken: cancellationToken);
    });

    [McpServerTool(Name = "core_api_get", Title = "读取 Core 属性或字段", ReadOnly = true, OpenWorld = true)]
    [Description("读取公开属性、索引器、常量或字段。实例成员填写 target；索引器按参数名填写 arguments。返回 $handle 可继续读取和调用原对象。像素、PCM 等二进制内容转成 Base64 或分块编号。")]
    public Task<CallToolResult> Get(string apiId, Guid? target = null, JsonElement? arguments = null, string[]? typeArguments = null) => ToolReply.Run(() =>
    {
        using var lease = target.HasValue ? objects.Use(target.Value) : null;
        object? instance = lease?.Value;
        var definition = catalog.Find(apiId);
        policy.Demand(definition);
        MemberInfo member = catalog.Close(definition, typeArguments, null, instance);
        object? result = member switch
        {
            PropertyInfo property when instance is ConsoleFrame frame && property.Name == "Pixels" => frame.CopyPixels(),
            PropertyInfo property when instance is ConsoleAudioPacket audio && property.Name == "Samples" => audio.Samples.ToArray(),
            PropertyInfo { GetMethod.IsPublic: true } property => property.GetValue(instance, IndexArguments(property, arguments ?? default)),
            FieldInfo field => field.GetValue(instance),
            _ => throw new ArgumentException("请使用具有公开读取入口的属性或字段标识。")
        };
        return Task.FromResult(ToolReply.Result(new { success = true, value = codec.Pack(result, owned: false, parent: target) }));
    });

    [McpServerTool(Name = "core_api_set", Title = "写入 Core 属性或字段", Destructive = true, OpenWorld = true)]
    [Description("写入目录允许设置的公开属性或字段。模型赋值后仍需调用对应保存 API 才会应用到宿主；静态配置属性可能即时生效。索引器使用 arguments 提供索引。")]
    public Task<CallToolResult> Set(string apiId, JsonElement value, Guid? target = null, JsonElement? arguments = null, string[]? typeArguments = null) => ToolReply.Run(() =>
    {
        ToolReply.RequireAdministrator();
        using var lease = target.HasValue ? objects.Use(target.Value) : null;
        object? instance = lease?.Value;
        var definition = catalog.Find(apiId);
        policy.Demand(definition, write: true);
        MemberInfo member = catalog.Close(definition, typeArguments, null, instance);
        switch (member)
        {
            case PropertyInfo property when ApiCatalog.Writable(property): property.SetValue(instance, codec.Decode(value, property.PropertyType), IndexArguments(property, arguments ?? default)); break;
            case FieldInfo { IsLiteral: false, IsInitOnly: false } field: field.SetValue(instance, codec.Decode(value, field.FieldType)); break;
            default: throw new ArgumentException("此成员仅提供读取入口。");
        }
        return Task.FromResult(ToolReply.Result(new { success = true }));
    });

    private object?[] IndexArguments(PropertyInfo property, JsonElement arguments) => property.GetIndexParameters().Select(parameter =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(parameter.Name!, out var input)
            ? codec.Decode(input, parameter.ParameterType) : parameter.HasDefaultValue ? parameter.DefaultValue : throw new ArgumentException($"缺少索引：{parameter.Name}。")).ToArray();

    [McpServerTool(Name = "core_operation_status", Title = "查看 API 操作进度和结果", ReadOnly = true, OpenWorld = false)]
    [Description("读取后台操作状态、最终结果、进度和待回答回调。事件按读取顺序取出；dropped 表示缓冲被新事件替换的数量。Running 表示 Core 仍在执行；请求取消后也要等待最终结果。")]
    public Task<CallToolResult> Status(Guid operationId, int eventLimit = 30) => ToolReply.Run(() => Task.FromResult(ToolReply.Result(execution.Status(operationId, eventLimit))));

    [McpServerTool(Name = "core_operations", Title = "列出 Core 操作", ReadOnly = true, OpenWorld = false)]
    [Description("列出当前进程的 Core 操作编号、API、时间和状态。用于断线重连或协议任务取消后继续核对底层操作。")]
    public CallToolResult Operations() => ToolReply.Result(new { operations = execution.List() });

    [McpServerTool(Name = "core_operation_cancel", Title = "请求取消 API 操作", Destructive = true, OpenWorld = false)]
    [Description("向此操作的 Core CancellationToken 与待回答回调发送取消请求。同步原生操作可能继续执行；通过 core_operation_status 核对结果及待清理资源。")]
    public Task<CallToolResult> Cancel(Guid operationId) => ToolReply.Run(() =>
    { execution.Get(operationId).Cancellation.Cancel(); return Task.FromResult(ToolReply.Result(new { cancellationRequested = true, operationId })); });

    [McpServerTool(Name = "core_callback_reply", Title = "回答 Core 确认回调", Destructive = true, OpenWorld = true)]
    [Description("回答操作进度中的 requestId。value 符合 pendingCallbacks.returnType；例如布尔确认提交 true 或 false。先将确认内容交给用户决定，再继续原 Core 操作。")]
    public Task<CallToolResult> Reply(Guid operationId, Guid requestId, JsonElement value) => ToolReply.Run(() =>
    { execution.Reply(operationId, requestId, value); return Task.FromResult(ToolReply.Result(new { success = true })); });

    [McpServerTool(Name = "core_callback_prompt", Title = "请用户回答 Core 待定事项", Destructive = true, OpenWorld = true)]
    [Description("将当前 Core 布尔确认交给支持表单交互的客户端。接受后提交用户填写的值；关闭表单或客户端缺少此能力时保留待定事项，继续使用 core_callback_reply。")]
    public Task<CallToolResult> Prompt(Guid operationId, Guid requestId, McpServer server, CancellationToken cancellationToken = default) => ToolReply.Run(async () =>
    {
        if (!execution.Get(operationId).Decisions.TryGetValue(requestId, out var decision)) throw new ArgumentException("确认编号已过期。");
        if (decision.ReturnType != typeof(bool) || server.ClientCapabilities?.Elicitation?.Form == null)
            return ToolReply.Result(new { operationId, requestId, state = "AwaitingInput", replyWith = "core_callback_reply", decision.Arguments });
        var answer = await server.ElicitAsync(new ElicitRequestParams
        {
            Message = decision.Name + "\n" + JsonSerializer.Serialize(decision.Arguments),
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>
                { ["accepted"] = new ElicitRequestParams.BooleanSchema { Description = "是否同意上述 Core 操作？" } },
                Required = ["accepted"]
            }
        }, cancellationToken);
        if (answer.Action == "accept" && answer.Content?.TryGetValue("accepted", out var accepted) == true)
            execution.Reply(operationId, requestId, accepted);
        else if (answer.Action == "decline") execution.Reply(operationId, requestId, JsonSerializer.SerializeToElement(false));
        return ToolReply.Result(new { operationId, requestId, action = answer.Action });
    });

    [McpServerTool(Name = "core_operation_release", Title = "清理已结束的操作记录", Destructive = false, OpenWorld = false)]
    [Description("移除已结束操作的进度记录。返回的 Core 对象仍由 core_object_release 单独释放。")]
    public Task<CallToolResult> Forget(Guid operationId) => ToolReply.Run(() =>
    { execution.Forget(operationId); return Task.FromResult(ToolReply.Result(new { success = true })); });

    [McpServerTool(Name = "core_objects", Title = "列出持有的 Core 对象", ReadOnly = true, OpenWorld = false)]
    [Description("列出当前 MCP 进程保留的对象编号、类型、释放责任及执行中的引用数。")]
    public CallToolResult Objects() => ToolReply.Result(new { objects = objects.List() });

    [McpServerTool(Name = "core_object_release", Title = "释放 Core 对象和资源", Destructive = true, OpenWorld = true)]
    [Description("释放对象编号。拥有资源时等待 DisposeAsync／Dispose；GPU 流程先按 Core 取消和清理。已有调用需先结束，清理失败保留编号便于再次检查。导入、挂载、监控、连接等对象在使用结束后执行此步骤。")]
    public Task<CallToolResult> Release(Guid handle) => ToolReply.Run(async () => ToolReply.Result(await objects.ReleaseAsync(handle)));

    [McpServerTool(Name = "core_event_subscribe", Title = "订阅 Core 事件", Destructive = false, OpenWorld = false)]
    [Description("订阅目录中的公开事件，返回 subscriptionId。控制台先订阅 FrameReceived、Disconnected 等事件再 Start；监控、剪贴板和设备事件也使用此入口。")]
    public Task<CallToolResult> Subscribe(string apiId, Guid? target = null) => ToolReply.Run(() =>
    {
        if (catalog.Find(apiId) is not EventInfo evt) throw new ArgumentException("请提供 Event 类型的 API 标识。");
        policy.Demand(evt);
        Guid id = execution.Subscribe(evt, target);
        return Task.FromResult(ToolReply.Result(new { subscriptionId = id }));
    });

    [McpServerTool(Name = "core_event_read", Title = "接收 Core 事件", ReadOnly = true, OpenWorld = false)]
    [Description("读取并取出订阅事件。画面只保留最新帧，返回的帧编号可用 core_image_read 查看或读取 Pixels；使用后释放帧编号。其他事件保留最近 128 条，返回丢弃计数。")]
    public Task<CallToolResult> Events(Guid subscriptionId, int limit = 30) => ToolReply.Run(() => Task.FromResult(ToolReply.Result(execution.Events(subscriptionId, limit))));

    [McpServerTool(Name = "core_event_unsubscribe", Title = "结束 Core 事件订阅", Destructive = false, OpenWorld = false)]
    [Description("解绑事件并释放缓冲中尚未取出的帧租约。已返回的帧编号由调用方释放。")]
    public Task<CallToolResult> Unsubscribe(Guid subscriptionId) => ToolReply.Run(() =>
    { execution.Unsubscribe(subscriptionId); return Task.FromResult(ToolReply.Result(new { success = true })); });

    [McpServerTool(Name = "core_binary_create", Title = "准备二进制 API 参数", Destructive = false, OpenWorld = false)]
    [Description("将 Base64 数据保存在进程内，返回 byte[] 或 MemoryStream 的对象编号。用于字节数组、图像、PCM、剪贴板和流参数。")]
    public Task<CallToolResult> BinaryCreate(string base64, bool stream = false) => ToolReply.Run(() =>
    {
        objects.RequireCapacity();
        if (base64.Length > 16 * 1024 * 1024) throw new ArgumentException("单次二进制输入限制为 16 MiB Base64 文本。");
        objects.RequireCapacity((long)base64.Length * 3 / 4);
        byte[] bytes = Convert.FromBase64String(base64);
        object value = bytes;
        if (stream) { var buffer = new MemoryStream(); buffer.Write(bytes); buffer.Position = 0; value = buffer; }
        return Task.FromResult(ToolReply.Result(new { handle = objects.Add(value), length = bytes.Length }));
    });

    [McpServerTool(Name = "core_binary_read", Title = "分块读取二进制结果", ReadOnly = true, OpenWorld = false)]
    [Description("按字节偏移读取 byte[] 或可定位 Stream，返回 Base64。大图像和大块数据使用多次读取；流读取会暂存并恢复原位置。")]
    public Task<CallToolResult> BinaryRead(Guid handle, long offset = 0, int count = 65536) => ToolReply.Run(() =>
    {
        if (offset < 0 || count is < 1 or > 1048576) throw new ArgumentException("offset 从 0 开始，count 为 1 至 1048576 字节。");
        using var lease = objects.Use(handle);
        object value = lease.Value;
        byte[] output;
        long length;
        if (value is byte[] bytes)
        { length = bytes.Length; if (offset > length) throw new ArgumentException("偏移超出数据长度。"); output = bytes.AsSpan((int)offset, (int)Math.Min(count, length - offset)).ToArray(); }
        else if (value is Stream stream && stream.CanSeek)
        {
            lock (stream)
            {
                length = stream.Length; if (offset > length) throw new ArgumentException("偏移超出数据长度。");
                long previous = stream.Position;
                try { stream.Position = offset; output = new byte[Math.Min((long)count, length - offset)]; stream.ReadExactly(output); }
                finally { stream.Position = previous; }
            }
        }
        else throw new ArgumentException("对象类型要求 byte[] 或可定位的 Stream。");
        return Task.FromResult(ToolReply.Result(new { offset, length, count = output.Length, base64 = Convert.ToBase64String(output), end = offset + output.Length == length }));
    });

    [McpServerTool(Name = "core_image_read", Title = "查看 Core 画面或截图", ReadOnly = true, OpenWorld = false)]
    [Description("将 ConsoleFrame 或 VmScreenshotImage 对象转换成 MCP 图片，供 AI 直接查看虚拟机画面。画面对象继续保留，使用后释放其编号。")]
    public Task<CallToolResult> Image(Guid handle) => ToolReply.Run(() =>
    {
        using var lease = objects.Use(handle);
        object value = lease.Value;
        byte[] encoded;
        string mime;
        if (value is VmScreenshotImage screenshot) { encoded = screenshot.EncodeJpeg(); mime = "image/jpeg"; }
        else if (value is ConsoleFrame frame)
        {
            using var bitmap = new System.Drawing.Bitmap(frame.Width, frame.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bits = bitmap.LockBits(new(0, 0, frame.Width, frame.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                byte[] pixels = frame.CopyPixels();
                for (int y = 0; y < frame.Height; y++) System.Runtime.InteropServices.Marshal.Copy(pixels, y * frame.Stride, bits.Scan0 + y * bits.Stride, frame.Width * 4);
            }
            finally { bitmap.UnlockBits(bits); }
            using var output = new MemoryStream(); bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png); encoded = output.ToArray(); mime = "image/png";
        }
        else throw new ArgumentException("请提供 ConsoleFrame 或 VmScreenshotImage 编号。");
        return Task.FromResult(new CallToolResult { Content = [ImageContentBlock.FromBytes(encoded, mime)] });
    });

    [McpServerTool(Name = "core_api_guide", Title = "读取 Core 原始 API 文档", ReadOnly = true, OpenWorld = false)]
    [Description("按行读取随当前版本发布的 Core API 文档，保留调用示例和完整说明。可先用 search 定位，再用 startLine 继续阅读。")]
    public CallToolResult Guide(string? search = null, int startLine = 1, int lineCount = 100)
    {
        if (startLine < 1 || lineCount is < 1 or > 300) return ToolReply.Error("startLine 从 1 开始，lineCount 为 1 至 300。");
        var lines = catalog.Guide.Split('\n');
        if (!string.IsNullOrWhiteSpace(search)) return ToolReply.Result(new { totalLines = lines.Length, matches = lines.Select((line, index) => new { line = index + 1, text = line.TrimEnd('\r') }).Where(item => item.line >= startLine && item.text.Contains(search, StringComparison.OrdinalIgnoreCase)).Take(lineCount) });
        return ToolReply.Result(new { totalLines = lines.Length, startLine, text = string.Join('\n', lines.Skip(startLine - 1).Take(lineCount)) });
    }
}

internal static class JsonElementExtensions
{
    internal static JsonElement CloneOrDefault(this JsonElement value) => value.ValueKind == JsonValueKind.Undefined ? default : value.Clone();
}
