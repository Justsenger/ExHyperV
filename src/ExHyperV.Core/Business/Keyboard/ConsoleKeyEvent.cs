namespace ExHyperV.Services;

/// <summary>按键序列中的一个扫描码事件；按下和释放分别占一项。</summary>
/// <param name="ScanCode">键盘按键的 PC 扫描码。
/// <fielddoc>
/// <meaning>键盘按键的 PC 扫描码。</meaning>
/// <values kind="format">1～255。例如 A 键为 <c>0x1E</c>；实际字符由来宾键盘布局和修饰键决定。</values>
/// </fielddoc>
/// </param>
/// <param name="Extended">扫描码是否带 E0 前缀。
/// <fielddoc>
/// <meaning>扫描码是否带 E0 前缀。</meaning>
/// <values kind="options">true：带 E0 前缀；false：普通扫描码。</values>
/// </fielddoc>
/// </param>
/// <param name="Down">按键事件的动作。
/// <fielddoc>
/// <meaning>按键事件的动作。</meaning>
/// <values kind="options">true：按下；false：释放。</values>
/// </fielddoc>
/// </param>
public readonly record struct ConsoleKeyEvent(ushort ScanCode, bool Extended, bool Down);
