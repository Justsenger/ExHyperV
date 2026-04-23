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

                // --- 实验性高级功能 ---
                s.BackingType = GetNullableByteProperty(obj, "BackingType");
                s.DynMemOperationAlignment = GetNullableValueProperty<uint>(obj, "DynMemOperationAlignment");
                s.MemoryAccessTrackingPolicy = GetNullableByteProperty(obj, "MemoryAccessTrackingPolicy");
                s.MemoryAccessTrackingState = GetNullableByteProperty(obj, "MemoryAccessTrackingState");
                s.SgxEnabled = GetNullableValueProperty<bool>(obj, "SgxEnabled");;
                s.SgxSize = GetNullableValueProperty<ulong>(obj, "SgxSize") ?? 0;
                s.SgxLaunchControlMode = GetNullableValueProperty<uint>(obj, "SgxLaunchControlMode");
                s.EnableGpaPinning = GetNullableValueProperty<bool>(obj, "EnableGpaPinning");
                s.CxlEnabled = GetNullableValueProperty<bool>(obj, "CxlEnabled");

                return s;
            });

            return settingsList.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.VmMemoryService_1, ex));
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

            // 实验功能


            // 1. 内存后端类型 (物理/虚拟/混合) - 强制 byte 类型
            if (memorySettings.BackingType.HasValue && HasProperty(memData, "BackingType"))
                memData["BackingType"] = (byte)memorySettings.BackingType.Value;

            // 2. 动态内存操作对齐限制 - 强制 uint 类型
            if (memorySettings.DynMemOperationAlignment.HasValue && HasProperty(memData, "DynMemOperationAlignment"))
                memData["DynMemOperationAlignment"] = (uint)memorySettings.DynMemOperationAlignment.Value;

            // 3. 内存访问跟踪精度与状态
            if (memorySettings.MemoryAccessTrackingPolicy.HasValue && HasProperty(memData, "MemoryAccessTrackingPolicy"))
                memData["MemoryAccessTrackingPolicy"] = (byte)memorySettings.MemoryAccessTrackingPolicy.Value;

            if (memorySettings.MemoryAccessTrackingState.HasValue && HasProperty(memData, "MemoryAccessTrackingState"))
                memData["MemoryAccessTrackingState"] = (byte)memorySettings.MemoryAccessTrackingState.Value;

            // 4. Intel SGX 安全飞地 (核心修正：直接使用 MB 单位，严禁乘以 1024)
            if (memorySettings.SgxEnabled.HasValue && HasProperty(memData, "SgxEnabled"))
                memData["SgxEnabled"] = memorySettings.SgxEnabled.Value;

            if (memorySettings.SgxEnabled == true && memorySettings.SgxSize.HasValue && HasProperty(memData, "SgxSize"))
            {
                // 直接取 UI 上的数字，单位已经是 MB
                ulong sgxMb = (ulong)memorySettings.SgxSize.Value;

                // 2MB 对齐校验 (Hyper-V 最小分配单位是 2MB)
                if (sgxMb < 2) sgxMb = 2;
                sgxMb = (sgxMb / 2) * 2;

                // 写入 WMI (注意：这里绝对不能再乘以 1024 * 1024 了！)
                memData["SgxSize"] = sgxMb;
            }

            if (memorySettings.SgxLaunchControlMode.HasValue && HasProperty(memData, "SgxLaunchControlMode"))
                memData["SgxLaunchControlMode"] = (uint)memorySettings.SgxLaunchControlMode.Value;

            // 5. 预览版功能 (GPA Pinning & CXL)
            if (memorySettings.EnableGpaPinning.HasValue && HasProperty(memData, "EnableGpaPinning"))
                memData["EnableGpaPinning"] = memorySettings.EnableGpaPinning.Value;

            if (memorySettings.CxlEnabled.HasValue && HasProperty(memData, "CxlEnabled"))
                memData["CxlEnabled"] = memorySettings.CxlEnabled.Value;
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
    private static T? GetNullableValueProperty<T>(ManagementObject obj, string propName) where T : struct
    {
        if (!HasProperty(obj, propName)) return null;
        var val = obj[propName];
        if (val == null) return null;
        try
        {
            return (T)Convert.ChangeType(val, typeof(T));
        }
        catch { return null; }
    }
}