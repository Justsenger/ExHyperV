using System.Management;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>通过增删 Msvm_BatterySettingData 为虚拟机提供合成电池设备。</summary>
public static class VmBatteryService
{
    private const string BatteryClass = "Msvm_BatterySettingData";
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
    private const string RealizedType = "Microsoft:Hyper-V:System:Realized";

    public static Task<(bool Success, bool Available, bool Enabled, string Message)> GetStateAsync(string vmName)
        => Task.Run(() =>
        {
            try
            {
                using var service = WmiApi.GetVirtualSystemManagementService();
                var scope = service.Scope;
                using var template = FindFirst(scope,
                    $"SELECT * FROM {BatteryClass} WHERE InstanceID LIKE '%Default%'");
                if (template == null)
                    return (true, false, false, string.Empty);

                using var vm = WmiApi.GetVmComputerSystem(vmName);
                if (vm == null)
                    return (false, true, false, Properties.Resources.Error_Net_VmNotFound);

                string vmGuid = vm["Name"]?.ToString() ?? string.Empty;
                using var battery = FindFirst(scope,
                    $"SELECT * FROM {BatteryClass} WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'");
                return (true, true, battery != null, string.Empty);
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidClass)
            {
                return (true, false, false, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, false, false, ex.Message);
            }
        });

    public static Task<(bool Success, string Message)> SetEnabledAsync(string vmName, bool enabled)
        => Task.Run(async () =>
        {
            try
            {
                using var service = WmiApi.GetVirtualSystemManagementService();
                var scope = service.Scope;
                using var vm = WmiApi.GetVmComputerSystem(vmName);
                if (vm == null)
                    return (false, Properties.Resources.Error_Net_VmNotFound);

                string vmGuid = vm["Name"]?.ToString() ?? string.Empty;
                using var battery = FindFirst(scope,
                    $"SELECT * FROM {BatteryClass} WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'");

                if (enabled)
                {
                    if (battery != null) return (true, string.Empty);

                    using var settings = FindFirst(scope,
                        $"SELECT * FROM Msvm_VirtualSystemSettingData " +
                        $"WHERE VirtualSystemIdentifier = '{WmiApi.Escape(vmGuid)}' " +
                        $"AND VirtualSystemType = '{RealizedType}'");
                    if (settings == null)
                        return (false, Properties.Resources.Error_Cpu_ConfigNotFound);

                    using var template = FindFirst(scope,
                        $"SELECT * FROM {BatteryClass} WHERE InstanceID LIKE '%Default%'");
                    if (template == null)
                        return (false, Properties.Resources.Error_VerNotSupport);

                    using var clonedTemplate = (ManagementObject)template.Clone();
                    string templateXml = clonedTemplate.GetText(TextFormat.CimDtd20);
                    var result = await WmiApi.InvokeAsync(ServiceWql, "AddResourceSettings", p =>
                    {
                        p["AffectedConfiguration"] = settings.Path.Path;
                        p["ResourceSettings"] = new[] { templateXml };
                    });
                    return result.Success ? (true, string.Empty) : (false, result.Error);
                }

                if (battery == null) return (true, string.Empty);
                var removeResult = await WmiApi.InvokeAsync(ServiceWql, "RemoveResourceSettings",
                    p => p["ResourceSettings"] = new[] { battery.Path.Path });
                return removeResult.Success ? (true, string.Empty) : (false, removeResult.Error);
            }
            catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.InvalidClass)
            {
                return (false, Properties.Resources.Error_VerNotSupport);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });

    private static ManagementObject? FindFirst(ManagementScope scope, string wql)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var results = searcher.Get();
        return results.Cast<ManagementObject>().FirstOrDefault();
    }
}
