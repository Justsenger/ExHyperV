using System.Diagnostics;
using ExHyperV.Models;
using ExHyperV.Properties;
using ExHyperV.Tools.Api;

namespace ExHyperV.Services
{
    public enum MmioCheckResultType { Ok, NeedsConfirmation, Error }

    public class HyperVDDAService
    {
        private const ulong RequiredMmioBytes = 64UL * 1024 * 1024 * 1024; // 64 GiB
        private readonly VmPowerService _powerService = new();

        private string GetPureId(string? instanceId)
        {
            if (string.IsNullOrEmpty(instanceId)) return string.Empty;
            int idx = instanceId.IndexOf(@"\VEN_", StringComparison.OrdinalIgnoreCase);
            return idx >= 0 ? instanceId.Substring(idx) : instanceId;
        }

        public async Task<(List<DeviceInfo> Devices, List<string> VmNames)> GetDdaInfoAsync()
        {
            var deviceList = new List<DeviceInfo>();
            var vmNameList = new List<string>();

            await Task.Run(async () =>
            {
                var pciInfoProvider = new PciInfoProvider();
                await pciInfoProvider.EnsureInitializedAsync();

                try
                {
                    var vmDeviceAssignments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    // ── 1. VM列表 + 已分配设备（Get-VMAssignableDevice，原始逻辑）──────
                    string hostName = WmiApi.Escape(Environment.MachineName);
                    var vmResp = await WmiApi.QueryAsync(
                        $"SELECT ElementName FROM Msvm_ComputerSystem WHERE Name <> '{hostName}'",
                        obj => obj["ElementName"]?.ToString() ?? string.Empty,
                        WmiScope.HyperV);
                    if (vmResp.Success && vmResp.Data != null)
                        vmNameList.AddRange(vmResp.Data.Where(n => !string.IsNullOrEmpty(n)));
                    Debug.WriteLine($"[DDA] vmNames={string.Join(",", vmNameList)}");

                    // WMI 替换 Get-VMAssignableDevice：
                    // Msvm_ComputerSystem → Msvm_VirtualSystemSettingData(Realized) → Msvm_PciExpressSettingData → HostResource
                    foreach (var vmName in vmNameList)
                    {
                        string escapedVmName = WmiApi.Escape(vmName);
                        var settingResp = await WmiApi.QueryAsync(
                            $"SELECT InstanceID FROM Msvm_VirtualSystemSettingData " +
                            $"WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized' " +
                            $"AND ElementName = '{escapedVmName}'",
                            obj => obj["InstanceID"]?.ToString() ?? string.Empty,
                            WmiScope.HyperV);
                        if (!settingResp.Success || settingResp.Data == null) continue;

                        foreach (var settingId in settingResp.Data.Where(s => !string.IsNullOrEmpty(s)))
                        {
                            string escapedSettingId = WmiApi.Escape(settingId);
                            var pciResp = await WmiApi.QueryAsync(
                                $"SELECT HostResource FROM Msvm_PciExpressSettingData " +
                                $"WHERE InstanceID LIKE '{escapedSettingId}\\\\%'",
                                obj => obj["HostResource"] is string[] hr && hr.Length > 0 ? hr[0] : null,
                                WmiScope.HyperV);

                            if (!pciResp.Success || pciResp.Data == null) continue;
                            foreach (var hostResource in pciResp.Data.Where(r => r != null))
                            {
                                var match = System.Text.RegularExpressions.Regex.Match(
                                    hostResource!, @"DeviceID=""Microsoft:[^\\]+\\\\(.+?)""");
                                if (!match.Success) continue;
                                string rawId = match.Groups[1].Value.Replace("\\\\", "\\");
                                string pureId = GetPureId(rawId);
                                if (!string.IsNullOrEmpty(pureId))
                                {
                                    vmDeviceAssignments[pureId] = vmName;
                                    Debug.WriteLine($"[DDA] WMI assigned: pureId='{pureId}' vm='{vmName}'");
                                }
                            }
                        }
                    }
                    Debug.WriteLine($"[DDA] vmDeviceAssignments count={vmDeviceAssignments.Count}");

                    // ── 2. 枚举所有 PCI 设备（Win32Api，替换 Get-PnpDevice）──────────
                    var allPciDevices = await Task.Run(() => Win32Api.GetAllDevices());
                    Debug.WriteLine($"[DDA] allPciDevices count={allPciDevices.Count}");

                    // PCIP（已卸除）且不在 vmDeviceAssignments → 标为 removed
                    foreach (var d in allPciDevices.Where(d =>
                        d.InstanceId.StartsWith("PCIP\\", StringComparison.OrdinalIgnoreCase)))
                    {
                        string pureId = GetPureId(d.InstanceId);
                        if (!string.IsNullOrEmpty(pureId) && !vmDeviceAssignments.ContainsKey(pureId))
                        {
                            vmDeviceAssignments[pureId] = Resources.removed;
                            Debug.WriteLine($"[DDA] PCIP removed: pureId='{pureId}'");
                        }
                    }

                    // ── 3. 构建 DeviceInfo（只用 PCI\* 在线设备）────────
                    var sortedDevices = allPciDevices
                        .Where(d => !string.IsNullOrEmpty(d.Service)
                            && d.InstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
                            && !d.InstanceId.StartsWith("PCIP\\", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(d => d.Service![0])
                        .ToList();

                    foreach (var pciDev in sortedDevices)
                    {
                        if (string.Equals(pciDev.Service, "pci", StringComparison.OrdinalIgnoreCase)) continue;

                        string pureId = GetPureId(pciDev.InstanceId);
                        vmDeviceAssignments.TryGetValue(pureId ?? string.Empty, out string? assignedVal);
                        bool inAssignments = !string.IsNullOrEmpty(pureId) && assignedVal != null;
                        assignedVal ??= string.Empty;

                        Debug.WriteLine($"[DDA] device '{pciDev.InstanceId}' status={pciDev.Status} inAssignments={inAssignments} assignedVal='{assignedVal}'");

                        string status;
                        if (pciDev.Status == "Unknown" && !string.IsNullOrEmpty(pureId))
                        {
                            if (!inAssignments)
                            {
                                Debug.WriteLine($"[DDA]   → SKIP (Unknown not in assignments)");
                                continue;
                            }
                            status = assignedVal;
                            Debug.WriteLine($"[DDA]   → status='{status}'");
                        }
                        else
                        {
                            status = Resources.Host;
                            Debug.WriteLine($"[DDA]   → Host");
                        }

                        string path = pciDev.FirstLocationPath ?? "";
                        string vendor = pciInfoProvider.GetVendorFromInstanceId(pciDev.InstanceId, pciDev.Class);
                        deviceList.Add(new DeviceInfo(pciDev.FriendlyName, status, pciDev.Class, pciDev.InstanceId, path, vendor));
                    }

                    // 按类型排序
                    deviceList.Sort((a, b) =>
                    {
                        static int GetOrder(DeviceInfo d)
                        {
                            string cls = d.ClassType ?? "";
                            string name = d.FriendlyName ?? "";
                            if (cls.Equals("Display", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("GPU", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("显卡", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Graphics", StringComparison.OrdinalIgnoreCase))
                                return 0;
                            if (name.Contains("Audio", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("声音", StringComparison.OrdinalIgnoreCase) ||
                                cls.Equals("Media", StringComparison.OrdinalIgnoreCase) ||
                                cls.Equals("Sound", StringComparison.OrdinalIgnoreCase))
                                return 1;
                            if (cls.Equals("Net", StringComparison.OrdinalIgnoreCase) ||
                                cls.Equals("NetClient", StringComparison.OrdinalIgnoreCase))
                                return 2;
                            if (cls.Equals("USB", StringComparison.OrdinalIgnoreCase))
                                return 3;
                            if (cls.Equals("SCSIAdapter", StringComparison.OrdinalIgnoreCase) ||
                                cls.Equals("HDC", StringComparison.OrdinalIgnoreCase) ||
                                cls.Equals("DiskDrive", StringComparison.OrdinalIgnoreCase))
                                return 4;
                            return 5;
                        }
                        return GetOrder(a).CompareTo(GetOrder(b));
                    });

                    Debug.WriteLine($"[DDA] deviceList final count={deviceList.Count}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DDA] EXCEPTION: {ex}");
                    deviceList.Clear();
                    vmNameList.Clear();
                }
            });

            return (deviceList, vmNameList);
        }

        public Task<bool> IsServerOperatingSystemAsync()
            => Task.FromResult(HyperVHostService.IsServerSystem());

        public async Task<(MmioCheckResultType Result, string Message)> CheckMmioSpaceAsync(string vmName)
        {
            string escapedVmName = WmiApi.Escape(vmName);
            var resp = await WmiApi.QueryAsync(
                $"SELECT HighMmioGapSize FROM Msvm_VirtualSystemSettingData " +
                $"WHERE ElementName = '{escapedVmName}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                obj => obj["HighMmioGapSize"],
                WmiScope.HyperV);

            if (!resp.Success || resp.Data == null || resp.Data.Count == 0)
                return (MmioCheckResultType.Error, Properties.Resources.Error_CannotGetVmInfo);

            try
            {
                ulong highMmioGapSizeMb = Convert.ToUInt64(resp.Data[0]);
                ulong currentMmioBytes = highMmioGapSizeMb * 1048576UL;
                if (currentMmioBytes < RequiredMmioBytes)
                {
                    long currentMmioGB = (long)(currentMmioBytes / (1024 * 1024 * 1024));
                    string message = string.Format(Properties.Resources.Warning_LowMmioSpace_ConfirmExpand, vmName, currentMmioGB);
                    return (MmioCheckResultType.NeedsConfirmation, message);
                }
                return (MmioCheckResultType.Ok, Properties.Resources.Info_MmioSpaceSufficient);
            }
            catch (Exception ex) { return (MmioCheckResultType.Error, ex.Message); }
        }

        public async Task<bool> UpdateMmioSpaceAsync(string vmName)
        {
            if (!await EnsureVmStoppedAsync(vmName)) return false;

            ulong requiredMb = RequiredMmioBytes / 1048576UL;
            var setResult = await WmiApi.WithObjectAsync(
                wql: $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                modifier: obj => obj["HighMmioGapSize"] = requiredMb,
                submitMethod: "ModifySystemSettings",
                submitParamName: "SystemSettings",
                wrapInArray: false,
                scope: WmiScope.HyperV,
                serviceWql: "SELECT * FROM Msvm_VirtualSystemManagementService");

            return setResult.Success;
        }

        public async Task<(bool Success, string? ErrorMessage)> ExecuteDdaOperationAsync(
            string targetVmName, string currentVmName, string instanceId, string path,
            IProgress<string>? progress = null)
        {
            try
            {
                var operations = DDACommands(targetVmName, instanceId, path, currentVmName);
                if (operations.Count == 0) return (true, null);

                // 只有当操作列表中包含 SetGuestCache（cpucache）时才需要关机
                // SetGuestCache 是唯一强制要求 VM Off 的 ModifySystemSettings 调用
                // 如果该 VM 的 GuestControlledCacheTypes 已经是 true，DDACommands 不会加入此步骤
                bool needsStop = operations.Any(op => op.Message == Resources.cpucache);
                if (needsStop)
                {
                    progress?.Report("正在关闭虚拟机...");
                    if (!await EnsureVmStoppedAsync(targetVmName))
                        return (false, "无法关闭虚拟机，操作终止。");
                }

                foreach (var operation in operations)
                {
                    progress?.Report(operation.Message);
                    var logOutput = await ExecuteOperationAsync(operation, instanceId);
                    var errorLogs = logOutput.Where(log => log.Contains("Error", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (errorLogs.Any())
                    {
                        var errorMessage = string.Join(Environment.NewLine, errorLogs);
                        if (targetVmName == Resources.Host)
                            Win32Api.EnablePnpDevice(instanceId);
                        return (false, errorMessage);
                    }
                }
                return (true, null);
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        private async Task<bool> EnsureVmStoppedAsync(string vmName)
        {
            string escapedVmName = WmiApi.Escape(vmName);

            var stateResp = await WmiApi.QueryAsync(
                $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE ElementName = '{escapedVmName}'",
                obj => Convert.ToUInt16(obj["EnabledState"]),
                WmiScope.HyperV);

            if (!stateResp.Success || stateResp.Data == null) return false;
            if (!stateResp.Data.Any(s => s == 2)) return true; // 已关机，无需操作

            await _powerService.ExecuteControlActionAsync(vmName, "Stop");

            // 等待关机完成（最多30秒）
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000);
                var checkResp = await WmiApi.QueryAsync(
                    $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE ElementName = '{escapedVmName}'",
                    obj => Convert.ToUInt16(obj["EnabledState"]),
                    WmiScope.HyperV);
                if (checkResp.Data?.Any(s => s == 3) == true) return true;
            }
            return false; // 30秒超时
        }

        // ── DDA 操作类型 ──────────────────────────────────────────────
        private enum DdaOpType { Wmi, PnpEnable, PnpDisable }

        private record DdaOperation(
            string Message,
            DdaOpType Type,
            Func<Task<ApiResponse>>? WmiAction = null);

        private async Task<List<string>> ExecuteOperationAsync(DdaOperation op, string instanceId)
        {
            switch (op.Type)
            {
                case DdaOpType.PnpEnable:
                    {
                        var r = Win32Api.EnablePnpDevice(instanceId);
                        return r.Success ? new List<string>() : new List<string> { $"Error: {r.Error}" };
                    }
                case DdaOpType.PnpDisable:
                    {
                        var r = Win32Api.DisablePnpDevice(instanceId);
                        return r.Success ? new List<string>() : new List<string> { $"Error: {r.Error}" };
                    }
                case DdaOpType.Wmi:
                    {
                        var r = await op.WmiAction!();
                        return r.Success ? new List<string>() : new List<string> { $"Error: {r.Error}" };
                    }
                default:
                    return new List<string>();
            }
        }

        private List<DdaOperation> DDACommands(string Vmname, string instanceId, string path, string Nowname)
        {
            var ops = new List<DdaOperation>();

            // WMI：Mount-VMHostAssignableDevice
            DdaOperation MountDevice(string devInstanceId, string locationPath) => new(
                Resources.mounting, DdaOpType.Wmi,
                WmiAction: () => WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_AssignableDeviceService",
                    "MountAssignableDevice",
                    p => {
                        string pcipId = devInstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
                            ? "PCIP\\" + devInstanceId.Substring(4)
                            : devInstanceId;
                        p["DeviceInstancePath"] = pcipId;
                        p["DeviceLocationPath"] = locationPath;
                    },
                    WmiScope.HyperV));

            // WMI：Add-VMAssignableDevice
            // 流程：拿 PciExpress Default 模板 → 设置 HostResource = PCIP 设备路径 → AddResourceSettings
            DdaOperation AddDevice(string devInstanceId, string locationPath, string vmName) => new(
                Resources.mounting, DdaOpType.Wmi,
                WmiAction: async () =>
                {
                    var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);

                    // 1. 拿 PciExpress Default 模板
                    using var templateSearcher = new System.Management.ManagementObjectSearcher(ms,
                        new System.Management.ObjectQuery(
                            "SELECT * FROM Msvm_PciExpressSettingData WHERE InstanceID LIKE '%Default%'"));
                    using var templateCol = templateSearcher.Get();
                    using var template = templateCol.Cast<System.Management.ManagementObject>().FirstOrDefault();
                    if (template is null) return ApiResponse.Fail("Cannot find PciExpress Default template");

                    // 2. 拿 PCIP 设备的 WMI 路径（用 LocationPath 查询）
                    string escapedLocationPath = WmiApi.Escape(locationPath);
                    using var pcipSearcher = new System.Management.ManagementObjectSearcher(ms,
                        new System.Management.ObjectQuery(
                            $"SELECT * FROM Msvm_PciExpress WHERE LocationPath='{escapedLocationPath}'"));
                    using var pcipCol = pcipSearcher.Get();
                    using var pcipDevice = pcipCol.Cast<System.Management.ManagementObject>().FirstOrDefault();
                    if (pcipDevice is null) return ApiResponse.Fail($"Cannot find PciExpress device at: {locationPath}");

                    template["HostResource"] = new string[] { pcipDevice["__PATH"]?.ToString() ?? "" };

                    // 3. 拿 VM VirtualSystemSettingData 路径
                    string escapedVmName = WmiApi.Escape(vmName);
                    using var vmSettingSearcher = new System.Management.ManagementObjectSearcher(ms,
                        new System.Management.ObjectQuery(
                            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName='{escapedVmName}' AND VirtualSystemType='Microsoft:Hyper-V:System:Realized'"));
                    using var vmSettingCol = vmSettingSearcher.Get();
                    using var vmSetting = vmSettingCol.Cast<System.Management.ManagementObject>().FirstOrDefault();
                    if (vmSetting is null) return ApiResponse.Fail($"Cannot find VM setting: {vmName}");

                    // 4. AddResourceSettings
                    return await WmiApi.InvokeAsync(
                        "SELECT * FROM Msvm_VirtualSystemManagementService",
                        "AddResourceSettings",
                        p => {
                            p["AffectedConfiguration"] = vmSetting["__PATH"]?.ToString();
                            p["ResourceSettings"] = new string[] { template.GetText(System.Management.TextFormat.CimDtd20) };
                        },
                        WmiScope.HyperV);
                });

            // WMI：Dismount-VMHostAssignableDevice
            DdaOperation DismountDevice(string devInstanceId, string locationPath) => new(
                Resources.Dismountdevice, DdaOpType.Wmi,
                WmiAction: () => WmiApi.InvokeAsync(
                    "SELECT * FROM Msvm_AssignableDeviceService",
                    "DismountAssignableDevice",
                    p => {
                        var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);
                        using var cls = new System.Management.ManagementClass(ms, new System.Management.ManagementPath("Msvm_AssignableDeviceDismountSettingData"), null);
                        using var inst = cls.CreateInstance();
                        inst["DeviceInstancePath"] = devInstanceId;
                        inst["DeviceLocationPath"] = locationPath;
                        inst["RequireAcsSupport"] = false;
                        inst["RequireDeviceMitigations"] = false;
                        p["DismountSettingData"] = inst.GetText(System.Management.TextFormat.CimDtd20);
                    },
                    WmiScope.HyperV));

            // WMI：Remove-VMAssignableDevice
            // 流程：从 VM 的 Msvm_PciExpressSettingData 找到对应 HostResource 的设备设置 → RemoveResourceSettings
            DdaOperation RemoveDevice(string devInstanceId, string locationPath, string vmName) => new(
                Resources.Dismountdevice, DdaOpType.Wmi,
                WmiAction: async () =>
                {
                    var ms = WmiConnectionCache.GetManagementScope(WmiScope.HyperV, WmiContext.Local);

                    // 1. 拿 VM 的 Realized VirtualSystemSettingData
                    string escapedVmName = WmiApi.Escape(vmName);
                    using var vmSettingSearcher = new System.Management.ManagementObjectSearcher(ms,
                        new System.Management.ObjectQuery(
                            $"SELECT InstanceID FROM Msvm_VirtualSystemSettingData WHERE ElementName='{escapedVmName}' AND VirtualSystemType='Microsoft:Hyper-V:System:Realized'"));
                    using var vmSettingCol = vmSettingSearcher.Get();
                    using var vmSetting = vmSettingCol.Cast<System.Management.ManagementObject>().FirstOrDefault();
                    if (vmSetting is null) return ApiResponse.Fail($"Cannot find VM setting: {vmName}");

                    string settingId = vmSetting["InstanceID"]?.ToString() ?? "";
                    string escapedSettingId = WmiApi.Escape(settingId);

                    // 2. 找到该 VM 下匹配 pureId 的 PciExpressSettingData
                    using var pciSettingSearcher = new System.Management.ManagementObjectSearcher(ms,
                        new System.Management.ObjectQuery(
                            $"SELECT * FROM Msvm_PciExpressSettingData WHERE InstanceID LIKE '{escapedSettingId}\\\\%'"));
                    using var pciSettingCol = pciSettingSearcher.Get();

                    string pureId = GetPureId(devInstanceId);
                    System.Management.ManagementObject? targetSetting = null;
                    foreach (System.Management.ManagementObject obj in pciSettingCol)
                    {
                        if (obj["HostResource"] is string[] hr && hr.Length > 0
                            && hr[0].Contains(pureId.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase))
                        {
                            targetSetting = obj;
                            break;
                        }
                        obj.Dispose();
                    }
                    if (targetSetting is null) return ApiResponse.Fail($"Cannot find PciExpress setting for location: {locationPath}");

                    using (targetSetting)
                    {
                        // 3. RemoveResourceSettings
                        return await WmiApi.InvokeAsync(
                            "SELECT * FROM Msvm_VirtualSystemManagementService",
                            "RemoveResourceSettings",
                            p => p["ResourceSettings"] = new string[] { targetSetting["__PATH"]?.ToString() ?? "" },
                            WmiScope.HyperV);
                    }
                });

            // WMI：Set-VM -AutomaticStopAction TurnOff（AutomaticShutdownAction=2）
            // 注意：AutomaticShutdownAction 可在 VM 运行时修改，无需关机
            DdaOperation SetAutoStop(string vmName) => new(
                Resources.string5, DdaOpType.Wmi,
                WmiAction: () => WmiApi.WithObjectAsync(
                    $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                    obj => obj["AutomaticShutdownAction"] = (ushort)2,
                    submitMethod: "ModifySystemSettings",
                    submitParamName: "SystemSettings",
                    wrapInArray: false,
                    scope: WmiScope.HyperV,
                    serviceWql: "SELECT * FROM Msvm_VirtualSystemManagementService"));

            // WMI：Set-VM -GuestControlledCacheTypes $true
            // 注意：此字段修改需要 VM 处于 Off 状态
            DdaOperation SetGuestCache(string vmName) => new(
                Resources.cpucache, DdaOpType.Wmi,
                WmiAction: () => WmiApi.WithObjectAsync(
                    $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE ElementName = '{WmiApi.Escape(vmName)}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                    obj => obj["GuestControlledCacheTypes"] = true,
                    submitMethod: "ModifySystemSettings",
                    submitParamName: "SystemSettings",
                    wrapInArray: false,
                    scope: WmiScope.HyperV,
                    serviceWql: "SELECT * FROM Msvm_VirtualSystemManagementService"));

            // ── 检查 GuestControlledCacheTypes 是否已设置，已设置则跳过（同时避免关机）──
            bool guestCacheAlreadySet = false;
            if (Vmname != Resources.Host)
            {
                string escapedVm = WmiApi.Escape(Vmname);
                var cacheResp = WmiApi.QueryAsync(
                    $"SELECT GuestControlledCacheTypes FROM Msvm_VirtualSystemSettingData " +
                    $"WHERE ElementName = '{escapedVm}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                    obj => obj["GuestControlledCacheTypes"] is bool b && b,
                    WmiScope.HyperV).GetAwaiter().GetResult();
                guestCacheAlreadySet = cacheResp.Success && (cacheResp.Data?.Any(x => x) ?? false);
            }

            if (Nowname == Resources.removed && Vmname == Resources.Host)
            {
                // 归还主机：Mount + PnpEnable，无需关机，无需改 VM 配置
                ops.Add(MountDevice(instanceId, path));
                ops.Add(new(Resources.enabling, DdaOpType.PnpEnable));
            }
            else if (Nowname == Resources.removed && Vmname != Resources.Host)
            {
                ops.Add(SetAutoStop(Vmname));
                if (!guestCacheAlreadySet) ops.Add(SetGuestCache(Vmname));
                ops.Add(AddDevice(instanceId, path, Vmname));
            }
            else if (Nowname == Resources.Host)
            {
                ops.Add(SetAutoStop(Vmname));
                if (!guestCacheAlreadySet) ops.Add(SetGuestCache(Vmname));
                ops.Add(new(Resources.Disabledevice, DdaOpType.PnpDisable));
                ops.Add(DismountDevice(instanceId, path));
                ops.Add(AddDevice(instanceId, path, Vmname));
            }
            else if (Vmname != Resources.Host && Nowname != Resources.Host)
            {
                ops.Add(SetAutoStop(Vmname));
                if (!guestCacheAlreadySet) ops.Add(SetGuestCache(Vmname));
                ops.Add(RemoveDevice(instanceId, path, Nowname));
                ops.Add(AddDevice(instanceId, path, Vmname));
            }
            else if (Vmname == Resources.Host && Nowname != Resources.Host)
            {
                // 从 VM 归还主机：Remove + Mount + PnpEnable，无需改目标配置
                ops.Add(RemoveDevice(instanceId, path, Nowname));
                ops.Add(MountDevice(instanceId, path));
                ops.Add(new(Resources.enabling, DdaOpType.PnpEnable));
            }

            return ops;
        }
    }
}