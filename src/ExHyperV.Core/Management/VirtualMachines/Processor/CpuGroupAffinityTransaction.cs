using ExHyperV.Tools;
using System.Text.Json;

namespace ExHyperV.Services;

internal sealed record CpuGroupAffinityResult(
    ApiResponse Operation, ApiResponse? Recovery, Guid? CurrentGroupId, Guid? RetainedGroupId);

internal sealed record CpuGroupAffinityOperations(
    Func<Guid, Task<ApiResponse<VmCpuGroupBinding>>> ReadBinding,
    Func<VmCpuGroupBinding, Guid, Task<ApiResponse>> SetBinding,
    Func<Task<ApiResponse<List<HcsCpuGroupDetail>>>> ReadGroups,
    Func<Task<ApiResponse<List<HcsLogicalProcessor>>>> ReadTopology,
    Func<Guid, uint[], Task<ApiResponse>> CreateGroup,
    Func<Guid, Task<ApiResponse>> DeleteGroup,
    Func<Task<HostCpuGroupBindings>> ReadBindings,
    Func<Guid, uint, uint, Task<ApiResponse>> SetProperty);

// 调用方持有宿主 CPU 组协调锁，直到恢复、清理与元数据保存全部结束。
// 该锁不能约束外部管理工具，因此每次使用组前仍检查身份与属性指纹。
internal static class CpuGroupAffinityTransaction
{
    internal static Task<CpuGroupAffinityResult> ApplyAsync(
        Guid vmId, IReadOnlyList<int> indices, CpuGroupAffinityOperations operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (vmId == Guid.Empty || indices is null)
            return Task.FromResult(new CpuGroupAffinityResult(
                Failure("虚拟机身份或处理器列表无效。", ApiErrorKind.InvalidArgument), null, null, null));

        int[] selection = indices.ToArray();
        if (selection.Any(index => index < 0))
            return Task.FromResult(new CpuGroupAffinityResult(
                Failure("逻辑处理器索引不能为负数。", ApiErrorKind.InvalidArgument), null, null, null));

        uint[] processors = selection.Select(index => (uint)index).Distinct().Order().ToArray();
        return new Transaction(vmId, processors, operations).RunAsync();
    }

    private sealed record GroupSnapshot(Guid Id, string Fingerprint);

    private sealed class Transaction(Guid vmId, uint[] processors, CpuGroupAffinityOperations operations)
    {
        private VmCpuGroupBinding? original;
        private GroupSnapshot? originalGroup;
        private GroupSnapshot? targetGroup;
        private Guid targetId;
        private Guid? createdId;
        private bool creationConfirmed;
        private bool writeAttempted;
        private bool outcomeUnknown;
        private bool configurationChanged;
        private Guid? currentId;

        internal async Task<CpuGroupAffinityResult> RunAsync()
        {
            try
            {
                return await RunCoreAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // 未预期的异常也不能触发盲目回滚；写入可能已经提交。
                var failure = ApiResponse.Fail(error.Message, error.HResult, ApiErrorSource.None, error,
                    writeAttempted ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.OperationFailed);
                return Result(failure);
            }
        }

        private async Task<CpuGroupAffinityResult> RunCoreAsync()
        {
            var binding = await ReadCurrentAsync().ConfigureAwait(false);
            if (!binding.HasData) return Result(CopyFailure(binding, "无法读取当前 CPU 组绑定。"));
            original = binding.Data;
            if (processors.Length == 0 && original.GroupId == Guid.Empty)
                return Success(Guid.Empty);

            if (processors.Length > 0)
            {
                var topology = await ReadAsync(operations.ReadTopology).ConfigureAwait(false);
                if (!topology.HasData) return Result(CopyFailure(topology, "HCS 未返回处理器拓扑。"));
                var ids = new HashSet<uint>();
                if (topology.Data.Any(processor => processor is null || !ids.Add(processor.LpIndex)))
                    return Result(Failure("HCS 处理器拓扑身份缺失或重复。", ApiErrorKind.InvalidState));
                if (processors.Any(index => !ids.Contains(index)))
                    return Result(Failure("所选处理器不在当前 HCS 宿主拓扑中。", ApiErrorKind.InvalidArgument));
            }

            var groups = await ReadGroupsAsync().ConfigureAwait(false);
            if (!groups.HasData) return Result(CopyFailure(groups, "无法读取宿主 CPU 分组。"));
            HcsCpuGroupDetail? existing = null;
            if (original.GroupId != Guid.Empty)
            {
                existing = groups.Data.SingleOrDefault(group => group.GroupId == original.GroupId);
                if (existing is null && processors.Length > 0)
                    return Result(Failure("当前虚拟机绑定的 CPU 组不存在。", ApiErrorKind.Conflict));
                // 明确解除允许清除已失效的组引用；缺失的原组不能作为恢复目标。
                originalGroup = existing is null ? null : Snapshot(existing);
            }

            if (processors.Length > 0)
            {
                var members = await ReadMembersAsync().ConfigureAwait(false);
                if (!members.HasData) return Result(CopyFailure(members, "无法确认 CPU 组成员。"));
                var member = members.Data.SingleOrDefault(item => item.VmId == vmId);
                if (member is null || !SameConfiguration(member, original) || member.GroupId != original.GroupId)
                    return Result(Failure("虚拟机配置或 CPU 组绑定在读取期间已改变。", ApiErrorKind.Conflict));
                // 只保留当前 VM 已独享的组，绝不按处理器集合复用别的组。
                if (existing is not null && SameProcessors(existing, processors)
                    && members.Data.Count(item => item.GroupId == existing.GroupId) == 1)
                {
                    var current = await ReadCurrentAsync().ConfigureAwait(false);
                    if (!current.HasData)
                        return Result(CopyFailure(current, "无法确认当前 CPU 组绑定。"));
                    if (current.Data.GroupId != original.GroupId)
                        return Result(Failure("CPU 组绑定已被其他操作改变。", ApiErrorKind.Conflict));
                    var unchangedGroup = await CheckGroupsAsync(originalGroup).ConfigureAwait(false);
                    if (!unchangedGroup.Success) return Result(unchangedGroup);
                    var exclusive = await CheckMembersAsync(existing.GroupId, current.Data).ConfigureAwait(false);
                    return exclusive.Success ? Success(original.GroupId) : Result(exclusive);
                }
                if (existing is not null)
                {
                    var policyCheck = ValidatePolicies(existing, processors.Length);
                    if (!policyCheck.Success) return Result(policyCheck);
                }
                createdId = Guid.NewGuid();
                targetId = createdId.Value;
                var creation = await WriteAsync(() => operations.CreateGroup(targetId, processors)).ConfigureAwait(false);
                if (!creation.Success)
                {
                    if (!outcomeUnknown) createdId = null;
                    return Result(creation);
                }
                creationConfirmed = true;
                var confirmed = await ReadGroupsAsync().ConfigureAwait(false);
                if (!confirmed.HasData)
                    return Result(CopyFailure(confirmed, "新 CPU 组已创建，但无法核对其实际属性。"));
                var created = confirmed.Data.SingleOrDefault(group => group.GroupId == targetId);
                if (created is null || !SameProcessors(created, processors) || !IsPlainFullCapacity(created))
                    return Result(Failure("新 CPU 组的身份或属性无法确认；已保留该组供检查。", ApiErrorKind.Conflict));
                targetGroup = Snapshot(created);
                if (existing is not null)
                {
                    var copied = await CopyPoliciesAsync(existing, created).ConfigureAwait(false);
                    if (!copied.Success) return await RecoverAsync(copied).ConfigureAwait(false);
                }
            }

            var unchanged = await CheckGroupsAsync(originalGroup, targetGroup).ConfigureAwait(false);
            if (!unchanged.Success) return await RecoverAsync(unchanged).ConfigureAwait(false);
            if (targetId != Guid.Empty)
            {
                var unbound = await CheckMembersAsync(targetId, null).ConfigureAwait(false);
                if (!unbound.Success) return await RecoverAsync(unbound).ConfigureAwait(false);
            }

            var expected = original;
            if (original.GroupId != Guid.Empty)
            {
                var detach = await WriteBindingAsync(expected, Guid.Empty).ConfigureAwait(false);
                if (!detach.Outcome.Success) return await RecoverAsync(detach.Outcome).ConfigureAwait(false);
                expected = detach.Binding!;
            }
            if (targetId == Guid.Empty) return Success(Guid.Empty);

            unchanged = await CheckGroupsAsync(originalGroup, targetGroup).ConfigureAwait(false);
            if (!unchanged.Success) return await RecoverAsync(unchanged).ConfigureAwait(false);
            var vacant = await CheckMembersAsync(targetId, null).ConfigureAwait(false);
            if (!vacant.Success) return await RecoverAsync(vacant).ConfigureAwait(false);
            var attach = await WriteBindingAsync(expected, targetId).ConfigureAwait(false);
            if (!attach.Outcome.Success) return await RecoverAsync(attach.Outcome).ConfigureAwait(false);
            unchanged = await CheckGroupsAsync(targetGroup).ConfigureAwait(false);
            if (unchanged.Success) unchanged = await CheckMembersAsync(targetId, attach.Binding!).ConfigureAwait(false);
            return unchanged.Success ? Success(targetId) : await RecoverAsync(unchanged).ConfigureAwait(false);
        }

        private async Task<ApiResponse> CopyPoliciesAsync(HcsCpuGroupDetail source, HcsCpuGroupDetail created)
        {
            var desired = source.GroupProperties!.OrderBy(item => item.PropertyCode).ToArray();
            if (!desired.Select(item => item.PropertyCode).SequenceEqual(
                created.GroupProperties!.Select(item => item.PropertyCode).Order()))
                return Failure("新旧 CPU 组的属性集合不同，不能保证保留原策略。", ApiErrorKind.Unsupported);
            var current = created;
            foreach (var policy in desired)
            {
                if (current.GroupProperties!.Single(item => item.PropertyCode == policy.PropertyCode).PropertyValue == policy.PropertyValue)
                    continue;
                var unchanged = await CheckGroupsAsync(originalGroup, targetGroup).ConfigureAwait(false);
                if (!unchanged.Success) return unchanged;
                var vacant = await CheckMembersAsync(targetId, null).ConfigureAwait(false);
                if (!vacant.Success) return vacant;
                var write = await WriteAsync(() => operations.SetProperty(targetId, policy.PropertyCode,
                    checked((uint)policy.PropertyValue))).ConfigureAwait(false);
                var readback = await ReadGroupsAsync().ConfigureAwait(false);
                var observed = readback.HasData ? readback.Data.SingleOrDefault(item => item.GroupId == targetId) : null;
                if (!write.Success)
                {
                    if (observed is null || Snapshot(observed) != targetGroup) outcomeUnknown = true;
                    return outcomeUnknown ? ApiResponse.Fail(write.Error + "；新组策略提交结果不确定，已保留供核对。",
                        write.Code, write.ErrorSource, write.Exception, ApiErrorKind.OutcomeUnknown) : write;
                }
                var expected = new HcsCpuGroupDetail
                {
                    GroupId = current.GroupId, HypervisorGroupId = current.HypervisorGroupId, Affinity = current.Affinity,
                    GroupProperties = current.GroupProperties!.Select(item => new HcsCpuGroupProperty
                    {
                        PropertyCode = item.PropertyCode,
                        PropertyValue = item.PropertyCode == policy.PropertyCode ? policy.PropertyValue : item.PropertyValue
                    }).ToArray()
                };
                if (observed is null || Snapshot(observed) != Snapshot(expected))
                {
                    outcomeUnknown = true;
                    return ApiResponse.Fail("新组策略已提交，但实际属性未能精确回读；未继续绑定或清理。" + readback.Error,
                        readback.Code, readback.ErrorSource, readback.Exception, ApiErrorKind.OutcomeUnknown);
                }
                current = observed;
                targetGroup = Snapshot(observed);
            }
            return ApiResponse.Ok();
        }

        private async Task<ApiResponse<List<VmCpuGroupBinding>>> ReadMembersAsync()
        {
            try
            {
                var result = await operations.ReadBindings().ConfigureAwait(false);
                if (result is null || !result.Complete)
                    return ApiResponse<List<VmCpuGroupBinding>>.Fail("无法完整读取 WMI 虚拟机的当前 CPU 组绑定：" + result?.Error,
                        kind: ApiErrorKind.InvalidState);
                var items = result.Items.Select(item => item.Binding).ToList();
                if (items.Any(item => item.VmId == Guid.Empty || string.IsNullOrWhiteSpace(item.ConfigurationId)
                        || string.IsNullOrWhiteSpace(item.ProcessorInstanceId)) || items.Select(item => item.VmId).Distinct().Count() != items.Count)
                    return ApiResponse<List<VmCpuGroupBinding>>.Fail("CPU 组成员身份缺失或重复。", kind: ApiErrorKind.InvalidState);
                return ApiResponse<List<VmCpuGroupBinding>>.Ok(items);
            }
            catch (Exception error)
            {
                return ApiResponse<List<VmCpuGroupBinding>>.Fail(error.Message, exception: error, kind: ApiErrorKind.OperationFailed);
            }
        }

        private async Task<ApiResponse> CheckMembersAsync(Guid groupId, VmCpuGroupBinding? expected)
        {
            var read = await ReadMembersAsync().ConfigureAwait(false);
            if (!read.HasData) return CopyFailure(read, "无法核对 CPU 组成员。");
            var members = read.Data.Where(item => item.GroupId == groupId).ToArray();
            bool matches = expected is null ? members.Length == 0 : members.Length == 1
                && members[0].VmId == expected.VmId && SameConfiguration(members[0], expected);
            return matches ? ApiResponse.Ok() : Failure("CPU 组已被其他虚拟机使用或成员配置改变，未继续操作。", ApiErrorKind.Conflict);
        }

        private async Task<CpuGroupAffinityResult> RecoverAsync(ApiResponse operation)
        {
            if (outcomeUnknown || configurationChanged || original is null)
                return Result(operation);

            var observed = await ReadCurrentAsync().ConfigureAwait(false);
            if (!observed.HasData)
                return Result(operation, CopyFailure(observed, "无法确认失败后的 CPU 组状态，未执行恢复。"));
            var current = observed.Data;
            if (current.GroupId == original.GroupId)
                return await CleanupCreatedAsync(operation, null).ConfigureAwait(false);
            if (current.GroupId != Guid.Empty && current.GroupId != targetId)
                return Result(operation, Failure("CPU 组绑定已被外部操作改为其他组，未覆盖该状态。", ApiErrorKind.Conflict));
            if (original.GroupId != Guid.Empty && originalGroup is null)
                return Result(operation, Failure("原 CPU 组已不存在，不能恢复失效的组绑定；当前绑定已保留。",
                    ApiErrorKind.Conflict));

            var unchanged = await CheckGroupsAsync(originalGroup,
                current.GroupId == targetId ? targetGroup : null).ConfigureAwait(false);
            if (!unchanged.Success) return Result(operation, unchanged);

            if (current.GroupId != Guid.Empty)
            {
                var detach = await WriteBindingAsync(current, Guid.Empty).ConfigureAwait(false);
                if (!detach.Outcome.Success)
                    return await FinishRecoveryFailureAsync(operation, detach.Outcome).ConfigureAwait(false);
                current = detach.Binding!;
            }
            if (original.GroupId != Guid.Empty)
            {
                // 恢复前重新确认原组，而不是按旧 GUID 绑定可能已被替换的资源。
                unchanged = await CheckGroupsAsync(originalGroup).ConfigureAwait(false);
                if (!unchanged.Success) return Result(operation, unchanged);
                var restore = await WriteBindingAsync(current, original.GroupId).ConfigureAwait(false);
                if (!restore.Outcome.Success)
                    return await FinishRecoveryFailureAsync(operation, restore.Outcome).ConfigureAwait(false);
            }
            return await CleanupCreatedAsync(operation, ApiResponse.Ok()).ConfigureAwait(false);
        }

        private async Task<CpuGroupAffinityResult> FinishRecoveryFailureAsync(ApiResponse operation, ApiResponse recovery)
        {
            if (outcomeUnknown || configurationChanged) return Result(operation, recovery);
            var observed = await ReadCurrentAsync().ConfigureAwait(false);
            if (observed.HasData && observed.Data.GroupId == original!.GroupId)
                return await CleanupCreatedAsync(operation, recovery).ConfigureAwait(false);
            if (!observed.HasData)
                recovery = Combine(recovery, CopyFailure(observed, "恢复失败后无法确认当前绑定。"));
            return Result(operation, recovery);
        }

        private async Task<CpuGroupAffinityResult> CleanupCreatedAsync(ApiResponse operation, ApiResponse? recovery)
        {
            if (!creationConfirmed || createdId is null || targetGroup is null)
                return Result(operation, recovery);
            if (outcomeUnknown || configurationChanged || currentId != original!.GroupId)
                return Result(operation, recovery);

            var groups = await ReadGroupsAsync().ConfigureAwait(false);
            if (!groups.HasData)
                return Result(operation, Combine(recovery, CopyFailure(groups, "无法确认本次新组，未执行清理。")));
            if (originalGroup is not null)
            {
                var prior = groups.Data.SingleOrDefault(item => item.GroupId == originalGroup.Id);
                if (prior is null || Snapshot(prior) != originalGroup)
                    return Result(operation, Combine(recovery,
                        Failure("原 CPU 组的身份或策略已改变，未确认恢复原状态；本次新组已保留。",
                            ApiErrorKind.Conflict)));
            }
            var group = groups.Data.SingleOrDefault(item => item.GroupId == createdId.Value);
            if (group is null)
            {
                createdId = null;
                creationConfirmed = false;
                return Result(operation, recovery ?? ApiResponse.Ok());
            }
            if (Snapshot(group) != targetGroup)
                return Result(operation, Combine(recovery,
                    Failure("本次新 CPU 组的身份或策略已改变，未删除该组。", ApiErrorKind.Conflict)));

            var vacant = await CheckMembersAsync(createdId.Value, null).ConfigureAwait(false);
            if (!vacant.Success) return Result(operation, Combine(recovery, vacant));

            // HCS 还会拒绝其他使用者占用的组；不强制解绑任何其他对象。
            var cleanup = await WriteAsync(() => operations.DeleteGroup(createdId.Value)).ConfigureAwait(false);
            if (!cleanup.Success) return Result(operation, Combine(recovery, cleanup));
            createdId = null;
            creationConfirmed = false;
            return Result(operation, recovery ?? ApiResponse.Ok());
        }

        private async Task<(ApiResponse Outcome, VmCpuGroupBinding? Binding)> WriteBindingAsync(
            VmCpuGroupBinding expected, Guid groupId)
        {
            var write = await WriteAsync(() => operations.SetBinding(expected, groupId)).ConfigureAwait(false);
            if (!write.Success)
            {
                if (outcomeUnknown) await ReadCurrentAsync().ConfigureAwait(false);
                return (write, null);
            }
            var observed = await ReadCurrentAsync().ConfigureAwait(false);
            if (!observed.HasData)
            {
                if (configurationChanged)
                    return (CopyFailure(observed, "虚拟机配置已改变。"), null);
                outcomeUnknown = true;
                return (ApiResponse.Fail("CPU 组写入已返回成功，但无法确认实际绑定。" + observed.Error,
                    observed.Code, observed.ErrorSource, observed.Exception, ApiErrorKind.OutcomeUnknown), null);
            }
            if (observed.Data.GroupId != groupId)
                return (Failure("CPU 组写入后的实际绑定与目标不一致。", ApiErrorKind.Conflict), observed.Data);
            return (ApiResponse.Ok(), observed.Data);
        }

        private async Task<ApiResponse<VmCpuGroupBinding>> ReadCurrentAsync()
        {
            var response = await ReadAsync(() => operations.ReadBinding(vmId)).ConfigureAwait(false);
            if (!response.HasData)
            {
                currentId = null;
                return response.Success
                    ? ApiResponse<VmCpuGroupBinding>.Fail("未返回当前 CPU 组绑定。", kind: ApiErrorKind.NotFound)
                    : response;
            }
            var binding = response.Data;
            if (binding.VmId != vmId || string.IsNullOrWhiteSpace(binding.ConfigurationId)
                || string.IsNullOrWhiteSpace(binding.ProcessorInstanceId)
                || original is not null && !SameConfiguration(original, binding))
            {
                configurationChanged = true;
                currentId = null;
                return ApiResponse<VmCpuGroupBinding>.Fail("虚拟机当前配置或处理器身份已改变，未继续修改。",
                    kind: ApiErrorKind.Conflict);
            }
            currentId = binding.GroupId;
            return response;
        }

        private async Task<ApiResponse<List<HcsCpuGroupDetail>>> ReadGroupsAsync()
        {
            var response = await ReadAsync(operations.ReadGroups).ConfigureAwait(false);
            if (!response.HasData) return response;
            var ids = new HashSet<Guid>();
            if (response.Data.Any(group => group is null || group.GroupId == Guid.Empty || !ids.Add(group.GroupId)))
                return ApiResponse<List<HcsCpuGroupDetail>>.Fail("HCS CPU 分组身份缺失或重复。",
                    kind: ApiErrorKind.InvalidState);
            return response;
        }

        private async Task<ApiResponse> CheckGroupsAsync(params GroupSnapshot?[] snapshots)
        {
            var required = snapshots.Where(snapshot => snapshot is not null).Cast<GroupSnapshot>().Distinct().ToArray();
            if (required.Length == 0) return ApiResponse.Ok();
            var groups = await ReadGroupsAsync().ConfigureAwait(false);
            if (!groups.HasData) return CopyFailure(groups, "无法重新核对 CPU 分组。");
            foreach (var expected in required)
            {
                var actual = groups.Data.SingleOrDefault(group => group.GroupId == expected.Id);
                if (actual is null || Snapshot(actual) != expected)
                    return Failure("CPU 分组的身份或策略已被其他操作改变。", ApiErrorKind.Conflict);
            }
            return ApiResponse.Ok();
        }

        private async Task<ApiResponse> WriteAsync(Func<Task<ApiResponse>> write)
        {
            writeAttempted = true;
            ApiResponse response;
            try
            {
                response = await write().ConfigureAwait(false)
                    ?? Failure("写入没有返回结果，无法确认是否已经提交。", ApiErrorKind.OutcomeUnknown);
            }
            catch (Exception error)
            {
                response = ApiResponse.Fail(error.Message, error.HResult, ApiErrorSource.None, error,
                    ApiErrorKind.OutcomeUnknown);
            }
            if (response.ErrorKind == ApiErrorKind.OutcomeUnknown) outcomeUnknown = true;
            return response;
        }

        private CpuGroupAffinityResult Success(Guid groupId) => new(ApiResponse.Ok(), null, groupId, null);

        private CpuGroupAffinityResult Result(ApiResponse operation, ApiResponse? recovery = null)
            => new(operation, recovery, currentId, createdId);
    }

    private static bool SameConfiguration(VmCpuGroupBinding first, VmCpuGroupBinding second)
        => first.VmId == second.VmId
        && string.Equals(first.ConfigurationId, second.ConfigurationId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.ProcessorInstanceId, second.ProcessorInstanceId, StringComparison.OrdinalIgnoreCase);

    private static bool SameProcessors(HcsCpuGroupDetail group, uint[] processors)
        => group.Affinity?.LogicalProcessors is { } actual && actual.Order().SequenceEqual(processors);

    private static bool IsPlainFullCapacity(HcsCpuGroupDetail group)
    {
        if (group.GroupProperties is not { Length: > 0 } policies) return false;
        var codes = new HashSet<uint>();
        bool fullCapacity = false;
        foreach (var policy in policies)
        {
            if (policy is null || !codes.Add(policy.PropertyCode)) return false;
            // HCS 可同时返回默认调度策略，不能要求列表中只有 CPU 配额。
            // 0x40000 为相对调度权重；100 是新建默认组的已验证值，不接受未知属性。
            bool isDefault = policy.PropertyCode switch
            {
                0x00010000 => fullCapacity = policy.PropertyValue == 65536,
                0x00020000 => policy.PropertyValue == 1,
                0x00030000 => policy.PropertyValue == 0,
                0x00040000 => policy.PropertyValue == 100,
                _ => false
            };
            if (!isDefault) return false;
        }
        return fullCapacity;
    }

    private static ApiResponse ValidatePolicies(HcsCpuGroupDetail group, int processorCount)
    {
        if (group.GroupProperties is not { Length: > 0 } policies)
            return Failure("原 CPU 组未报告策略，无法保证拆分后保留设置。", ApiErrorKind.Unsupported);
        var codes = new HashSet<uint>();
        foreach (var policy in policies)
        {
            if (policy is null || !codes.Add(policy.PropertyCode))
                return Failure("原 CPU 组属性无效或重复。", ApiErrorKind.InvalidState);
            bool supported = policy.PropertyCode switch
            {
                0x10000 => policy.PropertyValue <= 65536,
                0x20000 => policy.PropertyValue <= 7,
                0x30000 => policy.PropertyValue <= 255,
                0x40000 => policy.PropertyValue is >= 1 and <= 65535,
                _ => false
            };
            if (!supported) return Failure("原 CPU 组包含无法复制的属性或取值，未修改绑定。", ApiErrorKind.Unsupported);
            if (policy.PropertyCode == 0x30000 && policy.PropertyValue > (ulong)processorCount)
                return Failure("新处理器数量小于原空闲处理器保留数；请先明确调整保留数。", ApiErrorKind.InvalidArgument);
        }
        return codes.Contains(0x10000) ? ApiResponse.Ok()
            : Failure("原 CPU 组未报告配额，无法保证保留原策略。", ApiErrorKind.Unsupported);
    }

    private static GroupSnapshot Snapshot(HcsCpuGroupDetail group)
        => new(group.GroupId, JsonSerializer.Serialize(new
        {
            group.GroupId,
            group.HypervisorGroupId,
            Affinity = group.Affinity is null ? null : new
            {
                group.Affinity.LogicalProcessorCount,
                LogicalProcessors = group.Affinity.LogicalProcessors?.Order().ToArray()
            },
            GroupProperties = group.GroupProperties?.OrderBy(property => property.PropertyCode)
                .Select(property => new { property.PropertyCode, property.PropertyValue }).ToArray()
        }));

    private static async Task<ApiResponse<T>> ReadAsync<T>(Func<Task<ApiResponse<T>>> read)
    {
        try
        {
            return await read().ConfigureAwait(false)
                ?? ApiResponse<T>.Fail("查询没有返回结果。", kind: ApiErrorKind.OperationFailed);
        }
        catch (Exception error)
        {
            return ApiResponse<T>.Fail(error.Message, error.HResult, ApiErrorSource.None, error,
                ApiErrorKind.OperationFailed);
        }
    }

    private static ApiResponse CopyFailure<T>(ApiResponse<T> response, string emptyMessage)
        => !response.Success
            ? ApiResponse.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind)
            : Failure(emptyMessage, ApiErrorKind.InvalidState);

    private static ApiResponse Failure(string message, ApiErrorKind kind)
        => ApiResponse.Fail(message, kind: kind);

    private static ApiResponse Combine(ApiResponse? prior, ApiResponse next)
    {
        if (prior is null || prior.Success) return next;
        if (next.Success) return prior;
        return ApiResponse.Fail(
            $"{prior.Error} [{prior.ErrorSource}:{prior.Code}]；{next.Error} [{next.ErrorSource}:{next.Code}]",
            prior.Code, prior.ErrorSource,
            new AggregateException(prior.Exception ?? new InvalidOperationException(prior.Error),
                next.Exception ?? new InvalidOperationException(next.Error)),
            prior.ErrorKind == ApiErrorKind.OutcomeUnknown || next.ErrorKind == ApiErrorKind.OutcomeUnknown
                ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.CleanupRequired);
    }
}
