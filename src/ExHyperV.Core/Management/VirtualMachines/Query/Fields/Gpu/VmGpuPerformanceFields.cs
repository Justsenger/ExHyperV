using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>GPU 性能的独立可查询字段；可以任意选择其中一项，不要求整组读取或返回。</summary>
public static class VmGpuPerformanceFields
{
    /// <summary>GPU 三维引擎使用率：虚拟机 GPU 三维引擎的合计使用率，单位为 %。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机 GPU 三维引擎的合计使用率，单位为 %。</meaning>
    /// <unit>%</unit>
    /// <values>对同类 GPU 引擎的使用率求和，多引擎并行时可以超过 100%。</values>
    /// <source>VmQueryService.GetGpuPerformanceAsync 的 GPU 引擎样本，读取或派生 Gpu3d。</source>
    /// <references><see cref="VmGpuService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<double> Gpu3d = new("GpuPerformance.Gpu3d", "GPU 三维引擎使用率", "GPU", VmQueryCollection.VirtualMachine, "GpuPerformance", "Gpu3d");

    /// <summary>GPU 复制引擎使用率：虚拟机 GPU 复制引擎的合计使用率，单位为 %。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机 GPU 复制引擎的合计使用率，单位为 %。</meaning>
    /// <unit>%</unit>
    /// <values>对同类 GPU 引擎的使用率求和，多引擎并行时可以超过 100%。</values>
    /// <source>VmQueryService.GetGpuPerformanceAsync 的 GPU 引擎样本，读取或派生 GpuCopy。</source>
    /// <references><see cref="VmGpuService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<double> GpuCopy = new("GpuPerformance.GpuCopy", "GPU 复制引擎使用率", "GPU", VmQueryCollection.VirtualMachine, "GpuPerformance", "GpuCopy");

    /// <summary>GPU 编码引擎使用率：虚拟机 GPU 视频编码引擎的合计使用率，单位为 %。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机 GPU 视频编码引擎的合计使用率，单位为 %。</meaning>
    /// <unit>%</unit>
    /// <values>对同类 GPU 引擎的使用率求和，多引擎并行时可以超过 100%。</values>
    /// <source>VmQueryService.GetGpuPerformanceAsync 的 GPU 引擎样本，读取或派生 GpuEncode。</source>
    /// <references><see cref="VmGpuService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<double> GpuEncode = new("GpuPerformance.GpuEncode", "GPU 编码引擎使用率", "GPU", VmQueryCollection.VirtualMachine, "GpuPerformance", "GpuEncode");

    /// <summary>GPU 解码引擎使用率：虚拟机 GPU 视频解码引擎的合计使用率，单位为 %。</summary>
    /// <fielddoc>
    /// <meaning>虚拟机 GPU 视频解码引擎的合计使用率，单位为 %。</meaning>
    /// <unit>%</unit>
    /// <values>对同类 GPU 引擎的使用率求和，多引擎并行时可以超过 100%。</values>
    /// <source>VmQueryService.GetGpuPerformanceAsync 的 GPU 引擎样本，读取或派生 GpuDecode。</source>
    /// <references><see cref="VmGpuService"/>。</references>
    /// <modification kind="readonly" />
    /// </fielddoc>
    public static readonly VmQueryField<double> GpuDecode = new("GpuPerformance.GpuDecode", "GPU 解码引擎使用率", "GPU", VmQueryCollection.VirtualMachine, "GpuPerformance", "GpuDecode");

    /// <summary>本次是否枚举到归属于虚拟机进程的 GPU 引擎计数器实例。</summary>
    /// <fielddoc>
    /// <meaning>存在匹配的引擎实例即为 true，即使使用率为 0%；不证明来宾驱动已安装、正常工作或正在执行 GPU 负载。</meaning>
    /// <source>VmQueryService.GetGpuPerformanceAsync 的 GPU 引擎样本，读取或派生 HasGpuEngineInstance。</source>
    /// <references><see cref="VmGpuService"/>。</references>
    /// <modification kind="readonly" />
    /// <values kind="options"><c>true</c>：找到匹配的 GPU 引擎实例；<c>false</c>：未找到。</values>
    /// </fielddoc>
    public static readonly VmQueryField<bool> HasGpuEngineInstance = new("GpuPerformance.HasGpuEngineInstance", "GPU 引擎实例可见", "GPU", VmQueryCollection.VirtualMachine, "GpuPerformance", "HasGpuEngineInstance");

    internal static readonly VmQueryField[] Items = [Gpu3d, GpuCopy, GpuEncode, GpuDecode, HasGpuEngineInstance];
}
