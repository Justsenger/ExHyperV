using ExHyperV.Tools;
using System.Text.Json;

namespace ExHyperV.Services;

internal sealed record AffinityEnvironment(string HostFingerprint, string Backend, string TopologyFingerprint);

// 一个 HostOnlyItems 值保存选择及其编号所属环境，避免分别更新造成错配。
internal sealed record AffinityPreference(int Version, string Backend, int[] LogicalProcessors,
    string? HostFingerprint, string? TopologyFingerprint)
{
    internal const string RootBackend = "WindowsProcessAffinity";
    internal const string HcsBackend = "HcsCpuGroup";

    internal static AffinityPreference Create(IReadOnlyList<int> indices, string backend, AffinityEnvironment? environment)
        => new(2, backend, indices.Distinct().Order().ToArray(), environment?.HostFingerprint, environment?.TopologyFingerprint);

    internal string Serialize() => JsonSerializer.Serialize(this);

    internal static bool TryRead(string json, out AffinityPreference? preference)
    {
        preference = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                var legacy = ReadIndices(root);
                if (legacy is null) return false;
                preference = new(0, RootBackend, legacy, null, null);
                return true;
            }
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Version", out var version) || !version.TryGetInt32(out int number)
                || !root.TryGetProperty("Backend", out var backend) || backend.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("LogicalProcessors", out var processors)) return false;
            var indices = ReadIndices(processors);
            if (indices is null || string.IsNullOrWhiteSpace(backend.GetString())) return false;
            preference = new(number, backend.GetString()!, indices,
                ReadString(root, "HostFingerprint"), ReadString(root, "TopologyFingerprint"));
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { return false; }
    }

    private static int[]? ReadIndices(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return null;
        var values = new List<int>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int index) || index < 0) return null;
            values.Add(index);
        }
        return values.Distinct().Order().ToArray();
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal (CpuAffinityService.PreferenceStatus Status, string? Issue) Check(string backend, ApiResponse<AffinityEnvironment>? current)
    {
        if (LogicalProcessors.Length == 0) return (CpuAffinityService.PreferenceStatus.Unrestricted, null);
        if (Version is not (0 or 1 or 2))
            return (CpuAffinityService.PreferenceStatus.EnvironmentUnverified, "保存的亲和性格式版本尚不支持，请重新选择并保存。");
        if (Backend != backend)
            return (CpuAffinityService.PreferenceStatus.EnvironmentChanged, "保存的处理器编号属于其他亲和性后端，请重新选择并保存。");
        if (Version != 2 || !IsFingerprint(HostFingerprint) || !IsFingerprint(TopologyFingerprint))
            return (CpuAffinityService.PreferenceStatus.EnvironmentUnverified, "保存的亲和性缺少有效的宿主或拓扑信息，请重新选择并保存。");
        if (current is not { HasData: true })
            return (CpuAffinityService.PreferenceStatus.EnvironmentUnverified,
                "当前亲和性环境无法核实，未自动应用保存的选择。" + current?.Error);
        if (current.Data.Backend != Backend || current.Data.HostFingerprint != HostFingerprint || current.Data.TopologyFingerprint != TopologyFingerprint)
            return (CpuAffinityService.PreferenceStatus.EnvironmentChanged, "宿主、亲和性后端或处理器编号映射已改变，请重新选择并保存。");
        return (CpuAffinityService.PreferenceStatus.MatchesEnvironment, null);
    }

    private static bool IsFingerprint(string? value)
        => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    internal bool CanCompareEnvironment(string backend)
        => Version == 2 && Backend == backend && LogicalProcessors.Length > 0
            && IsFingerprint(HostFingerprint) && IsFingerprint(TopologyFingerprint);

    internal static async Task<ApiResponse<AffinityEnvironment>> ReadEnvironmentAsync(Func<Task<ApiResponse<AffinityEnvironment>>>? read)
    {
        if (read is null) return ApiResponse<AffinityEnvironment>.Fail("未提供环境读取能力。", kind: ApiErrorKind.Unsupported);
        try { return await read().ConfigureAwait(false); }
        catch (Exception error) { return ApiResponse<AffinityEnvironment>.Fail(error.Message, error.HResult, exception: error, kind: ApiErrorKind.OperationFailed); }
    }

    internal static AffinityEnvironment? StableEnvironment(ApiResponse<AffinityEnvironment> before, ApiResponse<AffinityEnvironment> after)
        => before.HasData && after.HasData && before.Data == after.Data ? before.Data : null;
}
