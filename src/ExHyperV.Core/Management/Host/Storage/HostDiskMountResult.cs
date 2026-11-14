using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>映像挂载结果，分别表达挂载状态、磁盘发现和清理责任。</summary>
/// <param name="ImagePath">映像完整路径。</param>
/// <param name="IsMounted">已确认挂载为 true，已确认未挂载为 false，无法确认时为 null。</param>
/// <param name="DiskNumber">已发现的宿主磁盘编号；未知为 null。</param>
/// <param name="CreatedByCall">挂载是否由本次调用建立；释放使用期时清理本次新增挂载，保留原有挂载。</param>
/// <param name="Error">挂载或发现失败说明；为空表示完整成功。</param>
/// <param name="IsReadOnly">宿主磁盘的实际访问模式：true 只读，false 读写，null 未取得。已有挂载沿用原模式，可能与请求不同。</param>
/// <param name="AccessError">未能取得实际访问模式的原因；查询成功时为空字符串。</param>
public sealed record HostDiskMountResult(string ImagePath, bool? IsMounted, int? DiskNumber,
    bool CreatedByCall, string Error, bool? IsReadOnly = null, string AccessError = "")
{
    /// <summary>挂载及宿主磁盘发现是否成功；实际访问模式见 IsReadOnly。</summary>
    public bool Success => IsMounted == true && DiskNumber.HasValue && Error.Length == 0;
    /// <summary>挂载状态是否未知；为 true 时需重新查询映像状态。</summary>
    public bool RequiresInspection => IsMounted == null;
}
