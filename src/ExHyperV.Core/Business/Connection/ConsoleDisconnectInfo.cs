namespace ExHyperV.Services;

/// <summary>连接结束时提供的诊断信息，用于显示原因或记录日志。</summary>
/// <param name="ErrorCode">连接结束时的后端诊断码。
/// <fielddoc>
/// <meaning>连接结束时的后端诊断码。</meaning>
/// <values kind="format">当前 RDP 后端使用 FreeRDP 或 Win32 错误码；<c>0xFFFFFFFF</c> 为托管异常，<c>0xFFFFFFFE</c> 为连接超时，0 为未报告错误。</values>
/// </fielddoc>
/// </param>
/// <param name="Error">连接结束时附带的错误说明。
/// <fielddoc>
/// <meaning>连接结束时附带的错误说明。</meaning>
/// <values kind="format">文字说明；未提供时为 null。</values>
/// </fielddoc>
/// </param>
/// <remarks>诊断码按后端解释，记录时一并保留 BackendId 和 Error。主动关闭和远端正常结束都可能返回 0。</remarks>
public sealed record ConsoleDisconnectInfo(uint ErrorCode, string? Error);
