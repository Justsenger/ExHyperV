namespace ExHyperV.Models;
/// <summary>宿主处理器核心的性能类别。</summary>
public enum CpuCoreType
{
    /// <summary>尚未识别核心类别。</summary>
    Unknown,
    /// <summary>性能核心。</summary>
    Performance,
    /// <summary>能效核心。</summary>
    Efficient
}
