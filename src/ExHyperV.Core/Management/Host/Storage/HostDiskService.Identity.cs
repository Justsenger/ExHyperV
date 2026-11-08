using System.Management;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class HostDiskService
{
    /// <summary>读取当前编号对应的磁盘身份，供调用方选择并在后续修改时原样回传。</summary>
    /// <param name="diskNumber">当前宿主磁盘编号，例如 0 表示磁盘 0。</param>
    /// <returns>磁盘身份；不存在或无法取得有效标识时失败。</returns>
    public static async Task<ApiResponse<HostDiskReference>> GetDiskReferenceAsync(int diskNumber)
    {
        if (diskNumber < 0) return ApiResponse<HostDiskReference>.Fail("磁盘编号不能为负数。", kind: ApiErrorKind.InvalidArgument);
        var read = await WmiApi.QueryFirstCimAsync(
            $"SELECT Number, UniqueId, SerialNumber, Size FROM MSFT_Disk WHERE Number = {diskNumber}", ReadDiskReference);
        if (!read.Success) return read;
        if (!read.HasData) return ApiResponse<HostDiskReference>.Fail("磁盘不存在。", kind: ApiErrorKind.NotFound);
        return read.Data.IsValid ? read : ApiResponse<HostDiskReference>.Fail("磁盘未提供足够的身份信息。", kind: ApiErrorKind.Unsupported);
    }

    /// <summary>读取所选磁盘上的分区身份，供添加或移除盘符使用。</summary>
    /// <param name="disk">已经选中的磁盘身份；读取前后均确认仍是同一设备。</param>
    /// <param name="partitionNumber">该磁盘上的分区编号。</param>
    /// <returns>分区身份；查询失败、身份变化或缺失时不返回可修改的引用。</returns>
    public static async Task<ApiResponse<HostPartitionReference>> GetPartitionReferenceAsync(HostDiskReference disk, int partitionNumber)
    {
        if (disk is not { IsValid: true } || partitionNumber < 1)
            return ApiResponse<HostPartitionReference>.Fail("磁盘身份或分区编号无效。", kind: ApiErrorKind.InvalidArgument);
        try
        {
            return await WithDiskLockAsync(disk.Number, () => GetPartitionReferenceCoreAsync(disk, partitionNumber));
        }
        catch (Exception error) { return PartitionFailure(LockFailure(error)); }
    }

    internal static async Task<ApiResponse<HostPartitionReference>> GetPartitionReferenceCoreAsync(HostDiskReference disk, int partitionNumber)
    {
        var before = await CheckDiskReferenceAsync(disk);
        if (!before.Success) return PartitionFailure(before);
        var part = await WmiApi.QueryFirstCimAsync(
            $"SELECT DiskNumber, PartitionNumber, ObjectId, Guid FROM MSFT_Partition WHERE DiskNumber = {disk.Number} AND PartitionNumber = {partitionNumber}",
            obj => ReadPartitionReference(disk, obj));
        if (!part.Success) return part;
        if (!part.HasData) return ApiResponse<HostPartitionReference>.Fail("分区不存在。", kind: ApiErrorKind.NotFound);
        if (!part.Data.IsValid) return ApiResponse<HostPartitionReference>.Fail("分区未提供有效身份。", kind: ApiErrorKind.Unsupported);
        var after = await CheckDiskReferenceAsync(disk);
        return after.Success ? part : PartitionFailure(after);
    }

    internal static ApiResponse ValidateDiskReference(HostDiskReference expected, HostDiskReference current)
        => expected.Matches(current) ? ApiResponse.Ok() :
            ApiResponse.Fail("所选磁盘身份已变化，未修改同编号的替代设备。", kind: ApiErrorKind.Conflict);

    internal static ApiResponse ValidatePartitionReference(HostPartitionReference expected, HostPartitionReference current)
        => expected.Matches(current) ? ApiResponse.Ok() :
            ApiResponse.Fail("所选分区身份已变化，未修改替代分区。", kind: ApiErrorKind.Conflict);

    private static HostDiskReference ReadDiskReference(ManagementObject obj) => new(
        Convert.ToInt32(obj["Number"]), obj["UniqueId"]?.ToString() ?? "",
        obj["SerialNumber"]?.ToString()?.Trim() ?? "", Convert.ToInt64(obj["Size"]));

    private static HostPartitionReference ReadPartitionReference(HostDiskReference disk, ManagementObject obj)
    {
        string? guid = obj["Guid"]?.ToString();
        return new(disk, Convert.ToInt32(obj["PartitionNumber"]), obj["ObjectId"]?.ToString() ?? "",
            string.IsNullOrWhiteSpace(guid) ? null : Guid.Parse(guid));
    }

    private static async Task<ApiResponse> CheckDiskReferenceAsync(HostDiskReference expected)
    {
        var current = await GetDiskReferenceAsync(expected.Number);
        return current.HasData ? ValidateDiskReference(expected, current.Data) :
            ApiResponse.Fail(current.Error, current.Code, current.ErrorSource, current.Exception, current.ErrorKind);
    }

    internal static async Task<ApiResponse> ModifyPartitionAccessPathAsync(HostPartitionReference partition, char letter, bool add)
    {
        HostResourceLock.AssertHeld(HostDiskResource.Disk(partition.Disk.Number));
        var disk = await CheckDiskReferenceAsync(partition.Disk);
        if (!disk.Success) return disk;
        return await WmiApi.InvokeAsync(
            $"SELECT * FROM MSFT_Partition WHERE DiskNumber = {partition.Disk.Number} AND PartitionNumber = {partition.PartitionNumber}",
            add ? "AddAccessPath" : "RemoveAccessPath", p => p["AccessPath"] = $"{char.ToUpperInvariant(letter)}:\\",
            WmiScope.Storage, validateTarget: obj => ValidatePartitionReference(partition, ReadPartitionReference(partition.Disk, obj)));
    }

    private static ApiResponse<HostPartitionReference> PartitionFailure(ApiResponse failure)
        => ApiResponse<HostPartitionReference>.Fail(failure.Error, failure.Code, failure.ErrorSource, failure.Exception, failure.ErrorKind);
}
