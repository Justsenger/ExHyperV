namespace ExHyperV.Services;

public partial interface IConsoleInput
{
    /// <summary>发送一次鼠标移动、按下、释放或垂直滚轮输入。</summary>
    /// <param name="x">相对来宾桌面左上角的横坐标，单位为像素，必须小于最近一帧的 Width。</param>
    /// <param name="y">相对来宾桌面左上角的纵坐标，单位为像素，必须小于最近一帧的 Height。</param>
    /// <param name="button">本次按下或释放的一个按键；移动和滚轮使用 None。</param>
    /// <param name="down">true 为按下，false 为释放；button 为 None 时必须为 false。</param>
    /// <param name="wheel">垂直滚轮增量，-255～255。通常一格为 120，正值向前滚、负值向后滚；非零时 button 必须为 None，down 为 false。</param>
    /// <returns>Core 接受输入时为 true。尚未收到首帧、按下或移动坐标越界、参数组合无效、未连接或暂不能接受新输入时为 false。有效释放事件在底层拒绝时保留重试。</returns>
    /// <remarks>
    /// <para>移动时只传 x、y；点击时在同一位置先 down=true 再 down=false；拖动时先按下，再连续发送移动，最后释放。</para>
    /// <para>释放坐标越界时自动限制到最新桌面边界，分辨率缩小不会丢掉释放；其他事件仍需提交有效坐标。失焦使用 ReleaseAllInputs，普通点击可用 SendClick 一次提交。</para>
    /// <para>显示区域经过缩放时，先减去画面的显示起点，再按来宾尺寸与显示尺寸的比例换算坐标。
    /// 黑边和画面外的输入不应发送。滚轮事件也需要合法的 x、y；较大的累计增量拆成多次发送。</para>
    /// <para>滚轮单位沿用 Windows 的 WHEEL_DELTA，120 通常表示一格。
    /// 来宾应用决定实际滚动多少内容。参见 <see href="https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-mousewheel">WM_MOUSEWHEEL</see>。</para>
    /// </remarks>
    /// <example>
    /// <para>在一个已经换算好的来宾坐标上点击左键。</para>
    /// <code>
    /// using ExHyperV.Services;
    /// public static class PointerExample
    /// {
    ///     public static bool Click(IConsoleInput input, ushort guestX, ushort guestY)
    ///     {
    ///         return input.SendClick(guestX, guestY);
    ///     }
    /// }
    /// </code>
    /// </example>
    bool SendPointer(ushort x, ushort y, ConsolePointerButton button = ConsolePointerButton.None,
        bool down = false, short wheel = 0);
    /// <summary>一次提交一个鼠标按键的按下和释放，两项一起进入 Core 队列。</summary>
    /// <param name="x">最近桌面范围内的横坐标，单位为像素。</param>
    /// <param name="y">最近桌面范围内的纵坐标，单位为像素。</param>
    /// <param name="button">Left、Right 或 Middle，默认左键。</param>
    /// <returns>true 表示已接受完整点击；false 时本次按下与释放均未入队。底层拥塞时 Core 继续发送；断连仍可能中止尚未发出的事件。</returns>
    bool SendClick(ushort x, ushort y, ConsolePointerButton button = ConsolePointerButton.Left);
}
