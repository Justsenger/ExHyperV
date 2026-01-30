using ExHyperV.Models;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

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

                    ExposeVirtualizationExtensions = GetNullableBoolProperty(procData, "ExposeVirtualizationExtensions") ?? false,
                    EnableHostResourceProtection = GetNullableBoolProperty(procData, "EnableHostResourceProtection") ?? false,
                    CompatibilityForMigrationEnabled = GetNullableBoolProperty(procData, "LimitProcessorFeatures") ?? false,
                    CompatibilityForOlderOperatingSystemsEnabled = GetNullableBoolProperty(procData, "LimitCPUID") ?? false,
                    SmtMode = ConvertHwThreadsToSmtMode(Convert.ToUInt32(procData["HwThreadsPerCore"])),

                    DisableSpeculationControls = GetNullableBoolProperty(procData, "DisableSpeculationControls"),
                    HideHypervisorPresent = GetNullableBoolProperty(procData, "HideHypervisorPresent"),
                    EnablePerfmonArchPmu = GetNullableBoolProperty(procData, "EnablePerfmonArchPmu"),
                    AllowAcountMcount = GetNullableBoolProperty(procData, "AllowAcountMcount"),
                    EnableSocketTopology = GetNullableBoolProperty(procData, "EnableSocketTopology")
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

                    if (!procData.Path.Path.Contains("Realized"))
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

                    TrySetNullableProperty(procData, "DisableSpeculationControls", newSettings.DisableSpeculationControls);
                    TrySetNullableProperty(procData, "HideHypervisorPresent", newSettings.HideHypervisorPresent);
                    TrySetNullableProperty(procData, "EnablePerfmonArchPmu", newSettings.EnablePerfmonArchPmu);
                    TrySetNullableProperty(procData, "AllowAcountMcount", newSettings.AllowAcountMcount);
                    TrySetNullableProperty(procData, "EnableSocketTopology", newSettings.EnableSocketTopology);

                    return procData.GetText(TextFormat.CimDtd20);
                });

                var xml = xmlResults.FirstOrDefault();
                if (string.IsNullOrEmpty(xml)) return (false, "找不到虚拟机或处理器配置。");

                var inParams = new Dictionary<string, object>
                {
                    { "ResourceSettings", new string[] { xml } }
                };

                return await WmiTools.ExecuteMethodAsync(
                    "SELECT * FROM Msvm_VirtualSystemManagementService",
                    "ModifyResourceSettings",
                    inParams
                );
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

        private static void TrySetNullableProperty(ManagementObject obj, string propName, bool? value)
        {
            if (value.HasValue && HasProperty(obj, propName))
            {
                try { obj[propName] = value.Value; } catch { }
            }
        }

        private static bool? GetNullableBoolProperty(ManagementObject obj, string propName)
        {
            if (!HasProperty(obj, propName) || obj[propName] == null) return null;
            try { return Convert.ToBoolean(obj[propName]); } catch { return null; }
        }
    }
}