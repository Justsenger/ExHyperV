using System.Runtime.InteropServices;

namespace ExHyperV.Services;

/// <summary>桌面上需要更新的一块矩形区域，位置和大小以像素表示。</summary>
/// <param name="X">区域左边界相对桌面左上角的横坐标。
/// <fielddoc>
/// <meaning>区域左边界相对桌面左上角的横坐标。</meaning>
/// <values kind="format">非负整数，单位为像素。</values>
/// </fielddoc>
/// </param>
/// <param name="Y">区域上边界相对桌面左上角的纵坐标。
/// <fielddoc>
/// <meaning>区域上边界相对桌面左上角的纵坐标。</meaning>
/// <values kind="format">非负整数，单位为像素。</values>
/// </fielddoc>
/// </param>
/// <param name="Width">区域的宽度。
/// <fielddoc>
/// <meaning>区域的宽度。</meaning>
/// <values kind="format">非负整数，单位为像素；0 表示空区域。</values>
/// </fielddoc>
/// </param>
/// <param name="Height">区域的高度。
/// <fielddoc>
/// <meaning>区域的高度。</meaning>
/// <values kind="format">非负整数，单位为像素；0 表示空区域。</values>
/// </fielddoc>
/// </param>
/// <remarks>右边界 X + Width 和下边界 Y + Height 不计入区域。
/// 例如 (10, 20, 2, 1) 包含 (10, 20)、(11, 20) 两个像素。
/// 自行构造时请提供非负坐标和尺寸；此记录的构造器不检查是否越出桌面。</remarks>
public readonly record struct ConsoleFrameRegion(int X, int Y, int Width, int Height)
{
    /// <summary>区域是否不包含像素。</summary>
    /// <fielddoc>
    /// <meaning>区域是否不包含像素。</meaning>
    /// <values kind="options">宽度或高度为 0 时为 true，否则为 false。</values>
    /// </fielddoc>
    public bool IsEmpty => Width == 0 || Height == 0;

    internal bool IsWithin(int width, int height) => X >= 0 && Y >= 0 && Width >= 0 && Height >= 0
        && (long)X + Width <= width && (long)Y + Height <= height;

    /// <summary>取得能同时覆盖本区域和另一区域的最小矩形。</summary>
    /// <param name="other">要合并的区域，与当前区域使用同一桌面坐标系。</param>
    /// <returns>合并后的外接矩形；其中一项为空时直接返回另一项。</returns>
    /// <remarks>合并不相邻区域时，返回的矩形也会包含两者之间的像素。</remarks>
    /// <exception cref="OverflowException">计算矩形边界时超出整数范围。</exception>
    public ConsoleFrameRegion Union(ConsoleFrameRegion other)
    {
        if (IsEmpty) return other;
        if (other.IsEmpty) return this;
        int x = Math.Min(X, other.X), y = Math.Min(Y, other.Y);
        return new(x, y, checked(Math.Max(X + Width, other.X + other.Width) - x),
            checked(Math.Max(Y + Height, other.Y + other.Height) - y));
    }
}
