using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>读取宿主逻辑处理器及可用于虚拟机亲和性的处理器集合。</summary>
public static class HostProcessorService
{

        /// <summary>读取当前亲和性后端允许选择的宿主逻辑处理器。</summary>
        /// <returns>Classic/Core 返回 HCS 真实编号与拓扑；Root 返回唯一 Windows 处理器组的活动编号，多组宿主返回 Unsupported。查询失败不生成替代编号。</returns>
        /// <remarks>保存亲和性时使用返回项的 Index：Classic/Core 为 HCS 逻辑处理器编号，Root 为 Windows 处理器组内编号。</remarks>
        public static async Task<ApiResponse<IReadOnlyList<HostAffinityProcessor>>> GetAffinityProcessorsAsync()
        {
            var scheduler = HyperVSchedulerService.GetSchedulerType();
            if (scheduler == HyperVSchedulerType.Root)
            {
                var root = await Task.Run(ProcessAffinityService.GetTopology).ConfigureAwait(false);
                if (!root.HasData) return ApiResponse<IReadOnlyList<HostAffinityProcessor>>.Fail(root.Error, root.Code, root.ErrorSource, root.Exception, root.ErrorKind);
                return ApiResponse<IReadOnlyList<HostAffinityProcessor>>.Ok(Array.AsReadOnly(root.Data.Indices
                    .Select(i => new HostAffinityProcessor(i, null, null, null, HyperVCpuMonitorService.GetCoreType(i))).ToArray()));
            }
            if (scheduler is not (HyperVSchedulerType.Classic or HyperVSchedulerType.Core))
                return ApiResponse<IReadOnlyList<HostAffinityProcessor>>.Fail("无法确定当前 Hyper-V 调度器。", kind: ApiErrorKind.Unsupported);
            var topology = await HcsApi.GetProcessorTopologyAsync().ConfigureAwait(false);
            if (!topology.HasData)
                return ApiResponse<IReadOnlyList<HostAffinityProcessor>>.Fail(topology.Error, topology.Code, topology.ErrorSource, topology.Exception, topology.ErrorKind);
            if (topology.Data.Count == 0 || topology.Data.Any(p => p.LpIndex > int.MaxValue))
                return ApiResponse<IReadOnlyList<HostAffinityProcessor>>.Fail("HCS 处理器拓扑为空或编号无法表示。", kind: ApiErrorKind.InvalidState);
            return ApiResponse<IReadOnlyList<HostAffinityProcessor>>.Ok(Array.AsReadOnly(topology.Data.OrderBy(p => p.LpIndex)
                .Select(p => new HostAffinityProcessor((int)p.LpIndex, p.PackageId, p.CoreId, p.NodeNumber, CpuCoreType.Unknown)).ToArray()));
        }

}
