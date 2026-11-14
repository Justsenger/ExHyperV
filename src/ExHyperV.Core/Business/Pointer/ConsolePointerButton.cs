namespace ExHyperV.Services;

/// <summary>鼠标输入中要按下或释放的一个按键。</summary>
/// <remarks>SendPointer 每次接收一个按键；移动和滚轮事件使用 None。</remarks>
public enum ConsolePointerButton
{
    /// <summary>没有鼠标按键，用于移动或滚轮事件。</summary>
    None = 0,
    /// <summary>鼠标左键。</summary>
    Left = 1,
    /// <summary>鼠标右键。</summary>
    Right = 2,
    /// <summary>鼠标中键。</summary>
    Middle = 4
}
