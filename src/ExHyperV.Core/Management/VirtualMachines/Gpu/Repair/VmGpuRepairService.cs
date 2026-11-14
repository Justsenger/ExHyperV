using System.IO;
using ExHyperV.Models;
using ExHyperV.Tools;
using ExHyperV.Vmcx;

namespace ExHyperV.Services
{
    /// <summary>
    /// 失效 GPU-PV(GPU Partition 钉死的物理 GPU 路径在当前主机失配)的检测与修复。
    ///
    /// 此类记录不会出现在 Get-VMGpuPartitionAdapter 或 Msvm_GpuPartitionSettingData 中，
    /// 因此需要通过统一 VmcxDocument / VmcxEditSession 编辑对应的 .vmcx 文件。
    /// 检测读取文件快照，修复通过 VmDataStore 提交配置修改。
    ///
    /// 失配判定使用完整路径精确比对；GPU-PV 绑定的是某块卡的
    /// 完整 GPUPARAV 路径(含 PCIe 位置段),主机重启/重插后即使同一张卡仍在,位置段也可能变 → 路径失配 → 起不来。
    /// 失配后再比对硬件标识(VEN/DEV/SUBSYS/REV)区分:
    ///   · 只有一个同标识候选：更新为该候选路径；多个候选必须由调用方选择；
    ///   · 池里无同标识的卡：删除失效的 GPU-PV 设备。
    /// </summary>
    /// <example>
    /// <para>调用方已确认修复范围后，为每个多候选设备取得用户选择。choose 返回候选 Id，不应默认取列表第一项。</para>
    /// <code>
    /// using ExHyperV.Services;
    /// using ExHyperV.Tools;
    /// public static class GpuRepairChoiceExample
    /// {
    ///     public static async Task&lt;ApiResponse&lt;VmGpuRepairService.RepairResult&gt;&gt; RepairSelectedAsync(
    ///         Guid vmId, IReadOnlyList&lt;VmGpuRepairService.StaleGpuPartition&gt; scanned,
    ///         Func&lt;VmGpuRepairService.StaleGpuPartition, Task&lt;string&gt;&gt; choose)
    ///     {
    ///         var targets = new List&lt;VmGpuRepairService.StaleGpuPartition&gt;();
    ///         foreach (var target in scanned)
    ///             targets.Add(target.RequiresSelection ? target.SelectCandidate(await choose(target)) : target);
    ///         return await VmGpuRepairService.RepairAsync(vmId, targets);
    ///     }
    ///
    ///     public static async Task ResumeApprovedPowerRepairAsync(VmPowerOperationSession session,
    ///         Func&lt;VmGpuRepairService.StaleGpuPartition, Task&lt;string&gt;&gt; choose)
    ///     {
    ///         var confirmation = session.Confirmation ?? throw new InvalidOperationException("尚无修复确认。");
    ///         var selections = new Dictionary&lt;Guid, string&gt;();
    ///         foreach (var target in confirmation.GpuTargets.Where(t =&gt; t.RequiresSelection))
    ///             selections.Add(Guid.Parse(target.Instance.Trim('{', '}', '_', ' ')), await choose(target));
    ///         await session.SubmitDecisionAsync(confirmation.RequestId, true, selections);
    ///         if (session.Stage == VmPowerOperationStage.ReadyToRetry) await session.AdvanceAsync();
    ///     }
    /// }
    /// </code>
    /// </example>
    public static partial class VmGpuRepairService
    {
        /// <summary>当前宿主上可用于重绑的具体 GPU；由扫描结果提供。</summary>
        public sealed record RebindCandidate
        {
            /// <summary>本次查询的完整 GPU 分区路径，用于 SelectCandidate 选择设备。</summary>
            public string Id { get; }
            /// <summary>PnP 设备实例标识，包含区分同型号显卡的位置段；可与 HostGpuInfo.InstanceId 比对以取得名称。无法按标准分区路径解析时保留原路径。</summary>
            public string DeviceInstanceId { get; }
            internal RebindCandidate(string id)
            {
                Id = id;
                string path = id.StartsWith(@"\\?\", StringComparison.Ordinal) ? id[4..] : id;
                int end = path.IndexOf("#{", StringComparison.Ordinal);
                DeviceInstanceId = end >= 0 ? path[..end].Replace('#', '\\') : id;
            }
        }

        /// <summary>扫描得到的失效 GPU 分区配置；可交给 RepairAsync 修复。</summary>
        public sealed record StaleGpuPartition
        {
            /// <summary>VMCX 中的虚拟设备编号。</summary>
            public int VdevNumber { get; }
            /// <summary>该设备配置的实例 GUID 文本。</summary>
            public string Instance { get; }
            /// <summary>设备缺少核心字段，无法按完整 GPU 分区设备使用。</summary>
            public bool IsStructurallyIncomplete { get; }
            /// <summary>是否存在重绑候选；多个候选时需先选择。</summary>
            public bool CanRebind => Candidates.Count != 0;
            /// <summary>具有相同硬件标识的全部候选，按完整身份去重。</summary>
            public IReadOnlyList<RebindCandidate> Candidates { get; }
            /// <summary>是否有多个待选择候选；为 true 时，修复返回 AmbiguousTarget。</summary>
            public bool RequiresSelection => Candidates.Count > 1 && _selectedCandidateId == null;
            /// <summary>本次将使用的候选；单候选自动采用，多候选经 SelectCandidate 选择，零候选或待选择时为 null。</summary>
            public RebindCandidate? SelectedCandidate => Candidates.FirstOrDefault(c =>
                string.Equals(c.Id, RebindPath, StringComparison.OrdinalIgnoreCase));
            internal Guid VmId { get; }
            internal string HostResource { get; }
            private readonly string? _selectedCandidateId;
            internal bool HasExplicitSelection => _selectedCandidateId != null;
            internal string? RebindPath => _selectedCandidateId ?? (Candidates.Count == 1 ? Candidates[0].Id : null);
            internal StaleGpuPartition(Guid vmId, int vdevNumber, string instance, string hostResource,
                string? rebindPath, bool isStructurallyIncomplete = false)
                : this(vmId, vdevNumber, instance, hostResource,
                    string.IsNullOrEmpty(rebindPath) ? [] : [new RebindCandidate(rebindPath)], isStructurallyIncomplete, null) { }

            internal StaleGpuPartition(Guid vmId, int vdevNumber, string instance, string hostResource,
                IReadOnlyList<RebindCandidate> candidates, bool isStructurallyIncomplete, string? selectedCandidateId)
            {
                VmId = vmId; VdevNumber = vdevNumber; Instance = instance; HostResource = hostResource;
                Candidates = Array.AsReadOnly(candidates.ToArray());
                _selectedCandidateId = selectedCandidateId; IsStructurallyIncomplete = isStructurallyIncomplete;
            }

            /// <summary>选择一张候选显卡，返回携带选择的新修复目标；原扫描结果和宿主均不改变。</summary>
            /// <param name="candidateId">Candidates 中所选项的 Id。</param>
            /// <returns>可交给 RepairAsync 的新目标；执行时还会核对该候选仍然可用。</returns>
            /// <exception cref="ArgumentException">标识为空，或不是本目标的候选。</exception>
            public StaleGpuPartition SelectCandidate(string candidateId)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(candidateId);
                var candidate = Candidates.SingleOrDefault(c => string.Equals(c.Id, candidateId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException("请选择本次扫描返回的 GPU 候选。", nameof(candidateId));
                return new(VmId, VdevNumber, Instance, HostResource, Candidates, IsStructurallyIncomplete, candidate.Id);
            }
        }

        // VMComputerSystemState: 2=Running, 3=Off
        private const VmState State_Off = VmState.PoweredOff;

        /// <summary>注册虚拟机的配置文件位置与当前运行状态。</summary>
        /// <param name="VmId">虚拟机的稳定 GUID。</param>
        /// <param name="Path">现存配置文件的绝对路径。</param>
        /// <param name="State">本次查询的类型化运行状态。</param>
        internal sealed record ConfigurationFile(Guid VmId, string Path, VmState State);

        /// <summary>本次提交完成的 GPU 分区修复数量。</summary>
        /// <param name="Rebound">重新绑定宿主 GPU 的设备数量。</param>
        /// <param name="Removed">删除失效设备定义的数量。</param>
        public sealed record RepairResult(int Rebound, int Removed);

        /// <summary>按稳定身份定位注册虚拟机的配置文件，并读取当前运行状态。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <returns>现存配置文件与状态；查询失败或文件缺失均返回失败，不返回拼凑默认路径。</returns>
        internal static async Task<ApiResponse<ConfigurationFile>> ResolveVmcxAsync(Guid vmId)
        {
            if (vmId == Guid.Empty) return ApiResponse<ConfigurationFile>.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            var config = await WmiApi.QueryFirstAsync(
                $"SELECT ConfigurationDataRoot, ConfigurationFile FROM Msvm_VirtualSystemSettingData WHERE {VmIdentity.SettingsPredicate(vmId)}",
                obj => (Root: obj["ConfigurationDataRoot"]?.ToString(), File: obj["ConfigurationFile"]?.ToString()));
            if (!config.HasData) return ApiResponse<ConfigurationFile>.Fail(config.Success ? Properties.Resources.GpuRepair_VmcxNotFound : config.Error,
                config.Code, config.ErrorSource, config.Exception, config.Success ? ApiErrorKind.NotFound : config.ErrorKind);
            var state = await WmiApi.QueryFirstAsync(
                $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE {VmIdentity.ComputerSystemPredicate(vmId)}",
                obj => Convert.ToUInt16(obj["EnabledState"] ?? 0));
            if (!state.HasData) return ApiResponse<ConfigurationFile>.Fail(state.Success ? "虚拟机已不存在。" : state.Error,
                state.Code, state.ErrorSource, state.Exception, state.Success ? ApiErrorKind.NotFound : state.ErrorKind);
            try
            {
                string path = VmConfigurationPaths.Resolve(config.Data.File ?? string.Empty, config.Data.Root);
                if (File.Exists(path))
                    return ApiResponse<ConfigurationFile>.Ok(new(vmId, path, VmMapper.MapState(state.Data)));
                return ApiResponse<ConfigurationFile>.Fail(Properties.Resources.GpuRepair_VmcxNotFound, kind: ApiErrorKind.NotFound);
            }
            catch (Exception error) { return ApiResponse<ConfigurationFile>.Fail(error.Message, exception: error, kind: ApiErrorKind.OperationFailed); }
        }

        private static async Task<ApiResponse<IReadOnlyList<string>>> GetPartitionableGpuPoolAsync()
        {
            var response = await WmiApi.QueryAsync("SELECT Name FROM Msvm_PartitionableGpu", obj => obj["Name"]?.ToString() ?? string.Empty);
            return response.Success ? ApiResponse<IReadOnlyList<string>>.Ok((response.Data ?? []).Where(n => !string.IsNullOrEmpty(n)).ToArray())
                : ApiResponse<IReadOnlyList<string>>.Fail(response.Error, response.Code, response.ErrorSource, response.Exception, response.ErrorKind);
        }
        /// <summary>从 GPUPARAV 路径提取硬件标识段(VEN_xxxx&amp;DEV_xxxx&amp;SUBSYS_xxxx&amp;REV_xx)，用于寻找同型号硬件候选。
        /// 路径形如 \\?\PCI#&lt;标识&gt;#&lt;PCIe位置&gt;#{接口GUID}\GPUPARAV,标识是第一个 '#' 与第二个 '#' 之间那段。</summary>
        private static string GpuIdentity(string hostResource)
        {
            if (string.IsNullOrEmpty(hostResource)) return string.Empty;
            var parts = hostResource.Split('#');
            return (parts.Length >= 2 ? parts[1] : hostResource).ToUpperInvariant();
        }

        /// <summary>扫描注册虚拟机的结构残缺或宿主 GPU 绑定失效的分区设备。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
        /// <returns>完整扫描结果；配置读取或必要的 GPU 池查询失败时返回失败，不把未完成的扫描表示为成功空列表。</returns>
        /// <remarks>RequiresSelection 为 true 时，从 Candidates 中选择一项并调用 SelectCandidate。</remarks>
        public static async Task<ApiResponse<IReadOnlyList<StaleGpuPartition>>> FindStaleGpuPartitionsAsync(Guid vmId)
        {
            var location = await ResolveVmcxAsync(vmId);
            if (!location.HasData) return ApiResponse<IReadOnlyList<StaleGpuPartition>>.Fail(location.Error, location.Code, location.ErrorSource, location.Exception, location.ErrorKind);
            try
            {
                var devices = await Task.Run(() => VmcxSchema.GpuPvDevices(VmcxDocument.Read(location.Data.Path).ToLegacyNodes()));
                var result = devices.Where(p => !p.IsStructurallyComplete)
                    .Select(p => new StaleGpuPartition(vmId, p.VdevNumber, p.Instance, p.HostResource, null, true)).ToList();
                var pinned = devices.Where(p => p.IsStructurallyComplete && !string.IsNullOrEmpty(p.HostResource)).ToList();
                if (pinned.Count == 0) return ApiResponse<IReadOnlyList<StaleGpuPartition>>.Ok(result.AsReadOnly());
                var pool = await GetPartitionableGpuPoolAsync();
                if (!pool.HasData) return ApiResponse<IReadOnlyList<StaleGpuPartition>>.Fail(pool.Error, pool.Code, pool.ErrorSource, pool.Exception, pool.ErrorKind);
                var exact = new HashSet<string>(pool.Data, StringComparer.OrdinalIgnoreCase);
                foreach (var device in pinned)
                {
                    if (exact.Contains(device.HostResource)) continue;
                    result.Add(new(vmId, device.VdevNumber, device.Instance, device.HostResource,
                        FindRebindCandidates(device.HostResource, pool.Data), false, null));
                }
                return ApiResponse<IReadOnlyList<StaleGpuPartition>>.Ok(result.AsReadOnly());
            }
            catch (Exception error) { return ApiResponse<IReadOnlyList<StaleGpuPartition>>.Fail(error.Message, exception: error, kind: ApiErrorKind.OperationFailed); }
        }

        internal static ApiResponse ValidateRepairTargets(Guid vmId, IReadOnlyList<StaleGpuPartition> targets)
        {
            if (vmId == Guid.Empty) return ApiResponse.Fail("虚拟机 GUID 不能为空。", kind: ApiErrorKind.InvalidArgument);
            var seen = new HashSet<Guid>();
            foreach (var target in targets)
            {
                if (target == null || target.VmId != vmId) return ApiResponse.Fail("修复清单包含其他虚拟机或未查询的设备。", kind: ApiErrorKind.InvalidArgument);
                if (!Guid.TryParse(target.Instance.Trim('{', '}', '_', ' '), out Guid instance) || instance == Guid.Empty || !seen.Add(instance))
                    return ApiResponse.Fail("修复清单包含无效或重复的设备实例。", kind: ApiErrorKind.InvalidArgument);
            }
            return ApiResponse.Ok();
        }

        /// <summary>按扫描结果及显卡选择修复 GPU 分区；零候选或结构残缺时移除失效定义，多候选未选择时不执行。</summary>
        /// <param name="vmId">目标虚拟机的稳定 GUID；必须完全关机。</param>
        /// <param name="targets">扫描目标或其 SelectCandidate 返回的新目标。所提交目标中的全部多候选项都必须完成选择；空集合表示无需修复。</param>
        /// <returns>成功时返回重绑与删除计数；失败时返回原因及提交故障的备份位置。</returns>
        /// <remarks>全部目标先校验，再准备文件修改。选中的卡失效时返回 Conflict，不替换成另一张卡或删除该设备。单候选变为多候选时要求重新扫描选择。显式选择仍在当前候选中时，不受候选枚举顺序影响。原生提交失败可能留下需要恢复的配置，请保留错误中的备份。</remarks>
    /// <editentry label="修复 GPU 配置" category="GPU"/>
        public static async Task<ApiResponse<RepairResult>> RepairAsync(Guid vmId, IEnumerable<StaleGpuPartition> targets)
        {
            if (targets == null) return ApiResponse<RepairResult>.Fail("修复目标不能为空。", kind: ApiErrorKind.InvalidArgument);
            var list = targets.ToArray();
            var validation = ValidateRepairTargets(vmId, list);
            if (!validation.Success) return ApiResponse<RepairResult>.Fail(validation.Error, kind: validation.ErrorKind);
            if (list.Any(target => target.RequiresSelection))
                return ApiResponse<RepairResult>.Fail("多个 GPU 符合修复条件，请为每个待修复设备明确选择候选。尚未修改配置。", kind: ApiErrorKind.AmbiguousTarget);
            if (list.Length == 0) return ApiResponse<RepairResult>.Ok(new(0, 0));
            var location = await ResolveVmcxAsync(vmId);
            if (!location.HasData) return ApiResponse<RepairResult>.Fail(location.Error, location.Code, location.ErrorSource, location.Exception, location.ErrorKind);
            if (location.Data.State != State_Off) return ApiResponse<RepairResult>.Fail("GPU 修复要求虚拟机已关机；暂停、保存或无法确认状态时不能修改配置。", kind: ApiErrorKind.InvalidState);
            var scan = await FindStaleGpuPartitionsAsync(vmId);
            if (!scan.HasData) return ApiResponse<RepairResult>.Fail(scan.Error, scan.Code, scan.ErrorSource, scan.Exception, scan.ErrorKind);
            var refreshed = ValidateRepairSnapshot(list, scan.Data);
            if (!refreshed.Success) return ApiResponse<RepairResult>.Fail(refreshed.Error, kind: refreshed.ErrorKind);
            try
            {
                int rebound = 0, removed = 0;
                var document = await Task.Run(() => VmcxDocument.Read(location.Data.Path));
                var edit = document.BeginEdit();
                var currentDevices = VmcxSchema.GpuPvDevices(document.ToLegacyNodes());
                foreach (var target in list)
                {
                    Guid instance = Guid.Parse(target.Instance.Trim('{', '}', '_', ' '));
                    var current = currentDevices.SingleOrDefault(p => Guid.TryParse(p.Instance.Trim('{', '}', '_', ' '), out var id) && id == instance);
                    if (current == null || current.HostResource != target.HostResource)
                        return ApiResponse<RepairResult>.Fail("GPU 配置已变化，请重新扫描。", kind: ApiErrorKind.Conflict);
                    if (!string.IsNullOrEmpty(target.RebindPath))
                    {
                        var key = document.Find("/configuration/_" + instance.ToString("D") + "_/HostResource").Single();
                        edit.SetValue(key, VmcxValue.String(target.RebindPath));
                        rebound++;
                    }
                    else { edit.RemoveDevice(instance); removed++; }
                }
                var confirm = await ResolveVmcxAsync(vmId);
                if (!confirm.HasData) return ApiResponse<RepairResult>.Fail(confirm.Error, confirm.Code, confirm.ErrorSource, confirm.Exception, confirm.ErrorKind);
                if (confirm.Data.State != State_Off || !string.Equals(confirm.Data.Path, location.Data.Path, StringComparison.OrdinalIgnoreCase))
                    return ApiResponse<RepairResult>.Fail("虚拟机配置路径或电源状态已变化，请重新扫描。", kind: ApiErrorKind.Conflict);
                await Task.Run(() => edit.Commit());
                return ApiResponse<RepairResult>.Ok(new(rebound, removed));
            }
            catch (Exception error)
            {
                string detail = error.Message;
                if (error is VmcxEditException { BackupPath: { } backup }) detail += " 备份：" + backup;
                return ApiResponse<RepairResult>.Fail(Properties.Resources.GpuRepair_RepairFailed + detail, exception: error,
                    kind: error is VmcxEditException ? ApiErrorKind.CleanupRequired : ApiErrorKind.OperationFailed);
            }
        }

        internal static async Task<List<StaleGpuPartition>> FindStaleGpuPartitionsAsync(string vmName)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            if (!identity.HasData) return [];
            var result = await FindStaleGpuPartitionsAsync(identity.Data);
            return result.Data?.ToList() ?? [];
        }

        internal static async Task<(bool Success, string Message, int Rebound, int Removed)> RepairAsync(string vmName, IEnumerable<StaleGpuPartition> targets)
        {
            var identity = await VmIdentity.ResolveAsync(vmName);
            if (!identity.HasData) return (false, identity.Error, 0, 0);
            var result = await RepairAsync(identity.Data, targets);
            return result.HasData ? (true, string.Empty, result.Data.Rebound, result.Data.Removed) : (false, result.Error, 0, 0);
        }
    }
}
