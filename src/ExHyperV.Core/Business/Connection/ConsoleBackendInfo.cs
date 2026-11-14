namespace ExHyperV.Services;

/// <summary>一个控制台后端的名称和功能列表。</summary>
/// <param name="BackendId">创建连接时用于选择后端的标识。
/// <fielddoc>
/// <meaning>创建连接时用于选择后端的标识。</meaning>
/// <values kind="options"><c>RdpBasic</c>、<c>RdpEnhanced</c>；区分大小写。</values>
/// </fielddoc>
/// </param>
/// <param name="Name">后端的显示名称。
/// <fielddoc>
/// <meaning>后端的显示名称。</meaning>
/// <values kind="format">例如 <c>RDP Basic</c>。</values>
/// </fielddoc>
/// </param>
/// <param name="Capabilities">后端已实现的交互能力，可按位组合。
/// <fielddoc>
/// <meaning>后端已实现的交互能力，可按位组合。</meaning>
/// </fielddoc>
/// </param>
/// <remarks>这些信息来自内置目录，适合用来展示连接方式。
/// 剪贴板等功能是否已经可以使用，还要在创建连接后查看相应接口的 IsAvailable。</remarks>
public sealed record ConsoleBackendInfo(string BackendId, string Name,
    ConsoleCapabilities Capabilities);
