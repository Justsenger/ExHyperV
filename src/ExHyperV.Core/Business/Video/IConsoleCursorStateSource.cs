namespace ExHyperV.Services;

/// <summary>视频源可附加的独立光标流。Optional remote cursor state stream.</summary>
public interface IConsoleCursorStateSource
{
    /// <summary>最近一次位置和合成状态。</summary>
    /// <fielddoc><meaning>首次报告前为 null。</meaning></fielddoc>
    ConsoleCursorState? CursorState { get; }

    /// <summary>后端报告位置、可见性或形状版本变化时触发。</summary>
    event Action<ConsoleCursorState>? CursorStateChanged;
}
