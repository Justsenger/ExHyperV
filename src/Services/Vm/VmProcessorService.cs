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

            return new VmProcessorSettings
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

                procData.TrySet("DisableSpeculationControls", newSettings.DisableSpeculationControls);
                procData.TrySet("HideHypervisorPresent", newSettings.HideHypervisorPresent);
                procData.TrySet("EnablePerfmonArchPmu", newSettings.EnablePerfmonArchPmu);
                procData.TrySet("AllowAcountMcount", newSettings.AllowAcountMcount);
                procData.TrySet("EnableSocketTopology", newSettings.EnableSocketTopology);

                // CpuBrandString 清空必须写空字符串而非 null：
                // null 在 CIM-DTD 序列化时不带 <VALUE> 元素，ModifyResourceSettings 视为"未指定/不修改"→ 清不掉；
                // 空字符串会序列化成 <VALUE></VALUE>，provider 才会真正把自定义名清除（来宾恢复真实 CPU 名）。
                if (procData.HasProperty("CpuBrandString"))
                    procData["CpuBrandString"] = string.IsNullOrWhiteSpace(newSettings.CpuBrandString)
                        ? string.Empty
                        : newSettings.CpuBrandString;

                // ── 新增 CPU 字段（TrySet 自带 HasProperty 守卫：旧 build 无此属性则跳过）──
                if (newSettings.ApicMode is { } am) procData.TrySet<byte>("ApicMode", (byte)am);
                if (newSettings.L3DistributionPolicy is { } dp) procData.TrySet<byte>("L3ProcessorDistributionPolicy", (byte)dp);
                if (newSettings.PageShatterMode is { } ps) procData.TrySet<byte>("EnablePageShattering", (byte)ps);
                procData.TrySet("L3CacheWays", newSettings.L3CacheWays);

                procData.TrySet("PerfCpuFreqCapMhz", newSettings.PerfCpuFreqCapMhz);
                procData.TrySet("PerfCpuFreqMinMhz", newSettings.PerfCpuFreqMinMhz);
                procData.TrySet("PerfCpuFreqDesiredMhz", newSettings.PerfCpuFreqDesiredMhz);
                procData.TrySet("PerfCpuEnergyPerformancePreference", newSettings.PerfCpuEnergyPerformancePreference);
                procData.TrySet("PerfCpuAutonomousActivityWindow", newSettings.PerfCpuAutonomousActivityWindow);
                procData.TrySet("PerfCpuIgnoreHostMaxFrequency", newSettings.PerfCpuIgnoreHostMaxFrequency);

                procData.TrySet("EnablePerfmonPmu", newSettings.EnablePerfmonPmu);
                procData.TrySet("EnablePerfmonLbr", newSettings.EnablePerfmonLbr);
                procData.TrySet("EnablePerfmonPebs", newSettings.EnablePerfmonPebs);
                procData.TrySet("EnablePerfmonIpt", newSettings.EnablePerfmonIpt);

                procData.TrySet("ExtendedVirtualizationExtensions", newSettings.ExtendedVirtualizationExtensions);
                procData.TrySet("MaxHwIsolatedGuests", newSettings.MaxHwIsolatedGuests);
                procData.TrySet("MaxClusterCountPerSocket", newSettings.MaxClusterCountPerSocket);
                procData.TrySet("MaxProcessorCountPerL3", newSettings.MaxProcessorCountPerL3);

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

    // uint 字段未设置时 WMI 返回 0xFFFFFFFF（如 AMD CCX 拓扑字段），归一为 null → UI 显示空白
    private static uint? Nz(uint? v) => v == uint.MaxValue ? (uint?)null : v;

    // 门控读取：属性存在但值 null(高版本新属性、当前 VM 未设过) → 返回默认值(非 null)，令"值 null"仅代表"属性不在 schema(不支持)"。
    private static bool? PBool(ManagementObject p, string n) => p.HasProperty(n) ? (p.TryGet<bool>(n) ?? false) : (bool?)null;
    private static byte? PByte(ManagementObject p, string n) => p.HasProperty(n) ? (p.TryGetByte(n) ?? (byte)0) : (byte?)null;
    private static uint? PUInt(ManagementObject p, string n) => p.HasProperty(n) ? (p.TryGet<uint>(n) ?? 0u) : (uint?)null;
    private static string? PStr(ManagementObject p, string n) => p.HasProperty(n) ? (p.TryGetString(n) ?? "") : null;

    private static SmtMode ConvertHwThreadsToSmtMode(uint hwThreads)
        => hwThreads == 1 ? SmtMode.SingleThread : SmtMode.MultiThread;

    private static uint ConvertSmtModeToHwThreads(SmtMode smtMode)
        => smtMode == SmtMode.SingleThread ? 1u : 2u;
}