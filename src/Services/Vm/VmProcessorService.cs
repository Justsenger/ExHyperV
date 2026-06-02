using ExHyperV.Api;
using ExHyperV.Models;
using System.Management;

namespace ExHyperV.Services;

public class VmProcessorService
{
    public async Task<VmProcessorSettings?> GetVmProcessorAsync(string vmName)
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

                DisableSpeculationControls = procData.TryGet<bool>("DisableSpeculationControls"),
                HideHypervisorPresent = procData.TryGet<bool>("HideHypervisorPresent"),
                EnablePerfmonArchPmu = procData.TryGet<bool>("EnablePerfmonArchPmu"),
                AllowAcountMcount = procData.TryGet<bool>("AllowAcountMcount"),
                EnableSocketTopology = procData.TryGet<bool>("EnableSocketTopology"),
                CpuBrandString = procData.TryGetString("CpuBrandString"),
            };
        });

        return results.Data?.FirstOrDefault();
    }

    public async Task<(bool Success, string Message)> SetVmProcessorAsync(
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

                // 核数只能在关机状态（非 Realized）下修改
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

                // CpuBrandString 空字符串写 null（清除品牌字符串）
                if (procData.HasProperty("CpuBrandString"))
                    procData["CpuBrandString"] = string.IsNullOrWhiteSpace(newSettings.CpuBrandString)
                        ? null
                        : newSettings.CpuBrandString;

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

    private static SmtMode ConvertHwThreadsToSmtMode(uint hwThreads)
        => hwThreads == 1 ? SmtMode.SingleThread : SmtMode.MultiThread;

    private static uint ConvertSmtModeToHwThreads(SmtMode smtMode)
        => smtMode == SmtMode.SingleThread ? 1u : 2u;
}