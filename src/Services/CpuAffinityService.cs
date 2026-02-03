using ExHyperV.Models;
using ExHyperV.Tools;
using System.Text.Json;

namespace ExHyperV.Services
{
    public class CpuAffinityService
    {
        public async Task<List<int>> GetCpuAffinityAsync(Guid vmId)
        {
            if (vmId == Guid.Empty) return new List<int>();

            try
            {
                string json = await Task.Run(() => HcsManager.GetVmCpuGroupAsJson(vmId));
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("CpuGroupId", out var prop))
                {
                    if (Guid.TryParse(prop.GetString(), out Guid groupId) && groupId != Guid.Empty)
                    {
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
        public async Task<bool> SetCpuAffinityAsync(Guid vmId, List<int> coreIndices)
        {
            if (vmId == Guid.Empty) return false;

            try
            {
                Guid targetGroupId = Guid.Empty;
                if (coreIndices != null && coreIndices.Count > 0)
                {
                    targetGroupId = await FindOrCreateCpuGroupAsync(coreIndices);
                    if (targetGroupId == Guid.Empty) return false;
                }
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