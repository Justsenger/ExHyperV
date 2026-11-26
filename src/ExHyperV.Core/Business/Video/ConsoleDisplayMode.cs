namespace ExHyperV.Services;

/// <summary>后端可协商的显示模式。Negotiated guest display mode.</summary>
/// <param name="id">模式的稳定标识，区分大小写。</param>
/// <param name="width">桌面宽度，单位为像素。</param>
/// <param name="height">桌面高度，单位为像素。</param>
/// <param name="refreshNumerator">刷新率分子，单位为赫兹。</param>
/// <param name="refreshDenominator">刷新率分母，例如 60000/1001 的分母为 1001。</param>
public sealed class ConsoleDisplayMode(string id,
    int width,
    int height,
    uint refreshNumerator,
    uint refreshDenominator)
{
    /// <summary>模式的稳定标识，区分大小写。</summary>
    /// <fielddoc><meaning>模式的稳定标识，区分大小写。</meaning></fielddoc>
    public string Id { get; } = !string.IsNullOrWhiteSpace(id) ? id : throw new ArgumentException("需要模式标识。", nameof(id));

    /// <summary>桌面宽度，单位为像素。</summary>
    /// <fielddoc><meaning>桌面宽度，单位为像素。</meaning></fielddoc>
    public int Width { get; } = width > 0 ? width : throw new ArgumentOutOfRangeException(nameof(width));

    /// <summary>桌面高度，单位为像素。</summary>
    /// <fielddoc><meaning>桌面高度，单位为像素。</meaning></fielddoc>
    public int Height { get; } = height > 0 ? height : throw new ArgumentOutOfRangeException(nameof(height));

    /// <summary>刷新率分子，单位为赫兹。</summary>
    /// <fielddoc><meaning>刷新率分子，单位为赫兹。</meaning></fielddoc>
    public uint RefreshNumerator { get; } = refreshNumerator > 0 ? refreshNumerator : throw new ArgumentOutOfRangeException(nameof(refreshNumerator));

    /// <summary>刷新率分母，例如 60000/1001 的分母为 1001。</summary>
    /// <fielddoc><meaning>刷新率分母，例如 60000/1001 的分母为 1001。</meaning></fielddoc>
    public uint RefreshDenominator { get; } = refreshDenominator > 0 ? refreshDenominator : throw new ArgumentOutOfRangeException(nameof(refreshDenominator));
}
