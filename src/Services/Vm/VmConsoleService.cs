using System.Management;
using ExHyperV.Tools;

namespace ExHyperV.Services;

// 控制台支持开关。对应 PowerShell Enable/Disable-VMConsoleSupport——增删合成显示控制器
// Msvm_SyntheticDisplayControllerSettingData（源码 VMVideo.AddSyntheticDisplayController 取资源池
// DefaultCapability 模板后 AddDeviceSetting；Remove 走 RemoveResourceSettings，与删网卡同一套）。
// 该控制器决定 VM 有无控制台画面，移除即禁用控制台（适合已配置串流/RDP 的场景）。需 VM 关机。
public static class VmConsoleService
{
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
    private const string DisplayClass = "Msvm_SyntheticDisplayControllerSettingData";

    // 控制台支持是否启用 = 该 VM 是否存在合成显示控制器
    public static async Task<bool> IsConsoleSupportEnabledAsync(string vmName)
    {
        if (string.IsNullOrEmpty(vmName)) return false;
        var vmResp = await WmiApi.QueryFirstAsync(
            $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{WmiApi.Escape(vmName)}'",
            obj => obj["Name"]?.ToString());
        if (!vmResp.HasData) return false;
        var resp = await WmiApi.QueryFirstAsync(
            $"SELECT InstanceID FROM {DisplayClass} WHERE InstanceID LIKE 'Microsoft:{vmResp.Data}%'",
            obj => obj["InstanceID"]?.ToString());
        return resp.HasData;
    }

    // 启用=加默认合成显示控制器；禁用=移除之。需 VM 关机；幂等。
    public static async Task<(bool Success, string Message)> SetConsoleSupportAsync(string vmName, bool enable)
    {
        if (string.IsNullOrEmpty(vmName)) return (false, "VM name is empty");

        using var vm = WmiApi.GetVmComputerSystem(vmName);
        if (vm == null) return (false, Properties.Resources.Error_Net_VmNotFound);
        using var svcForScope = WmiApi.GetVirtualSystemManagementService();

        using var existSearcher = new ManagementObjectSearcher(svcForScope.Scope,
            new ObjectQuery($"SELECT * FROM {DisplayClass} WHERE InstanceID LIKE 'Microsoft:{vm["Name"]}%'"));
        using var existCol = existSearcher.Get();
        using var existing = existCol.Cast<ManagementObject>().FirstOrDefault();

        if (enable)
        {
            if (existing != null) return (true, string.Empty);   // 已有，幂等

            using var tmplSearcher = new ManagementObjectSearcher(svcForScope.Scope,
                new ObjectQuery($"SELECT * FROM {DisplayClass} WHERE InstanceID LIKE '%Default%'"));
            using var tmplCol = tmplSearcher.Get();
            using var tmpl = tmplCol.Cast<ManagementObject>().FirstOrDefault();
            if (tmpl == null) return (false, "Default display controller template not found");

            string xml = tmpl.GetText(TextFormat.CimDtd20);
            var addResult = await WmiApi.InvokeAsync(ServiceWql, "AddResourceSettings",
                p => { p["AffectedConfiguration"] = vm.Path.Path; p["ResourceSettings"] = new string[] { xml }; });
            return addResult.Success ? (true, string.Empty) : (false, addResult.Error);
        }
        else
        {
            if (existing == null) return (true, string.Empty);   // 已无，幂等

            var rmResult = await WmiApi.InvokeAsync(ServiceWql, "RemoveResourceSettings",
                p => p["ResourceSettings"] = new string[] { existing.Path.Path });
            return rmResult.Success ? (true, string.Empty) : (false, rmResult.Error);
        }
    }
}
