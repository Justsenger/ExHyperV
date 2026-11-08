namespace ExHyperV.Services;

/// <summary>剪贴板内容的一种表示方式，由格式编号、名称和原始字节组成。</summary>
/// <param name="Id">剪贴板格式的编号。
/// <fielddoc>
/// <meaning>剪贴板格式的编号。</meaning>
/// <values kind="options">13：Unicode 文本；8：DIB 位图；0xC000～0xFFFF：HTML 使用的注册格式编号。</values>
/// </fielddoc>
/// </param>
/// <param name="Name">注册剪贴板格式的名称。
/// <fielddoc>
/// <meaning>注册剪贴板格式的名称。</meaning>
/// <values kind="format">HTML 使用 <c>HTML Format</c>，区分大小写；文本和 DIB 使用 null 或空字符串。</values>
/// </fielddoc>
/// </param>
/// <param name="Data">该格式的原始字节，只读视图保留调用方提供的内存。
/// <fielddoc>
/// <meaning>该格式的原始字节，只读视图保留调用方提供的内存。</meaning>
/// <values kind="format">Unicode 文本使用 UTF-16LE 并以两个零字节结束；位图使用 CF_DIB 布局；HTML 使用含头部的 CF_HTML 布局。单种数据最多 16 MiB。</values>
/// </fielddoc>
/// </param>
/// <remarks>
/// <para>CF_DIB 包含位图信息头和像素数据，不含 BMP 文件头。CF_HTML 包含片段范围头和 UTF-8 内容，头中的偏移量按字节计算。
/// HTML 布局见 <see href="https://learn.microsoft.com/en-us/windows/win32/dataxchg/html-clipboard-format">HTML Clipboard Format</see>。</para>
/// <para>SetClipboard 会复制数据并检查格式编号、名称、长度和文本结束符，不会完整验证 DIB 或 HTML 的内部结构。
/// 调用方应提供由对应格式生成器或系统剪贴板取得的合法数据。</para>
/// </remarks>
public sealed record ConsoleClipboardFormat(uint Id, string? Name, ReadOnlyMemory<byte> Data);
