namespace ExHyperV.Services;

/// <summary>向已经建立的控制台连接发送键盘和鼠标输入。</summary>
/// <remarks>
/// <para>从连接的 Input 属性取得此接口。键盘使用扫描码并分别发送按下、释放；鼠标使用来宾桌面的像素坐标。</para>
/// <para>所有方法的 bool 返回值表示是否接受到发送队列，不能用来确认来宾已经处理了输入。
/// 未连接、参数无效或队列无法接收时通常返回 false；SendKeys 的集合为 null 时抛出异常。</para>
/// </remarks>
/// <businessdoc category="控制">
/// <summary>向来宾发送键盘和鼠标输入，两者共用连接的 Input 接口。</summary>
/// <para>键盘：发送按键的按下、释放事件，并同步 Caps Lock、Num Lock 等锁定状态。输入使用 PC 扫描码，
/// 不是字符编码或 Windows 虚拟键码；具体例子见 <see cref="IConsoleInput.SendKey"/>。</para>
/// <para>需要 Ctrl+Alt+Del 时，也可按虚拟机 GUID 调用 <see cref="VmInputService.SendCtrlAltDelAsync"/>，无需建立 RDP 连接。</para>
/// <para>鼠标：使用 <see cref="IConsoleInput.SendPointer"/> 发送移动、左中右键和垂直滚轮输入。坐标以收到的桌面帧为准；
/// 如果画面在界面中被缩放或留有黑边，先换算为来宾像素坐标，再发送。</para>
/// <para>一次点击由按下、释放两个事件组成；拖动则是在两者之间发送移动。滚轮每格通常为 120，正负值表示方向。</para>
/// <para>输入返回 true 表示 Core 已接收，不表示来宾已处理。失去焦点时调用 <see cref="IConsoleInput.ReleaseAllInputs"/> 释放键鼠状态。</para>
/// </businessdoc>
public partial interface IConsoleInput
{
    /// <summary>发送一个按键的按下或释放事件。</summary>
    /// <param name="scanCode">PC 键盘扫描码，1～255。例如 A 键为 0x1E；它表示按键位置，实际字符由来宾键盘布局和修饰键决定。</param>
    /// <param name="extended">扫描码带 E0 前缀时为 true，例如右 Ctrl 使用 scanCode=0x1D、extended=true；普通左 Ctrl 为 false。Pause 请用 SendPause。</param>
    /// <param name="down">true 表示按下，false 表示释放。同一个按键的两次事件应使用相同扫描码和 extended。</param>
    /// <returns>Core 接受输入时为 true；未连接、参数无效或暂不能接受新的按下事件时为 false。</returns>
    /// <remarks>连接存续期间，有效的释放事件会在底层队列拒绝时保留重试；后来的按下事件不会越过待发事件。返回 true 不等于来宾已处理。
    /// 失去焦点时调用 ReleaseAllInputs；断连后无法保证释放到达来宾，新连接不重放旧输入。整组按键可用 SendKeys 一次提交。</remarks>
    /// <example>
    /// <para>发送 A 键的按下和释放。这里只演示按键位置，不保证产生大写字母 A。</para>
    /// <code>
    /// using ExHyperV.Services;
    /// public static class KeyboardExample
    /// {
    ///     public static bool PressA(IConsoleInput input)
    ///     {
    ///         return input.SendKeys([new(0x1E, false, true), new(0x1E, false, false)]);
    ///     }
    /// }
    /// </code>
    /// </example>
    bool SendKey(ushort scanCode, bool extended, bool down);
    /// <summary>一次提交整组按键事件，按顺序发送。</summary>
    /// <param name="events">1～128 项扫描码事件，调用时复制；所有事件须有效。调用方应提供所需的释放事件。</param>
    /// <returns>true 表示整组进入 Core 队列；false 时本组没有任何事件入队。底层暂时无法接收时由 Core 继续发送，不需重交整组。</returns>
    /// <remarks>这里的整体性指 Core 接受请求，不是来宾原子执行；断连可能发生在序列中途。
    /// 托管待发序列尚未排空时拒绝新的按下和移动，仍接收释放。
    /// 失焦调用 ReleaseAllInputs 会取消尚未交给底层的事件，并为底层已接受的按下安排释放。</remarks>
    /// <exception cref="ArgumentNullException">events 为 null。</exception>
    bool SendKeys(IReadOnlyList<ConsoleKeyEvent> events);
    /// <summary>释放本连接记录的全部按键和鼠标按住状态，用于失焦或结束输入。</summary>
    /// <returns>连接可用且 Core 已接收释放请求时为 true；未连接或正在断开时为 false。</returns>
    /// <remarks>清空托管待重试队列，为底层已接受的按下安排释放；底层拒绝释放时保留重试。
    /// 已进入底层队列的输入不会撤回，仍可能先于释放到达来宾。重复调用安全，只依据本连接记录的按住状态发送释放，
    /// 不查询来宾当前键盘状态，也不能保证断连后的释放送达。</remarks>
    bool ReleaseAllInputs();
    /// <summary>发送 Pause 键所需的特殊扫描码序列。</summary>
    /// <returns>序列被接受到发送队列时为 true；未连接或队列无法接收时为 false。</returns>
    /// <remarks>一次调用发送完整的 Pause 特殊扫描码序列。</remarks>
    bool SendPause();
    /// <summary>让来宾的键盘锁定状态与调用方提供的状态一致。</summary>
    /// <param name="flags">当前开启的锁定键，可用 | 组合。None 表示全部关闭。</param>
    /// <returns>同步请求被接受到发送队列时为 true；未连接、标志无效或队列无法接收时为 false。</returns>
    /// <remarks>适合在控制台获得输入焦点时调用，例如传入 NumLock | CapsLock。
    /// Core 不读取宿主键盘状态，调用方自行取得并传入当前值。</remarks>
    bool SynchronizeKeyboard(ConsoleKeyboardToggleState flags);
}
