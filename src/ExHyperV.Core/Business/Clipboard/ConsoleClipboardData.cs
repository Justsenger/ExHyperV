namespace ExHyperV.Services;

/// <summary>一次剪贴板内容，以及这份内容可供接收方选择的不同格式。</summary>
/// <param name="Formats">同一份剪贴板内容提供的格式列表。
/// <fielddoc>
/// <meaning>同一份剪贴板内容提供的格式列表。</meaning>
/// <values kind="format">0～3 种受支持且不重复的格式。每种数据最多 16 MiB，连同编号、名称、长度信息的传输总量最多 32 MiB。</values>
/// </fielddoc>
/// </param>
/// <remarks>
/// <para>例如同一段网页内容可同时提供纯文本和 HTML，接收方选择能处理的格式。</para>
/// <para>构造记录时保存原集合；SetClipboard 在发送时检查并复制。提交期间保持集合及数据不变。</para>
/// <para>从 ClipboardReceived 收到的数据由托管内存持有，可在回调结束后继续使用，无需 Dispose。
/// 接收列表包含成功取得的受支持格式；格式不受支持或取数失败时也可能为空。</para>
/// </remarks>
public sealed record ConsoleClipboardData(IReadOnlyList<ConsoleClipboardFormat> Formats);
