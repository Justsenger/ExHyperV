using ExHyperV.Models;
using ExHyperV.Tools;
using System.Diagnostics;
using System.Management;

namespace ExHyperV.Services;

public class VmMemoryService
{
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

                s.BackingPageSize = GetNullableByteProperty(obj, "BackingPageSize");
                s.MemoryEncryptionPolicy = GetNullableByteProperty(obj, "MemoryEncryptionPolicy");

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

    public async Task<(bool Success, string Message)> SetVmMemorySettingsAsync(string vmName, VmMemorySettings newSettings, bool isVmRunning)
    {
        return await Task.Run(async () =>
        {
            try
            {
                string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'";
                var vmList = await WmiTools.QueryAsync(vmWql, obj => obj["Name"]?.ToString());
                string vmId = vmList.FirstOrDefault();
                if (string.IsNullOrEmpty(vmId)) return (false, Properties.Resources.Error_Memory_VmNotFound);

                string memWql = $"SELECT * FROM Msvm_MemorySettingData WHERE InstanceID LIKE 'Microsoft:{vmId}%' AND ResourceType = 4";

                using var searcher = new ManagementObjectSearcher(WmiTools.HyperVScope, memWql);
                using var collection = searcher.Get();
                using var memObj = collection.Cast<ManagementObject>().FirstOrDefault();

                if (memObj == null) return (false, Properties.Resources.Error_Memory_ObjNotFound);

                ApplyMemorySettingsToWmiObject(memObj, newSettings, isVmRunning);

                string xml = memObj.GetText(TextFormat.CimDtd20);

                string serviceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
                var parameters = new Dictionary<string, object>
            {
                { "ResourceSettings", new string[] { xml } }
            };

                var result = await WmiTools.ExecuteMethodAsync(serviceWql, "ModifyResourceSettings", parameters);

                if (!result.Success)
                {
                    return (false, string.Format(Properties.Resources.VmMemory_ModFailed, result.Message));
                }

                return (true, Properties.Resources.Msg_Memory_Applied);
            }
            catch (Exception ex)
            {
                return (false, string.Format(Properties.Resources.VmMemory_AdvSetException, ex.Message));
            }
        });
    }
    private void ApplyMemorySettingsToWmiObject(ManagementObject memData, VmMemorySettings memorySettings, bool isVmRunning)
    {
        long alignment = 1;

        // 1. 确定对齐基数 (只有在关机状态下才允许修改 BackingPageSize)
        if (memorySettings.BackingPageSize.HasValue && HasProperty(memData, "BackingPageSize"))
        {
            byte pageSize = memorySettings.BackingPageSize.Value;
            if (!isVmRunning)
            {
                memData["BackingPageSize"] = pageSize;
            }

            if (pageSize == 1) alignment = 2;         // 2MB 模式
            else if (pageSize == 2) alignment = 1024; // 1GB 巨页模式
        }

        // 安全对齐函数：增加溢出保护 (防止 long.MaxValue 溢出)
        ulong Align(long value, long alg)
        {
            if (value <= 0) return (ulong)alg;
            if (value > (long.MaxValue - alg)) return (ulong)value; // 接近上限不再处理
            return (ulong)((value + alg - 1) / alg * alg);
        }

        // 2. 计算并设置启动内存 (VirtualQuantity)
        ulong alignedStartup = Align(memorySettings.Startup, alignment);
        memData["VirtualQuantity"] = alignedStartup;
        memData["Weight"] = (uint)(memorySettings.Priority * 100);

        // 3. 处理关机状态下的独占修改 (代号：安全护卫)
        if (!isVmRunning)
        {
            // 处理加密策略
            if (memorySettings.MemoryEncryptionPolicy.HasValue && HasProperty(memData, "MemoryEncryptionPolicy"))
            {
                memData["MemoryEncryptionPolicy"] = memorySettings.MemoryEncryptionPolicy.Value;
            }

            // --- 核心修复点：移除人为拦截，尊重用户意图 ---
            // 逻辑：直接透传用户开关。如果 WMI 真的不接受（如某些特殊环境），ModifyResourceSettings 会报错。
            memData["DynamicMemoryEnabled"] = memorySettings.DynamicMemoryEnabled;

            if (memorySettings.DynamicMemoryEnabled)
            {
                memData["Reservation"] = Align(memorySettings.Minimum, alignment);
                memData["Limit"] = Align(memorySettings.Maximum, alignment);
                if (HasProperty(memData, "TargetMemoryBuffer"))
                    memData["TargetMemoryBuffer"] = (uint)memorySettings.Buffer;
            }
            else
            {
                // 静态模式：Min/Max 强制对齐 Startup
                memData["Reservation"] = alignedStartup;
                memData["Limit"] = alignedStartup;
            }

            // --- 针对 NUMA 对齐的增强修复 (解决 6962 错误) ---
            // 恢复 Version A 逻辑：只要开启了大页(1)或巨页(2)，都执行 NUMA 对齐修正
            if (memorySettings.BackingPageSize > 0 && HasProperty(memData, "MaxMemoryBlocksPerNumaNode"))
            {
                ulong currentMaxNuma = (ulong)memData["MaxMemoryBlocksPerNumaNode"];
                // 执行向下对齐，确保 NUMA 节点内存块是 alignment 的整数倍
                ulong correctedMaxNuma = (currentMaxNuma / (ulong)alignment) * (ulong)alignment;
                if (correctedMaxNuma == 0) correctedMaxNuma = (ulong)alignment;
                memData["MaxMemoryBlocksPerNumaNode"] = correctedMaxNuma;
            }
        }
        else
        {
            // 4. 运行时热调整 (仅允许在已开启 DM 的情况下修改数值)
            if (memorySettings.DynamicMemoryEnabled)
            {
                memData["Reservation"] = Align(memorySettings.Minimum, alignment);
                memData["Limit"] = Align(memorySettings.Maximum, alignment);
                if (HasProperty(memData, "TargetMemoryBuffer"))
                    memData["TargetMemoryBuffer"] = (uint)memorySettings.Buffer;
            }
        }
    }
    private static bool HasProperty(ManagementObject obj, string propName) =>
        obj.Properties.Cast<PropertyData>().Any(p => p.Name.Equals(propName, StringComparison.OrdinalIgnoreCase));

    private static byte? GetNullableByteProperty(ManagementObject obj, string propName)
    {
        if (!HasProperty(obj, propName)) return null;
        var val = obj[propName];
        return val == null ? null : Convert.ToByte(val);
    }
}