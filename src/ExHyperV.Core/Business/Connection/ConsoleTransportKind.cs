namespace ExHyperV.Services;

/// <summary>当前画面的传输种类。Frame transport kind.</summary>
public enum ConsoleTransportKind
{
    /// <summary>路径未知。</summary>
    Unknown = 0,
    /// <summary>共享 CPU 内存。</summary>
    CpuShared = 1,
    /// <summary>共享 GPU 表面。</summary>
    GpuShared = 2,
    /// <summary>基本会话。</summary>
    RdpBasic = 3,
    /// <summary>增强会话。</summary>
    RdpEnhanced = 4
}
