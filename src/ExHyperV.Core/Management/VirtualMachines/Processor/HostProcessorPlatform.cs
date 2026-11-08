namespace ExHyperV.Services;

/// <summary>虚拟处理器设置规则使用的宿主平台分类；由架构和厂商信息组合得到，不是独立的宿主查询字段。</summary>
public enum HostProcessorPlatform
{
    /// <summary>未能归入已知平台，也可能是厂商查询失败。</summary>
    Unknown,
    /// <summary>AMD 处理器平台。</summary>
    Amd,
    /// <summary>Intel 处理器平台。</summary>
    Intel,
    /// <summary>宿主原生 ARM64 平台。</summary>
    Arm64
}
