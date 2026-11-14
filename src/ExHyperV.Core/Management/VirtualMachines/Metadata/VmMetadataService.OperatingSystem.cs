using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class VmMetadataService
{

    /// <summary>按稳定 GUID 将用于界面识别的操作系统分类保存到 HostOnlyItems。</summary>
    /// <param name="vmId">目标虚拟机的稳定 GUID。</param>
    /// <param name="osType">操作系统分类，不改变来宾系统本身。</param>
    /// <returns>操作系统分类的保存结果。</returns>
    public static Task<ApiResponse> SetOperatingSystemTypeAsync(Guid vmId, string osType)
    {
        return VmMetadataService.SetAsync(vmId, VmMetadataService.OsTypeKey, osType);
    }

}
