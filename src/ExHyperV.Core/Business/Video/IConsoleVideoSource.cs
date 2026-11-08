namespace ExHyperV.Services;

/// <summary>从同一连接获取单帧或持续接收画面，并在后端支持时请求调整分辨率。</summary>
/// <remarks>
/// <para>从连接的 Video 属性取得此接口，在 Start 前订阅 FrameReceived。
/// 每帧包含完整画面，可整帧绘制，也可根据 Damage 局部刷新。</para>
/// <para>画面以 BGRA32 原始像素交付，包含远端光标；按画面更新触发事件，帧率随更新频率变化。</para>
/// <para>回调返回后，借出的帧会被释放。在回调内完成读取即可；需要延后处理时，用 Retain 保留并在用完后 Dispose。
/// 需要与连接完全独立的字节副本时用 CopyPixels。</para>
/// </remarks>
/// <businessdoc category="视频">
/// <summary>读取虚拟机画面，或在需要时获取一张截图。</summary>
/// <para>持续显示桌面使用连接的 Video 接口和 FrameReceived 事件。画面已解码为 BGRA32 像素，包含远端光标；
/// 帧的保留、复制和局部刷新规则见 <see cref="ConsoleFrame"/>。</para>
/// <para>对当前连接截图使用 <see cref="CaptureFrame"/>，取得最近一张完整画面的独立租约；基本、增强会话各取自己的画面。
/// 它不等待新帧，也不回退到其他画面来源。用完后 Dispose；需要独立字节副本时调用 CopyPixels。</para>
/// <para>未建立连接的列表缩略图使用 <see cref="VmScreenshotSource.CaptureAsync"/>。
/// 该接口通过 Hyper-V WMI 获取基本控制台的 RGB565 图像，不能代替增强会话桌面的截图。</para>
/// </businessdoc>
/// <example>
/// <para>下面同步消费一帧，避免额外复制。consume 应快速读取或绘制，不能在返回后继续使用传入的帧。</para>
/// <code>
/// using ExHyperV.Services;
/// public static class VideoExample
/// {
///     public static void Subscribe(IConsoleConnection connection, Action&lt;ConsoleFrame&gt; consume)
///     {
///         if (connection.Video is { } video)
///             video.FrameReceived += frame => consume(frame);
///     }
///     public static bool Capture(IConsoleConnection connection, Action&lt;ConsoleFrame&gt; consume)
///     {
///         using var frame = connection.Video?.CaptureFrame();
///         if (frame is null) return false;
///         consume(frame); // 完整画面；使用结束自动释放，不等待下一次 FrameReceived。
///         return true;
///     }
/// }
/// </code>
/// </example>
public interface IConsoleVideoSource
{
    /// <summary>取得此连接最近生成的完整画面，返回一份由调用方负责释放的帧租约。</summary>
    /// <returns>完整 BGRA32 画面的独立租约，包含远端光标；尚未连接、尚无画面或已请求断开时返回 null。</returns>
    /// <remarks>
    /// <para>立即返回此连接缓存的最近画面，静止桌面也可获取。增强会话取增强桌面，基本会话取基本控制台。</para>
    /// <para>此租约不复制像素，取得后像素不再变化，连接关闭后仍可读取。用 using 或 finally 及时 Dispose；
    /// 长期保留多张帧可能耗尽缓冲池并暂停新帧输出。保存独立副本时可调用 CopyPixels，再释放租约。</para>
    /// <para>画面可能尚未被界面绘制。截图读取完整 Pixels，尺寸和格式保持原样；Sequence 仅在本连接内有效。</para>
    /// </remarks>
    ConsoleFrame? CaptureFrame();

    /// <summary>收到新画面时通知；参数中的帧在本次回调期间可用。</summary>
    /// <remarks>回调通常在后台线程执行。可直接读取 frame.Pixels；要交给其他线程，先调用 frame.Retain() 并负责释放保留的帧。
    /// 显示缩放由调用方处理，帧中的尺寸始终是来宾桌面像素。</remarks>
    event Action<ConsoleFrame>? FrameReceived;
    /// <summary>后端是否提供桌面分辨率和缩放调整能力。</summary>
    /// <fielddoc>
    /// <meaning>后端是否提供桌面分辨率和缩放调整能力。</meaning>
    /// <values kind="options">RdpEnhanced 为 true，RdpBasic 为 false。</values>
    /// </fielddoc>
    /// <remarks>此值表示功能支持；连接及显示通道就绪后才能提交 Resize 请求。</remarks>
    bool SupportsResize { get; }
    /// <summary>已入队的尺寸调整请求在显示通道发送失败时通知。</summary>
    /// <remarks>参数是后端返回的诊断码，可与连接的 BackendId 一起记录。
    /// 当前 RDP 后端报告的是本地通道发送结果，不是来宾拒绝调整的回执。
    /// 连续失败可能合并通知，事件也不携带原请求尺寸；实际分辨率请查看后续帧的 Width 和 Height。</remarks>
    event Action<uint>? ResizeRejected;
    /// <summary>请求调整来宾桌面的分辨率和缩放比例。</summary>
    /// <param name="width">桌面宽度，200～8192 像素；当前 RDP 实现会将奇数向下取为偶数。</param>
    /// <param name="height">桌面高度，200～8192 像素。</param>
    /// <param name="desktopScale">来宾桌面缩放百分比，100～500，默认 100；不是调用方窗口的缩放比例。</param>
    /// <returns>true 表示请求已加入发送队列。参数无效、未连接、不支持调整或队列无法接收时返回 false。</returns>
    /// <remarks>
    /// <para>宽 × 高 × 4 不得超过 64 MiB 减 64 字节。该限制在宽度取偶数前检查。</para>
    /// <para>返回 true 不代表尺寸已经改变。显示通道发送失败时可收到 ResizeRejected；没有失败通知也不表示来宾已应用请求。
    /// 显示端应按后续 ConsoleFrame 的实际尺寸创建或调整图像。</para>
    /// </remarks>
    bool Resize(int width, int height, uint desktopScale = 100);
}
