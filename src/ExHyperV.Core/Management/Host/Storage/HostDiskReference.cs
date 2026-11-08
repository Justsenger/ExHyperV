namespace ExHyperV.Services;

/// <summary>查询时选中的宿主磁盘身份；修改时原样回传，不以磁盘编号单独确认设备。</summary>
/// <param name="Number">查询时的宿主磁盘编号；编号变化后需要重新查询。</param>
/// <param name="UniqueId">存储提供程序返回的磁盘唯一标识，未提供时为空。</param>
/// <param name="SerialNumber">磁盘序列号，未提供时为空。</param>
/// <param name="SizeBytes">查询时的容量，单位为字节。</param>
/// <remarks>由 HostDiskInfo.Reference 或 GetDiskReferenceAsync 取得，修改时原样传回。设备标识或容量已变化时拒绝修改；联机、只读状态的变化不影响身份核对。</remarks>
public sealed record HostDiskReference(int Number, string UniqueId, string SerialNumber, long SizeBytes)
{
    internal bool IsValid => Number >= 0 && SizeBytes > 0 &&
        (!string.IsNullOrWhiteSpace(UniqueId) || !string.IsNullOrWhiteSpace(SerialNumber));

    internal bool Matches(HostDiskReference current) => IsValid && Number == current.Number && SizeBytes == current.SizeBytes &&
        string.Equals(UniqueId, current.UniqueId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(SerialNumber, current.SerialNumber, StringComparison.OrdinalIgnoreCase);
}

/// <summary>查询时选中的宿主分区身份，包含所属磁盘；添加或移除盘符时原样回传。</summary>
/// <param name="Disk">所属磁盘的查询身份。</param>
/// <param name="PartitionNumber">查询时的分区编号。</param>
/// <param name="ObjectId">存储系统提供的分区标识，查询后原样保留。</param>
/// <param name="PartitionGuid">GPT 分区 GUID；MBR 等不提供 GUID 的分区为 null。</param>
/// <remarks>通过 GetPartitionReferenceAsync 取得，修改时原样传回。盘符和访问路径的变化不影响分区身份核对。</remarks>
public sealed record HostPartitionReference(HostDiskReference Disk, int PartitionNumber, string ObjectId, Guid? PartitionGuid)
{
    internal bool IsValid => Disk is { IsValid: true } && PartitionNumber > 0 && !string.IsNullOrWhiteSpace(ObjectId);
    internal bool Matches(HostPartitionReference current) => IsValid && Disk.Matches(current.Disk) &&
        PartitionNumber == current.PartitionNumber && string.Equals(ObjectId, current.ObjectId, StringComparison.Ordinal) &&
        PartitionGuid == current.PartitionGuid;
}
