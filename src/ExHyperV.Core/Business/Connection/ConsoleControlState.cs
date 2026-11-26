namespace ExHyperV.Services;

/// <summary>观众当前有效权限和控制代次。Effective viewer access and control generation.</summary>
/// <param name="canView">观众是否获准观看。</param>
/// <param name="hasControl">观众是否持有控制权。</param>
/// <param name="generation">控制代次，交接后旧代次失效。</param>
public sealed class ConsoleControlState(bool canView, bool hasControl, ulong generation)
{
    /// <summary>观众是否获准观看。</summary>
    /// <fielddoc><meaning>观众是否获准观看。</meaning></fielddoc>
    public bool CanView { get; } = canView;

    /// <summary>观众是否持有控制权。</summary>
    /// <fielddoc><meaning>观众是否持有控制权。</meaning></fielddoc>
    public bool HasControl { get; } = hasControl && !canView ? throw new ArgumentException("控制者需要观看权限。", nameof(hasControl)) : hasControl;

    /// <summary>控制代次，交接后旧代次失效。</summary>
    /// <fielddoc><meaning>控制代次，交接后旧代次失效。</meaning></fielddoc>
    public ulong Generation { get; } = generation;
}
