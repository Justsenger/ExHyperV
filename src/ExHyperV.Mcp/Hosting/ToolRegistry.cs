using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExHyperV.Mcp.Api;
using ExHyperV.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ExHyperV.Mcp.Hosting;

// The same registered tools serve listing, authorization, execution and contract verification.
public static class ToolRegistry
{
    public static IReadOnlyList<McpServerTool> Create(McpLaunchOptions launch)
    {
        Type[] types = [typeof(HostTools), typeof(QueryTools), typeof(VmTools), typeof(GpuTools), typeof(ApiTools)];
        return types.SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null)
            .Select(method => Create(method, launch)).Where(tool => tool != null).Cast<McpServerTool>()
            .OrderBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal).ToArray();
    }

    private static McpServerTool? Create(MethodInfo method, McpLaunchOptions launch)
    {
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;
        string name = attribute.Name ?? method.Name;
        string group = Group(name);
        if (!launch.Toolsets.Contains(group) || launch.ReadOnly && !attribute.ReadOnly && !IsCleanup(name)) return null;
        var inner = method.IsStatic ? McpServerTool.Create(method, (object?)null) : McpServerTool.Create(method,
            context => context.Services!.GetRequiredService(method.DeclaringType!));
        var protocol = inner.ProtocolTool;
        protocol.OutputSchema = JsonSerializer.SerializeToElement(OutputSchema(name));
        var input = JsonNode.Parse(protocol.InputSchema.GetRawText())!.AsObject();
        if (input["properties"] is JsonObject properties && properties.ContainsKey("arguments"))
            properties["arguments"] = new JsonObject { ["type"] = new JsonArray("object", "null"),
                ["description"] = "参数名到值的映射，准确结构见 core_api_describe.inputSchema。", ["default"] = null };
        bool journaled = !attribute.ReadOnly && !IsCleanup(name) && name != "server_request_release";
        if (journaled && input["properties"] is JsonObject inputs)
            inputs["requestKey"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128,
                ["description"] = "可选的请求去重键。网络重试使用相同键及参数；不同操作使用新键。" };
        protocol.InputSchema = JsonSerializer.SerializeToElement(input);
        return new RegisteredTool(inner, protocol, journaled);
    }

    private static string Group(string name) => name.StartsWith("core_", StringComparison.Ordinal) || name.StartsWith("server_", StringComparison.Ordinal) ? "core"
        : name.Contains("gpu", StringComparison.Ordinal) ? "devices"
        : name.StartsWith("vm_", StringComparison.Ordinal) ? "vm" : "host";

    // A read-only server still needs to cancel its queries and release their records/resources.
    internal static bool IsCleanup(string name) => name is "core_operation_release" or "core_object_release" or "core_operation_cancel" or "server_task_release";

    public static JsonObject OutputSchema(string name)
    {
        var data = new JsonObject { ["type"] = "object" };
        if (CoreResultType(name) is { } resultType)
        {
            data = ApiSchema.Type(resultType, input: false, depth: 3);
            CamelCaseProperties(data);
        }
        else if (name == "core_api_describe") data = Shape(("apiId", "string"), ("description", "string"), ("category", "string"),
            ("group", "string"), ("readOnly", "boolean"), ("inputSchema", "object"), ("valueSchema", "object"));
        else if (name == "core_api_list") data = Shape(("total", "integer"), ("offset", "integer"), ("limit", "integer"), ("entries", "array"));
        else if (name == "core_operation_status") data = Shape(("operationId", "string"), ("state", "string"),
            ("cancellationRequested", "boolean"), ("events", "object"), ("pendingCallbacks", "array"));
        else if (name == "core_objects") data = Shape(("objects", "array"));
        else if (name == "core_operations") data = Shape(("operations", "array"));
        else if (name == "core_image_read") data = new() { ["type"] = "null" };
        else if (name is "core_api_call" or "core_api_query") data = Shape(("operationId", "string"), ("accepted", "boolean"), ("response", "object"));
        else if (name == "core_api_get") data = Shape(("success", "boolean"));
        else if (name == "core_api_coverage") data = Shape(("coreVersion", "string"), ("coreModuleId", "string"), ("contractReviewed", "boolean"),
            ("contractFingerprint", "string"), ("types", "integer"), ("members", "integer"), ("transportGaps", "array"));
        else if (name == "core_event_read") data = Shape(("events", "array"), ("dropped", "integer"), ("remaining", "integer"), ("resourceLimitReached", "boolean"));
        else if (name == "core_event_subscribe") data = Shape(("subscriptionId", "string"));
        else if (name == "core_binary_create") data = Shape(("handle", "string"), ("length", "integer"));
        else if (name == "core_binary_read") data = Shape(("offset", "integer"), ("length", "integer"), ("count", "integer"), ("base64", "string"), ("end", "boolean"));
        else if (name == "core_api_guide") data = Shape(("totalLines", "integer"), ("startLine", "integer"), ("text", "string"), ("matches", "array"));
        else if (name == "core_callback_prompt") data = Shape(("operationId", "string"), ("requestId", "string"), ("action", "string"), ("state", "string"), ("replyWith", "string"));
        else if (name == "core_operation_cancel") data = Shape(("operationId", "string"), ("cancellationRequested", "boolean"));
        else if (name == "server_info") data = Shape(("success", "boolean"), ("transport", "string"), ("authentication", "string"), ("readOnly", "boolean"),
            ("toolsets", "array"), ("processId", "integer"), ("coreModuleId", "string"), ("responseContract", "integer"));
        else if (name == "server_requests") data = Shape(("total", "integer"), ("offset", "integer"), ("entries", "array"));
        else if (name == "host_hyperv_status") data = Shape(("success", "boolean"), ("isReady", "boolean"), ("statusText", "string"), ("diagnostics", "object"));
        else if (name == "host_system_info") data = Shape(("success", "boolean"));
        else if (name == "vm_query_fields") data = Shape(("success", "boolean"), ("total", "integer"), ("offset", "integer"), ("limit", "integer"), ("fields", "array"));
        else if (name == "gpu_linux_scripts") data = Shape(("success", "boolean"), ("scripts", "array"));
        else if (name is "server_request_release" or "server_task_release" or "core_api_set" or "core_callback_reply" or "core_operation_release" or "core_object_release" or "core_event_unsubscribe" or "gpu_deployment_release")
            data = Shape(("success", "boolean"));
        else throw new InvalidOperationException($"工具 {name} 需要登记返回结构。");
        return new()
        {
            ["type"] = "object", ["required"] = new JsonArray("success", "data", "error"),
            ["properties"] = new JsonObject
            {
                ["success"] = new JsonObject { ["type"] = "boolean" },
                ["data"] = new JsonObject { ["description"] = "工具结果；Core 原始状态、部分完成步骤与清理信息保留在这里。" },
                ["error"] = new JsonObject { ["type"] = new JsonArray("object", "null"), ["properties"] = new JsonObject
                    { ["kind"] = new JsonObject { ["type"] = "string" }, ["message"] = new JsonObject { ["type"] = "string" } } }
            },
            ["allOf"] = new JsonArray(new JsonObject { ["if"] = new JsonObject { ["properties"] = new JsonObject { ["success"] = new JsonObject { ["const"] = true } } },
                ["then"] = new JsonObject { ["properties"] = new JsonObject { ["data"] = data, ["error"] = new JsonObject { ["type"] = "null" } } } })
        };
    }

    private static Type? CoreResultType(string name)
    {
        if (name.StartsWith("gpu_deployment_", StringComparison.Ordinal) && name != "gpu_deployment_release") return typeof(ExHyperV.Models.GpuDeploymentSnapshot);
        var source = name switch
        {
            "vm_memory_get" => ("VmMemoryService", "GetVmMemorySettingsAsync"), "vm_memory_set" => ("VmMemoryService", "SetVmMemorySettingsAsync"),
            "vm_processor_get" => ("VmProcessorService", "GetVmProcessorAsync"), "vm_processor_set" => ("VmProcessorService", "SetVmProcessorAsync"),
            "vm_power" => ("VmPowerService", "ExecuteControlActionAsync"), "vm_rename" => ("VmEditService", "RenameVmAsync"),
            "vm_create" => ("VmCreateService", "CreateVirtualMachineAsync"), "vm_creation_options" => ("VmCreateService", "GetCreationOptionsAsync"),
            "vm_delete_preview" => ("VmDeleteService", "PreviewPurgeAsync"), "vm_unregister" => ("VmDeleteService", "DeleteVmAsync"), "vm_purge" => ("VmDeleteService", "PurgeVmAsync"),
            "vm_network_adapters" => ("VmNetworkService", "GetNetworkAdaptersAsync"), "vm_checkpoints" => ("VmSpacetimeService", "GetSpacetimeNodesAsync"),
            "vm_query" => ("VmQueryService", "QueryAsync"), "vm_gpu_assignments" => ("VmGpuService", "GetVmGpuAssignmentsAsync"),
            "host_disks" => ("HostDiskService", "GetHostDisksAsync"), "host_optical_drives" => ("HostDiskService", "GetHostOpticalDrivesAsync"),
            "host_gpus" => ("HostGpuService", "GetHostGpusAsync"), "host_switches" => ("VmNetworkService", "GetAvailableSwitchesAsync"), _ => default
        };
        return source.Item1 == null ? null : ApiCatalog.Core.GetType("ExHyperV.Services." + source.Item1)!
            .GetMethods().First(method => method.Name == source.Item2).ReturnType;
    }

    private static JsonObject Shape(params (string Name, string Type)[] properties) => new()
    { ["type"] = "object", ["properties"] = new JsonObject(properties.Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Name, new JsonObject { ["type"] = pair.Type }))) };

    private static void CamelCaseProperties(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj["properties"] is JsonObject properties)
                foreach (var pair in properties.ToArray())
                {
                    properties.Remove(pair.Key);
                    properties[JsonNamingPolicy.CamelCase.ConvertName(pair.Key)] = pair.Value;
                }
            foreach (var pair in obj) if (pair.Value != null) CamelCaseProperties(pair.Value);
        }
        else if (node is JsonArray array) foreach (var child in array) if (child != null) CamelCaseProperties(child);
    }

    private sealed class RegisteredTool(McpServerTool inner, Tool protocol, bool journaled) : McpServerTool
    {
        public override IReadOnlyList<object> Metadata => inner.Metadata;
        public override Tool ProtocolTool => protocol;
        public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
        {
            var result = await ToolReply.Run(async () =>
            {
                if (!journaled) return await inner.InvokeAsync(request, cancellationToken);
                var args = request.Params!.Arguments?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? [];
                string? key = args.Remove("requestKey", out var value) ? value.GetString() : null;
                request.Params.Arguments = args;
                return await request.Services!.GetRequiredService<InvocationJournal>().RunAsync(key, protocol.Name,
                    JsonSerializer.SerializeToElement(args), async () => await inner.InvokeAsync(request, cancellationToken),
                    id => request.Services!.GetRequiredService<ApiExecution>().Get(id).Task);
            });
            return ToolReply.Normalize(result);
        }
    }
}
