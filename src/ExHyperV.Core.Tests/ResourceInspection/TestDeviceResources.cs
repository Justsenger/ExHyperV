using System.Management;
using System.Security.Cryptography;
using System.Text;
using ExHyperV.Models;
using ExHyperV.Services;

namespace ExHyperV.Core.Tests.ResourceInspection;

// Resource inspection only selects candidates. Execution must refresh identity and ownership before transfer.
internal static class TestDeviceResources
{
    internal static async Task<ResourceResult> DisksAsync(InspectionConfiguration config)
    {
        var disks = await ReadDisksAsync();
        return DescribeSelection("物理测试盘", config.PhysicalDisk, DiskChoices(disks, ProtectedDisks(config)),
            "未发现物理磁盘。", "当前磁盘均受保护或缺少稳定身份，请查看选择列表中的原因。");
    }

    internal static async Task<ResourceResult> OpticalAsync(InspectionConfiguration config)
    {
        var response = await HostDiskService.GetHostOpticalDrivesAsync();
        if (!response.Success) throw new InvalidOperationException(response.Error);
        var choices = (response.Data ?? []).Select(d => new ResourceChoice(d.PnpDeviceId,
            string.IsNullOrEmpty(d.Drive) ? d.Model : $"{d.Drive} · {d.Model}",
            Fingerprint: Fingerprint(d.PnpDeviceId, d.Model))).ToArray();
        return DescribeSelection("测试光驱", config.OpticalDrive, choices, "未发现光驱，相关测试跳过。", "当前光驱缺少可用身份。");
    }

    private static async Task<List<HostDiskInfo>> ReadDisksAsync()
    {
        // Core's disk inventory includes assignment ownership; require its Hyper-V source to be readable.
        if (ResourceProbes.Query("root/virtualization/v2", "SELECT Name FROM Msvm_VirtualSystemManagementService").Count == 0)
            throw new InvalidOperationException("Hyper-V 管理接口未返回数据，磁盘归属尚待核验。");
        ResourceProbes.Query("root/virtualization/v2", "SELECT DriveNumber FROM Msvm_DiskDrive");
        ResourceProbes.Query("root/virtualization/v2", "SELECT HostResource FROM Msvm_ResourceAllocationSettingData WHERE ResourceSubType='Microsoft:Hyper-V:Physical Disk Drive'");
        var response = await HostDiskService.GetHostDisksAsync();
        if (!response.Success) throw new InvalidOperationException(response.Error);
        return response.Data ?? [];
    }

    internal static ResourceChoice[] DiskChoices(IEnumerable<HostDiskInfo> disks, ISet<int> protectedNumbers)
    {
        var choices = disks.Select(d =>
        {
            var stableId = !string.IsNullOrWhiteSpace(d.UniqueId) ? "id:" + d.UniqueId.Trim()
                : !string.IsNullOrWhiteSpace(d.SerialNumber) ? "serial:" + d.SerialNumber.Trim() : "number:" + d.Number;
            var reason = !d.CanPassthrough ? d.Status : protectedNumbers.Contains(d.Number)
                ? "包含测试程序、缓存或分页文件" : stableId.StartsWith("number:") ? "缺少稳定设备身份" : null;
            return new ResourceChoice(stableId, $"磁盘 {d.Number} · {d.FriendlyName} · {d.SizeGiB:0.0} GiB",
                d.Status, UnavailableReason: reason,
                Fingerprint: Fingerprint(d.UniqueId, d.SerialNumber, d.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }).ToArray();
        var duplicateIds = choices.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return choices.Select(c => duplicateIds.Contains(c.Id) ? c with { UnavailableReason = "设备身份重复" } : c).ToArray();
    }

    internal static ResourceResult DescribeSelection(string label, RegisteredDevice? selected, ResourceChoice[] choices,
        string empty, string blocked)
    {
        var details = choices.Select(c => c.Name + (c.UnavailableReason is null ? "" : " · " + c.UnavailableReason)).ToArray();
        ResourceResult Result(InspectionStatus status, string text) => new(status, text, details, choices);
        if (selected is not null)
        {
            var current = choices.SingleOrDefault(c => c.Id.Equals(selected.Id, StringComparison.OrdinalIgnoreCase));
            if (current is null) return Result(InspectionStatus.Missing, $"已选设备 {selected.Name} 已离开清单，请在上方重新选择。");
            if (current.UnavailableReason is not null) return Result(InspectionStatus.Missing, $"{current.Name}：{current.UnavailableReason}。");
            if (current.Fingerprint != selected.Fingerprint)
                return Result(InspectionStatus.Missing, "设备身份发生变化，请清除原选择、保存后重新指定。");
            return Result(InspectionStatus.Ready, "已选择 · " + current.Name);
        }
        if (choices.Length == 0) return Result(InspectionStatus.Missing, empty);
        if (choices.All(c => c.UnavailableReason is not null)) return Result(InspectionStatus.Missing, blocked);
        return Result(InspectionStatus.Pending, $"请在上方“{label}”选择设备，再点“保存并检查”。");
    }

    private static HashSet<int> ProtectedDisks(InspectionConfiguration config)
    {
        var paths = new List<string> { AppContext.BaseDirectory, config.WorkDirectory!, Environment.SystemDirectory };
        paths.AddRange(ResourceProbes.Query("root/cimv2", "SELECT Name FROM Win32_PageFileUsage").Select(p => p["Name"]));
        var result = new HashSet<int>();
        using var searcher = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT DiskNumber, AccessPaths FROM MSFT_Partition");
        using var rows = searcher.Get();
        foreach (ManagementObject row in rows)
        {
            using (row)
                if ((row["AccessPaths"] as string[] ?? []).Any(root => paths.Any(path =>
                    path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                    || path.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))))
                    result.Add(Convert.ToInt32(row["DiskNumber"]));
        }
        return result;
    }

    private static string Fingerprint(params string[] parts) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(parts.Select(p => p.Trim().ToUpperInvariant())))));
}
