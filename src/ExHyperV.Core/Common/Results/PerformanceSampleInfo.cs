namespace ExHyperV.Models;

/// <summary>一次性能读取的状态；只有 Valid 表示本次取得了可使用的新数值。</summary>
public enum PerformanceSampleState
{
    /// <summary>尚未执行读取。</summary>
    NotSampled,
    /// <summary>本次读取成功，数值有效；零也可以是有效数值。</summary>
    Valid,
    /// <summary>计数器正在建立基线，尚无有效的时间差样本。</summary>
    WarmingUp,
    /// <summary>本次未取得样本或必需字段。</summary>
    Unavailable,
    /// <summary>当前环境未提供该采集能力。</summary>
    Unsupported,
    /// <summary>本次读取失败。</summary>
    Failed,
    /// <summary>样本无法唯一对应到目标，未采用有歧义的数据。</summary>
    Ambiguous,
    /// <summary>当前目标状态不适用此指标，例如虚拟机已关闭。</summary>
    NotApplicable
}

/// <summary>性能值对应的本次读取状态；调用方应先检查 IsValid 再更新数值和历史曲线。</summary>
/// <param name="State">本次读取状态。</param>
/// <param name="ObservedAtUtc">本次读取或确认缺测的 UTC 时间；NotSampled 时为默认值。</param>
/// <param name="Error">缺测、失败或归属不明确的说明。</param>
/// <remarks>状态无效时，关联数值可能是默认值或此前保留值，均不可当作本次新样本。是否继续显示旧值由调用方决定。</remarks>
public sealed record PerformanceSampleInfo(PerformanceSampleState State, DateTimeOffset ObservedAtUtc, string? Error = null)
{
    /// <summary>是否取得了可用的新样本；仅 Valid 为 true。</summary>
    public bool IsValid => State == PerformanceSampleState.Valid;
}
