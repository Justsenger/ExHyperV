namespace ExHyperV.Services;

/// <summary>虚拟电池在当前虚拟机上的可用性和启用状态。</summary>
/// <param name="Available">当前主机和虚拟机是否支持此功能。</param>
/// <param name="Enabled">此功能当前是否启用；应先检查 Available。</param>
public readonly record struct VmBatteryState(bool Available, bool Enabled);
