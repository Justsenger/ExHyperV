using System.Text.Json;

namespace ExHyperV.Server.Hosting;

internal static class OpenApiDocument
{
    private static readonly object Text = new { type = "string" };
    private static readonly object Id = new { type = "string", format = "uuid" };
    private static readonly object Time = new { type = "string", format = "date-time" };
    private static readonly object Flag = new { type = "boolean" };
    private static readonly object Number = new { type = "integer" };
    private static readonly object Any = new Dictionary<string, object>();
    private static readonly object Map = new { type = "object", additionalProperties = true };

    private static object ArrayOf(object items) => new { type = "array", items };
    private static object Ref(string name) => new Dictionary<string, string> { ["$ref"] = "#/components/schemas/" + name };
    private static object Shape(params (string Key, object Schema)[] fields)
    {
        var shape = new Dictionary<string, object> { ["type"] = "object",
            ["properties"] = fields.ToDictionary(f => f.Key.TrimEnd('?'), f => f.Schema) };
        var required = fields.Where(f => !f.Key.EndsWith('?')).Select(f => f.Key).ToArray();
        if (required.Length > 0) shape["required"] = required;
        return shape;
    }

    private static object Query(string name, object schema, bool required = false) => new { name, @in = "query", required, schema };
    private static object Content(string media, object schema) => new Dictionary<string, object> { [media] = new { schema } };

    private static JsonElement Build()
    {
        var schemas = new Dictionary<string, object>
        {
            ["Health"] = Shape(("status", Text), ("version", Text), ("authRequired", Flag)),
            ["ServerInfo"] = Shape(("version", Text), ("isAdministrator", Flag), ("authenticationMode", Text), ("readOnly", Flag),
                ("toolsets", ArrayOf(Text)), ("url", Text), ("contractFingerprint", Text), ("contractReviewed", Flag), ("osArchitecture", Text), ("processId", Number),
                ("supportedConsoleEncodings", ArrayOf(Text)), ("h264HardwareAvailable", Flag), ("h264VideoProcessorAvailable", Flag), ("h264VideoProcessorStatus", Text), ("consoleBackends", ArrayOf(Text)),
                ("consoleProtocolVersions", ArrayOf(Number)), ("extensionBackends", ArrayOf(Text)),
                ("extensionSummary", ArrayOf(Shape(("backendId", Text), ("connectorRegistered", Flag), ("host", Ref("Stage")), ("diagnosticId?", Text)))),
                ("consoleCapabilities", Shape(("scopedTickets", Flag), ("viewOnly", Flag), ("dirtyRegions", Flag), ("moveRegions", Flag),
                    ("independentCursor", Flag), ("displayModes", Flag)))),
            ["Login"] = new { oneOf = new[] { Shape(("password", Text), ("clientName?", Text)), Shape(("token", Text), ("clientName?", Text)) } },
            ["Session"] = Shape(("sessionId", Id), ("expiresAt", Time), ("clientName", Text)),
            ["LoginReply"] = Shape(("sessionId", Id), ("expiresAt", Time), ("clientName", Text), ("token", Text)),
            ["Rights"] = ArrayOf(new { type = "string", @enum = new[] { "Watch", "Control", "AudioOutput", "AudioInput", "Clipboard", "DisplayMode" } }),
            ["ScopedTicket"] = Shape(("vmId", Id), ("channel", new { type = "string", @enum = new[] { "console" } }),
                ("backendId", Text), ("requestedRights", Ref("Rights")), ("viewerContextId?", Text)),
            ["Mode"] = Shape(("id", Text), ("width", Number), ("height", Number), ("refreshNumerator", Number), ("refreshDenominator", Number)),
            ["Stage"] = Shape(("rawCode", Number), ("code", Number)),
            ["Extension"] = Shape(("version", Number), ("host", Ref("Stage")), ("authorization", Ref("Stage")),
                ("guest", Ref("Stage")), ("transport", Ref("Stage")), ("transportKind", Number), ("surfaceEpoch", Number),
                ("supported", Number), ("granted", Number), ("control", Shape(("canView", Flag), ("hasControl", Flag), ("generation", Number))),
                ("availableActions", ArrayOf(Number)), ("reason?", Text), ("diagnosticId?", Text)),
            ["Ticket"] = Shape(("ticket", Text), ("expiresAt", Time)),
            ["Call"] = Shape(("apiId", Text), ("targetObjectId?", Id), ("arguments?", Map), ("background?", Flag), ("retainResult?", Flag),
                ("typeArguments?", ArrayOf(Text)), ("methodArguments?", ArrayOf(Text))),
            ["Get"] = Shape(("apiId", Text), ("targetObjectId?", Id), ("arguments?", Map), ("typeArguments?", ArrayOf(Text))),
            ["Set"] = Shape(("apiId", Text), ("targetObjectId?", Id), ("arguments?", Map), ("typeArguments?", ArrayOf(Text)), ("value", Any)),
            ["Summary"] = Shape(("apiId", Text), ("name", Text), ("type", Text), ("kind", Text), ("description", Text),
                ("category", Text), ("group", Text), ("readOnly", Flag), ("auxiliary", Flag)),
            ["Description"] = new { allOf = new[] { Ref("Summary"), Shape(("parameters", ArrayOf(Map)), ("inputSchema", Map), ("valueSchema", Map),
                ("fieldModification", new { type = "object", nullable = true }), ("exceptions", new { type = "array", items = Map, nullable = true })) } },
            ["Result"] = Shape(("success", Flag), ("result", Any), ("errorKind", new { type = "string", nullable = true }), ("error", new { type = "string", nullable = true })),
            ["EventBatch"] = Shape(("events", ArrayOf(Shape(("sequence", Number), ("time", Time), ("name", Text),
                ("requestId", new { type = "string", format = "uuid", nullable = true }), ("arguments", ArrayOf(Any))))),
                ("dropped", Number), ("remaining", Number), ("resourceLimitReached", Flag)),
            ["Operation"] = Shape(("operationId", Id), ("state", Text), ("createdAt", Time),
                ("completedAt", new { type = "string", format = "date-time", nullable = true }), ("cancellationRequested", Flag)),
            ["Error"] = Shape(("error", Text))
        };
        var paths = new Dictionary<string, Dictionary<string, object>>();
        void Route(string verb, string path, string summary, object? result = null, object? input = null,
            object[]? queries = null, bool anonymous = false, string media = "application/json", bool background = false)
        {
            List<object> parameters = [..queries ?? []];
            foreach (var segment in path.Split('/').Where(part => part.StartsWith('{')))
                parameters.Add(new { name = segment.Trim('{', '}'), @in = "path", required = true, schema = Id });
            var responses = new Dictionary<string, object>();
            if (result == null) responses["204"] = new { description = "请求已完成。" };
            else responses["200"] = new { description = "请求已完成。", content = Content(media, result) };
            if (background) responses["202"] = new { description = "操作已进入队列。", content = Content("application/json", Shape(("operationId", Id))) };
            if (path.StartsWith("/binaries/"))
            {
                var file = new { schema = new { type = "string", format = "binary" } };
                responses["200"] = new { description = "二进制内容。", content = new Dictionary<string, object>
                    { ["application/octet-stream"] = file, ["image/png"] = file, ["image/jpeg"] = file } };
            }
            if (path == "/auth/login") responses["429"] = new { description = "登录暂时受限，可按 Retry-After 重试。" };
            responses["default"] = new { description = "请求无效、未经授权或操作失败。", content = Content("application/json", Ref("Error")) };
            var operation = new Dictionary<string, object> { ["summary"] = summary, ["parameters"] = parameters, ["responses"] = responses };
            if (input != null) operation["requestBody"] = new { required = path != "/auth/ws-ticket", content = Content("application/json", input) };
            if (anonymous) operation["security"] = Array.Empty<object>();
            bool socket = path.StartsWith("/ws/", StringComparison.Ordinal);
            if (socket)
            {
                responses.Remove("204");
                responses["101"] = new { description = "已升级为 WebSocket。" };
                parameters.Add(Query("ticket", Text));
                operation["security"] = new object[] { new { bearer = Array.Empty<string>() }, new { wsTicket = Array.Empty<string>() } };
            }
            string full = socket ? path : "/api/v1" + path;
            if (!paths.TryGetValue(full, out var methods)) paths[full] = methods = [];
            methods.Add(verb, operation);
        }
        Route("get", "/health", "查看服务健康状态。", Ref("Health"), anonymous: true);
        Route("get", "/server/info", "查看权限、运行配置和 Core 契约。", Ref("ServerInfo"));
        Route("get", "/openapi.json", "读取本服务的 OpenAPI 文档。", Map);
        Route("get", "/meta/guide", "读取当前 Core 的原始 Markdown 指南。", Text, media: "text/markdown");
        Route("get", "/meta/field-help", "读取当前 Core 的字段帮助文档。", Map);
        Route("post", "/auth/login", "使用口令或根令牌建立 24 小时会话。", Ref("LoginReply"), Ref("Login"), anonymous: true);
        Route("get", "/auth/sessions", "列出有效会话的元数据。", Shape(("sessions", ArrayOf(Ref("Session")))));
        Route("delete", "/auth/sessions/{sessionId}", "立即吊销会话及其票据。");
        Route("post", "/auth/logout", "结束当前会话，根令牌保持有效。");
        Route("post", "/auth/password", "持久保存新口令并吊销现有会话和票据。", input: Shape(("password", Text)));
        Route("post", "/auth/ws-ticket", "领取 30 秒单次票据，空对象使用 v1。", Ref("Ticket"),
            new { oneOf = new object[] { new { type = "object", maxProperties = 0 }, Ref("ScopedTicket") } });
        Route("get", "/vms/{vmId}/console/backends", "查询当前主体可用的后端与扩展状态。", Shape(("vmId", Id),
            ("backends", ArrayOf(Shape(("backendId", Text), ("rights", Ref("Rights")), ("extension?", Ref("Extension")), ("modes", ArrayOf(Ref("Mode"))))))));
        Route("get", "/core/catalog", "按文本、工具组、种类和只读标记检索 Core 成员。", Shape(("total", Number), ("offset", Number), ("limit", Number), ("entries", ArrayOf(Ref("Summary")))),
            queries: [Query("search", Text), Query("group", Text), Query("kind", Text), Query("includeAuxiliary", Flag), Query("readOnlyOnly", Flag),
                Query("limit", new { type = "integer", minimum = 1, maximum = 100, @default = 30 }), Query("offset", new { type = "integer", minimum = 0, @default = 0 })]);
        Route("get", "/core/describe", "查看完整签名、参数与值的 JSON Schema、字段修改说明和异常。", Ref("Description"), queries: [Query("apiId", Text, true)]);
        Route("post", "/core/call", "调用方法或构造函数，可选择后台执行。", new { allOf = new[] { Ref("Result"), Shape(("operationId", Id)) } }, Ref("Call"), background: true);
        Route("post", "/core/get", "读取属性、索引器或字段。", Shape(("value", Any)), Ref("Get"));
        Route("post", "/core/set", "写入属性、索引器或字段。", Shape(("success", Flag)), Ref("Set"));
        Route("get", "/objects", "列出持有对象和借用状态。", Shape(("objects", ArrayOf(Shape(("handle", Id), ("type", Text), ("owned", Flag), ("activeCalls", Number))))));
        Route("delete", "/objects/{objectId}", "释放对象并等待资源清理。", Shape(("handle", Id), ("success", Flag)));
        Route("get", "/operations", "列出运行中及已结束的操作。", Shape(("operations", ArrayOf(Ref("Operation")))));
        Route("get", "/operations/{operationId}", "读取状态、最终结果、进度和待回答事项。", new { allOf = new[] { Ref("Operation"),
            Shape(("result", Any), ("events", Ref("EventBatch")), ("pendingCallbacks", ArrayOf(Shape(("requestId", Id), ("name", Text), ("arguments", ArrayOf(Any)), ("returnType", Text))))) } },
            queries: [Query("eventLimit", Number)]);
        Route("post", "/operations/{operationId}/cancel", "请求取消，最终状态以操作结果为准。", Shape(("operationId", Id), ("cancellationRequested", Flag)));
        Route("post", "/operations/{operationId}/reply", "向挂起的 Core 回调提交回答。", input: Shape(("requestId", Id), ("value", Any)));
        Route("delete", "/operations/{operationId}", "清理已结束的操作记录。");
        Route("post", "/events/subscribe", "订阅 Core 事件。", Shape(("subscriptionId", Id)), Shape(("apiId", Text), ("targetObjectId?", Id)));
        Route("get", "/events/{subscriptionId}", "取出事件及丢弃计数，画面只保留最新帧。", Ref("EventBatch"), queries: [Query("limit", Number)]);
        Route("delete", "/events/{subscriptionId}", "解绑事件并归还缓冲的画面租约。");
        Route("get", "/binaries/{objectId}", "读取二进制数据、PNG 画面或 JPEG 截图。", Any);
        Route("get", "/ws/v1/events", "订阅操作状态、待处理回调和 Core 事件。");
        Route("get", "/ws/v1/telemetry", "按需订阅宿主机和虚拟机遥测。");
        Route("get", "/ws/v1/console/{vmId}", "连接原生虚拟机控制台，传输视频、音频和输入。",
            queries: [Query("backend", Text), Query("width", Number), Query("height", Number), Query("desktopScale", Number),
                Query("connectTimeoutMs", Number), Query("frameEncoding", Text), Query("hostCursor", Flag),
                Query("clipboard", Flag), Query("audioOutput", Flag), Query("audioInput", Flag)]);
        Route("get", "/ws/v2/console/{vmId}", "使用限域票据订阅控制台。", queries: [Query("backend", Text),
            Query("requiredFeatures", Number), Query("frameEncoding", Text), Query("hostCursor", Flag), Query("width", Number),
            Query("height", Number), Query("desktopScale", Number), Query("connectTimeoutMs", Number), Query("audioInput", Flag),
            Query("audioOutput", Flag), Query("clipboard", Flag)]);
        var v2 = (Dictionary<string, object>)paths["/ws/v2/console/{vmId}"]["get"];
        v2["security"] = new[] { new { wsTicket = Array.Empty<string>() } };
        v2["x-console-wire"] = new {
            extension = "Discrete backends accept setMode(id). RDP desktop dimensions and scale are restricted to built-in backends.",
            byteOrder = "little-endian", requiredFeatures = 0, legacyVideoHeader = 34, audioHeader = 18,
            regions = "EX type=4 flags:u8 format=0 version=2 total:u32 seq:i64 base:i64 w:u16 h:u16 dirty:u16 moves:u16 header:u32 required:u16",
            records = "Dirty x,y,w,h and move sx,sy,x,y,w,h are u16. Copy all move sources from the base before applying dirty BGRA rows.",
            commands = new[] { "displayed(sequence)", "takeControl(generation)", "setMode(id)", "setEncoding(encoding)", "captureFrame",
                "key", "keys", "pause", "releaseAllInputs", "syncKeyboard", "pointer", "click", "resize", "clipboard" },
            input = "Mutating commands require generation and increasing inputSequence. Microphone packets prefix the v1 packet with two i64 values in that order.",
            messages = new[] { "state", "control(hasControl,canTake,generation)", "capabilities(modes)", "cursor", "cursorState(state)",
                "displayMode(result)", "transport(transportKind,surfaceEpoch)", "clipboard", "resizeRejected", "encodingChanged", "error" }
        };
        return JsonSerializer.SerializeToElement(new { openapi = "3.0.3", info = new { title = "ExHyperV.Server", version = Program.Version }, paths,
            security = new[] { new { bearer = Array.Empty<string>() } },
            components = new { schemas, securitySchemes = new { wsTicket = new { type = "apiKey", @in = "query", name = "ticket" }, bearer = new { type = "http", scheme = "bearer",
                description = "使用根令牌或登录取得的会话令牌。仅回环且尚未配置凭证时允许本机直接访问。" } } } });
    }
    internal static readonly JsonElement Document = Build();
}
