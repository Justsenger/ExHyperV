namespace ExHyperV.Services;

/// <summary>需要在来宾中开启的键盘锁定键，可用 | 组合。</summary>
/// <remarks>由调用方读取锁定状态，再交给 SynchronizeKeyboard 同步到来宾。</remarks>
[Flags]
public enum ConsoleKeyboardToggleState : uint
{
    /// <summary>所有锁定键均关闭。</summary>
    None = 0,
    /// <summary>Scroll Lock 开启。</summary>
    ScrollLock = 1,
    /// <summary>Num Lock 开启。</summary>
    NumLock = 2,
    /// <summary>Caps Lock 开启。</summary>
    CapsLock = 4,
    /// <summary>Kana Lock 开启。</summary>
    KanaLock = 8
}
