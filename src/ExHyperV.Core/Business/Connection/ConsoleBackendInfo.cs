namespace ExHyperV.Services;

/// <summary>一个控制台后端的名称、实现状态和功能列表。</summary>
/// <param name="BackendId">创建连接时用于选择后端的标识。
/// <fielddoc>
/// <meaning>创建连接时用于选择后端的标识。</meaning>
/// <values kind="options"><c>RdpBasic</c>、<c>RdpEnhanced</c>、<c>Nimue</c>；区分大小写。</values>
/// </fielddoc>
/// </param>
/// <param name="Name">后端的显示名称。
/// <fielddoc>
/// <meaning>后端的显示名称。</meaning>
/// <values kind="format">例如 <c>RDP Basic</c>。</values>
/// </fielddoc>
/// </param>
/// <param name="IsImplemented">当前 Core 是否实现了此后端。
/// <fielddoc>
/// <meaning>当前 Core 是否实现了此后端。</meaning>
/// <values kind="options">true：可以创建连接；false：仅登记标识。实际连接结果由目标虚拟机和通道状态决定。</values>
/// </fielddoc>
/// </param>
/// <param name="Capabilities">后端已实现的交互能力，可按位组合。
/// <fielddoc>
/// <meaning>后端已实现的交互能力，可按位组合。</meaning>
/// </fielddoc>
/// </param>
/// <param name="UnavailableReason">后端尚未实现的原因。
/// <fielddoc>
/// <meaning>后端尚未实现的原因。</meaning>
/// <values kind="format">文字说明；已实现时为 null。</values>
/// </fielddoc>
/// </param>
/// <remarks>这些信息来自内置目录，适合用来展示连接方式。
/// 剪贴板等功能是否已经可以使用，还要在创建连接后查看相应接口的 IsAvailable。</remarks>
public sealed record ConsoleBackendInfo(string BackendId, string Name, bool IsImplemented,
    ConsoleCapabilities Capabilities, string? UnavailableReason);
