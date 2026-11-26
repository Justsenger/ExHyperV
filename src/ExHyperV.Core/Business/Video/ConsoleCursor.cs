namespace ExHyperV.Services;

/// <summary>远端虚拟机发来的鼠标指针形状与热点坐标。</summary>
/// <param name="Width">光标位图宽度。
/// <fielddoc>
/// <meaning>光标位图宽度。</meaning>
/// <values kind="format">正整数，单位为像素，范围 1 至 384。</values>
/// </fielddoc>
/// </param>
/// <param name="Height">光标位图高度。
/// <fielddoc>
/// <meaning>光标位图高度。</meaning>
/// <values kind="format">正整数，单位为像素，范围 1 至 384。</values>
/// </fielddoc>
/// </param>
/// <param name="HotX">热点横向偏移。
/// <fielddoc>
/// <meaning>热点横向偏移。</meaning>
/// <values kind="format">非负整数，单位为像素，小于 Width。</values>
/// </fielddoc>
/// </param>
/// <param name="HotY">热点纵向偏移。
/// <fielddoc>
/// <meaning>热点纵向偏移。</meaning>
/// <values kind="format">非负整数，单位为像素，小于 Height。</values>
/// </fielddoc>
/// </param>
/// <param name="Pixels">BGRA32 光标像素数据。
/// <fielddoc>
/// <meaning>BGRA32 光标像素数据。</meaning>
/// <values kind="format">长度等于 Width × Height × 4 的字节数组。</values>
/// </fielddoc>
/// </param>
public sealed record ConsoleCursor(int Width, int Height, int HotX, int HotY, byte[] Pixels);
