using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>查询宿主 CPU 组，修改配额、优先级、空闲处理器保留数量和相对权重。</summary>
/// <remarks>
/// <para>修改适用于 Classic/Core 调度器。先读取组快照，将非空 Revision 原样提交；配置或成员已经变化时返回 Conflict。修改影响该组全部使用者，成功后返回新的快照与 Revision。</para>
/// <para>Core 调用之间按宿主排队。成员列表仅覆盖 WMI 可见虚拟机。原生提交后等待结果与回读；无法确认结果时返回 OutcomeUnknown，重新查询后再决定后续操作。</para>
/// </remarks>
public static class HostCpuGroupService
{
    private const string Resource = "CpuAffinity:HcsGroups";
    private enum Property : uint { CpuCap = 0x10000, Priority = 0x20000, IdleReserve = 0x30000, RelativeWeight = 0x40000 }
    private sealed record Snapshot(HcsCpuGroupDetail Raw, HostCpuGroupBindings Bindings, HostCpuGroupInfo Info);

    /// <summary>读取全部宿主 CPU 组及其当前成员；没有 CPU 组时返回成功空列表。</summary>
    /// <param name="cancellationToken">可取消排队及读取步骤；已进行的同步原生查询不会被强行中断。</param>
    /// <returns>组的只读快照；逐组检查 MembersComplete 和 Revision 后才可提交修改。</returns>
    public static async Task<ApiResponse<IReadOnlyList<HostCpuGroupInfo>>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await HostResourceLock.RunAsync(Resource, async () =>
            {
                var snapshots = await CaptureAsync(cancellationToken).ConfigureAwait(false);
                return ApiResponse<IReadOnlyList<HostCpuGroupInfo>>.Ok(Array.AsReadOnly(snapshots.Select(item => item.Info).ToArray()));
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { return FromException<IReadOnlyList<HostCpuGroupInfo>>(error, false); }
    }

    /// <summary>读取指定宿主 CPU 组的策略、成员及修改所需的版本摘要。</summary>
    /// <param name="groupId">宿主 CPU 组 GUID，不能为全零。</param>
    /// <param name="cancellationToken">可取消排队及读取步骤，不强行中断同步原生查询。</param>
    /// <returns>指定组快照；组不存在时返回 NotFound，不创建新组。</returns>
    /// <remarks>组 GUID 可取自亲和性保存结果的 CurrentGroupId，或用 <see cref="HostCpuGroupService.GetAllAsync"/> 按 Members 中的 VmId 查找。读取后将非空 Revision 原样传给本组策略修改方法；每次成功修改返回的新快照包含新的 Revision。</remarks>
    public static async Task<ApiResponse<HostCpuGroupInfo>> GetAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        if (groupId == Guid.Empty) return Invalid("CPU 组 GUID 不能为空。");
        try
        {
            return await HostResourceLock.RunAsync(Resource, async () =>
                ApiResponse<HostCpuGroupInfo>.Ok(Find(await CaptureAsync(cancellationToken).ConfigureAwait(false), groupId).Info),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) { return FromException<HostCpuGroupInfo>(error, false); }
    }

    /// <summary>修改 CPU 组的总配额，并确认实际回读值；影响该组全部成员。</summary>
    /// <param name="groupId">待修改的宿主 CPU 组 GUID。</param>
    /// <param name="cpuCap">组总配额，0 至 65536；65536 为满额，32768 为 50%。</param>
    /// <param name="expectedRevision">GetAsync 或 GetAllAsync 返回的非空 Revision；过期时在任何写入前返回 Conflict。</param>
    /// <param name="cancellationToken">在原生提交前响应取消；提交后等待操作结束并回读。</param>
    /// <returns>修改后的快照；OutcomeUnknown 表示结果待确认，重试前重新读取。</returns>
    public static Task<ApiResponse<HostCpuGroupInfo>> SetCpuCapAsync(Guid groupId, uint cpuCap, string expectedRevision, CancellationToken cancellationToken = default)
        => SetAsync(groupId, Property.CpuCap, cpuCap, expectedRevision, cancellationToken);

    /// <summary>修改 CPU 组的调度优先级，影响该组全部成员。</summary>
    /// <param name="groupId">待修改的宿主 CPU 组 GUID。</param>
    /// <param name="priority">调度优先级，0 至 7；数值越大，优先级越高。</param>
    /// <param name="expectedRevision">最近读取的非空 Revision；过期时不执行修改。</param>
    /// <param name="cancellationToken">仅在原生提交前取消；提交后完成返回和回读。</param>
    /// <returns>修改后经确认的组快照；失败后先重新读取实际状态。</returns>
    public static Task<ApiResponse<HostCpuGroupInfo>> SetPriorityAsync(Guid groupId, uint priority, string expectedRevision, CancellationToken cancellationToken = default)
        => SetAsync(groupId, Property.Priority, priority, expectedRevision, cancellationToken);

    /// <summary>修改 CPU 组在空闲时保留的逻辑处理器数量。</summary>
    /// <param name="groupId">待修改的宿主 CPU 组 GUID。</param>
    /// <param name="count">Core 支持 0 至 min(255,组逻辑处理器数)；具体 Core 调度器、SMT 和拓扑条件仍由 HCS 校验。</param>
    /// <param name="expectedRevision">最近读取的非空 Revision；过期时不执行修改。</param>
    /// <param name="cancellationToken">仅在原生提交前取消；提交后完成返回和回读。</param>
    /// <returns>修改后经确认的组快照；保留系统原始失败，不猜测或自动调整数量。</returns>
    public static Task<ApiResponse<HostCpuGroupInfo>> SetIdleProcessorReserveAsync(Guid groupId, uint count, string expectedRevision, CancellationToken cancellationToken = default)
        => SetAsync(groupId, Property.IdleReserve, count, expectedRevision, cancellationToken);

    /// <summary>修改 CPU 组竞争处理器时间时的相对权重。</summary>
    /// <remarks>RelativeWeight 对应 HCS 属性 0x40000，其名称与语义尚未纳入微软公开接口约定。</remarks>
    /// <param name="groupId">待修改的宿主 CPU 组 GUID。</param>
    /// <param name="weight">相对权重原始值，1 至 65535。</param>
    /// <param name="expectedRevision">最近读取的非空 Revision；过期时不执行修改。</param>
    /// <param name="cancellationToken">仅在原生提交前取消；提交后完成返回和回读。</param>
    /// <returns>修改后经确认的组快照；系统未提供该属性时返回 Unsupported。</returns>
    public static Task<ApiResponse<HostCpuGroupInfo>> SetRelativeWeightAsync(Guid groupId, uint weight, string expectedRevision, CancellationToken cancellationToken = default)
        => SetAsync(groupId, Property.RelativeWeight, weight, expectedRevision, cancellationToken);

    private static async Task<ApiResponse<HostCpuGroupInfo>> SetAsync(Guid groupId, Property property, uint value, string revision, CancellationToken token)
    {
        if (groupId == Guid.Empty || string.IsNullOrWhiteSpace(revision)) return Invalid("CPU 组 GUID 和读取版本不能为空。");
        if (property == Property.CpuCap && value > 65536 || property == Property.Priority && value > 7
            || property == Property.IdleReserve && value > 255 || property == Property.RelativeWeight && (value == 0 || value > 65535))
            return Invalid("CPU 组属性值超出此入口支持的范围。");
        bool submitted = false;
        try
        {
            return await HostResourceLock.RunAsync(Resource, async () =>
            {
                token.ThrowIfCancellationRequested();
                if (HyperVSchedulerService.GetSchedulerType() is not (HyperVSchedulerType.Classic or HyperVSchedulerType.Core))
                    return ApiResponse<HostCpuGroupInfo>.Fail("当前调度器不支持修改 HCS CPU 组策略。", kind: ApiErrorKind.Unsupported);
                var current = Find(await CaptureAsync(token).ConfigureAwait(false), groupId);
                RequireWritable(current, revision, property, value);
                // 在准备提交时重新核对完整组属性和成员，检测不遵循 Core 锁的外部改动。
                var confirmed = Find(await CaptureAsync(token).ConfigureAwait(false), groupId);
                RequireWritable(confirmed, revision, property, value);
                if (ReadValue(confirmed.Raw, property) == value) return ApiResponse<HostCpuGroupInfo>.Ok(confirmed.Info);
                token.ThrowIfCancellationRequested();
                submitted = true;
                ApiResponse result = await (property switch
                {
                    Property.CpuCap => HcsApi.SetCpuGroupCapAsync(groupId, value),
                    Property.Priority => HcsApi.SetCpuGroupPriorityAsync(groupId, value),
                    Property.IdleReserve => HcsApi.SetCpuGroupIdleProcessorReserveAsync(groupId, value),
                    Property.RelativeWeight => HcsApi.SetCpuGroupRelativeWeightAsync(groupId, value),
                    _ => throw new InvalidOperationException("未知 CPU 组属性。")
                }).ConfigureAwait(false);
                // 成功和失败都读取提交后的状态；原生返回失败不证明配置一定没有变化。
                // 提交后的取消不会跳过回读，也不会提前释放资源锁。
                Snapshot? after = null;
                Exception? readbackError = null;
                try { after = Find(await CaptureAsync(CancellationToken.None).ConfigureAwait(false), groupId); }
                catch (Exception error) { readbackError = error; }
                if (!result.Success)
                {
                    bool unchanged = after?.Info.MembersComplete == true
                        && string.Equals(confirmed.Info.Revision, after.Info.Revision, StringComparison.Ordinal);
                    bool unknown = result.ErrorKind == ApiErrorKind.OutcomeUnknown || !unchanged;
                    string error = result.Error + (unknown
                        ? "；提交后的 CPU 组状态未能确认保持原值，未自动回滚或重放。"
                        : "；已回读确认 CPU 组属性与已知成员保持原值。");
                    if (readbackError is not null) error += " 回读失败：" + readbackError.Message;
                    Exception? cause = readbackError is null ? result.Exception : result.Exception is null
                        ? readbackError : new AggregateException(result.Exception, readbackError);
                    return ApiResponse<HostCpuGroupInfo>.Fail(error, result.Code, result.ErrorSource, cause,
                        unknown ? ApiErrorKind.OutcomeUnknown : result.ErrorKind);
                }
                if (after is null)
                {
                    var failure = FromException<HostCpuGroupInfo>(readbackError
                        ?? new InvalidOperationException("未取得 CPU 组回读结果。"), submitted: true);
                    return ApiResponse<HostCpuGroupInfo>.Fail("系统已接受修改，但无法回读实际 CPU 组状态；未自动回滚或重放。" + failure.Error,
                        failure.Code, failure.ErrorSource, failure.Exception, failure.ErrorKind);
                }
                if (!after.Info.MembersComplete || after.Info.Revision is null
                    || ReadValue(after.Raw, property) != value
                    || Fingerprint(confirmed.Raw, confirmed.Bindings, property) != Fingerprint(after.Raw, after.Bindings, property))
                    return ApiResponse<HostCpuGroupInfo>.Fail("系统已接受修改，但回读的属性、组身份或成员与预期不一致；未自动回滚或重放。",
                        kind: ApiErrorKind.OutcomeUnknown);
                return ApiResponse<HostCpuGroupInfo>.Ok(after.Info);
            }, token).ConfigureAwait(false);
        }
        catch (Exception error) { return FromException<HostCpuGroupInfo>(error, submitted); }
    }

    private static void RequireWritable(Snapshot snapshot, string revision, Property property, uint value)
    {
        if (!snapshot.Info.MembersComplete)
            throw Failure("CPU 组成员清单不完整，未修改组策略：" + snapshot.Info.MembersError, ApiErrorKind.InvalidState);
        if (snapshot.Info.Revision is null) throw Failure("系统未提供完整 CPU 组实例身份或属性，不能确认修改目标。", ApiErrorKind.Unsupported);
        if (!string.Equals(snapshot.Info.Revision, revision, StringComparison.Ordinal))
            throw Failure("CPU 组属性、实例或成员已改变，请重新读取后确认。", ApiErrorKind.Conflict);
        if (ReadValue(snapshot.Raw, property) is null) throw Failure("系统未提供待修改的 CPU 组属性。", ApiErrorKind.Unsupported);
        if (property == Property.IdleReserve && value > snapshot.Raw.Affinity!.LogicalProcessors!.Length)
            throw Failure("保留数量不能超过该 CPU 组的逻辑处理器数量。", ApiErrorKind.InvalidArgument);
    }

    private static async Task<List<Snapshot>> CaptureAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var before = Require(await HcsApi.GetAllCpuGroupsAsync().ConfigureAwait(false));
        var bindings = await HostCpuGroupMemberReader.ReadAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var after = Require(await HcsApi.GetAllCpuGroupsAsync().ConfigureAwait(false));
        if (!before.OrderBy(group => group.GroupId).Select(group => Fingerprint(group, null))
            .SequenceEqual(after.OrderBy(group => group.GroupId).Select(group => Fingerprint(group, null))))
            throw Failure("读取期间宿主 CPU 组清单或属性发生变化，请重新读取。", ApiErrorKind.Conflict);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return after.OrderBy(group => group.GroupId).Select(group =>
        {
            var members = bindings.Items.Where(item => item.Binding.GroupId == group.GroupId).OrderBy(item => item.Binding.VmId).ToArray();
            bool identifiable = group.HypervisorGroupId.HasValue && group.Affinity?.LogicalProcessors is not null && group.GroupProperties is not null;
            var info = new HostCpuGroupInfo(group.GroupId, group.Affinity?.LogicalProcessors?.Order().ToArray(),
                ReadValue(group, Property.CpuCap), ReadValue(group, Property.Priority), ReadValue(group, Property.IdleReserve), ReadValue(group, Property.RelativeWeight),
                members.Select(item => item.Member).ToArray(), bindings.Complete, bindings.Error,
                bindings.Complete && identifiable ? Fingerprint(group, bindings) : null, now);
            return new Snapshot(group, bindings, info);
        }).ToList();
    }

    private static string Fingerprint(HcsCpuGroupDetail group, HostCpuGroupBindings? bindings, Property? ignored = null)
    {
        string json = JsonSerializer.Serialize(new
        {
            group.GroupId, group.HypervisorGroupId,
            Count = group.Affinity?.LogicalProcessorCount,
            Processors = group.Affinity?.LogicalProcessors?.Order().ToArray(),
            Policies = group.GroupProperties?.Where(item => item.PropertyCode != (uint?)ignored).OrderBy(item => item.PropertyCode)
                .Select(item => new { item.PropertyCode, item.PropertyValue }).ToArray(),
            Members = bindings?.Items.Where(item => item.Binding.GroupId == group.GroupId).OrderBy(item => item.Binding.VmId)
                .Select(item => new { item.Binding.VmId, item.Binding.ConfigurationId, item.Binding.ProcessorInstanceId }).ToArray()
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
    private static uint? ReadValue(HcsCpuGroupDetail group, Property property)
    {
        ulong? value = group.GroupProperties?.SingleOrDefault(item => item.PropertyCode == (uint)property)?.PropertyValue;
        return value is null ? null : checked((uint)value.Value);
    }
    private static Snapshot Find(List<Snapshot> snapshots, Guid id) => snapshots.SingleOrDefault(item => item.Raw.GroupId == id)
        ?? throw Failure("指定 CPU 组不存在。", ApiErrorKind.NotFound);
    private static T Require<T>(ApiResponse<T> result) => result.HasData ? result.Data
        : throw new CoreOperationException(ApiResponse.Fail(result.Error, result.Code, result.ErrorSource, result.Exception, result.ErrorKind));
    private static CoreOperationException Failure(string message, ApiErrorKind kind) => new(ApiResponse.Fail(message, kind: kind));
    private static ApiResponse<HostCpuGroupInfo> Invalid(string message) => ApiResponse<HostCpuGroupInfo>.Fail(message, kind: ApiErrorKind.InvalidArgument);
    private static ApiResponse<T> FromException<T>(Exception error, bool submitted)
    {
        if (error is CoreOperationException core)
            return ApiResponse<T>.Fail(core.Result.Error, core.Result.Code, core.Result.ErrorSource, error,
                submitted ? ApiErrorKind.OutcomeUnknown : core.Result.ErrorKind);
        return ApiResponse<T>.Fail(error.Message, error.HResult, ApiErrorSource.None, error,
            submitted ? ApiErrorKind.OutcomeUnknown : error is OperationCanceledException ? ApiErrorKind.Cancelled
            : error is AbandonedMutexException ? ApiErrorKind.Conflict
            : error is UnauthorizedAccessException ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed);
    }
}
