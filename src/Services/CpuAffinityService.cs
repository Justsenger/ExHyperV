using ExHyperV.Api;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    public class CpuAffinityService
    {
        /// <summary>
        /// 获取虚拟机的 CPU 亲和性设置
        /// </summary>
        public async Task<List<int>> GetCpuAffinityAsync(Guid vmId, string notes)
        {
            // 1. 优先尝试从 Notes 中解析（持久化配置）
            string savedAffinity = Utils.GetTagValue(notes, "Affinity");
            if (!string.IsNullOrEmpty(savedAffinity))
            {
                try
                {
                    return savedAffinity.Split(',')
                        .Select(s => int.Parse(s.Trim()))
                        .ToList();
                }
                catch { /* 解析失败则回退到探测逻辑 */ }
            }

            // 2. 如果 Notes 没数据，再执行原有的实时探测逻辑
            if (vmId == Guid.Empty) return new List<int>();
            var scheduler = HyperVSchedulerService.GetSchedulerType();

            if (scheduler == HyperVSchedulerType.Root)
            {
                return await Task.Run(() => ProcessAffinityManager.GetVmProcessAffinity(vmId));
            }

            // Classic/Core 模式：从 HCS CPU Group 中读取
            var groupIdResp = await GetVmCpuGroupIdAsync(vmId);
            if (!groupIdResp.HasData || groupIdResp.Data == Guid.Empty)
                return new List<int>();

            var groupDetail = await GetCpuGroupDetailsAsync(groupIdResp.Data);
            if (groupDetail?.Affinity?.LogicalProcessors == null)
                return new List<int>();

            return groupDetail.Affinity.LogicalProcessors.Select(u => (int)u).ToList();
        }

        /// <summary>
        /// 设置虚拟机的 CPU 亲和性
        /// </summary>
        /// <param name="vmId">虚拟机 ID</param>
        /// <param name="coreIndices">选中的核心索引列表</param>
        /// <param name="isVmRunning">虚拟机当前是否正在运行（Root 模式必须开启）</param>
        public async Task<bool> SetCpuAffinityAsync(Guid vmId, List<int> coreIndices, bool isVmRunning)
        {
            if (vmId == Guid.Empty) return false;

            // 1. 获取当前调度器类型
            var scheduler = HyperVSchedulerService.GetSchedulerType();

            if (scheduler == HyperVSchedulerType.Root)
            {
                // --- Root 调度器路径 ---
                // 根据经验，Root 模式只能在运行时控制 vmmem 进程
                if (!isVmRunning)
                {
                    // 这里我们返回 false，由 ViewModel 层决定是否提示用户“必须启动后设置”
                    return false;
                }

                await Task.Run(() => ProcessAffinityManager.SetVmProcessAffinity(vmId, coreIndices));
                return true;
            }
            else
            {
                // --- Classic / Core 调度器路径 ---
                // 使用 CPU Group 方式，支持静态/动态设置
                try
                {
                    Guid targetGroupId = Guid.Empty;
                    if (coreIndices != null && coreIndices.Count > 0)
                    {
                        targetGroupId = await FindOrCreateCpuGroupAsync(coreIndices);
                        if (targetGroupId == Guid.Empty) return false;
                    }

                    // 将 VM 关联到该组（targetGroupId == Empty 即解除组限制）
                    // 走 WMI ModifyResourceSettings 改 Msvm_ProcessorSettingData.CpuGroupId
                    string groupIdStr = targetGroupId.ToString("D");
                    var bindResp = await WmiApi.WithObjectAsync(
                        wql: $"SELECT * FROM Msvm_ProcessorSettingData WHERE InstanceID LIKE '%{vmId}%'",
                        modifier: obj => obj["CpuGroupId"] = groupIdStr,
                        submitMethod: "ModifyResourceSettings",
                        submitParamName: "ResourceSettings",
                        wrapInArray: true);

                    return bindResp.Success;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // ----------------------------------------------------------------------------------
        // HCS CPU Group 辅助方法（仅供 Classic/Core 模式使用，外部无调用方 → private）
        // ----------------------------------------------------------------------------------

        private async Task<Guid> FindOrCreateCpuGroupAsync(List<int> selectedCores)
        {
            if (selectedCores == null || !selectedCores.Any())
            {
                return Guid.Empty;
            }

            var selectedCoresSet = new HashSet<uint>(selectedCores.Select(c => (uint)c));
            var existingGroups = await GetAllCpuGroupsAsync();

            if (existingGroups != null)
            {
                foreach (var group in existingGroups)
                {
                    if (group.Affinity?.LogicalProcessors != null)
                    {
                        var existingCoresSet = new HashSet<uint>(group.Affinity.LogicalProcessors);
                        if (existingCoresSet.SetEquals(selectedCoresSet))
                        {
                            return group.GroupId;
                        }
                    }
                }
            }

            var sortedSelectedCores = selectedCores.Select(c => (uint)c).OrderBy(c => c).ToArray();
            var newGroupId = Guid.NewGuid();
            var createResp = await HcsApi.CreateCpuGroupAsync(newGroupId, sortedSelectedCores);
            if (!createResp.Success) return Guid.Empty;

            return newGroupId;
        }

        private async Task<HcsCpuGroupDetail?> GetCpuGroupDetailsAsync(Guid groupId)
        {
            if (groupId == Guid.Empty) return null;
            var allGroups = await GetAllCpuGroupsAsync();
            return allGroups?.FirstOrDefault(g => g.GroupId == groupId);
        }

        private async Task<List<HcsCpuGroupDetail>?> GetAllCpuGroupsAsync()
        {
            var resp = await HcsApi.GetAllCpuGroupsAsync();
            return resp.HasData ? resp.Data : null;
        }

        /// <summary>
        /// 读取 VM 当前所属的 CPU Group ID。
        /// VM↔组的关联存于 WMI（Msvm_ProcessorSettingData.CpuGroupId），读写都走 WmiApi；
        /// 组本身的增删/列举才走 HcsApi(vmcompute)。返回 Guid.Empty 表示未分配。
        /// </summary>
        private async Task<ApiResponse<Guid>> GetVmCpuGroupIdAsync(Guid vmId)
        {
            return await WmiApi.QueryFirstAsync(
                $"SELECT CpuGroupId FROM Msvm_ProcessorSettingData WHERE InstanceID LIKE '%{vmId}%'",
                obj => (obj["CpuGroupId"] != null &&
                        Guid.TryParse(obj["CpuGroupId"].ToString(), out Guid g))
                       ? g : Guid.Empty);
        }
    }
}
