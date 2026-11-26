namespace ExHyperV.Services;

/// <summary>从不可变基帧读取并覆盖目标矩形。Move from the base frame before applying dirty pixels.</summary>
/// <param name="SourceX">源矩形的左边界，单位为像素。
/// <fielddoc><meaning>源矩形的左边界，单位为像素。</meaning></fielddoc></param>
/// <param name="SourceY">源矩形的上边界，单位为像素。
/// <fielddoc><meaning>源矩形的上边界，单位为像素。</meaning></fielddoc></param>
/// <param name="Destination">目标矩形，源矩形与其等大。
/// <fielddoc><meaning>目标矩形，源矩形与其等大。</meaning></fielddoc></param>
public readonly record struct ConsoleFrameMove(int SourceX, int SourceY, ConsoleFrameRegion Destination);
