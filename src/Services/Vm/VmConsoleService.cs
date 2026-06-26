using System.Management;
using ExHyperV.Tools;

namespace ExHyperV.Services;

// 控制台支持开关——忠实复刻 Enable/Disable-VMConsoleSupport：增删合成 [鼠标 + 键盘 + 显示] 三件套。
//   显示    = Msvm_SyntheticDisplayControllerSettingData
//   鼠标/键盘 = 带特定 ResourceSubType 的通用 Msvm_ResourceAllocationSettingData
//     （源码 IVMSyntheticMouseControllerSetting 的 WmiName 即 Msvm_ResourceAllocationSettingData；
//      子类型常量取自 DLL 的 VMDeviceSettingTypeMapper）。
// 增删均走 Add/RemoveResourceSettings。需 VM 关机。
public static class VmConsoleService
{
    private const string ServiceWql = "SELECT * FROM Msvm_VirtualSystemManagementService";
    private const string DisplayClass = "Msvm_SyntheticDisplayControllerSettingData";
    private const string RasdClass = "Msvm_ResourceAllocationSettingData";
    private const string MouseSubType = "Microsoft:Hyper-V:Synthetic Mouse";
    private const string KeyboardSubType = "Microsoft:Hyper-V:Synthetic Keyboard";

    // 控制台支持是否启用 = 该 VM 是否存在合成显示控制器（控制台画面所需）
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

    // 启用/禁用控制台支持。Disable 依 cmdlet 顺序删 鼠标→键盘→显示；Enable 缺则补 显示→键盘→鼠标。需 VM 关机；幂等。
    // 整体放进 Task.Run：首个 await 前的同步 WMI(GetVmComputerSystem/GetVirtualSystemManagementService)及循环里的
    // FindForVm/FindDefaultTemplate(searcher.Get) 都跑在调用线程上；被控制台开关在 UI 线程 await 调到就卡界面。
    public static Task<(bool Success, string Message)> SetConsoleSupportAsync(string vmName, bool enable) => Task.Run(async () =>
    {
        if (string.IsNullOrEmpty(vmName)) return (false, Properties.Resources.Error_Vm_NameEmpty);

        using var vm = WmiApi.GetVmComputerSystem(vmName);
        if (vm == null) return (false, Properties.Resources.Error_Net_VmNotFound);
        using var svcForScope = WmiApi.GetVirtualSystemManagementService();
        var scope = svcForScope.Scope;
        string guid = vm["Name"]?.ToString() ?? "";
        string affected = vm.Path.Path;

        // (设置类, 子类型)。显示无子类型(类名即专属)；鼠标/键盘为通用 RASD + 子类型区分。
        var devices = enable
            ? new (string Cls, string? Sub)[] { (DisplayClass, null), (RasdClass, KeyboardSubType), (RasdClass, MouseSubType) }
            : new (string Cls, string? Sub)[] { (RasdClass, MouseSubType), (RasdClass, KeyboardSubType), (DisplayClass, null) };

        foreach (var (cls, sub) in devices)
        {
            string? existingPath = FindForVm(scope, cls, guid, sub);

            if (enable)
            {
                if (existingPath != null) continue;   // 已有 → 跳过(幂等)
                string? tmplXml = FindDefaultTemplate(scope, cls, sub);
                if (tmplXml == null) return (false, $"未找到默认模板: {cls} {sub}");
                var r = await WmiApi.InvokeAsync(ServiceWql, "AddResourceSettings",
                    p => { p["AffectedConfiguration"] = affected; p["ResourceSettings"] = new string[] { tmplXml }; });
                if (!r.Success) return (false, r.Error);
            }
            else
            {
                if (existingPath == null) continue;   // 已无 → 跳过(幂等)
                var r = await WmiApi.InvokeAsync(ServiceWql, "RemoveResourceSettings",
                    p => p["ResourceSettings"] = new string[] { existingPath });
                if (!r.Success) return (false, r.Error);
            }
        }
        return (true, string.Empty);
    });

    // 查该 VM 现有的设备设置 __PATH（无则 null）
    private static string? FindForVm(ManagementScope scope, string cls, string vmGuid, string? subType)
    {
        string wql = $"SELECT * FROM {cls} WHERE InstanceID LIKE 'Microsoft:{vmGuid}%'"
            + (subType != null ? $" AND ResourceSubType = '{subType}'" : "");
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var col = searcher.Get();
        using var obj = col.Cast<ManagementObject>().FirstOrDefault();
        return obj?.Path.Path;
    }

    // 取该设备类型的默认(Default)模板 XML（无则 null）
    private static string? FindDefaultTemplate(ManagementScope scope, string cls, string? subType)
    {
        string wql = $"SELECT * FROM {cls} WHERE InstanceID LIKE '%Default%'"
            + (subType != null ? $" AND ResourceSubType = '{subType}'" : "");
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(wql));
        using var col = searcher.Get();
        using var tmpl = col.Cast<ManagementObject>().FirstOrDefault();
        return tmpl?.GetText(TextFormat.CimDtd20);
    }
}
