using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class VmGpuRepairService
{
    internal static IReadOnlyList<RebindCandidate> FindRebindCandidates(string source, IReadOnlyList<string> pool)
    {
        string identity = GpuIdentity(source);
        return Array.AsReadOnly(pool.Where(path => !string.IsNullOrWhiteSpace(path) && GpuIdentity(path) == identity)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new RebindCandidate(path)).ToArray());
    }

    internal static ApiResponse ValidateRepairSnapshot(IReadOnlyList<StaleGpuPartition> targets,
        IReadOnlyList<StaleGpuPartition> current)
    {
        foreach (var target in targets)
        {
            if (target.RequiresSelection)
                return ApiResponse.Fail("存在尚未选择候选的 GPU 设备。", kind: ApiErrorKind.AmbiguousTarget);
            var matches = current.Where(item => string.Equals(item.Instance, target.Instance, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0].HostResource != target.HostResource
                || matches[0].IsStructurallyIncomplete != target.IsStructurallyIncomplete)
                return ApiResponse.Fail("GPU 配置已变化，请重新扫描。", kind: ApiErrorKind.Conflict);
            var fresh = matches[0];
            // 没有明确选卡时，保留原先零/单候选的决定；不能把新出现的多候选当成删除或任选。
            if (!target.HasExplicitSelection)
            {
                if (fresh.Candidates.Count != target.Candidates.Count
                    || !string.Equals(fresh.RebindPath, target.RebindPath, StringComparison.OrdinalIgnoreCase))
                    return ApiResponse.Fail("GPU 修复候选已变化，请重新扫描并确认。", kind: ApiErrorKind.Conflict);
            }
            else if (!fresh.Candidates.Any(c => string.Equals(c.Id, target.RebindPath, StringComparison.OrdinalIgnoreCase)))
                return ApiResponse.Fail("选中的 GPU 已不在当前修复候选中，尚未改绑或删除设备。", kind: ApiErrorKind.Conflict);
        }
        return ApiResponse.Ok();
    }

    internal static ApiResponse<IReadOnlyList<StaleGpuPartition>> SelectRepairTargets(Guid vmId,
        IReadOnlyList<StaleGpuPartition> targets, IReadOnlyDictionary<Guid, string>? selections)
    {
        var validation = ValidateRepairTargets(vmId, targets);
        if (!validation.Success)
            return ApiResponse<IReadOnlyList<StaleGpuPartition>>.Fail(validation.Error, kind: validation.ErrorKind);
        var byInstance = targets.ToDictionary(t => Guid.Parse(t.Instance.Trim('{', '}', '_', ' ')));
        if (selections != null && selections.Keys.Any(id => !byInstance.ContainsKey(id)))
            return ApiResponse<IReadOnlyList<StaleGpuPartition>>.Fail("选卡结果包含不属于当前修复清单的设备。", kind: ApiErrorKind.InvalidArgument);
        try
        {
            var chosen = targets.Select(target => selections != null
                    && selections.TryGetValue(Guid.Parse(target.Instance.Trim('{', '}', '_', ' ')), out string? candidate)
                    ? target.SelectCandidate(candidate) : target).ToArray();
            if (chosen.Any(t => t.RequiresSelection))
                return ApiResponse<IReadOnlyList<StaleGpuPartition>>.Fail("请为每个存在多个候选的 GPU 设备选择一张显卡。尚未执行修复。", kind: ApiErrorKind.AmbiguousTarget);
            return ApiResponse<IReadOnlyList<StaleGpuPartition>>.Ok(Array.AsReadOnly(chosen));
        }
        catch (ArgumentException error)
        {
            return ApiResponse<IReadOnlyList<StaleGpuPartition>>.Fail(error.Message, kind: ApiErrorKind.InvalidArgument);
        }
    }
}
