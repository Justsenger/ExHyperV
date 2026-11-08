using System.Text.Json;

namespace ExHyperV.Core.Tests.TestExecution;

internal sealed class ApiCaseContext(string directory, CancellationToken token)
{
    private static readonly JsonSerializerOptions EvidenceJson = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal string DirectoryPath { get; } = directory;
    internal ResourceInspection.InspectionConfiguration Configuration => ResourceInspection.InspectionConfiguration.Read(Path.Combine(DirectoryPath, "resource-settings.json"));
    internal CancellationToken Token { get; } = token;
    internal int Calls { get; private set; }
    internal int Assertions { get; private set; }
    internal bool GuardNativeOutcomes { get; set; }
    internal List<string> Evidence { get; } = [];
    internal void RequireSettled(bool outcomeUnknown, string detail)
    {
        if (!outcomeUnknown) return;
        File.WriteAllText(Path.Combine(DirectoryPath, "recovery-hold.txt"), detail);
        throw new ApiCheckFailureException("原生任务终态待核实，已保留资源：" + detail);
    }

    internal T Call<T>(Func<T> action) { Token.ThrowIfCancellationRequested(); Calls++; return CheckOutcome(action()); }
    internal void Call(Action action) { Token.ThrowIfCancellationRequested(); Calls++; action(); }
    internal async Task<T> CallAsync<T>(Func<Task<T>> action) { Token.ThrowIfCancellationRequested(); Calls++; return CheckOutcome(await action()); }
    private T CheckOutcome<T>(T value)
    {
        if (!GuardNativeOutcomes || value is null) return value;
        var type = value.GetType();
        bool unknown = type.GetProperty("OutcomeUnknown")?.GetValue(value) is true
            || type.GetProperty("OperationStateUnknown")?.GetValue(value) is true
            || type.GetProperty("OwnershipUncertain")?.GetValue(value) is true
            || type.GetProperty("ErrorKind")?.GetValue(value) is ExHyperV.Tools.ApiErrorKind.OutcomeUnknown;
        if (unknown)
        {
            string detail = type.GetProperty("Error")?.GetValue(value)?.ToString() ?? "原生操作尚未确认终态";
            try { DurableJson.Write(Path.Combine(DirectoryPath, "native-uncertain-result.json"), value); }
            finally { File.WriteAllText(Path.Combine(DirectoryPath, "recovery-hold.txt"), detail); }
            throw new ApiCheckFailureException("原生操作回执已保存，后续操作等待终态核验：" + detail);
        }
        return value;
    }
    internal void Equal<T>(T expected, T actual, string label)
    {
        Require(EqualityComparer<T>.Default.Equals(expected, actual), $"{label}：预期 {Text(expected)}，实际 {Text(actual)}");
    }
    internal void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string label)
    {
        var wanted = expected.ToArray(); var found = actual.ToArray();
        Require(wanted.SequenceEqual(found), $"{label}：预期 {Text(wanted)}，实际 {Text(found)}");
    }
    internal void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool success, string detail)
    {
        Assertions++;
        Evidence.Add(detail);
        if (!success) throw new ApiCheckFailureException(detail);
    }
    internal void Throws<T>(Action action, string label) where T : Exception
    {
        try { action(); }
        catch (T error) { Assertions++; Evidence.Add($"{label}：{error.GetType().Name}，{error.Message}"); return; }
        throw new ApiCheckFailureException($"{label}：预期 {typeof(T).Name}，实际调用正常返回。");
    }
    private static string Text<T>(T value) => value switch
    {
        IntPtr pointer => $"0x{pointer.ToInt64():X}",
        UIntPtr pointer => $"0x{pointer.ToUInt64():X}",
        _ => JsonSerializer.Serialize(value, EvidenceJson)
    };
}

internal sealed class ApiCheckFailureException(string message) : Exception(message);
internal sealed class ApiCheckSkippedException(string message) : Exception(message);
internal sealed record ApiCaseDefinition(string Id, string Api, Func<ApiCaseContext, Task> Run, bool Exclusive = false, bool UsesHostResources = false, int TimeoutSeconds = 30);
