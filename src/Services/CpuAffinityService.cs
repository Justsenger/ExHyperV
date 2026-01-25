using ExHyperV.Models;
using ExHyperV.Tools;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExHyperV.Services
{
    public class CpuAffinityService
    {
        // =========================================================
        // ↓↓↓↓↓↓ 针对 ViewModel 缺失方法的实现 (新增部分) ↓↓↓↓↓↓
        // =========================================================

        /// <summary>
        /// 获取指定 VM 当前绑定的逻辑核心列表
        /// </summary>
        public async Task<List<int>> GetCpuAffinityAsync(Guid vmId)
        {
            if (vmId == Guid.Empty) return new List<int>();

            try
            {
                // 1. 直接调用 HcsManager (WMI/COM)，这是微秒/毫秒级的
                string json = await Task.Run(() => HcsManager.GetVmCpuGroupAsJson(vmId));

                // 2. 解析 JSON 获取 GroupId
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("CpuGroupId", out var prop))
                {
                    if (Guid.TryParse(prop.GetString(), out Guid groupId) && groupId != Guid.Empty)
                    {
                        // 3. 内存中查询 Group 详情 (极速)
                        var groupDetail = await GetCpuGroupDetailsAsync(groupId);
                        if (groupDetail?.Affinity?.LogicalProcessors != null)
                        {
                            return groupDetail.Affinity.LogicalProcessors.Select(u => (int)u).ToList();
                        }
                    }
                }
                return new List<int>();
            }
            catch
            {
                return new List<int>();
            }
        }

        /// <summary>
        /// 将 VM 绑定到指定的核心列表
        /// </summary>
        /// <summary>
        /// 将 VM 绑定到指定的核心列表。如果 coreIndices 为空，则传入 Guid.Empty 执行解除绑定。
        /// </summary>
        public async Task<bool> SetCpuAffinityAsync(Guid vmId, List<int> coreIndices)
        {
            if (vmId == Guid.Empty) return false;

            try
            {
                Guid targetGroupId = Guid.Empty;

                // 只有当用户选择了核心时，才尝试寻找或创建 HCS CPU Group
                if (coreIndices != null && coreIndices.Count > 0)
                {
                    targetGroupId = await FindOrCreateCpuGroupAsync(coreIndices);

                    // 如果列表不为空但创建组失败（依然返回 Empty），此时才认为是失败
                    if (targetGroupId == Guid.Empty) return false;
                }

                // 1. 如果 targetGroupId 是具体的 GUID -> 执行绑定
                // 2. 如果 targetGroupId 是 Guid.Empty (全0) -> 执行解除绑定
                await Task.Run(() => HcsManager.SetVmCpuGroup(vmId, targetGroupId));

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
        public async Task<Guid> FindOrCreateCpuGroupAsync(List<int> selectedCores)
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

            // 调用 HcsManager 创建底层组
            await Task.Run(() => HcsManager.CreateCpuGroup(newGroupId, sortedSelectedCores));

            return newGroupId;
        }

        public async Task<HcsCpuGroupDetail> GetCpuGroupDetailsAsync(Guid groupId)
        {
            if (groupId == Guid.Empty)
            {
                return null;
            }

            var allGroups = await GetAllCpuGroupsAsync();
            return allGroups?.FirstOrDefault(g => g.GroupId == groupId);
        }

        public async Task<List<HcsCpuGroupDetail>> GetAllCpuGroupsAsync()
        {
            try
            {
                string jsonResult = await Task.Run(() => HcsManager.GetAllCpuGroupsAsJson());
                if (string.IsNullOrEmpty(jsonResult))
                {
                    return new List<HcsCpuGroupDetail>();
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var result = JsonSerializer.Deserialize<HcsQueryResult>(jsonResult, options);

                return result?.Properties?
                               .FirstOrDefault()?
                               .CpuGroups ?? new List<HcsCpuGroupDetail>();
            }
            catch
            {
                return null;
            }
        }
    }
}