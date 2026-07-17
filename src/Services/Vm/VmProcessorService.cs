using ExHyperV.Tools;
using ExHyperV.Models;
using System.Management;

namespace ExHyperV.Services;

public static class VmProcessorService
{
    public static async Task<VmProcessorSettings?> GetVmProcessorAsync(string vmName)
    {
        string query = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'";

        var results = await WmiApi.QueryAsync(query, vmEntry =>
        {
            var allSettings = vmEntry.GetRelated("Msvm_VirtualSystemSettingData")
                .Cast<ManagementObject>().ToList();

            var settingData =
                allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Realized")
             ?? allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Definition");

            if (settingData == null) return null;

            using var procData = settingData.GetRelated("Msvm_ProcessorSettingData")
                .Cast<ManagementObject>().FirstOrDefault();
            if (procData == null) return null;

            return MapProcessor(procData);
        });

        return results.Data?.FirstOrDefault();
    }

    public static async Task<(bool Success, string Message)> SetVmProcessorAsync(
        string vmName, VmProcessorSettings newSettings)
    {
        try
        {
            string query = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'";

            var xmlResults = await WmiApi.QueryAsync(query, vmEntry =>
            {
                var allSettings = vmEntry.GetRelated("Msvm_VirtualSystemSettingData")
                    .Cast<ManagementObject>().ToList();

                var settingData =
                    allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Realized")
                 ?? allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Definition");

                if (settingData == null) return null;

                using var procData = settingData.GetRelated("Msvm_ProcessorSettingData")
                    .Cast<ManagementObject>().FirstOrDefault();
                if (procData == null) return null;

                // ����ֻ���ڹػ�״̬���� Realized�����޸�
                var current = MapProcessor(procData);

                if (!procData.Path.Path.Contains("Realized"))
                    procData["VirtualQuantity"] = (ulong)newSettings.Count;

                procData["Reservation"] = (ulong)(newSettings.Reserve * 1000);
                procData["Limit"] = (ulong)(newSettings.Maximum * 1000);
                procData["Weight"] = (uint)newSettings.RelativeWeight;

                procData.TrySet("ExposeVirtualizationExtensions", newSettings.ExposeVirtualizationExtensions);
                procData.TrySet("EnableHostResourceProtection", newSettings.EnableHostResourceProtection);
                procData.TrySet("LimitProcessorFeatures", newSettings.CompatibilityForMigrationEnabled);
                procData.TrySet("LimitCPUID", newSettings.CompatibilityForOlderOperatingSystemsEnabled);

                if (newSettings.SmtMode.HasValue)
                    procData.TrySetAlways("HwThreadsPerCore",
                        (ulong)ConvertSmtModeToHwThreads(newSettings.SmtMode.Value));

                // 门控字段只在"用户真改过"（提交值≠当前值）才写：整份提交时 provider 按"提交值≠存储值"判改动，
                // 把读取时强转的默认值原样写回会造成假改动 → 触发版本校验拒整包（12.3 VM 遇仅28000才有的 PerfCpuIgnoreHostMaxFrequency 即此）。
                SetIfChanged(procData, "DisableSpeculationControls", newSettings.DisableSpeculationControls, current.DisableSpeculationControls);
                SetIfChanged(procData, "HideHypervisorPresent", newSettings.HideHypervisorPresent, current.HideHypervisorPresent);
                SetIfChanged(procData, "EnablePerfmonArchPmu", newSettings.EnablePerfmonArchPmu, current.EnablePerfmonArchPmu);
                SetIfChanged(procData, "AllowAcountMcount", newSettings.AllowAcountMcount, current.AllowAcountMcount);
                SetIfChanged(procData, "EnableSocketTopology", newSettings.EnableSocketTopology, current.EnableSocketTopology);

                // 清空要写空串：null 序列化不带 <VALUE>，provider 当"不改"清不掉
                if (!Equals(newSettings.CpuBrandString ?? "", current.CpuBrandString ?? "") && procData.HasProperty("CpuBrandString"))
                    procData["CpuBrandString"] = string.IsNullOrWhiteSpace(newSettings.CpuBrandString) ? string.Empty : newSettings.CpuBrandString;

                if (newSettings.ApicMode != current.ApicMode && newSettings.ApicMode is { } am) procData.TrySet<byte>("ApicMode", (byte)am);
                if (newSettings.L3DistributionPolicy != current.L3DistributionPolicy && newSettings.L3DistributionPolicy is { } dp) procData.TrySet<byte>("L3ProcessorDistributionPolicy", (byte)dp);
                if (newSettings.PageShatterMode != current.PageShatterMode && newSettings.PageShatterMode is { } ps) procData.TrySet<byte>("EnablePageShattering", (byte)ps);
                SetIfChanged(procData, "L3CacheWays", newSettings.L3CacheWays, current.L3CacheWays);

                SetIfChanged(procData, "PerfCpuFreqCapMhz", newSettings.PerfCpuFreqCapMhz, current.PerfCpuFreqCapMhz);
                SetIfChanged(procData, "PerfCpuFreqMinMhz", newSettings.PerfCpuFreqMinMhz, current.PerfCpuFreqMinMhz);
                SetIfChanged(procData, "PerfCpuFreqDesiredMhz", newSettings.PerfCpuFreqDesiredMhz, current.PerfCpuFreqDesiredMhz);
                SetIfChanged(procData, "PerfCpuEnergyPerformancePreference", newSettings.PerfCpuEnergyPerformancePreference, current.PerfCpuEnergyPerformancePreference);
                SetIfChanged(procData, "PerfCpuAutonomousActivityWindow", newSettings.PerfCpuAutonomousActivityWindow, current.PerfCpuAutonomousActivityWindow);
                SetIfChanged(procData, "PerfCpuIgnoreHostMaxFrequency", newSettings.PerfCpuIgnoreHostMaxFrequency, current.PerfCpuIgnoreHostMaxFrequency);

                SetIfChanged(procData, "EnablePerfmonPmu", newSettings.EnablePerfmonPmu, current.EnablePerfmonPmu);
                SetIfChanged(procData, "EnablePerfmonLbr", newSettings.EnablePerfmonLbr, current.EnablePerfmonLbr);
                SetIfChanged(procData, "EnablePerfmonPebs", newSettings.EnablePerfmonPebs, current.EnablePerfmonPebs);
                SetIfChanged(procData, "EnablePerfmonIpt", newSettings.EnablePerfmonIpt, current.EnablePerfmonIpt);

                SetIfChanged(procData, "ExtendedVirtualizationExtensions", newSettings.ExtendedVirtualizationExtensions, current.ExtendedVirtualizationExtensions);
                SetIfChanged(procData, "MaxHwIsolatedGuests", newSettings.MaxHwIsolatedGuests, current.MaxHwIsolatedGuests);
                SetIfChanged(procData, "MaxClusterCountPerSocket", newSettings.MaxClusterCountPerSocket, current.MaxClusterCountPerSocket);
                SetIfChanged(procData, "MaxProcessorCountPerL3", newSettings.MaxProcessorCountPerL3, current.MaxProcessorCountPerL3);

                return procData.GetText(TextFormat.CimDtd20);
            });

            string? xml = xmlResults.Data?.FirstOrDefault();
            if (string.IsNullOrEmpty(xml))
                return (false, Properties.Resources.Error_Cpu_ConfigNotFound);

            var result = await WmiApi.InvokeAsync(
                "SELECT * FROM Msvm_VirtualSystemManagementService",
                "ModifyResourceSettings",
                p => p["ResourceSettings"] = new string[] { xml });

            return result.Success
                ? (true, string.Empty)
                : (false, result.Error);
        }
        catch (Exception ex)
        {
            return (false, string.Format(Properties.Resources.VmProcessor_Exception, ex.Message));
        }
    }

    private static VmProcessorSettings MapProcessor(ManagementObject procData) =>
        new VmProcessorSettings
            {
                Count = Convert.ToInt32(procData["VirtualQuantity"]),
                Reserve = Convert.ToInt32(procData["Reservation"]) / 1000,
                Maximum = Convert.ToInt32(procData["Limit"]) / 1000,
                RelativeWeight = Convert.ToInt32(procData["Weight"]),

                ExposeVirtualizationExtensions = procData.TryGet<bool>("ExposeVirtualizationExtensions") ?? false,
                EnableHostResourceProtection = procData.TryGet<bool>("EnableHostResourceProtection") ?? false,
                CompatibilityForMigrationEnabled = procData.TryGet<bool>("LimitProcessorFeatures") ?? false,
                CompatibilityForOlderOperatingSystemsEnabled = procData.TryGet<bool>("LimitCPUID") ?? false,
                SmtMode = ConvertHwThreadsToSmtMode(Convert.ToUInt32(procData["HwThreadsPerCore"])),

                // 门控字段：用 P*(HasProperty ? 值 ?? 默认 : null) 读——令"值 null"仅代表"属性不在 schema(不支持)"，
                // 避免高版本"属性存在但当前 VM 默认值 null"被 UI 的值-null 门控误灰（29617 上 Perfmon/调频项就是这样）。
                DisableSpeculationControls = PBool(procData, "DisableSpeculationControls"),
                HideHypervisorPresent = PBool(procData, "HideHypervisorPresent"),
                EnablePerfmonArchPmu = PBool(procData, "EnablePerfmonArchPmu"),
                AllowAcountMcount = PBool(procData, "AllowAcountMcount"),
                EnableSocketTopology = PBool(procData, "EnableSocketTopology"),
                CpuBrandString = PStr(procData, "CpuBrandString"),

                ApicMode = (VmApicMode?)PByte(procData, "ApicMode"),
                L3CacheWays = PUInt(procData, "L3CacheWays"),
                L3DistributionPolicy = (L3DistributionPolicy?)PByte(procData, "L3ProcessorDistributionPolicy"),
                PageShatterMode = (PageShatterMode?)PByte(procData, "EnablePageShattering"),

                // 频率值要留 null 显示空白、且不能误写默认，故值保持 Nz(未设=null)；UI 的 Tag 改按 SupportedProps 判支持(下方)。
                PerfCpuFreqCapMhz = Nz(procData.TryGet<uint>("PerfCpuFreqCapMhz")),
                PerfCpuFreqMinMhz = Nz(procData.TryGet<uint>("PerfCpuFreqMinMhz")),
                PerfCpuFreqDesiredMhz = Nz(procData.TryGet<uint>("PerfCpuFreqDesiredMhz")),
                PerfCpuEnergyPerformancePreference = Nz(procData.TryGet<uint>("PerfCpuEnergyPerformancePreference")),
                PerfCpuAutonomousActivityWindow = Nz(procData.TryGet<uint>("PerfCpuAutonomousActivityWindow")),
                PerfCpuIgnoreHostMaxFrequency = PBool(procData, "PerfCpuIgnoreHostMaxFrequency"),

                EnablePerfmonPmu = PBool(procData, "EnablePerfmonPmu"),
                EnablePerfmonLbr = PBool(procData, "EnablePerfmonLbr"),
                EnablePerfmonPebs = PBool(procData, "EnablePerfmonPebs"),
                EnablePerfmonIpt = PBool(procData, "EnablePerfmonIpt"),

                ExtendedVirtualizationExtensions = Nz(procData.TryGet<uint>("ExtendedVirtualizationExtensions")),
                MaxHwIsolatedGuests = Nz(procData.TryGet<uint>("MaxHwIsolatedGuests")),
                MaxClusterCountPerSocket = Nz(procData.TryGet<uint>("MaxClusterCountPerSocket")),
                MaxProcessorCountPerL3 = Nz(procData.TryGet<uint>("MaxProcessorCountPerL3")),

                // 宿主实际存在的属性名集合(schema)，供频率字段 UI 门控判"支持"。
                SupportedProps = new HashSet<string>(
                    procData.Properties.Cast<PropertyData>().Select(p => p.Name), StringComparer.OrdinalIgnoreCase),
            };

    // uint 字段未设置时 WMI 返回 0xFFFFFFFF（如 AMD CCX 拓扑字段），归一为 null → UI 显示空白
    private static uint? Nz(uint? v) => v == uint.MaxValue ? (uint?)null : v;

    // 门控读取：属性存在但值 null(高版本新属性、当前 VM 未设过) → 返回默认值(非 null)，令"值 null"仅代表"属性不在 schema(不支持)"。
    // 整份提交时，门控字段只在"提交值≠当前值"才真正写入。provider 按"提交值≠存储值"判改动；
    // 若把读取强转的默认值原样写回会造成假改动 → 触发版本校验拒整包（见上）。
    private static void SetIfChanged<T>(ManagementObject o, string name, T? nv, T? cv) where T : struct
    {
        if (!EqualityComparer<T?>.Default.Equals(nv, cv)) o.TrySet(name, nv);
    }

    private static bool? PBool(ManagementObject p, string n) => p.HasProperty(n) ? (p.TryGet<bool>(n) ?? false) : (bool?)null;
    private static byte? PByte(ManagementObject p, string n) => p.HasProperty(n) ? (p.TryGetByte(n) ?? (byte)0) : (byte?)null;
    private static uint? PUInt(ManagementObject p, string n) => p.HasProperty(n) ? (p.TryGet<uint>(n) ?? 0u) : (uint?)null;
    private static string? PStr(ManagementObject p, string n) => p.HasProperty(n) ? (p.TryGetString(n) ?? "") : null;

    private static SmtMode ConvertHwThreadsToSmtMode(uint hwThreads)
        => hwThreads == 1 ? SmtMode.SingleThread : SmtMode.MultiThread;

    private static uint ConvertSmtModeToHwThreads(SmtMode smtMode)
        => smtMode == SmtMode.SingleThread ? 1u : 2u;
}