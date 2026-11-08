using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>处理器的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmProcessorMetricFields
{
    /// <summary>虚拟处理器编号：虚拟机内的虚拟处理器编号，从 0 开始。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机内的虚拟处理器编号。</meaning>
    /// <source>Windows 格式化虚拟处理器性能提供者，按唯一虚拟机名称与处理器编号关联，读取或派生 Index。</source>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="readonly">性能采样中虚拟处理器的编号，不是可编辑配置。</modification>
    /// <values kind="format">从 <c>0</c> 开始的整数编号。</values>
    /// </fielddoc>
    public static readonly VmQueryField<int> Index = new("ProcessorMetric.Index", "虚拟处理器编号", "处理器", VmQueryCollection.Processors, "ProcessorPerformance", "Index");

    /// <summary>逐虚拟处理器使用率：单个虚拟处理器的运行时间占采样时间的比例，单位为 %。</summary>
    /// <fielddoc>
    /// <meaning>单个虚拟处理器的运行时间占采样时间的比例。</meaning>
    /// <applicability>适用于运行中的虚拟机。</applicability>
    /// <source>Windows 格式化虚拟处理器性能提供者，按唯一虚拟机名称与处理器编号关联，读取或派生 UsagePercent。</source>
    /// <absence>计数器名称对应多个同名虚拟机时返回 Ambiguous。</absence>
    /// <references><see cref="VmProcessorService"/>。</references>
    /// <modification kind="readonly">逐虚拟处理器的运行时采样值，不能直接设置。</modification>
    /// <values kind="format">百分数；例如 <c>25</c> 表示 25%。</values>
    /// </fielddoc>
    public static readonly VmQueryField<double> UsagePercent = new("ProcessorMetric.UsagePercent", "逐虚拟处理器使用率", "处理器", VmQueryCollection.Processors, "ProcessorPerformance", "UsagePercent");

    internal static readonly VmQueryField[] Items = [Index, UsagePercent];
}
