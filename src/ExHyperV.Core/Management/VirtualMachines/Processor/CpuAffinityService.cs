using ExHyperV.Tools;
using ExHyperV.Models;
using System.Text.Json;

namespace ExHyperV.Services
{
    /// <summary>读取、应用和保存虚拟机 CPU 亲和性，按宿主调度器选择 CPU 组或进程接口。</summary>
    public static class CpuAffinityService
    {
        private const string HcsResource = "CpuAffinity:HcsGroups";
        private static readonly CpuGroupAffinityOperations HcsOperations = new(
            VmCpuGroupBindingStore.ReadAsync, VmCpuGroupBindingStore.SetAsync,
            HcsApi.GetAllCpuGroupsAsync, HcsApi.GetProcessorTopologyAsync,
            HcsApi.CreateCpuGroupAsync, HcsApi.DeleteCpuGroupAsync,
            () => HostCpuGroupMemberReader.ReadAsync(CancellationToken.None),
            (id, code, value) => code switch
            {
                0x10000 => HcsApi.SetCpuGroupCapAsync(id, value),
                0x20000 => HcsApi.SetCpuGroupPriorityAsync(id, value),
                0x30000 => HcsApi.SetCpuGroupIdleProcessorReserveAsync(id, value),
                0x40000 => HcsApi.SetCpuGroupRelativeWeightAsync(id, value),
                _ => Task.FromResult(ApiResponse.Fail("不支持复制此 CPU 组策略。", kind: ApiErrorKind.Unsupported))
            });
        private static readonly RootAffinityWorkflowOperations RootOperations = new(
            () => Task.Run(ProcessAffinityService.GetTopology), ReadVmStateAsync, ReadPreferenceAsync,
            (id, value) => VmMetadataService.SetAsync(id, VmMetadataService.CpuAffinityKey, value),
            id => Task.Run(() => ProcessAffinityService.Read(id)),
            (id, indices) => Task.Run(() => ProcessAffinityService.Apply(id, indices)),
            (delay, token) => Task.Delay(delay, token), ReadEnvironment: AffinityEnvironmentReader.ReadRootAsync);
        private static readonly RootAffinityAttemptCache RootAttempts = new();
        private static readonly RootAffinityWorkflowOperations TrackedRootOperations = RootOperations with
        {
            ApplyProcess = (id, indices) => RootAttempts.ApplyAsync(id, indices, RootOperations),
            BeforeRestore = id => RootAttempts.ShouldRestoreAsync(id, RootOperations)
        };


        /// <summary>分别表示已确认的亲和性和保存的启动偏好，不把保存成功当成运行时已生效。</summary>
        /// <param name="State">本次读取的虚拟机状态。</param>
        /// <param name="AppliedProcessors">实际处理器编号；Root 关机或保存状态为 null，Classic/Core 未绑定为非 null 空集合。</param>
        /// <param name="SavedProcessors">保存的处理器编号，包括旧环境或其他后端的选择；使用前检查 SavedPreferenceStatus。未保存或无法解析时为 null，空集合表示不限制。</param>
        public sealed record AffinityState(VmState State, IReadOnlyList<int>? AppliedProcessors,
            IReadOnlyList<int>? SavedProcessors)
        {
            internal AffinityState(VmState state, IReadOnlyList<int>? appliedProcessors,
                IReadOnlyList<int>? savedProcessors, int? processId, long? processCreationTime)
                : this(state, appliedProcessors, savedProcessors)
            {
                ProcessId = processId;
                ProcessCreationTime = processCreationTime;
            }

            internal int? ProcessId { get; init; }
            internal long? ProcessCreationTime { get; init; }
            /// <summary>保存的处理器选择与当前环境的匹配状态。</summary>
            public PreferenceStatus SavedPreferenceStatus { get; init; }
            /// <summary>需要重新选择或无法核实的原因，无需说明时为 null。</summary>
            public string? SavedPreferenceIssue { get; init; }
        }

        /// <summary>保存的处理器选择是否适用于当前宿主、后端和编号映射。</summary>
        public enum PreferenceStatus
        {
            /// <summary>没有保存亲和性选择。</summary>
            None,
            /// <summary>保存了空选择，表示不要求限制处理器；无需环境匹配。</summary>
            Unrestricted,
            /// <summary>宿主、后端和处理器拓扑摘要一致；实际生效状态另查 AppliedProcessors。</summary>
            MatchesEnvironment,
            /// <summary>宿主、后端或编号映射不匹配；保留原选择，重新选择并保存后才能复用。</summary>
            EnvironmentChanged,
            /// <summary>旧记录缺少环境信息、格式版本尚不支持或当前信息无法读取；未自动应用。</summary>
            EnvironmentUnverified,
            /// <summary>保存内容无法解析为处理器选择；原始元数据保留。</summary>
            InvalidConfiguration
        }

        /// <summary>保存亲和性请求后的应用状态；元数据持久化另由 MetadataSaved 表示。</summary>
        public enum SaveStatus {
            /// <summary>已向运行时或 CPU 组应用所选处理器。</summary>
            Applied,
            /// <summary>当前不能立即应用，选择供下次启动后恢复。</summary>
            Queued,
            /// <summary>亲和性操作失败。</summary>
            Failed }
        /// <summary>亲和性应用状态与 HostOnlyItems 元数据保存结果。</summary>
        /// <param name="Status">亲和性已应用、等待应用或失败的状态。</param>
        /// <param name="MetadataSaved">处理器选择是否已保存到虚拟机配置。</param>
        public sealed record SaveResult(SaveStatus Status, bool MetadataSaved)
        {
            /// <summary>应用或元数据保存的失败说明；Status=Applied 时失败仅表示后续保存未成功。</summary>
            public string? Error { get; init; }
            /// <summary>原始失败类别；OutcomeUnknown 时不得直接重试或反向写入。</summary>
            public ApiErrorKind ErrorKind { get; init; }
            /// <summary>原始错误码，结合 ErrorSource 解释。</summary>
            public int ErrorCode { get; init; }
            /// <summary>原始错误码的来源。</summary>
            public ApiErrorSource ErrorSource { get; init; }
            /// <summary>恢复原绑定或清理本次新组的失败说明，不覆盖原始错误。</summary>
            public string? CleanupError { get; init; }
            /// <summary>恢复或清理失败的类别；未发生此类失败时为 None。</summary>
            public ApiErrorKind CleanupErrorKind { get; init; }
            /// <summary>Classic/Core 下本次最后确认的绑定；全零为未绑定，null 为未确认或 Root 路径。</summary>
            public Guid? CurrentGroupId { get; init; }
            /// <summary>失败后保留或无法确认删除的本次新建组；既有组从不由本操作删除。</summary>
            public Guid? RetainedGroupId { get; init; }
            /// <summary>本次保存的处理器选择与当前环境是否匹配；保存失败时为 EnvironmentUnverified。</summary>
            public PreferenceStatus SavedPreferenceStatus { get; init; }
            /// <summary>环境无法核实或偏好未确认保存的说明；空选择无需环境核实。</summary>
            public string? SavedPreferenceIssue { get; init; }
        }
        /// <summary>启动后恢复保存亲和性的结果。</summary>
        public enum StartupApplyStatus {
            /// <summary>当前调度器或保存的偏好不要求恢复，或当前 Core 进程已处理过同一 vmmem 实例。</summary>
            NotRequired,
            /// <summary>已成功恢复亲和性。</summary>
            Applied,
            /// <summary>等待或重试期间虚拟机已停止。</summary>
            VmStopped,
            /// <summary>已用尽重试次数，仍未成功应用。</summary>
            Exhausted,
            /// <summary>保存的处理器选择无法形成有效配置。</summary>
            InvalidConfiguration,
            /// <summary>恢复期间保存的偏好已被其他操作改变，本轮停止应用旧选择。</summary>
            Superseded,
            /// <summary>保存环境不匹配或无法核实，需要重新选择并保存。</summary>
            ReselectionRequired }

        /// <summary>设置虚拟机使用哪些宿主逻辑处理器，并保存选择。</summary>
        /// <param name="vmId">目标虚拟机的标识。</param>
        /// <param name="coreIndices">从 <see cref="HostProcessorService.GetAffinityProcessorsAsync"/> 获取的处理器编号；空集合解除亲和性限制。</param>
        /// <returns><see cref="SaveResult"/>：Status 表示已应用、等待应用或失败；MetadataSaved 表示是否保存成功。保存环境未核实时，SavedPreferenceStatus 为 EnvironmentUnverified。</returns>
        /// <remarks>
        /// <para>Classic/Core 调度器为每台虚拟机使用独立 CPU 组；更换组时继承旧组策略，保留旧组。</para>
        /// <para>Root 调度器修改虚拟机进程亲和性，目前支持单 Windows 处理器组；关机时仅保存选择，启动后调用<see cref="M:ExHyperV.Services.CpuAffinityService.ApplySavedAffinityAfterStartAsync(System.Guid,System.Threading.CancellationToken)">恢复亲和性</see>应用。</para>
        /// </remarks>
        public static async Task<SaveResult> SaveAsync(Guid vmId, List<int> coreIndices)
        {
            ArgumentNullException.ThrowIfNull(coreIndices);
            coreIndices = coreIndices.Distinct().Order().ToList();
            if (vmId == Guid.Empty || coreIndices.Any(core => core < 0))
                return Failed(ApiResponse.Fail("虚拟机 GUID 或处理器编号无效。", kind: ApiErrorKind.InvalidArgument));
            var scheduler = HyperVSchedulerService.GetSchedulerType();
            if (scheduler is HyperVSchedulerType.Classic or HyperVSchedulerType.Core)
            {
                bool actionStarted = false;
                try
                {
                    return await HostResourceLock.RunAsync(HcsResource, async () =>
                    {
                        actionStarted = true;
                        var environmentBefore = await AffinityPreference.ReadEnvironmentAsync(AffinityEnvironmentReader.ReadHcsAsync).ConfigureAwait(false);
                        var applied = await CpuGroupAffinityTransaction.ApplyAsync(vmId, coreIndices, HcsOperations).ConfigureAwait(false);
                        if (!applied.Operation.Success)
                            return Failed(applied.Operation, applied.Recovery, applied.CurrentGroupId, applied.RetainedGroupId);
                        ApiResponse saved;
                        var environmentAfter = await AffinityPreference.ReadEnvironmentAsync(AffinityEnvironmentReader.ReadHcsAsync).ConfigureAwait(false);
                        var preference = AffinityPreference.Create(coreIndices, AffinityPreference.HcsBackend,
                            AffinityPreference.StableEnvironment(environmentBefore, environmentAfter));
                        var assessment = preference.Check(AffinityPreference.HcsBackend, environmentAfter);
                        try
                        {
                            saved = await VmMetadataService.SetAsync(vmId, VmMetadataService.CpuAffinityKey,
                                preference.Serialize()).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            saved = ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.OutcomeUnknown);
                        }
                        return new SaveResult(SaveStatus.Applied, saved.Success)
                        {
                            CurrentGroupId = applied.CurrentGroupId,
                            Error = saved.Success ? null : saved.Error,
                            ErrorKind = saved.ErrorKind, ErrorCode = saved.Code, ErrorSource = saved.ErrorSource,
                            SavedPreferenceStatus = saved.Success ? assessment.Status : PreferenceStatus.EnvironmentUnverified,
                            SavedPreferenceIssue = saved.Success ? assessment.Issue : "本次选择未确认保存成功。"
                        };
                    }).ConfigureAwait(false);
                }
                catch (AbandonedMutexException ex)
                {
                    return Failed(ApiResponse.Fail(ex.Message, exception: ex, kind: ApiErrorKind.Conflict));
                }
                catch (Exception ex)
                {
                    return Failed(ApiResponse.Fail(ex.Message, exception: ex, kind: actionStarted
                        ? ApiErrorKind.OutcomeUnknown
                        : ex is UnauthorizedAccessException ? ApiErrorKind.AccessDenied : ApiErrorKind.OperationFailed));
                }
            }
            if (scheduler != HyperVSchedulerType.Root)
                return Failed(ApiResponse.Fail("无法确定当前 Hyper-V 调度器，未修改亲和性。", kind: ApiErrorKind.Unsupported));
            bool rootStarted = false;
            try
            {
                return await HostResourceLock.RunAsync(RootResource(vmId), () =>
                {
                    rootStarted = true;
                    return RootAffinityWorkflow.SaveAsync(vmId, coreIndices, TrackedRootOperations);
                }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                return Failed(ExceptionResult(error, rootStarted));
            }
        }

        internal static async Task<SaveResult> SaveAsync(List<int> coreIndices, bool isVmRunning,
            HyperVSchedulerType scheduler, Func<Task<bool>> apply, Func<string, Task<bool>> persistMetadata)
        {
            string value = JsonSerializer.Serialize(coreIndices);
            bool applied = await apply();
            bool queued = scheduler == HyperVSchedulerType.Root && !isVmRunning;
            if (!applied && !queued) return Failed(ApiResponse.Fail("无法应用虚拟机 CPU 亲和性。", kind: ApiErrorKind.OperationFailed));

            bool saved = await persistMetadata(value);
            return new(applied ? SaveStatus.Applied : SaveStatus.Queued, saved);
        }

        /// <summary>在虚拟机启动后恢复已保存的 Root 处理器亲和性。</summary>
        /// <param name="vmId">目标虚拟机的标识。</param>
        /// <param name="cancellationToken">取消等待与后续尝试。</param>
        /// <returns>恢复状态。ReselectionRequired 表示宿主或处理器拓扑已变更，或保存环境无法核实，需要重新选择并保存。</returns>
        /// <remarks>等待进程最多尝试五轮；Classic/Core 调度器仅核对保存的选择。</remarks>
        /// <exception cref="CoreOperationException">查询或应用失败，Result 包含具体原因。</exception>
        /// <exception cref="OperationCanceledException">取消了本次恢复。</exception>
        public static async Task<StartupApplyStatus> ApplySavedAffinityAfterStartAsync(
            Guid vmId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (vmId == Guid.Empty)
                throw new CoreOperationException(ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument));
            var scheduler = HyperVSchedulerService.GetSchedulerType();
            if (scheduler is HyperVSchedulerType.Classic or HyperVSchedulerType.Core)
            {
                var preference = await ReadHcsPreferenceAsync(vmId).ConfigureAwait(false);
                return preference.Status switch
                {
                    PreferenceStatus.EnvironmentChanged or PreferenceStatus.EnvironmentUnverified => StartupApplyStatus.ReselectionRequired,
                    PreferenceStatus.InvalidConfiguration => StartupApplyStatus.InvalidConfiguration,
                    _ => StartupApplyStatus.NotRequired
                };
            }
            if (scheduler != HyperVSchedulerType.Root)
                throw new CoreOperationException(ApiResponse.Fail("无法确定当前 Hyper-V 调度器。", kind: ApiErrorKind.Unsupported));
            bool started = false;
            try
            {
                return await HostResourceLock.RunAsync(RootResource(vmId), () =>
                {
                    started = true;
                    return RootAffinityWorkflow.RestoreAsync(vmId, TrackedRootOperations, cancellationToken);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (CoreOperationException) { throw; }
            catch (Exception error) { throw new CoreOperationException(ExceptionResult(error, started)); }
        }

        internal static async Task<StartupApplyStatus> ApplySavedAffinityAfterStartAsync(
            string? savedAffinityJson, HyperVSchedulerType scheduler, Func<Task<bool>> isRunning,
            Func<List<int>, Task<bool>> apply, Func<TimeSpan, CancellationToken, Task> delay,
            CancellationToken cancellationToken = default)
        {
            if (scheduler != HyperVSchedulerType.Root || savedAffinityJson == null)
                return StartupApplyStatus.NotRequired;
            if (!AffinityPreference.TryRead(savedAffinityJson, out var preference))
                return StartupApplyStatus.InvalidConfiguration;
            cancellationToken.ThrowIfCancellationRequested();
            // 此旧内部入口没有宿主环境读取能力，不能用于放行自动恢复。
            await Task.CompletedTask;
            return preference!.LogicalProcessors.Length == 0 ? StartupApplyStatus.NotRequired : StartupApplyStatus.ReselectionRequired;
        }

        /// <summary>读取当前实际生效的处理器亲和性。</summary>
        /// <param name="vmId">目标虚拟机的标识。</param>
        /// <returns>宿主逻辑处理器编号列表。Classic/Core 未绑定 CPU 组时返回空列表。</returns>
        /// <exception cref="CoreOperationException">查询失败，或 Root 调度器下虚拟机进程尚未运行；Result 包含具体原因。</exception>
        public static async Task<List<int>> GetCpuAffinityAsync(Guid vmId)
        {
            if (vmId == Guid.Empty)
                throw new CoreOperationException(ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument));
            var scheduler = HyperVSchedulerService.GetSchedulerType();
            if (scheduler is HyperVSchedulerType.Classic or HyperVSchedulerType.Core)
                return await HostResourceLock.RunAsync(HcsResource, () => ReadClassicAffinityAsync(vmId)).ConfigureAwait(false);
            if (scheduler != HyperVSchedulerType.Root)
                throw new CoreOperationException(ApiResponse.Fail("无法确定当前 Hyper-V 调度器。", kind: ApiErrorKind.Unsupported));
            try
            {
                return await HostResourceLock.RunAsync(RootResource(vmId), async () =>
                    RootAffinityWorkflow.DecodeMask(Require(await RootOperations.ReadProcess(vmId).ConfigureAwait(false)).Mask).ToList())
                    .ConfigureAwait(false);
            }
            catch (CoreOperationException) { throw; }
            catch (Exception error) { throw new CoreOperationException(ExceptionResult(error, false)); }
        }

        /// <summary>读取当前亲和性、保存的处理器选择及其环境匹配状态。</summary>
        /// <param name="vmId">目标虚拟机的标识。</param>
        /// <returns>当前电源状态、实际处理器选择和保存的亲和性偏好；查询失败时返回原因。</returns>
        public static async Task<ApiResponse<AffinityState>> ReadStateAsync(Guid vmId)
        {
            if (vmId == Guid.Empty) return ApiResponse<AffinityState>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            try
            {
                var scheduler = HyperVSchedulerService.GetSchedulerType();
                if (scheduler == HyperVSchedulerType.Root)
                    return ApiResponse<AffinityState>.Ok(await HostResourceLock.RunAsync(RootResource(vmId),
                        () => RootAffinityWorkflow.ReadAsync(vmId, RootOperations)).ConfigureAwait(false));
                if (scheduler is not (HyperVSchedulerType.Classic or HyperVSchedulerType.Core))
                    return ApiResponse<AffinityState>.Fail("无法确定当前 Hyper-V 调度器。", kind: ApiErrorKind.Unsupported);
                return await HostResourceLock.RunAsync(HcsResource, async () =>
                {
                    ushort state = Require(await ReadVmStateAsync(vmId).ConfigureAwait(false));
                    var processors = await ReadClassicAffinityAsync(vmId).ConfigureAwait(false);
                    var saved = await ReadHcsPreferenceAsync(vmId).ConfigureAwait(false);
                    return ApiResponse<AffinityState>.Ok(new(VmMapper.MapState(state), processors.AsReadOnly(),
                        saved.Preference is null ? null : Array.AsReadOnly(saved.Preference.LogicalProcessors), null, null)
                    { SavedPreferenceStatus = saved.Status, SavedPreferenceIssue = saved.Issue });
                }).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                var result = ExceptionResult(error, false);
                return ApiResponse<AffinityState>.Fail(result.Error, result.Code, result.ErrorSource, result.Exception, result.ErrorKind);
            }
        }


        private static async Task<List<int>> ReadClassicAffinityAsync(Guid vmId)
        {
            var binding = Require(await VmCpuGroupBindingStore.ReadAsync(vmId).ConfigureAwait(false));
            if (binding.GroupId == Guid.Empty) return [];
            var groups = Require(await HcsApi.GetAllCpuGroupsAsync().ConfigureAwait(false));
            var group = groups.SingleOrDefault(g => g.GroupId == binding.GroupId);
            if (group?.Affinity?.LogicalProcessors is not { Length: > 0 } processors)
                throw new CoreOperationException(ApiResponse.Fail("虚拟机绑定的 CPU 组不存在或缺少处理器集合。", kind: ApiErrorKind.Conflict));
            var topology = Require(await HcsApi.GetProcessorTopologyAsync().ConfigureAwait(false));
            var valid = topology.Select(p => p.LpIndex).ToHashSet();
            if (processors.Any(p => p > int.MaxValue || !valid.Contains(p)))
                throw new CoreOperationException(ApiResponse.Fail("CPU 组引用了当前宿主拓扑中不存在的处理器。", kind: ApiErrorKind.Conflict));
            var confirmed = Require(await VmCpuGroupBindingStore.ReadAsync(vmId).ConfigureAwait(false));
            if (confirmed != binding)
                throw new CoreOperationException(ApiResponse.Fail("读取期间虚拟机 CPU 组绑定已变化，请重新读取。", kind: ApiErrorKind.Conflict));
            return processors.Select(p => (int)p).Distinct().Order().ToList();
        }

        private static T Require<T>(ApiResponse<T> response)
        {
            if (response.HasData) return response.Data;
            throw new CoreOperationException(ApiResponse.Fail(response.Success ? "未返回所需的亲和性数据。" : response.Error,
                response.Code, response.ErrorSource, response.Exception, response.Success ? ApiErrorKind.NotFound : response.ErrorKind));
        }

        private static SaveResult Failed(ApiResponse error, ApiResponse? recovery = null, Guid? group = null, Guid? retained = null)
            => new(SaveStatus.Failed, false)
            {
                Error = error.Error, ErrorKind = error.ErrorKind, ErrorCode = error.Code, ErrorSource = error.ErrorSource,
                CleanupError = recovery is { Success: false } ? recovery.Error : null,
                CleanupErrorKind = recovery is { Success: false } ? recovery.ErrorKind : ApiErrorKind.None,
                CurrentGroupId = group, RetainedGroupId = retained,
                SavedPreferenceStatus = PreferenceStatus.EnvironmentUnverified,
                SavedPreferenceIssue = "本次选择未确认保存成功。"
            };

        // 旧格式保留用于识别既有记录；公开保存入口统一写入带环境信息的版本 2。
        internal static string SerializeHcsPreference(IReadOnlyList<int> indices)
            => JsonSerializer.Serialize(new { Version = 1, Backend = "HcsCpuGroup", LogicalProcessors = indices });

        internal static bool IsHcsPreference(string json)
            => AffinityPreference.TryRead(json, out var preference) && preference!.Backend == AffinityPreference.HcsBackend;

        private static async Task<(AffinityPreference? Preference, PreferenceStatus Status, string? Issue)> ReadHcsPreferenceAsync(Guid vmId)
        {
            var response = await ReadPreferenceAsync(vmId).ConfigureAwait(false);
            if (response.IsEmpty) return (null, PreferenceStatus.None, null);
            string json = Require(response);
            if (!AffinityPreference.TryRead(json, out var preference))
                return (null, PreferenceStatus.InvalidConfiguration, "保存的亲和性内容无效，原元数据保留。");
            var environment = preference!.CanCompareEnvironment(AffinityPreference.HcsBackend)
                ? await AffinityPreference.ReadEnvironmentAsync(AffinityEnvironmentReader.ReadHcsAsync).ConfigureAwait(false) : null;
            var assessment = preference.Check(AffinityPreference.HcsBackend, environment);
            return (preference, assessment.Status, assessment.Issue);
        }

        private static async Task<ApiResponse<string>> ReadPreferenceAsync(Guid vmId)
        {
            var metadata = await VmMetadataService.ReadAsync(vmId).ConfigureAwait(false);
            if (!metadata.HasData)
                return ApiResponse<string>.Fail(metadata.Success ? "未返回虚拟机元数据。" : metadata.Error,
                    metadata.Code, metadata.ErrorSource, metadata.Exception, metadata.Success ? ApiErrorKind.InvalidState : metadata.ErrorKind);
            return metadata.Data.TryGetValue(VmMetadataService.CpuAffinityKey, out var value)
                ? ApiResponse<string>.Ok(value) : ApiResponse<string>.Empty();
        }

        private static async Task<ApiResponse<ushort>> ReadVmStateAsync(Guid vmId)
        {
            var state = await WmiApi.QueryAsync(
                $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => Convert.ToUInt16(obj["EnabledState"])).ConfigureAwait(false);
            if (!state.Success)
                return ApiResponse<ushort>.Fail(state.Error, state.Code, state.ErrorSource, state.Exception, state.ErrorKind);
            if (!state.HasData || state.Data.Count == 0)
                return ApiResponse<ushort>.Fail("未找到虚拟机。", kind: ApiErrorKind.NotFound);
            if (state.Data.Count != 1)
                return ApiResponse<ushort>.Fail("无法确定唯一的虚拟机状态。", kind: ApiErrorKind.AmbiguousTarget);
            return ApiResponse<ushort>.Ok(state.Data[0]);
        }

        private static string RootResource(Guid vmId) => $"CpuAffinity:Root:{vmId:D}";

        private static ApiResponse ExceptionResult(Exception error, bool actionStarted)
            => error is CoreOperationException operation ? operation.Result : ApiResponse.Fail(error.Message, error.HResult,
                exception: error, kind: error is AbandonedMutexException ? ApiErrorKind.Conflict
                    : error is UnauthorizedAccessException ? ApiErrorKind.AccessDenied
                    : actionStarted ? ApiErrorKind.OutcomeUnknown : ApiErrorKind.OperationFailed);

        internal static bool TryParseAffinity(string json, out List<int> cores)
        {
            cores = new List<int>();
            try
            {
                int[]? parsed = JsonSerializer.Deserialize<int[]>(json);
                if (parsed == null || parsed.Any(core => core < 0)) return false;
                cores.AddRange(parsed);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

    }
}
