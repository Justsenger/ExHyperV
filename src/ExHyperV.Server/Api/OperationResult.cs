using System.Reflection;
using System.Text.Json;
using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Server.Api;

internal static class DisplayText
{
    internal static string Clean(string text) => text.Replace('\u003b', '，').Replace('\uff1b', '，').Replace('\u00b7', ' ');
}

internal sealed record OperationResult(bool Success, object? Result, string? ErrorKind = null, string? Error = null)
{
    public static OperationResult FromCore(object? original, object? packed)
    {
        if (!ApiCodec.Failed(original)) return new(true, packed);
        if (original is GpuDeploymentSnapshot { OutcomeUnknown: true })
            return new(false, packed, "OutcomeUnknown", "请核对资源的实际状态。");
        JsonElement data = JsonSerializer.SerializeToElement(packed);
        string? Field(string key) => data.ValueKind == JsonValueKind.Object
            ? data.EnumerateObject().FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Value.ToString() : null;
        string? kind = Field("errorKind");
        return new(false, packed, string.IsNullOrEmpty(kind) ? "OperationFailed" : kind, DisplayText.Clean(Field("error") ?? "操作失败。"));
    }

    public static OperationResult FromException(Exception error, ApiCodec codec)
    {
        if (error is TargetInvocationException { InnerException: { } inner }) error = inner;
        if (error is CoreOperationException core) return FromCore(core.Result, codec.Pack(core.Result));
        string kind = error switch
        {
            OperationCanceledException => "Cancelled",
            UnauthorizedAccessException => "AccessDenied",
            ArgumentException or FormatException or OverflowException or JsonException or TargetException or TargetParameterCountException => "InvalidArgument",
            InvalidOperationException => "InvalidState",
            _ => "OperationFailed"
        };
        return new(false, codec.Snapshot(ApiExceptionDetails.Capture(error)), kind, DisplayText.Clean(error.Message));
    }
}
