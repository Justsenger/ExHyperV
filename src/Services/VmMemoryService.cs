using ExHyperV.Models;
using ExHyperV.Tools;
using System.Diagnostics;
using System.Management;

namespace ExHyperV.Services;

public class VmMemoryService
{
    //读取虚拟机的内存配置
    public async Task<VmMemorySettings?> GetVmMemorySettingsAsync(string vmName)
    {
        try
        {
            string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'";
            var vmInstanceId = (await WmiTools.QueryAsync(vmWql, obj => obj["Name"]?.ToString())).FirstOrDefault();

            if (string.IsNullOrEmpty(vmInstanceId)) return null;

            string memWql = $"SELECT * FROM Msvm_MemorySettingData WHERE InstanceID LIKE 'Microsoft:{vmInstanceId}%' AND ResourceType = 4";

            var settingsList = await WmiTools.QueryAsync(memWql, obj => {
                var s = new VmMemorySettings();

                s.Startup = Convert.ToInt64(obj["VirtualQuantity"] ?? 0);
                s.Minimum = Convert.ToInt64(obj["Reservation"] ?? 0);
                s.Maximum = Convert.ToInt64(obj["Limit"] ?? 0);
                s.Priority = obj["Weight"] != null ? Convert.ToInt32(obj["Weight"]) / 100 : 50;
                s.DynamicMemoryEnabled = Convert.ToBoolean(obj["DynamicMemoryEnabled"] ?? false);
                s.Buffer = obj["TargetMemoryBuffer"] != null ? Convert.ToInt32(obj["TargetMemoryBuffer"]) : 20;

                s.IsPageSizeSelectionSupported = HasProperty(obj, "BackingPageSize");
                if (s.IsPageSizeSelectionSupported)
                    s.BackingPageSize = obj["BackingPageSize"] != null ? Convert.ToByte(obj["BackingPageSize"]) : (byte)0;
                else
                    s.BackingPageSize = 0;

                s.IsMemoryEncryptionSupported = HasProperty(obj, "MemoryEncryptionPolicy");
                if (s.IsMemoryEncryptionSupported)
                    s.MemoryEncryptionPolicy = obj["MemoryEncryptionPolicy"] != null ? Convert.ToByte(obj["MemoryEncryptionPolicy"]) : (byte)0;
                else
                    s.MemoryEncryptionPolicy = 0;

                return s;
            });

            return settingsList.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取内存配置时发生严重异常: {ex}");
            return null;
        }
    }

    public async Task<(bool Success, string Message)> SetVmMemorySettingsAsync(string vmName, VmMemorySettings newSettings)
    {
        return await Task.Run(async () =>
        {
            try
            {
                string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'";
                var vmList = await WmiTools.QueryAsync(vmWql, obj => obj["Name"]?.ToString());
                string vmId = vmList.FirstOrDefault();
                if (string.IsNullOrEmpty(vmId)) return (false, "找不到虚拟机实例。");

                string memWql = $"SELECT * FROM Msvm_MemorySettingData WHERE InstanceID LIKE 'Microsoft:{vmId}%' AND ResourceType = 4";

                // 使用原生 Searcher 获取对象以便修改
                using var searcher = new ManagementObjectSearcher(WmiTools.HyperVScope, memWql);
                using var collection = searcher.Get();
                using var memObj = collection.Cast<ManagementObject>().FirstOrDefault();

                if (memObj == null) return (false, "无法定位内存配置对象。");

                ApplyMemorySettingsToWmiObject(memObj, newSettings);

                string xml = memObj.GetText(TextFormat.CimDtd20);

                string serviceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
                var parameters = new Dictionary<string, object>
                {
                    { "ResourceSettings", new string[] { xml } }
                };

                bool success = await WmiTools.ExecuteMethodAsync(serviceWql, "ModifyResourceSettings", parameters);

                return success
                    ? (true, "内存设置已应用。")
                    : (false, "Hyper-V 任务执行失败。");
            }
            catch (Exception ex)
            {
                return (false, $"高级设置应用异常: {ex.Message}");
            }
        });
    }

    private void ApplyMemorySettingsToWmiObject(ManagementObject memData, VmMemorySettings memorySettings)
    {
        long alignment = 1;

        if (HasProperty(memData, "BackingPageSize"))
        {
            byte pageSize = memorySettings.BackingPageSize;
            memData["BackingPageSize"] = pageSize;

            if (pageSize == 1) alignment = 2;
            else if (pageSize == 2) alignment = 1024;
        }

        long originalStartup = memorySettings.Startup;
        long alignedStartup = (originalStartup + alignment - 1) / alignment * alignment;
        memData["VirtualQuantity"] = (ulong)alignedStartup;

        memData["Weight"] = (uint)(memorySettings.Priority * 100);

        if (HasProperty(memData, "MemoryEncryptionPolicy"))
        {
            memData["MemoryEncryptionPolicy"] = (byte)memorySettings.MemoryEncryptionPolicy;
        }

        if (HasProperty(memData, "MaxMemoryBlocksPerNumaNode"))
        {
            if (alignment > 1)
            {
                long currentNumaNodeSize = Convert.ToInt64(memData["MaxMemoryBlocksPerNumaNode"]);
                long alignedNumaNodeSize = (currentNumaNodeSize + alignment - 1) / alignment * alignment;
                memData["MaxMemoryBlocksPerNumaNode"] = (ulong)alignedNumaNodeSize;
            }
        }

        if (memorySettings.BackingPageSize > 0)
        {
            memData["DynamicMemoryEnabled"] = false;
            memData["Reservation"] = (ulong)alignedStartup;
            memData["Limit"] = (ulong)alignedStartup;
        }
        else
        {
            memData["DynamicMemoryEnabled"] = memorySettings.DynamicMemoryEnabled;
            if (memorySettings.DynamicMemoryEnabled)
            {
                memData["Reservation"] = (ulong)memorySettings.Minimum;
                memData["Limit"] = (ulong)memorySettings.Maximum;
                if (HasProperty(memData, "TargetMemoryBuffer"))
                    memData["TargetMemoryBuffer"] = (uint)memorySettings.Buffer;
            }
            else
            {
                memData["Reservation"] = (ulong)originalStartup;
                memData["Limit"] = (ulong)originalStartup;
            }
        }
    }

    private bool HasProperty(ManagementObject obj, string propName) =>
        obj.Properties.Cast<PropertyData>().Any(p => p.Name.Equals(propName, StringComparison.OrdinalIgnoreCase));
}