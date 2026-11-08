namespace ExHyperV.Models;

/// <summary>虚拟机当前的运行状态；不包含后端状态码。</summary>
public enum VmState
{
    /// <summary>当前状态未知或无法识别。</summary>
    Unknown,
    /// <summary>后端报告的其他状态。</summary>
    Other,
    /// <summary>正在运行。</summary>
    Running,
    /// <summary>已经完全关机。</summary>
    PoweredOff,
    /// <summary>正在正常关机。</summary>
    ShuttingDown,
    /// <summary>运行状态不适用。</summary>
    NotApplicable,
    /// <summary>运行状态已经保存。</summary>
    Saved,
    /// <summary>正在测试。</summary>
    InTest,
    /// <summary>操作已延期。</summary>
    Deferred,
    /// <summary>执行已经挂起。</summary>
    Suspended,
    /// <summary>正在启动。</summary>
    Starting,
    /// <summary>正在等待启动。</summary>
    WaitingToStart,
    /// <summary>正在合并磁盘。</summary>
    MergingDisks,
    /// <summary>正在保存运行状态。</summary>
    Saving,
    /// <summary>正在停止。</summary>
    Stopping,
    /// <summary>正在处理操作。</summary>
    Processing,
    /// <summary>正在挂起执行。</summary>
    Suspending,
    /// <summary>正在恢复执行。</summary>
    Resuming,
    /// <summary>运行状态已经快速保存。</summary>
    FastSaved,
    /// <summary>正在快速保存运行状态。</summary>
    FastSaving,
    /// <summary>正在强制关机。</summary>
    ForceShuttingDown,
    /// <summary>正在强制重新启动。</summary>
    ForceRestarting,
    /// <summary>已经休眠。</summary>
    Hibernated
}
