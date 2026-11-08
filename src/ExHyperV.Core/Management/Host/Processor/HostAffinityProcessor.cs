using ExHyperV.Models;
namespace ExHyperV.Services;

/// <summary>当前亲和性后端提供的一个宿主逻辑处理器选项。</summary>
/// <param name="Index">原样传给 CpuAffinityService.SaveAsync 的编号；Classic/Core 为 HCS LpIndex，Root 为唯一 Windows 处理器组内的编号。</param>
/// <param name="PackageId">HCS 返回的插槽标识；Root 枚举未提供时为 null。</param>
/// <param name="CoreId">HCS 返回的物理核心标识；未提供时为 null。</param>
/// <param name="NumaNode">HCS 返回的 NUMA 节点；未提供时为 null。</param>
/// <param name="CoreType">核心性能类别；未识别时为 Unknown。</param>
public sealed record HostAffinityProcessor(int Index, uint? PackageId, uint? CoreId, uint? NumaNode, CpuCoreType CoreType);
