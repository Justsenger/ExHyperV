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
        public static async Task<ApiResponse<List<HostDiskInfo>>> GetHostDisksAsync()
        {
            var usedResp = await WmiApi.QueryAsync(
                "SELECT DriveNumber FROM Msvm_DiskDrive WHERE DriveNumber >= 0",
                obj => WmiApi.Prop<int>(obj, "DriveNumber", -1),
                WmiScope.HyperV);

            var usedDiskNumbers = new HashSet<int>(
                usedResp.Success && usedResp.Data != null
                    ? usedResp.Data.Where(n => n >= 0)
                    : Enumerable.Empty<int>());

            var diskResp = await WmiApi.QueryCimAsync(
                "SELECT Number, FriendlyName, Size, IsSystem, IsBoot, BusType " +
                "FROM MSFT_Disk",
                obj =>
                {
                    int number = Convert.ToInt32(obj["Number"] ?? -1);
                    ushort busType = Convert.ToUInt16(obj["BusType"] ?? 0);
                    bool isSystem = Convert.ToBoolean(obj["IsSystem"] ?? false);
                    bool isBoot = Convert.ToBoolean(obj["IsBoot"] ?? false);
                    long sizeBytes = Convert.ToInt64(obj["Size"] ?? 0L);
                    string friendlyName = obj["FriendlyName"]?.ToString() ?? "";
                    return new { number, busType, isSystem, isBoot, sizeBytes, friendlyName };
                },
                WmiScope.Storage);

            if (!diskResp.Success)
                return ApiResponse<List<HostDiskInfo>>.Fail(
                    diskResp.Error, diskResp.Code, diskResp.ErrorSource);

            var result = diskResp.Data!
                .Where(d => d.number >= 0
                         && d.busType != 7
                         && !d.isSystem
                         && !d.isBoot
                         && !usedDiskNumbers.Contains(d.number))
                .Select(d => new HostDiskInfo
                {
                    Number = d.number,
                    FriendlyName = d.friendlyName,
                    SizeGB = Math.Round(d.sizeBytes / 1073741824.0, 2)
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
