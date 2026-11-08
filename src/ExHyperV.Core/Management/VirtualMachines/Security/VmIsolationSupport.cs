using ExHyperV.Tools;

namespace ExHyperV.Services;

/// <summary>宿主支持的虚拟机硬件隔离能力。</summary>
/// <param name="Supported">是否存在 Disabled 以外的已识别隔离类型。</param>
/// <param name="Types">可选隔离类型名称，始终包含 Disabled。</param>
public sealed record VmIsolationSupport(bool Supported, IReadOnlyList<string> Types);
