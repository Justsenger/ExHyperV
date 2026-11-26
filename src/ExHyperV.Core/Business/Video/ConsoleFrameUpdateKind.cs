namespace ExHyperV.Services;

/// <summary>帧相对基线的更新方式。Frame update kind.</summary>
public enum ConsoleFrameUpdateKind
{
    /// <summary>画面无变化。</summary>
    Unchanged = 0,
    /// <summary>增量刷新。</summary>
    Delta = 1,
    /// <summary>完整刷新。</summary>
    Full = 2
}
