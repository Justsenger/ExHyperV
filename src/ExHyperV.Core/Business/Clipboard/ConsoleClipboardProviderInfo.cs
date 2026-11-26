namespace ExHyperV.Services;

/// <summary>按虚拟机、来宾会话和传输方向选择剪贴板提供方。Session-scoped clipboard provider capabilities.</summary>
/// <param name="ProviderId">提供方稳定标识。
/// <fielddoc><meaning>提供方稳定标识。</meaning></fielddoc></param>
/// <param name="VmId">目标虚拟机标识。
/// <fielddoc><meaning>目标虚拟机标识。</meaning></fielddoc></param>
/// <param name="GuestSessionId">实际来宾会话标识。
/// <fielddoc><meaning>实际来宾会话标识。</meaning></fielddoc></param>
/// <param name="IsReady">提供方通道是否就绪。
/// <fielddoc><meaning>提供方通道是否就绪。</meaning></fielddoc></param>
/// <param name="CanRead">是否允许从来宾读取。
/// <fielddoc><meaning>是否允许从来宾读取。</meaning></fielddoc></param>
/// <param name="CanWrite">是否允许向来宾写入。
/// <fielddoc><meaning>是否允许向来宾写入。</meaning></fielddoc></param>
public sealed record ConsoleClipboardProviderInfo(string ProviderId,
    Guid VmId,
    string GuestSessionId,
    bool IsReady,
    bool CanRead,
    bool CanWrite)
{
    /// <summary>提供方的非空稳定标识。</summary>
    /// <fielddoc><meaning>提供方的非空稳定标识。</meaning></fielddoc>
    public string ProviderId { get; init => field = RequireId(value, nameof(ProviderId)); } = RequireId(ProviderId, nameof(ProviderId));

    /// <summary>提供方所属的目标虚拟机。</summary>
    /// <fielddoc><meaning>提供方所属的目标虚拟机，必须是非空 GUID。</meaning></fielddoc>
    public Guid VmId { get; init => field = RequireVm(value); } = RequireVm(VmId);

    /// <summary>实际来宾会话的非空标识。</summary>
    /// <fielddoc><meaning>实际来宾会话的非空标识。</meaning></fielddoc>
    public string GuestSessionId { get; init => field = RequireId(value, nameof(GuestSessionId)); } = RequireId(GuestSessionId, nameof(GuestSessionId));

    private static string RequireId(string value, string name) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("需要非空标识。", name);

    private static Guid RequireVm(Guid value) =>
        value != Guid.Empty ? value : throw new ArgumentException("需要虚拟机标识。", nameof(VmId));
}
