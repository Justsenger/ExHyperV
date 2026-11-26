namespace ExHyperV.Services;

/// <summary>模式请求的接受状态和实际生效值。Mode request acknowledgement.</summary>
/// <param name="Accepted">后端是否接受请求。
/// <fielddoc><meaning>后端是否接受请求。</meaning></fielddoc></param>
/// <param name="ActiveMode">当前可确认的实际模式，尚未确认时为 null。
/// <fielddoc><meaning>当前可确认的实际模式，尚未确认时为 null。</meaning></fielddoc></param>
public sealed record ConsoleDisplayModeResult(bool Accepted, ConsoleDisplayMode? ActiveMode);
