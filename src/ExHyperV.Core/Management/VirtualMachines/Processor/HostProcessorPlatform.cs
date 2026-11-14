namespace ExHyperV.Services;

/// <summary>虚拟处理器设置规则使用的宿主平台分类，由架构和厂商信息组合得到。</summary>
public enum HostProcessorPlatform
{
    /// <summary>未能归入已知平台，也可能是厂商查询失败。</summary>
    Unknown,
    /// <summary>AMD 处理器平台。</summary>
    Amd,
    /// <summary>Intel 处理器平台。</summary>
    Intel,
    /// <summary>ARM64 处理器平台。</summary>
    Arm64
}
