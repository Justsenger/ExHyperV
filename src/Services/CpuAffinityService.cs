using ExHyperV.Models;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public class CpuAffinityService
    {
        public async Task<Guid> FindOrCreateCpuGroupAsync(List<int> selectedCores)
        {
            if (selectedCores == null || !selectedCores.Any())
            {
                return Guid.Empty;
            }

            Debug.WriteLine("==========================================================");
            Debug.WriteLine($"[CpuAffinityService] 开始处理请求，用户选择的核心: [{string.Join(",", selectedCores)}]");

            var selectedCoresSet = new HashSet<uint>(selectedCores.Select(c => (uint)c));

            var existingGroups = await GetAllCpuGroupsAsync();

            var sb = new StringBuilder();
            sb.AppendLine("[CpuAffinityService] 获取到系统中已存在的 CPU 组列表:");
            if (existingGroups == null || !existingGroups.Any())
            {
                sb.AppendLine("  -> 列表为空或获取失败。");
            }
            else
            {
                foreach (var g in existingGroups)
                {
                    sb.AppendLine($"  -> GroupId: {g.GroupId}, Cores: [{string.Join(",", g.Affinity?.LogicalProcessors ?? new List<uint>())}]");
                }
            }
            Debug.WriteLine(sb.ToString());

            if (existingGroups != null)
            {
                foreach (var group in existingGroups)
                {
                    if (group.Affinity?.LogicalProcessors != null)
                    {
                        var existingCoresSet = new HashSet<uint>(group.Affinity.LogicalProcessors);

                        Debug.WriteLine($"[CpuAffinityService] 正在比对...");
                        Debug.WriteLine($"  -> 用户选择的Set: Count={selectedCoresSet.Count}, Cores=[{string.Join(",", selectedCoresSet.OrderBy(c => c))}]");
                        Debug.WriteLine($"  -> 当前系统组的Set: Count={existingCoresSet.Count}, Cores=[{string.Join(",", existingCoresSet.OrderBy(c => c))}]");

                        if (existingCoresSet.SetEquals(selectedCoresSet))
                        {
                            Debug.WriteLine($"[CpuAffinityService] >> 匹配成功！<< 返回已存在的 GroupId: {group.GroupId}");
                            Debug.WriteLine("==========================================================\n");
                            return group.GroupId;
                        }
                        else
                        {
                            Debug.WriteLine("[CpuAffinityService] >> 不匹配。继续下一个...");
                        }
                    }
                }
            }

            Debug.WriteLine("[CpuAffinityService] 未找到任何匹配的组。准备创建新组...");

            var sortedSelectedCores = selectedCores.Select(c => (uint)c).OrderBy(c => c).ToArray();
            var newGroupId = Guid.NewGuid();

            Debug.WriteLine($"[CpuAffinityService] 创建新组，New GroupId: {newGroupId}, Cores: [{string.Join(",", sortedSelectedCores)}]");
            Debug.WriteLine("==========================================================\n");

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
            string jsonResult = null;
            try
            {
                jsonResult = await Task.Run(() => HcsManager.GetAllCpuGroupsAsJson());
                if (string.IsNullOrEmpty(jsonResult))
                {
                    return new List<HcsCpuGroupDetail>();
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var result = JsonSerializer.Deserialize<HcsQueryResult>(jsonResult, options);

                // =======================================================
                // 修正点: 根据全新的、正确的模型结构来提取数据
                // =======================================================
                return result?.Properties?
                               .FirstOrDefault()?
                               .CpuGroups ?? new List<HcsCpuGroupDetail>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CpuAffinityService] GetAllCpuGroupsAsync 发生错误: {ex.Message}");
                Debug.WriteLine($"[CpuAffinityService] 导致错误的原始JSON: {jsonResult}");
                return null;
            }
        }
    }
}