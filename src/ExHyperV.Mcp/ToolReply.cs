using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Principal;
using ExHyperV.Mcp.Api;
using ExHyperV.Tools;
using ModelContextProtocol.Protocol;

namespace ExHyperV.Mcp;

internal static class ToolReply
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(), new ExceptionConverter() }
    };

    public static CallToolResult Result(object value, bool failed = false)
    {
        JsonElement data = JsonSerializer.SerializeToElement(value, value.GetType(), Json);
        return new()
        {
            IsError = failed, StructuredContent = data,
            Content = [new TextContentBlock { Text = data.GetRawText() }]
        };
    }

    public static CallToolResult From(ApiResponse response) => Result(response, !response.Success);

    internal static CallToolResult Normalize(CallToolResult result)
    {
        var data = result.StructuredContent;
        string? Field(string name) => data is { } element ? FailureField(element, name) : null;
        bool success = result.IsError != true;
        var normalized = Result(new { success, data, error = success ? null : new
        {
            kind = Field("errorKind") ?? "OperationFailed",
            message = Field("error") ?? "操作失败；详细结果见 data。"
        } }, !success);
        normalized.Content = normalized.Content.Concat(result.Content.Where(block => block is not TextContentBlock)).ToList();
        return normalized;
    }

    internal static string? FailureField(JsonElement data, string name, int depth = 0)
    {
        if (depth > 8 || data.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in data.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        foreach (var property in data.EnumerateObject())
            if (property.Name.Equals("result", StringComparison.OrdinalIgnoreCase) || property.Name.Equals("response", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
                if (FailureField(property.Value, name, depth + 1) is { } found) return found;
        return null;
    }
    public static CallToolResult From<T>(ApiResponse<T> response) => Result(response, !response.Success);
    public static CallToolResult Error(string message, ApiErrorKind kind = ApiErrorKind.InvalidArgument) =>
        From(ApiResponse.Fail(message, kind: kind));

    public static async Task<CallToolResult> Run(Func<Task<CallToolResult>> operation)
    {
        try { return await operation(); }
        catch (OperationCanceledException) { throw; }
        catch (CoreOperationException error) { return From(error.Result); }
        catch (ArgumentException error) { return Failure(error, ApiErrorKind.InvalidArgument); }
        catch (InvalidOperationException error) { return Failure(error, ApiErrorKind.InvalidState); }
        catch (UnauthorizedAccessException error) { return Failure(error, ApiErrorKind.AccessDenied); }
        catch (Exception error) { return Failure(error, ApiErrorKind.OperationFailed); }
    }

    private static CallToolResult Failure(Exception error, ApiErrorKind kind) =>
        From(ApiResponse.Fail(error.Message, exception: error, kind: kind));

    public static void RequireAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("当前 MCP 进程使用普通用户令牌。stdio 模式请以管理员身份启动客户端；HTTP 模式请以管理员身份启动服务器 EXE。");
    }

    public static Task<CallToolResult> Management(Func<Task<CallToolResult>> operation) => Run(async () =>
    {
        RequireAdministrator();
        return await operation();
    });

    public static Guid VmId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("请提供有效的虚拟机 GUID。");
        RequireAdministrator();
        return id;
    }

    // Exceptions can contain native objects and reflection metadata. Preserve Core diagnostics only.
    private sealed class ExceptionConverter : JsonConverter<Exception>
    {
        public override bool CanConvert(Type type) => typeof(Exception).IsAssignableFrom(type);
        public override Exception Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, Exception value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, ApiExceptionDetails.Capture(value), options);
        }
    }
}
