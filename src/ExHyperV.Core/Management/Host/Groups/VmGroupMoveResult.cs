namespace ExHyperV.Services;

/// <summary>分组移动后的目标标识，以及目标组是否发生重建。</summary>
/// <param name="RequestedTargetId">调用方最初指定的目标分组 GUID；未指定目标组时为 null。</param>
/// <param name="TargetId">实际接收成员的分组 GUID；目标空组发生类型转换时可能与原 GUID 不同。</param>
public sealed record VmGroupMoveResult(Guid? RequestedTargetId, Guid? TargetId)
{
    /// <summary>目标组是否因成员类型改变而重建。重建后使用 TargetId 作为新的目标标识。</summary>
    /// <fielddoc>
    /// <meaning>目标组是否因成员类型改变而重建。重建后使用 TargetId 作为新的目标标识。</meaning>
    /// <values kind="options">true：目标组已重建；false：目标组标识保持不变。</values>
    /// </fielddoc>
    public bool TargetRecreated => RequestedTargetId != TargetId;
}
