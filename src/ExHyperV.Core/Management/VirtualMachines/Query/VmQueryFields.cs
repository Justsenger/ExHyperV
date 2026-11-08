namespace ExHyperV.Services;

/// <summary>Core 当前支持的全部可查询字段；目录本身不执行任何主机查询。</summary>
public static class VmQueryFields
{
    /// <summary>按领域排列的完整只读目录，包含标识、名称、所属集合和结果类型；可以用于生成查询选择界面。</summary>
    public static IReadOnlyList<VmQueryField> All { get; } = Array.AsReadOnly(new VmQueryField[][]
    {
        VmIdentityFields.Items,
        VmGroupFields.Items,
        VmMetadataFields.Items,
        VmProcessorFields.Items,
        VmProcessorMetricFields.Items,
        VmAffinityFields.Items,
        VmMemoryFields.Items,
        VmStorageFields.Items,
        VmStorageGlobalFields.Items,
        VmNetworkFields.Items,
        VmGpuFields.Items,
        VmGpuPerformanceFields.Items,
        VmPcieFields.Items,
        VmPcieDeviceFields.Items,
        VmBootFields.Items,
        VmBootGlobalFields.Items,
        VmSecurityFields.Items,
        VmDisplayFields.Items,
        VmMmioFields.Items,
        VmConsoleFields.Items,
        VmPowerBatteryFields.Items,
        VmCheckpointFields.Items,
        VmCheckpointGlobalFields.Items,
    }.SelectMany(items => items).ToArray());

    /// <summary>根据稳定标识查找字段；不存在时返回 null，不接受任意属性名或底层查询文本。</summary>
    /// <param name="id">目录中的字段标识，区分大小写。</param>
    /// <returns>内置字段或 null。</returns>
    public static VmQueryField? Find(string id) => All.FirstOrDefault(field => field.Id == id);
}
