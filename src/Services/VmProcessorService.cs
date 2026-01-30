using ExHyperV.Models;
using ExHyperV.Tools;
using System.Management;

namespace ExHyperV.Services
{
    public class VmProcessorService
    {
        public async Task<VmProcessorSettings?> GetVmProcessorAsync(string vmName)
        {
            var query = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'";

            var results = await WmiTools.QueryAsync(query, (vmEntry) =>
            {
                var allSettings = vmEntry.GetRelated("Msvm_VirtualSystemSettingData").Cast<ManagementObject>().ToList();
                var settingData = allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Realized")
                               ?? allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Definition");

                if (settingData == null) return null;

                using var procData = settingData.GetRelated("Msvm_ProcessorSettingData").Cast<ManagementObject>().FirstOrDefault();
                if (procData == null) return null;

                return new VmProcessorSettings
                {
                    Count = Convert.ToInt32(procData["VirtualQuantity"]),
                    Reserve = Convert.ToInt32(procData["Reservation"]) / 1000,
                    Maximum = Convert.ToInt32(procData["Limit"]) / 1000,
                    RelativeWeight = Convert.ToInt32(procData["Weight"]),

                    ExposeVirtualizationExtensions = GetBoolProperty(procData, "ExposeVirtualizationExtensions"),
                    EnableHostResourceProtection = GetBoolProperty(procData, "EnableHostResourceProtection"),
                    CompatibilityForMigrationEnabled = GetBoolProperty(procData, "LimitProcessorFeatures"),
                    CompatibilityForOlderOperatingSystemsEnabled = GetBoolProperty(procData, "LimitCPUID"),
                    SmtMode = ConvertHwThreadsToSmtMode(Convert.ToUInt32(procData["HwThreadsPerCore"])),

                    DisableSpeculationControls = GetBoolProperty(procData, "DisableSpeculationControls"),
                    IsDisableSpeculationSupported = HasProperty(procData, "DisableSpeculationControls"),

                    HideHypervisorPresent = GetBoolProperty(procData, "HideHypervisorPresent"),
                    IsHideHypervisorSupported = HasProperty(procData, "HideHypervisorPresent"),

                    EnablePerfmonArchPmu = GetBoolProperty(procData, "EnablePerfmonArchPmu"),
                    IsPerfmonArchPmuSupported = HasProperty(procData, "EnablePerfmonArchPmu"),

                    AllowAcountMcount = GetBoolProperty(procData, "AllowAcountMcount"),
                    IsAcountMcountSupported = HasProperty(procData, "AllowAcountMcount"),

                    EnableSocketTopology = GetBoolProperty(procData, "EnableSocketTopology"),
                    IsSocketTopologySupported = HasProperty(procData, "EnableSocketTopology")
                };
            });

            return results.FirstOrDefault();
        }

        public async Task<(bool Success, string Message)> SetVmProcessorAsync(string vmName, VmProcessorSettings newSettings)
        {
            try
            {
                var query = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName.Replace("'", "''")}'";

                var xmlResults = await WmiTools.QueryAsync(query, (vmEntry) =>
                {
                    var allSettings = vmEntry.GetRelated("Msvm_VirtualSystemSettingData").Cast<ManagementObject>().ToList();
                    var settingData = allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Realized")
                                   ?? allSettings.FirstOrDefault(s => s["VirtualSystemType"]?.ToString() == "Microsoft:Hyper-V:System:Definition");

                    if (settingData == null) return null;

                    using var procData = settingData.GetRelated("Msvm_ProcessorSettingData").Cast<ManagementObject>().FirstOrDefault();
                    if (procData == null) return null;

                    bool isRealized = procData.Path.Path.Contains("Realized");

                    if (!isRealized)
                    {
                        procData["VirtualQuantity"] = (ulong)newSettings.Count;
                    }

                    procData["Reservation"] = (ulong)(newSettings.Reserve * 1000);
                    procData["Limit"] = (ulong)(newSettings.Maximum * 1000);
                    procData["Weight"] = (uint)newSettings.RelativeWeight;

                    procData["ExposeVirtualizationExtensions"] = newSettings.ExposeVirtualizationExtensions;
                    procData["EnableHostResourceProtection"] = newSettings.EnableHostResourceProtection;
                    procData["LimitProcessorFeatures"] = newSettings.CompatibilityForMigrationEnabled;
                    procData["LimitCPUID"] = newSettings.CompatibilityForOlderOperatingSystemsEnabled;
                    procData["HwThreadsPerCore"] = (ulong)ConvertSmtModeToHwThreads(newSettings.SmtMode);

                    TrySetProperty(procData, "DisableSpeculationControls", newSettings.DisableSpeculationControls);
                    TrySetProperty(procData, "HideHypervisorPresent", newSettings.HideHypervisorPresent);
                    TrySetProperty(procData, "EnablePerfmonArchPmu", newSettings.EnablePerfmonArchPmu);
                    TrySetProperty(procData, "AllowAcountMcount", newSettings.AllowAcountMcount);
                    TrySetProperty(procData, "EnableSocketTopology", newSettings.EnableSocketTopology);

                    return procData.GetText(TextFormat.CimDtd20);
                });

                var xml = xmlResults.FirstOrDefault();
                if (string.IsNullOrEmpty(xml)) return (false, "找不到虚拟机或处理器配置。");

                var inParams = new Dictionary<string, object>
                {
                    { "ResourceSettings", new string[] { xml } }
                };

                bool success = await WmiTools.ExecuteMethodAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "ModifyResourceSettings",
                    inParams
                );

                return success ? (true, "处理器设置已成功应用") : (false, "修改失败，请查看调试日志。");
            }
            catch (Exception ex)
            {
                return (false, $"异常: {ex.Message}");
            }
        }

        private static SmtMode ConvertHwThreadsToSmtMode(uint hwThreads) => hwThreads == 1 ? SmtMode.SingleThread : SmtMode.MultiThread;

        private static uint ConvertSmtModeToHwThreads(SmtMode smtMode) => smtMode == SmtMode.SingleThread ? 1u : 2u;

        private static bool HasProperty(ManagementObject obj, string propName) =>
            obj.Properties.Cast<PropertyData>().Any(p => p.Name.Equals(propName, StringComparison.OrdinalIgnoreCase));

        private static void TrySetProperty(ManagementObject obj, string propName, object value)
        {
            if (HasProperty(obj, propName))
            {
                try { obj[propName] = value; } catch { }
            }
        }

        private static bool GetBoolProperty(ManagementObject obj, string propName)
        {
            if (!HasProperty(obj, propName) || obj[propName] == null) return false;
            try { return Convert.ToBoolean(obj[propName]); } catch { return false; }
        }
    }
}