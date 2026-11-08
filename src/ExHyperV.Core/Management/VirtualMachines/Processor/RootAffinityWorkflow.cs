using ExHyperV.Tools;

namespace ExHyperV.Services;

internal sealed record RootAffinityWorkflowOperations(
    Func<Task<ApiResponse<RootProcessorTopology>>> ReadTopology,
    Func<Guid, Task<ApiResponse<ushort>>> ReadVmState,
    Func<Guid, Task<ApiResponse<string>>> ReadPreference,
    Func<Guid, string, Task<ApiResponse>> SavePreference,
    Func<Guid, Task<ApiResponse<RootProcessAffinity>>> ReadProcess,
    Func<Guid, IReadOnlyList<int>, Task<ApiResponse<RootProcessAffinity>>> ApplyProcess,
    Func<TimeSpan, CancellationToken, Task> Delay,
    Func<Guid, Task<ApiResponse<bool>>>? BeforeRestore = null,
    Func<Task<ApiResponse<AffinityEnvironment>>>? ReadEnvironment = null);

// Facade 持有同一 VM 的跨进程锁。启动恢复整轮持锁，手动保存排在其后，
// 即使新设置已生效而元数据保存失败，也不会被更早的恢复任务覆盖。
internal static class RootAffinityWorkflow
{
    internal static async Task<CpuAffinityService.SaveResult> SaveAsync(
        Guid vmId, IReadOnlyList<int> selection, RootAffinityWorkflowOperations operations)
    {
        bool mutationAttempted = false;
        try
        {
            var topology = Require(await operations.ReadTopology().ConfigureAwait(false));
            int[] processors = Validate(selection, topology);
            var environmentBefore = await AffinityPreference.ReadEnvironmentAsync(operations.ReadEnvironment).ConfigureAwait(false);
            ushort state = Require(await operations.ReadVmState(vmId).ConfigureAwait(false));
            CpuAffinityService.SaveStatus status;
            if (HasProcess(state))
            {
                mutationAttempted = true;
                Require(await operations.ApplyProcess(vmId, processors).ConfigureAwait(false));
                status = CpuAffinityService.SaveStatus.Applied;
            }
            else if (CanQueue(state)) status = CpuAffinityService.SaveStatus.Queued;
            else return Failed(ApiResponse.Fail("虚拟机正在转换状态，暂不能保存 CPU 亲和性。",
                kind: ApiErrorKind.InvalidState));

            mutationAttempted = true;
            ApiResponse saved;
            var environmentAfter = await AffinityPreference.ReadEnvironmentAsync(operations.ReadEnvironment).ConfigureAwait(false);
            var preference = AffinityPreference.Create(processors, AffinityPreference.RootBackend,
                AffinityPreference.StableEnvironment(environmentBefore, environmentAfter));
            var assessment = preference.Check(AffinityPreference.RootBackend, environmentAfter);
            try
            {
                saved = await operations.SavePreference(vmId, preference.Serialize()).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                saved = ApiResponse.Fail(error.Message, error.HResult, exception: error, kind: ApiErrorKind.OutcomeUnknown);
            }
            return new(status, saved.Success)
            {
                Error = saved.Success ? null : saved.Error,
                ErrorKind = saved.ErrorKind, ErrorCode = saved.Code, ErrorSource = saved.ErrorSource,
                SavedPreferenceStatus = saved.Success ? assessment.Status : CpuAffinityService.PreferenceStatus.EnvironmentUnverified,
                SavedPreferenceIssue = saved.Success ? assessment.Issue : "本次选择未确认保存成功。"
            };
        }
        catch (CoreOperationException error) { return Failed(error.Result); }
        catch (Exception error)
        {
            return Failed(ApiResponse.Fail(error.Message, error.HResult, exception: error,
                kind: mutationAttempted ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.OperationFailed));
        }
    }

    internal static async Task<CpuAffinityService.AffinityState> ReadAsync(
        Guid vmId, RootAffinityWorkflowOperations operations)
    {
        ushort state = Require(await operations.ReadVmState(vmId).ConfigureAwait(false));
        string? preference = ReadOptional(await operations.ReadPreference(vmId).ConfigureAwait(false));
        var saved = await InspectPreferenceAsync(preference, operations).ConfigureAwait(false);

        RootProcessAffinity? current = null;
        if (HasProcess(state))
            current = Require(await operations.ReadProcess(vmId).ConfigureAwait(false));
        else if (!CanQueue(state))
            throw Failure("虚拟机正在转换状态，无法确认当前 CPU 亲和性。", ApiErrorKind.InvalidState);

        ushort confirmed = Require(await operations.ReadVmState(vmId).ConfigureAwait(false));
        if (confirmed != state)
            throw Failure("读取期间虚拟机状态发生变化，请重新读取。", ApiErrorKind.Conflict);
        return new(VmMapper.MapState(state), current is null ? null : DecodeMask(current.Mask),
            saved.Preference is null ? null : Array.AsReadOnly(saved.Preference.LogicalProcessors), current?.ProcessId, current?.CreationTime)
        { SavedPreferenceStatus = saved.Status, SavedPreferenceIssue = saved.Issue };
    }

    internal static async Task<CpuAffinityService.StartupApplyStatus> RestoreAsync(
        Guid vmId, RootAffinityWorkflowOperations operations, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? expected = ReadOptional(await operations.ReadPreference(vmId).ConfigureAwait(false));
        var saved = await InspectPreferenceAsync(expected, operations).ConfigureAwait(false);
        if (saved.Status == CpuAffinityService.PreferenceStatus.InvalidConfiguration)
            return CpuAffinityService.StartupApplyStatus.InvalidConfiguration;
        if (saved.Status is CpuAffinityService.PreferenceStatus.None or CpuAffinityService.PreferenceStatus.Unrestricted)
            return CpuAffinityService.StartupApplyStatus.NotRequired;
        if (saved.Status != CpuAffinityService.PreferenceStatus.MatchesEnvironment)
            return CpuAffinityService.StartupApplyStatus.ReselectionRequired;
        int[] processors = saved.Preference!.LogicalProcessors;

        var topology = Require(await operations.ReadTopology().ConfigureAwait(false));
        try { processors = Validate(processors, topology); }
        catch (CoreOperationException) { return CpuAffinityService.StartupApplyStatus.InvalidConfiguration; }
        for (int attempt = 0; attempt < 5; attempt++)
        {
            await operations.Delay(TimeSpan.FromSeconds(attempt == 0 ? 1 : 2), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            string? latest = ReadOptional(await operations.ReadPreference(vmId).ConfigureAwait(false));
            if (!string.Equals(expected, latest, StringComparison.Ordinal))
                return CpuAffinityService.StartupApplyStatus.Superseded;
            ushort state = Require(await operations.ReadVmState(vmId).ConfigureAwait(false));
            if (CanQueue(state)) return CpuAffinityService.StartupApplyStatus.VmStopped;
            if (!HasProcess(state))
            {
                if (state is 10 or 32770 or 32771 or 32777) continue;
                throw Failure("虚拟机当前状态不允许恢复 CPU 亲和性。", ApiErrorKind.InvalidState);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // 每次实际尝试前重新核实，不用进入等待循环前的旧环境放行后续重试。
            var environment = await AffinityPreference.ReadEnvironmentAsync(operations.ReadEnvironment).ConfigureAwait(false);
            if (saved.Preference.Check(AffinityPreference.RootBackend, environment).Status != CpuAffinityService.PreferenceStatus.MatchesEnvironment)
                return CpuAffinityService.StartupApplyStatus.ReselectionRequired;
            cancellationToken.ThrowIfCancellationRequested();
            if (operations.BeforeRestore is { } beforeRestore)
            {
                var permitted = await beforeRestore(vmId).ConfigureAwait(false);
                if (!permitted.HasData && permitted.ErrorKind == ApiErrorKind.NotFound) continue;
                if (!Require(permitted)) return CpuAffinityService.StartupApplyStatus.NotRequired;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var applied = await operations.ApplyProcess(vmId, processors).ConfigureAwait(false);
            if (applied.HasData) return CpuAffinityService.StartupApplyStatus.Applied;
            // 只重试明确尚未出现的进程。写入结果未知、身份冲突等不能自动重放。
            if (applied.ErrorKind != ApiErrorKind.NotFound) Require(applied);
        }
        return CpuAffinityService.StartupApplyStatus.Exhausted;
    }

    internal static IReadOnlyList<int> DecodeMask(ulong mask)
        => Array.AsReadOnly(Enumerable.Range(0, 64).Where(index => (mask & (1UL << index)) != 0).ToArray());

    private static int[] Validate(IReadOnlyList<int> selection, RootProcessorTopology topology)
    {
        int[] indices = selection.Distinct().Order().ToArray();
        if (topology.Group != 0 || topology.ActiveMask == 0)
            throw Failure("Root 亲和性要求有效的单 Windows 处理器组拓扑。", ApiErrorKind.Unsupported);
        if (indices.Any(index => index is < 0 or >= 64 || (topology.ActiveMask & (1UL << index)) == 0))
            throw Failure("所选处理器不在当前宿主允许的处理器范围中。", ApiErrorKind.InvalidArgument);
        return indices;
    }

    private static bool HasProcess(ushort state) => state is 2 or 9 or 32768;
    private static bool CanQueue(ushort state) => state is 3 or 6 or 32769 or 32779 or 32783;

    internal static async Task<(AffinityPreference? Preference, CpuAffinityService.PreferenceStatus Status, string? Issue)> InspectPreferenceAsync(
        string? value, RootAffinityWorkflowOperations operations)
    {
        if (value is null) return (null, CpuAffinityService.PreferenceStatus.None, null);
        if (!AffinityPreference.TryRead(value, out var preference))
            return (null, CpuAffinityService.PreferenceStatus.InvalidConfiguration, "保存的亲和性内容无效，未自动应用；原元数据保留。");
        var environment = preference!.CanCompareEnvironment(AffinityPreference.RootBackend)
            ? await AffinityPreference.ReadEnvironmentAsync(operations.ReadEnvironment).ConfigureAwait(false) : null;
        var result = preference.Check(AffinityPreference.RootBackend, environment);
        return (preference, result.Status, result.Issue);
    }

    private static string? ReadOptional(ApiResponse<string> response)
        => response.IsEmpty ? null : Require(response);

    private static T Require<T>(ApiResponse<T> response)
    {
        if (response.HasData) return response.Data;
        throw new CoreOperationException(ApiResponse.Fail(response.Success ? "未返回所需的亲和性数据。" : response.Error,
            response.Code, response.ErrorSource, response.Exception, response.Success ? ApiErrorKind.InvalidState : response.ErrorKind));
    }

    private static CoreOperationException Failure(string message, ApiErrorKind kind)
        => new(ApiResponse.Fail(message, kind: kind));

    private static CpuAffinityService.SaveResult Failed(ApiResponse error) => new(CpuAffinityService.SaveStatus.Failed, false)
    {
        Error = error.Error, ErrorKind = error.ErrorKind, ErrorCode = error.Code, ErrorSource = error.ErrorSource,
        SavedPreferenceStatus = CpuAffinityService.PreferenceStatus.EnvironmentUnverified,
        SavedPreferenceIssue = "本次选择未确认保存成功。"
    };
}
