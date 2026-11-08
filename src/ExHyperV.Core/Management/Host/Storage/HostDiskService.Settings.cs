using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

public static partial class HostDiskService
{

    /// <summary>修改新建虚拟磁盘的宿主默认目录，不移动已有磁盘。</summary>
    /// <returns>Hyper-V 配置提交响应。</returns>
    /// <param name="path">宿主上的默认磁盘目录路径。</param>
    public static Task<ApiResponse> SetDefaultVirtualHardDiskPathAsync(string path) =>
        HyperVHostSettingsService.ModifyAsync(obj => obj["DefaultVirtualHardDiskPath"] = path);

}
