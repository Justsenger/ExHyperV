using ExHyperV.Models;
using ExHyperV.ViewModels;
using System;
using System.Linq;
using System.Management;
using System.Threading.Tasks;

namespace ExHyperV.Services
{
    public class VmProcessorService : IVmProcessorService
    {
        private const string WmiNamespace = @"root\virtualization\v2";

        public async Task<VMProcessorViewModel?> GetVmProcessorAsync(string vmName)
        {
            return await Task.Run(() =>
            {
                ManagementObject vmSettings = null;
                ManagementObject processorSetting = null;
                try
                {
                    var scope = new ManagementScope(WmiNamespace);
                    scope.Connect();

                    // 步骤 1 & 2: 使用 Msvm_SummaryInformation 将 vmName 转换为可靠的 GUID
                    var vmGuid = GetVmGuidByNameFromSummary(vmName, scope);
                    if (vmGuid == Guid.Empty) return null;

                    // 步骤 3: 使用 GUID 查询主设置对象
                    vmSettings = GetVirtualSystemSettingDataByGuid(vmGuid, scope);
                    if (vmSettings == null) return null;

                    // 步骤 4: 获取处理器设置
                    processorSetting = GetAssociatedProcessorSetting(vmSettings);
                    if (processorSetting == null) return null;

                    var processor = new VMProcessorViewModel
                    {
                        Count = (ushort)processorSetting["VirtualQuantity"],
                        Reserve = (ushort)processorSetting["Reservation"],
                        Maximum = (ushort)processorSetting["Limit"],
                        RelativeWeight = (ushort)processorSetting["Weight"],
                        ExposeVirtualizationExtensions = (bool)processorSetting["ExposeVirtualizationExtensions"],
                        EnableHostResourceProtection = (bool)processorSetting["EnableHostResourceProtection"],
                        CompatibilityForMigrationEnabled = (bool)processorSetting["CompatibilityForMigrationEnabled"],
                        CompatibilityForOlderOperatingSystemsEnabled = (bool)processorSetting["CompatibilityForOlderOperatingSystemsEnabled"],
                        SmtMode = ConvertHwThreadsToSmtMode((ushort)processorSetting["HwThreadsPerCore"])
                    };
                    return processor;
                }
                catch
                {
                    return null;
                }
                finally
                {
                    processorSetting?.Dispose();
                    vmSettings?.Dispose();
                }
            });
        }

        public async Task<(bool Success, string Message)> SetVmProcessorAsync(string vmName, VMProcessorViewModel processorSettings)
        {
            return await Task.Run(() =>
            {
                ManagementObject vmSettings = null;
                ManagementObject processorSetting = null;
                try
                {
                    var scope = new ManagementScope(WmiNamespace);
                    scope.Connect();

                    var vmGuid = GetVmGuidByNameFromSummary(vmName, scope);
                    if (vmGuid == Guid.Empty) return (false, "找不到指定的虚拟机。");

                    vmSettings = GetVirtualSystemSettingDataByGuid(vmGuid, scope);
                    if (vmSettings == null) return (false, "找不到虚拟机的主设置对象。");

                    processorSetting = GetAssociatedProcessorSetting(vmSettings);
                    if (processorSetting == null) return (false, "找不到虚拟机的处理器设置。");

                    processorSetting["VirtualQuantity"] = (ulong)processorSettings.Count;
                    processorSetting["Reservation"] = (ulong)processorSettings.Reserve;
                    processorSetting["Limit"] = (ulong)processorSettings.Maximum;
                    processorSetting["Weight"] = (uint)processorSettings.RelativeWeight;
                    processorSetting["ExposeVirtualizationExtensions"] = processorSettings.ExposeVirtualizationExtensions;
                    processorSetting["EnableHostResourceProtection"] = processorSettings.EnableHostResourceProtection;
                    processorSetting["CompatibilityForMigrationEnabled"] = processorSettings.CompatibilityForMigrationEnabled;
                    processorSetting["CompatibilityForOlderOperatingSystemsEnabled"] = processorSettings.CompatibilityForOlderOperatingSystemsEnabled;
                    processorSetting["HwThreadsPerCore"] = ConvertSmtModeToHwThreads(processorSettings.SmtMode);

                    using (var managementService = new ManagementClass(WmiNamespace, "Msvm_VirtualSystemManagementService", null))
                    {
                        using (var inParams = managementService.GetMethodParameters("ModifySystemSettings"))
                        {
                            inParams["SystemSettings"] = vmSettings.GetText(TextFormat.CimDtd20);

                            using (var outParams = managementService.InvokeMethod("ModifySystemSettings", inParams, null))
                            {
                                if ((uint)outParams["ReturnValue"] == 4096)
                                {
                                    return WaitForJob((string)outParams["Job"], scope);
                                }
                                if ((uint)outParams["ReturnValue"] == 0)
                                {
                                    return (true, "设置已成功应用。");
                                }
                                return (false, $"应用设置失败，错误码: {outParams["ReturnValue"]}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    return (false, $"应用设置时发生异常: {ex.Message}");
                }
                finally
                {
                    processorSetting?.Dispose();
                    vmSettings?.Dispose();
                }
            });
        }

        // 最可靠的辅助方法：通过 Msvm_SummaryInformation 将名称映射到 GUID
        private Guid GetVmGuidByNameFromSummary(string vmName, ManagementScope scope)
        {
            string queryText = $"SELECT Name FROM Msvm_SummaryInformation WHERE ElementName = '{vmName}'";
            var query = new ObjectQuery(queryText);
            using (var searcher = new ManagementObjectSearcher(scope, query))
            {
                var summaryObject = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                if (summaryObject != null && Guid.TryParse((string)summaryObject["Name"], out Guid vmId))
                {
                    summaryObject.Dispose();
                    return vmId;
                }
            }
            return Guid.Empty;
        }

        // 通过 GUID 获取主设置对象
        private ManagementObject GetVirtualSystemSettingDataByGuid(Guid vmGuid, ManagementScope scope)
        {
            string queryText = $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ConfigurationID = '{vmGuid}'";
            var query = new ObjectQuery(queryText);
            using (var searcher = new ManagementObjectSearcher(scope, query))
            {
                return searcher.Get().Cast<ManagementObject>().FirstOrDefault();
            }
        }

        private ManagementObject GetAssociatedProcessorSetting(ManagementObject vmSettings)
        {
            var relatedObjects = vmSettings.GetRelated("Msvm_ProcessorSettingData", "Msvm_ComponentSettingData", null, null, "GroupComponent", "PartComponent", false, null);
            return relatedObjects.OfType<ManagementObject>().FirstOrDefault();
        }

        private (bool Success, string Message) WaitForJob(string jobPath, ManagementScope scope)
        {
            using (var job = new ManagementObject(scope, new ManagementPath(jobPath), null))
            {
                job.Get();
                while ((ushort)job["JobState"] == 4) { System.Threading.Thread.Sleep(500); job.Get(); }
                if ((ushort)job["JobState"] == 7) { return (true, "设置已成功应用。"); }
                else { return (false, $"操作失败: {job["ErrorDescription"]?.ToString() ?? "未知错误。"}"); }
            }
        }

        private SmtMode ConvertHwThreadsToSmtMode(ushort hwThreads) => hwThreads switch { 0 => SmtMode.Inherit, 1 => SmtMode.SingleThread, _ => SmtMode.MultiThread };
        private ulong ConvertSmtModeToHwThreads(SmtMode smtMode) => smtMode switch { SmtMode.Inherit => 0, SmtMode.SingleThread => 1, SmtMode.MultiThread => 2, _ => 0 };
    }
}