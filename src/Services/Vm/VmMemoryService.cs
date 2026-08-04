using ExHyperV.Tools;
using ExHyperV.Models;
using System.Diagnostics;
using System.Management;

namespace ExHyperV.Services;

public static class VmMemoryService
{
    public static async Task<VmMemorySettings?> GetVmMemorySettingsAsync(string vmName)
    {
        try
        {
            string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'";
            var vmResponse = await WmiApi.QueryFirstAsync(vmWql, obj => obj["Name"]?.ToString());

            if (!vmResponse.Success || vmResponse.IsEmpty || string.IsNullOrEmpty(vmResponse.Data))
                return null;

            string vmInstanceId = vmResponse.Data;
            string memWql = $"SELECT * FROM Msvm_MemorySettingData WHERE InstanceID LIKE 'Microsoft:{vmInstanceId}%' AND ResourceType = 4";

            var memResponse = await WmiApi.QueryFirstAsync(memWql, obj =>
            {
                var s = new VmMemorySettings();

                s.Startup = Convert.ToInt64(obj["VirtualQuantity"] ?? 0);
                s.Minimum = Convert.ToInt64(obj["Reservation"] ?? 0);
                s.Maximum = Convert.ToInt64(obj["Limit"] ?? 0);
                s.Priority = obj["Weight"] != null ? Convert.ToInt32(obj["Weight"]) / 100 : 50;
                s.DynamicMemoryEnabled = Convert.ToBoolean(obj["DynamicMemoryEnabled"] ?? false);
                s.Buffer = obj["TargetMemoryBuffer"] != null ? Convert.ToInt32(obj["TargetMemoryBuffer"]) : 20;

                bool supportsBackingPageSize = obj.HasProperty("BackingPageSize");
                s.ConfigurePageSizeSupport(supportsBackingPageSize);

                // VMMS 内部只有一个页面大小值。新系统直接使用 0/1/2；旧系统只能通过
                // HugePagesEnabled 表达 2MB(false) 与 1GB(true)，在 WMI 边界转换为统一列表值。
                s.BackingPageSize = supportsBackingPageSize
                    ? obj.TryGetByte("BackingPageSize") ?? 1
                    : obj.TryGet<bool>("HugePagesEnabled") == true ? (byte)2 : (byte)1;
                s.MemoryEncryptionPolicy = obj.TryGetByte("MemoryEncryptionPolicy");

                s.EnableColdHint = obj.TryGet<bool>("EnableColdHint");
                s.EnableHotHint = obj.TryGet<bool>("EnableHotHint");
                s.EnableEpf = obj.TryGet<bool>("EnableEpf");
                s.EnablePrivateCompressionStore = obj.TryGet<bool>("EnablePrivateCompressionStore");

                s.MaxMemoryBlocksPerNumaNode = obj.TryGet<ulong>("MaxMemoryBlocksPerNumaNode");

                s.BackingType = obj.TryGetByte("BackingType");
                s.DynMemOperationAlignment = obj.TryGet<uint>("DynMemOperationAlignment");
                s.MemoryAccessTrackingPolicy = obj.TryGetByte("MemoryAccessTrackingPolicy");
                s.MemoryAccessTrackingState = obj.TryGetByte("MemoryAccessTrackingState");

                s.SgxEnabled = obj.TryGet<bool>("SgxEnabled");
                s.SgxSize = obj.TryGet<ulong>("SgxSize") ?? 0;
                s.SgxLaunchControlMode = obj.TryGet<uint>("SgxLaunchControlMode");
                s.SgxLaunchControlDefault = obj.TryGetString("SgxLaunchControlDefault");

                s.EnableGpaPinning = obj.TryGet<bool>("EnableGpaPinning");
                s.CxlEnabled = obj.TryGet<bool>("CxlEnabled");

                return s;
            });

            return memResponse.HasData ? memResponse.Data : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(string.Format(Properties.Resources.VmMemoryService_ErrReadConfig, ex));
            return null;
        }
    }

    public static async Task<(bool Success, string Message)> SetVmMemorySettingsAsync(
        string vmName, VmMemorySettings newSettings, bool isVmRunning)
    {
        try
        {
            string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'";
            var vmResponse = await WmiApi.QueryFirstAsync(vmWql, obj => obj["Name"]?.ToString());

            if (!vmResponse.Success || vmResponse.IsEmpty || string.IsNullOrEmpty(vmResponse.Data))
                return (false, Properties.Resources.Error_Memory_VmNotFound);

            string vmId = vmResponse.Data;
            string memWql = $"SELECT * FROM Msvm_MemorySettingData WHERE InstanceID LIKE 'Microsoft:{vmId}%' AND ResourceType = 4";

            // 动态内存与 vNUMA 互斥（对齐 PS Set-VMMemory）：启用动态内存前先关 vNUMA。仅离线可改。
            if (!isVmRunning && newSettings.DynamicMemoryEnabled)
            {
                var numaOff = await SetVirtualNumaEnabledAsync(vmName, false);
                if (!numaOff.Success)
                    return (false, string.Format(Properties.Resources.VmMemory_ModFailed, numaOff.Error));
            }

            var result = await WmiApi.WithObjectAsync(
                wql: memWql,
                modifier: obj => ApplyMemorySettingsToWmiObject(obj, newSettings, isVmRunning),
                submitMethod: "ModifyResourceSettings",
                submitParamName: "ResourceSettings",
                wrapInArray: true);

            if (!result.Success)
                return (false, string.Format(Properties.Resources.VmMemory_ModFailed, result.Error));

            // 静态内存：内存改完后再开 vNUMA（与 PS 顺序一致）。
            if (!isVmRunning && !newSettings.DynamicMemoryEnabled)
                await SetVirtualNumaEnabledAsync(vmName, true);

            return (true, Properties.Resources.Msg_Memory_Applied);
        }
        catch (Exception ex)
        {
            return (false, string.Format(Properties.Resources.VmMemory_AdvSetException, ex.Message));
        }
    }

    /// <summary>切换 VM 的 VirtualNumaEnabled（vNUMA 与动态内存互斥，对齐 Set-VMMemory；仅离线可改）。</summary>
    private static Task<ApiResponse> SetVirtualNumaEnabledAsync(string vmName, bool enabled)
        => WmiApi.WithObjectAsync(
            wql: $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
            modifier: obj => { if (obj.HasProperty("VirtualNumaEnabled")) obj["VirtualNumaEnabled"] = enabled; },
            submitMethod: "ModifySystemSettings",
            submitParamName: "SystemSettings",
            wrapInArray: false);

    // ── 业务逻辑（不改动）────────────────────────────────────────

    private static void ApplyMemorySettingsToWmiObject(ManagementObject memData, VmMemorySettings memorySettings, bool isVmRunning)
    {
        // 默认 2MB 对齐（对齐 PS Set-VMMemory.ValidateAlignment；巨页在下方改为 1024MB）
        long alignment = 2;

        if (memorySettings.BackingPageSize.HasValue)
        {
            byte pageSize = memorySettings.BackingPageSize.Value;
            if (!isVmRunning)
            {
                if (memData.HasProperty("BackingPageSize"))
                {
                    memData["BackingPageSize"] = pageSize;
                }
                else
                {
                    // 旧版 WMI 没有三档枚举：大页(1)映射为 false，巨页(2)映射为 true。
                    // 旧版列表不会提供小页(0)，这里仍按非巨页处理以保证兼容。
                    memData.TrySet("HugePagesEnabled", (bool?)(pageSize == 2));
                }
            }

            if (pageSize == 1) alignment = 2;
            else if (pageSize == 2) alignment = 1024;
        }

        ulong Align(long value, long alg)
        {
            if (value <= 0) return (ulong)alg;
            if (value > (long.MaxValue - alg)) return (ulong)value;
            return (ulong)((value + alg - 1) / alg * alg);
        }

        ulong alignedStartup = Align(memorySettings.Startup, alignment);
        memData["VirtualQuantity"] = alignedStartup;
        memData["Weight"] = (uint)(memorySettings.Priority * 100);

        if (!isVmRunning)
        {
            memData.TrySet("MemoryEncryptionPolicy", memorySettings.MemoryEncryptionPolicy);

            memData["DynamicMemoryEnabled"] = memorySettings.DynamicMemoryEnabled;

            if (memorySettings.DynamicMemoryEnabled)
            {
                memData["Reservation"] = Align(memorySettings.Minimum, alignment);
                memData["Limit"] = Align(memorySettings.Maximum, alignment);
                memData.TrySetAlways("TargetMemoryBuffer", (uint)memorySettings.Buffer);
            }
            else
            {
                memData["Reservation"] = alignedStartup;
                memData["Limit"] = alignedStartup;
            }

            // 冷页与热页提示是两个独立能力：分别按 UI 中的状态写回，
            // null 表示当前 Hyper-V 版本不支持该属性，TrySet 会跳过。
            memData.TrySet("EnableColdHint", memorySettings.EnableColdHint);
            memData.TrySet("EnableHotHint", memorySettings.EnableHotHint);
            memData.TrySet("EnableEpf", memorySettings.EnableEpf);
            memData.TrySet("EnablePrivateCompressionStore", memorySettings.EnablePrivateCompressionStore);

            // MaxMemoryBlocksPerNumaNode 实为"每 vNUMA 节点最大内存(MB)"：须 ≥32 且按 2MB 对齐
            // （开巨页/大页后端时改按页粒度 alignment 对齐），否则 Hyper-V 拒（"未正确对齐"/"最小 32 MB"）。
            // 无论用户是否显式设、无论页大小，这里统一向下取整到合法值，保证任意输入都能落地。
            bool needBlockAlign = memorySettings.BackingPageSize > 0;
            ulong blockAlign = needBlockAlign ? (ulong)alignment : 2;
            if (memorySettings.MaxMemoryBlocksPerNumaNode.HasValue)
            {
                ulong val = (memorySettings.MaxMemoryBlocksPerNumaNode.Value / blockAlign) * blockAlign;
                if (val < 32) val = ((32 + blockAlign - 1) / blockAlign) * blockAlign;
                memData.TrySet("MaxMemoryBlocksPerNumaNode", (ulong?)val);
            }
            else if (needBlockAlign && memData.HasProperty("MaxMemoryBlocksPerNumaNode")
                     && memData["MaxMemoryBlocksPerNumaNode"] != null)
            {
                ulong current = (ulong)memData["MaxMemoryBlocksPerNumaNode"];
                ulong corrected = (current / blockAlign) * blockAlign;
                if (corrected < 32) corrected = ((32 + blockAlign - 1) / blockAlign) * blockAlign;
                memData["MaxMemoryBlocksPerNumaNode"] = corrected;
            }

            memData.TrySet("BackingType", memorySettings.BackingType);
            memData.TrySet("DynMemOperationAlignment", memorySettings.DynMemOperationAlignment);
            memData.TrySet("MemoryAccessTrackingPolicy", memorySettings.MemoryAccessTrackingPolicy);
            memData.TrySet("MemoryAccessTrackingState", memorySettings.MemoryAccessTrackingState);

            memData.TrySet("SgxEnabled", memorySettings.SgxEnabled);
            if (memorySettings.SgxEnabled == true && memorySettings.SgxSize.HasValue)
            {
                ulong sgxMb = (ulong)memorySettings.SgxSize.Value;
                if (sgxMb < 2) sgxMb = 2;
                sgxMb = (sgxMb / 2) * 2;
                memData.TrySetAlways("SgxSize", sgxMb);
            }
            memData.TrySet("SgxLaunchControlMode", memorySettings.SgxLaunchControlMode);
            memData.TrySet("SgxLaunchControlDefault", memorySettings.SgxLaunchControlDefault);

            memData.TrySet("EnableGpaPinning", memorySettings.EnableGpaPinning);
            memData.TrySet("CxlEnabled", memorySettings.CxlEnabled);
        }
        else
        {
            if (memorySettings.DynamicMemoryEnabled)
            {
                memData["Reservation"] = Align(memorySettings.Minimum, alignment);
                memData["Limit"] = Align(memorySettings.Maximum, alignment);
                memData.TrySetAlways("TargetMemoryBuffer", (uint)memorySettings.Buffer);
            }
        }
    }
}
