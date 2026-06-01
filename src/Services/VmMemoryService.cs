using ExHyperV.Api;
using ExHyperV.Models;
using System.Diagnostics;
using System.Management;

namespace ExHyperV.Services;

public class VmMemoryService
{
    public async Task<VmMemorySettings?> GetVmMemorySettingsAsync(string vmName)
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

                s.BackingPageSize = obj.TryGetByte("BackingPageSize");
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

    public async Task<(bool Success, string Message)> SetVmMemorySettingsAsync(
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

            var result = await WmiApi.WithObjectAsync(
                wql: memWql,
                modifier: obj => ApplyMemorySettingsToWmiObject(obj, newSettings, isVmRunning),
                submitMethod: "ModifyResourceSettings",
                submitParamName: "ResourceSettings",
                wrapInArray: true);

            if (!result.Success)
                return (false, string.Format(Properties.Resources.VmMemory_ModFailed, result.Error));

            return (true, Properties.Resources.Msg_Memory_Applied);
        }
        catch (Exception ex)
        {
            return (false, string.Format(Properties.Resources.VmMemory_AdvSetException, ex.Message));
        }
    }

    // ── 业务逻辑（不改动）────────────────────────────────────────

    private void ApplyMemorySettingsToWmiObject(ManagementObject memData, VmMemorySettings memorySettings, bool isVmRunning)
    {
        long alignment = 1;

        if (memorySettings.BackingPageSize.HasValue && memData.HasProperty("BackingPageSize"))
        {
            byte pageSize = memorySettings.BackingPageSize.Value;
            if (!isVmRunning) memData["BackingPageSize"] = pageSize;

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

            // ColdHint 和 HotHint 强制同步
            if (memorySettings.EnableColdHint.HasValue && memData.HasProperty("EnableColdHint"))
            {
                memData["EnableColdHint"] = memorySettings.EnableColdHint.Value;
                memData.TrySetAlways("EnableHotHint", memorySettings.EnableColdHint.Value);
            }
            memData.TrySet("EnableHotHint", memorySettings.EnableHotHint);
            memData.TrySet("EnableEpf", memorySettings.EnableEpf);
            memData.TrySet("EnablePrivateCompressionStore", memorySettings.EnablePrivateCompressionStore);

            // NUMA 节点对齐修正（防止 6962 错误）
            if (memorySettings.MaxMemoryBlocksPerNumaNode.HasValue)
            {
                memData.TrySet("MaxMemoryBlocksPerNumaNode", memorySettings.MaxMemoryBlocksPerNumaNode);
            }
            else if (memorySettings.BackingPageSize > 0 && memData.HasProperty("MaxMemoryBlocksPerNumaNode"))
            {
                ulong current = (ulong)memData["MaxMemoryBlocksPerNumaNode"];
                ulong corrected = (current / (ulong)alignment) * (ulong)alignment;
                if (corrected == 0) corrected = (ulong)alignment;
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