using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services
{
    /// <summary>
    /// 宿主物理磁盘操作:枚举可分配磁盘、上下线、读写保护。
    /// 从 VmStorageService 抽出——这些是宿主层关注点,不属于"VM 存储"。
    /// </summary>
    public static class HostDiskService
    {
        // 列出宿主上【全部】物理硬盘，逐块标注状态(可直通/系统盘/已分配给某VM/在线需脱机/只读/USB不支持)；
        // 不可直通的由 CanPassthrough=false 交给 UI 置灰。这样用户看得到每块盘的真实状态，而不是"能直通的才显示"。
        public static async Task<ApiResponse<List<HostDiskInfo>>> GetHostDisksAsync()
        {
            // VM 配置 GUID → VM 名(一次查全,供"已分配给谁"关联)
            var vmNames = new Dictionary<string, string>();
            var vmResp = await WmiApi.QueryAsync(
                "SELECT ConfigurationID, ElementName FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'",
                obj => (Id: obj["ConfigurationID"]?.ToString() ?? string.Empty, Name: obj["ElementName"]?.ToString() ?? string.Empty),
                WmiScope.HyperV);
            if (vmResp.Success && vmResp.Data != null)
                foreach (var vm in vmResp.Data)
                    if (!string.IsNullOrEmpty(vm.Id)) vmNames[vm.Id.ToUpperInvariant()] = vm.Name;

            // 已挂给某 VM 的盘号 → VM 名：物理盘直通 RASD 的 HostResource 解析盘号、InstanceID 里的 GUID 关联 VM。
            var assignedTo = new Dictionary<int, string>();
            var attachedResp = await WmiApi.QueryAsync(
                "SELECT InstanceID, HostResource FROM Msvm_ResourceAllocationSettingData WHERE ResourceSubType = 'Microsoft:Hyper-V:Physical Disk Drive'",
                obj => (Inst: obj["InstanceID"]?.ToString() ?? string.Empty, Hr: (obj["HostResource"] as string[])?.FirstOrDefault() ?? string.Empty),
                WmiScope.HyperV);
            if (attachedResp.Success && attachedResp.Data != null)
                foreach (var a in attachedResp.Data)
                {
                    if (string.IsNullOrEmpty(a.Hr)) continue;   // Definition 模板(Default/Minimum/…)的 HostResource 为空
                    var dm = System.Text.RegularExpressions.Regex.Match(a.Hr, "DeviceID=\"[^\"]*?(\\d+)\"");
                    if (!dm.Success || !int.TryParse(dm.Groups[1].Value, out int dn)) continue;
                    string vmName = string.Empty;
                    var gm = System.Text.RegularExpressions.Regex.Match(a.Inst, "([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})");
                    if (gm.Success) vmNames.TryGetValue(gm.Groups[1].Value.ToUpperInvariant(), out vmName);
                    assignedTo[dn] = vmName ?? string.Empty;
                }

            var diskResp = await WmiApi.QueryCimAsync(
                "SELECT Number, FriendlyName, Size, IsSystem, IsBoot, BusType, IsOffline, IsReadOnly FROM MSFT_Disk",
                obj => new
                {
                    number = Convert.ToInt32(obj["Number"] ?? -1),
                    busType = Convert.ToUInt16(obj["BusType"] ?? 0),
                    isSystem = Convert.ToBoolean(obj["IsSystem"] ?? false),
                    isBoot = Convert.ToBoolean(obj["IsBoot"] ?? false),
                    isOffline = Convert.ToBoolean(obj["IsOffline"] ?? false),
                    isReadOnly = Convert.ToBoolean(obj["IsReadOnly"] ?? false),
                    sizeBytes = Convert.ToInt64(obj["Size"] ?? 0L),
                    friendlyName = obj["FriendlyName"]?.ToString() ?? ""
                },
                WmiScope.Storage);

            if (!diskResp.Success)
                return ApiResponse<List<HostDiskInfo>>.Fail(
                    diskResp.Error, diskResp.Code, diskResp.ErrorSource);

            var result = diskResp.Data!
                .Where(d => d.number >= 0)
                .OrderBy(d => d.number)
                .Select(d =>
                {
                    // 优先级:系统盘 > 已分配 > 只读 > USB > 脱机可直通 > 在线(将脱机)
                    string status;
                    bool can;
                    if (d.isSystem || d.isBoot) { can = false; status = Properties.Resources.Storage_DiskStatus_System; }
                    else if (assignedTo.TryGetValue(d.number, out var vm))
                    {
                        can = false;
                        status = string.Format(Properties.Resources.Storage_DiskStatus_Assigned,
                            string.IsNullOrEmpty(vm) ? "VM" : vm);
                    }
                    else if (d.isReadOnly) { can = false; status = Properties.Resources.Storage_DiskStatus_ReadOnly; }
                    else if (d.busType == 7) { can = false; status = Properties.Resources.Storage_DiskStatus_Usb; }
                    // 脱机/在线未挂都算"可用"(在线盘添加时会自动脱机)
                    else { can = true; status = Properties.Resources.Storage_DiskStatus_Available; }

                    return new HostDiskInfo
                    {
                        Number = d.number,
                        FriendlyName = d.friendlyName,
                        SizeGB = Math.Round(d.sizeBytes / 1073741824.0, 2),
                        Status = status,
                        CanPassthrough = can
                    };
                })
                .ToList();

            return ApiResponse<List<HostDiskInfo>>.Ok(result);
        }

        /// <summary>枚举宿主物理光驱(Win32_CDROMDrive)——用于"物理光驱直通到第 1 代 VM"。
        /// 直通时取 PNPDeviceID 作为 DVD 的 SASD HostResource(与微软 Add-VMDvdDrive 物理直通同款)。</summary>
        public static async Task<ApiResponse<List<HostOpticalInfo>>> GetHostOpticalDrivesAsync()
        {
            var resp = await WmiApi.QueryAsync(
                "SELECT DeviceID, Drive, PNPDeviceID, Caption FROM Win32_CDROMDrive",
                obj => new HostOpticalInfo
                {
                    PnpDeviceId = obj["PNPDeviceID"]?.ToString() ?? string.Empty,
                    Drive = obj["Drive"]?.ToString() ?? string.Empty,
                    Model = obj["Caption"]?.ToString() ?? string.Empty
                },
                WmiScope.CimV2);

            if (!resp.Success)
                return ApiResponse<List<HostOpticalInfo>>.Fail(resp.Error, resp.Code, resp.ErrorSource);

            var result = (resp.Data ?? new List<HostOpticalInfo>())
                .Where(o => !string.IsNullOrWhiteSpace(o.PnpDeviceId))
                .ToList();
            return ApiResponse<List<HostOpticalInfo>>.Ok(result);
        }

        public static async Task<ApiResponse> SetDiskOfflineStatusAsync(int diskNumber, bool isOffline)
        {
            var diskResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}",
                obj => obj,
                WmiScope.Storage);

            if (!diskResp.HasData)
                return ApiResponse.Fail($"Disk {diskNumber} not found", -1, ApiErrorSource.Wmi);

            string methodName = isOffline ? "Offline" : "Online";

            return await WmiApi.InvokeCimMethodAsync(
                diskResp.Data!,
                methodName,
                WmiScope.Storage);
        }

        public static async Task<ApiResponse> SetDiskReadOnlyAsync(int diskNumber, bool isReadOnly)
        {
            var diskResp = await WmiApi.QueryFirstCimAsync(
                $"SELECT * FROM MSFT_Disk WHERE Number = {diskNumber}",
                obj => obj,
                WmiScope.Storage);

            if (!diskResp.HasData)
                return ApiResponse.Fail($"Disk {diskNumber} not found", -1, ApiErrorSource.Wmi);

            return await WmiApi.InvokeCimMethodAsync(
                diskResp.Data!,
                "SetAttributes",
                WmiScope.Storage,
                p => p["IsReadOnly"] = isReadOnly);
        }
    }
}
