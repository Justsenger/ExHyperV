using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Core.Tests.ResourceInspection;

namespace ExHyperV.Core.Tests.TestExecution;

internal sealed class PhysicalDiskLease(HostDiskInfo disk, HostStateJournal journal) : IAsyncDisposable
{
    internal HostDiskInfo Disk { get; } = disk;
    internal static async Task<PhysicalDiskLease> AcquireAsync(ApiCaseContext c)
    {
        var selected = c.Configuration.PhysicalDisk ?? throw new ApiCheckSkippedException("第一步尚未选择闲置物理测试盘。");
        var verified = await TestDeviceResources.DisksAsync(c.Configuration);
        if (verified.Status != InspectionStatus.Ready) throw new ApiCheckSkippedException(verified.Detail);
        var list = await HostDiskService.GetHostDisksAsync(); c.Require(list.HasData, list.Error);
        var disk = list.Data!.Single(d => (!string.IsNullOrWhiteSpace(d.UniqueId) ? "id:" + d.UniqueId.Trim() : "serial:" + d.SerialNumber.Trim()).Equals(selected.Id, StringComparison.OrdinalIgnoreCase));
        var journal = new HostStateJournal(c); await journal.DiskAsync(disk.Reference);
        c.Evidence.Add($"已核对指定物理盘身份、系统盘标志及占用状态：{disk.Number}"); return new(disk, journal);
    }
    public ValueTask DisposeAsync() => journal.DisposeAsync();
}
