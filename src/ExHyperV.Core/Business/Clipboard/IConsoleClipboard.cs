namespace ExHyperV.Services;

/// <summary>与来宾交换剪贴板内容，例如双向复制粘贴文字。</summary>
/// <remarks>
/// <para>RdpEnhanced 的剪贴板通道就绪后，IsAvailable 为 true，此时可以收发内容。</para>
/// <para>向来宾发送时调用 SetClipboard；来宾提供新内容时，通过 ClipboardReceived 接收。
/// 宿主系统剪贴板的读取、写入及展示由调用方完成。</para>
/// <para>事件可能在后台线程触发；需要操作界面或宿主剪贴板时，调度到调用方要求的线程。
/// 双向同步时应识别自己刚写入的内容，避免两端反复回传。</para>
/// </remarks>
/// <businessdoc category="剪贴板">
/// <summary>收发来宾剪贴板中的文本、位图和 HTML。</summary>
/// <para>当前仅 RdpEnhanced 提供此能力。取得 Clipboard 后订阅事件，等待 IsAvailable，再提交数据。</para>
/// <para>Core 负责传输；宿主剪贴板的读取和写入由调用方完成。
/// 一份内容可以同时提供多种格式，接收方选择合适的表示方式。双向文字示例见 <see cref="IConsoleClipboard"/>。</para>
/// </businessdoc>
/// <example>
/// <para>发送纯文本，并从收到的内容中选取纯文本格式。收到的文字交给调用方处理，不直接操作系统剪贴板。</para>
/// <code>
/// using ExHyperV.Services;
/// using System.Text;
/// public static class ClipboardExample
/// {
///     public static bool SendText(IConsoleClipboard clipboard, string text)
///     {
///         if (!clipboard.IsAvailable) return false;
///         byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
///         return clipboard.SetClipboard(new ConsoleClipboardData(
///             new[] { new ConsoleClipboardFormat(13, null, bytes) }));
///     }
///     public static string? ReadText(ConsoleClipboardData clipboard)
///     {
///         var text = clipboard.Formats.FirstOrDefault(format => format.Id == 13);
///         if (text is null) return null;
///         string value = Encoding.Unicode.GetString(text.Data.Span);
///         return value.EndsWith('\0') ? value[..^1] : value;
///     }
///     public static void Subscribe(IConsoleClipboard clipboard, Action&lt;string&gt; receive)
///     {
///         clipboard.ClipboardReceived += data =>
///         {
///             if (ReadText(data) is { } text) receive(text);
///         };
///     }
/// }
/// </code>
/// </example>
public interface IConsoleClipboard
{
    /// <summary>连接与剪贴板通道是否已就绪。</summary>
    /// <fielddoc>
    /// <meaning>连接与剪贴板通道是否已就绪。</meaning>
    /// <values kind="options">true：可以提交内容；false：通道尚未就绪、已暂停或连接已断开。</values>
    /// </fielddoc>
    /// <remarks>RdpEnhanced 读取远端格式超过 30 秒未获应答时暂停剪贴板，视频和控制继续运行。迟到应答被丢弃后恢复读取最新内容；始终没有应答时需要重建连接。提交时以 SetClipboard 返回值为准。</remarks>
    bool IsAvailable { get; }
    /// <summary>剪贴板通道变为可用或不可用时通知，参数为变化后的 IsAvailable。</summary>
    event Action<bool>? ClipboardAvailabilityChanged;
    /// <summary>取得来宾剪贴板中受支持格式的数据时通知。</summary>
    /// <remarks>从 Formats 中选择能处理的格式。空列表不一定表示来宾剪贴板为空，见 ConsoleClipboardData。
    /// 快速更新时，以较新的内容为准。
    /// 数据可在回调返回后继续使用，保存它不需要 Retain 或 Dispose。</remarks>
    event Action<ConsoleClipboardData>? ClipboardReceived;
    /// <summary>把调用方准备好的剪贴板内容发送给来宾。</summary>
    /// <param name="clipboard">同一份内容的文本、DIB 或 HTML 表示；具体格式布局见 ConsoleClipboardFormat。</param>
    /// <returns>true 表示内容已复制并加入发送队列；通道不可用、数据格式无效、超出大小限制或队列无法接收时为 false。</returns>
    /// <remarks>调用返回后可以复用输入数据。</remarks>
    bool SetClipboard(ConsoleClipboardData clipboard);
}
