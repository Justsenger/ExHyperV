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
            string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE {WmiApi.VmComputerSystemNamePredicate(vmName)}";
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
        string vmName, VmMemorySettings newSettings, bool isVmRunning, string? changedProperty = null)
    {
        try
        {
            string vmWql = $"SELECT * FROM Msvm_ComputerSystem WHERE {WmiApi.VmComputerSystemNamePredicate(vmName)}";
            var vmResponse = await WmiApi.QueryFirstAsync(vmWql, obj => obj["Name"]?.ToString());

            if (!vmResponse.Success || vmResponse.IsEmpty || string.IsNullOrEmpty(vmResponse.Data))
                return (false, Properties.Resources.Error_Memory_VmNotFound);

            string vmId = vmResponse.Data;
            string memWql = $"SELECT * FROM Msvm_MemorySettingData WHERE InstanceID LIKE 'Microsoft:{vmId}%' AND ResourceType = 4";

            // 动态内存与 vNUMA 互斥（对齐 PS Set-VMMemory）：启用动态内存前先关 vNUMA。仅离线可改。
            bool changesDynamicMemory = changedProperty is null ||
                changedProperty == nameof(VmMemorySettings.DynamicMemoryEnabled);
            if (changesDynamicMemory && !isVmRunning && newSettings.DynamicMemoryEnabled)
            {
                var numaOff = await SetVirtualNumaEnabledAsync(vmName, false);
                if (!numaOff.Success)
                    return (false, string.Format(Properties.Resources.VmMemory_ModFailed, numaOff.Error));
            }

            var result = await WmiApi.WithObjectAsync(
                wql: memWql,
                modifier: obj =>
                {
                    if (changedProperty is null)
                        ApplyMemorySettingsToWmiObject(obj, newSettings, isVmRunning);
                    else
                        ApplySingleMemorySettingToWmiObject(obj, newSettings, changedProperty);
                },
                submitMethod: "ModifyResourceSettings",
                submitParamName: "ResourceSettings",
                wrapInArray: true);

            if (!result.Success)
                return (false, string.Format(Properties.Resources.VmMemory_ModFailed, result.Error));

            // 静态内存：内存改完后再开 vNUMA（与 PS 顺序一致）。
            if (changesDynamicMemory && !isVmRunning && !newSettings.DynamicMemoryEnabled)
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

    /// <summary>
    /// 自动保存只提交刚刚变化的设置。只有 VMMS 的硬性约束需要联动时，
    /// 才附带写入必要字段；手动“应用”仍走完整写入路径。
    /// </summary>
    private static void ApplySingleMemorySettingToWmiObject(
        ManagementObject memData, VmMemorySettings settings, string changedProperty)
    {
        static ulong Align(long value, long alignment)
        {
            if (value <= 0) return (ulong)alignment;
            if (value > long.MaxValue - alignment) return (ulong)value;
            return (ulong)((value + alignment - 1) / alignment * alignment);
        }

        switch (changedProperty)
        {
            case nameof(VmMemorySettings.BackingPageSize):
            {
                byte pageSize = settings.BackingPageSize ?? 1;
                if (memData.HasProperty("BackingPageSize"))
                    memData["BackingPageSize"] = pageSize;
                else
                    memData.TrySet("HugePagesEnabled", (bool?)(pageSize == 2));

                // 1GB 页必须使用物理后端，相关内存范围也必须按 1GB 对齐。
                if (pageSize == 2)
                {
                    const long alignment = 1024;
                    memData.TrySet("BackingType", (byte?)0);
                    memData.TrySet("EnableColdHint", settings.EnableColdHint);
                    memData.TrySet("EnableHotHint", settings.EnableHotHint);
                    memData.TrySet("EnableEpf", settings.EnableEpf);
                    memData.TrySet("EnablePrivateCompressionStore", settings.EnablePrivateCompressionStore);
                    memData["VirtualQuantity"] = Align(settings.Startup, alignment);
                    memData["Reservation"] = Align(settings.DynamicMemoryEnabled ? settings.Minimum : settings.Startup, alignment);
                    memData["Limit"] = Align(settings.DynamicMemoryEnabled ? settings.Maximum : settings.Startup, alignment);
                    if (settings.MaxMemoryBlocksPerNumaNode.HasValue)
                    {
                        ulong value = Math.Max(1024UL, settings.MaxMemoryBlocksPerNumaNode.Value);
                        memData.TrySet("MaxMemoryBlocksPerNumaNode", (ulong?)((value / 1024UL) * 1024UL));
                    }
                }
                break;
            }

            case nameof(VmMemorySettings.DynamicMemoryEnabled):
                memData["DynamicMemoryEnabled"] = settings.DynamicMemoryEnabled;
                if (settings.DynamicMemoryEnabled)
                {
                    memData["Reservation"] = (ulong)settings.Minimum;
                    memData["Limit"] = (ulong)settings.Maximum;
                    memData.TrySetAlways("TargetMemoryBuffer", (uint)settings.Buffer);
                }
                else
                {
                    memData["Reservation"] = (ulong)settings.Startup;
                    memData["Limit"] = (ulong)settings.Startup;
                }
                break;

            case nameof(VmMemorySettings.MemoryEncryptionPolicy):
                memData.TrySet("MemoryEncryptionPolicy", settings.MemoryEncryptionPolicy);
                break;

            case nameof(VmMemorySettings.BackingType):
                memData.TrySet("BackingType", settings.BackingType);
                // 从物理切回虚拟时，UI 会把不兼容的 1GB 页同步改成 2MB。
                if (settings.BackingType != 0 && settings.BackingPageSize == 1)
                {
                    if (memData.HasProperty("BackingPageSize"))
                        memData["BackingPageSize"] = (byte)1;
                    else
                        memData.TrySet("HugePagesEnabled", (bool?)false);
                }
                // 物理后端不接受以下四项；UI 已同步关闭，因此一并落盘。
                if (settings.BackingType == 0)
                {
                    memData.TrySet("EnableColdHint", settings.EnableColdHint);
                    memData.TrySet("EnableHotHint", settings.EnableHotHint);
                    memData.TrySet("EnableEpf", settings.EnableEpf);
                    memData.TrySet("EnablePrivateCompressionStore", settings.EnablePrivateCompressionStore);
                }
                break;

            case nameof(VmMemorySettings.MemoryAccessTrackingState):
                memData.TrySet("MemoryAccessTrackingState", settings.MemoryAccessTrackingState);
                break;
            case nameof(VmMemorySettings.MemoryAccessTrackingPolicy):
                memData.TrySet("MemoryAccessTrackingPolicy", settings.MemoryAccessTrackingPolicy);
                break;
            case nameof(VmMemorySettings.EnableColdHint):
                memData.TrySet("EnableColdHint", settings.EnableColdHint);
                break;
            case nameof(VmMemorySettings.EnableHotHint):
                memData.TrySet("EnableHotHint", settings.EnableHotHint);
                break;
            case nameof(VmMemorySettings.EnableEpf):
                memData.TrySet("EnableEpf", settings.EnableEpf);
                break;
            case nameof(VmMemorySettings.EnablePrivateCompressionStore):
                memData.TrySet("EnablePrivateCompressionStore", settings.EnablePrivateCompressionStore);
                break;

            case nameof(VmMemorySettings.SgxEnabled):
                memData.TrySet("SgxEnabled", settings.SgxEnabled);
                if (settings.SgxEnabled == true && settings.SgxSize.HasValue)
                {
                    ulong size = Math.Max(2UL, (ulong)settings.SgxSize.Value);
                    memData.TrySetAlways("SgxSize", (size / 2UL) * 2UL);
                }
                break;

            case nameof(VmMemorySettings.CxlEnabled):
                memData.TrySet("CxlEnabled", settings.CxlEnabled);
                break;
            case nameof(VmMemorySettings.EnableGpaPinning):
                memData.TrySet("EnableGpaPinning", settings.EnableGpaPinning);
                break;
            case nameof(VmMemorySettings.DynMemOperationAlignment):
                memData.TrySet("DynMemOperationAlignment", settings.DynMemOperationAlignment);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(changedProperty), changedProperty, "Unsupported automatic memory setting");
        }
    }
}
