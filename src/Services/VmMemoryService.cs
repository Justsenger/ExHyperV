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
            // 获取虚拟机内部实例 ID
            string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'";
            var vmInstanceId = (await WmiTools.QueryAsync(vmWql, obj => obj["Name"]?.ToString())).FirstOrDefault();

            if (string.IsNullOrEmpty(vmInstanceId)) return null;

            // 查询对应的内存设置数据 (ResourceType 4 代表内存)
            string memWql = $"SELECT * FROM Msvm_MemorySettingData WHERE InstanceID LIKE 'Microsoft:{vmInstanceId}%' AND ResourceType = 4";

            var settingsList = await WmiTools.QueryAsync(memWql, obj => {
                var s = new VmMemorySettings();

                // 基础配额设置
                s.Startup = Convert.ToInt64(obj["VirtualQuantity"] ?? 0);
                s.Minimum = Convert.ToInt64(obj["Reservation"] ?? 0);
                s.Maximum = Convert.ToInt64(obj["Limit"] ?? 0);
                s.Priority = obj["Weight"] != null ? Convert.ToInt32(obj["Weight"]) / 100 : 50;
                s.DynamicMemoryEnabled = Convert.ToBoolean(obj["DynamicMemoryEnabled"] ?? false);
                s.Buffer = obj["TargetMemoryBuffer"] != null ? Convert.ToInt32(obj["TargetMemoryBuffer"]) : 20;

                // 页面对齐与加密策略
                s.BackingPageSize = GetNullableByteProperty(obj, "BackingPageSize");
                s.MemoryEncryptionPolicy = GetNullableByteProperty(obj, "MemoryEncryptionPolicy");

                // 性能优化选项
                s.EnableColdHint = GetNullableValueProperty<bool>(obj, "EnableColdHint");
                s.EnableHotHint = GetNullableValueProperty<bool>(obj, "EnableHotHint");
                s.EnableEpf = GetNullableValueProperty<bool>(obj, "EnableEpf");
                s.EnablePrivateCompressionStore = GetNullableValueProperty<bool>(obj, "EnablePrivateCompressionStore");

                // NUMA 拓扑
                s.MaxMemoryBlocksPerNumaNode = GetNullableValueProperty<ulong>(obj, "MaxMemoryBlocksPerNumaNode");

                // 高级/后端设置
                s.BackingType = GetNullableByteProperty(obj, "BackingType");
                s.DynMemOperationAlignment = GetNullableValueProperty<uint>(obj, "DynMemOperationAlignment");
                s.MemoryAccessTrackingPolicy = GetNullableByteProperty(obj, "MemoryAccessTrackingPolicy");
                s.MemoryAccessTrackingState = GetNullableByteProperty(obj, "MemoryAccessTrackingState");

                // Intel SGX 安全设置
                s.SgxEnabled = GetNullableValueProperty<bool>(obj, "SgxEnabled");
                s.SgxSize = GetNullableValueProperty<ulong>(obj, "SgxSize") ?? 0;
                s.SgxLaunchControlMode = GetNullableValueProperty<uint>(obj, "SgxLaunchControlMode");
                s.SgxLaunchControlDefault = obj["SgxLaunchControlDefault"]?.ToString();

                // 硬件虚拟化扩展
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

                // 应用参数到管理对象
                ApplyMemorySettingsToWmiObject(memObj, newSettings, isVmRunning);

                // 提交修改
                string xml = memObj.GetText(TextFormat.CimDtd20);
                string serviceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
                var parameters = new Dictionary<string, object> { { "ResourceSettings", new string[] { xml } } };

                var result = await WmiTools.ExecuteMethodAsync(serviceWql, "ModifyResourceSettings", parameters);

                if (!result.Success)
                    return (false, string.Format(Properties.Resources.VmMemory_ModFailed, result.Message));

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

        // 设置对齐基数（仅关机时允许修改页大小）
        if (memorySettings.BackingPageSize.HasValue && HasProperty(memData, "BackingPageSize"))
        {
            byte pageSize = memorySettings.BackingPageSize.Value;
            if (!isVmRunning) memData["BackingPageSize"] = pageSize;

            if (pageSize == 1) alignment = 2;         // 2MB 页
            else if (pageSize == 2) alignment = 1024; // 1GB 页
        }

        // 辅助对齐工具
        ulong Align(long value, long alg)
        {
            if (value <= 0) return (ulong)alg;
            if (value > (long.MaxValue - alg)) return (ulong)value;
            return (ulong)((value + alg - 1) / alg * alg);
        }

        // 应用启动内存与权重
        ulong alignedStartup = Align(memorySettings.Startup, alignment);
        memData["VirtualQuantity"] = alignedStartup;
        memData["Weight"] = (uint)(memorySettings.Priority * 100);

        // 关机状态下可修改的配置
        if (!isVmRunning)
        {
            if (memorySettings.MemoryEncryptionPolicy.HasValue && HasProperty(memData, "MemoryEncryptionPolicy"))
                memData["MemoryEncryptionPolicy"] = memorySettings.MemoryEncryptionPolicy.Value;

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
                // 静态内存模式下，强制同步 Min/Max
                memData["Reservation"] = alignedStartup;
                memData["Limit"] = alignedStartup;
            }

            // 内存优化提示特性
            if (memorySettings.EnableColdHint.HasValue && HasProperty(memData, "EnableColdHint"))
            {
                memData["EnableColdHint"] = memorySettings.EnableColdHint.Value;
                // 强制同步：只要 ColdHint 有值，HotHint 就跟着走
                if (HasProperty(memData, "EnableHotHint"))
                {
                    memData["EnableHotHint"] = memorySettings.EnableColdHint.Value;
                }
            }

            if (memorySettings.EnableHotHint.HasValue && HasProperty(memData, "EnableHotHint"))
                memData["EnableHotHint"] = memorySettings.EnableHotHint.Value;

            if (memorySettings.EnableEpf.HasValue && HasProperty(memData, "EnableEpf"))
                memData["EnableEpf"] = memorySettings.EnableEpf.Value;

            if (memorySettings.EnablePrivateCompressionStore.HasValue && HasProperty(memData, "EnablePrivateCompressionStore"))
                memData["EnablePrivateCompressionStore"] = memorySettings.EnablePrivateCompressionStore.Value;

            // NUMA 节点对齐修正（防止 6962 错误）
            if (memorySettings.MaxMemoryBlocksPerNumaNode.HasValue && HasProperty(memData, "MaxMemoryBlocksPerNumaNode"))
            {
                memData["MaxMemoryBlocksPerNumaNode"] = memorySettings.MaxMemoryBlocksPerNumaNode.Value;
            }
            else if (memorySettings.BackingPageSize > 0 && HasProperty(memData, "MaxMemoryBlocksPerNumaNode"))
            {
                ulong currentMaxNuma = (ulong)memData["MaxMemoryBlocksPerNumaNode"];
                ulong correctedMaxNuma = (currentMaxNuma / (ulong)alignment) * (ulong)alignment;
                if (correctedMaxNuma == 0) correctedMaxNuma = (ulong)alignment;
                memData["MaxMemoryBlocksPerNumaNode"] = correctedMaxNuma;
            }

            // 实验性后端参数
            if (memorySettings.BackingType.HasValue && HasProperty(memData, "BackingType"))
                memData["BackingType"] = (byte)memorySettings.BackingType.Value;

            if (memorySettings.DynMemOperationAlignment.HasValue && HasProperty(memData, "DynMemOperationAlignment"))
                memData["DynMemOperationAlignment"] = (uint)memorySettings.DynMemOperationAlignment.Value;

            if (memorySettings.MemoryAccessTrackingPolicy.HasValue && HasProperty(memData, "MemoryAccessTrackingPolicy"))
                memData["MemoryAccessTrackingPolicy"] = (byte)memorySettings.MemoryAccessTrackingPolicy.Value;

            if (memorySettings.MemoryAccessTrackingState.HasValue && HasProperty(memData, "MemoryAccessTrackingState"))
                memData["MemoryAccessTrackingState"] = (byte)memorySettings.MemoryAccessTrackingState.Value;

            // SGX 安全飞地设置
            if (memorySettings.SgxEnabled.HasValue && HasProperty(memData, "SgxEnabled"))
                memData["SgxEnabled"] = memorySettings.SgxEnabled.Value;

            if (memorySettings.SgxEnabled == true && memorySettings.SgxSize.HasValue && HasProperty(memData, "SgxSize"))
            {
                ulong sgxMb = (ulong)memorySettings.SgxSize.Value;
                if (sgxMb < 2) sgxMb = 2; // 最小 2MB 对齐
                sgxMb = (sgxMb / 2) * 2;
                memData["SgxSize"] = sgxMb;
            }

            if (memorySettings.SgxLaunchControlMode.HasValue && HasProperty(memData, "SgxLaunchControlMode"))
                memData["SgxLaunchControlMode"] = (uint)memorySettings.SgxLaunchControlMode.Value;

            if (!string.IsNullOrEmpty(memorySettings.SgxLaunchControlDefault) && HasProperty(memData, "SgxLaunchControlDefault"))
                memData["SgxLaunchControlDefault"] = memorySettings.SgxLaunchControlDefault;

            // 硬件加速扩展 (GPA/CXL)
            if (memorySettings.EnableGpaPinning.HasValue && HasProperty(memData, "EnableGpaPinning"))
                memData["EnableGpaPinning"] = memorySettings.EnableGpaPinning.Value;

            if (memorySettings.CxlEnabled.HasValue && HasProperty(memData, "CxlEnabled"))
                memData["CxlEnabled"] = memorySettings.CxlEnabled.Value;
        }
        else
        {
            // 运行状态下的热调整逻辑
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