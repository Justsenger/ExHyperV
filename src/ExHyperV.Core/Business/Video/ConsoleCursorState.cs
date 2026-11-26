namespace ExHyperV.Services;

/// <summary>与已有光标形状事件并存的位置状态。Remote cursor position and composition state.</summary>
/// <param name="X">热点横坐标，原点按坐标空间解释。
/// <fielddoc><meaning>热点横坐标，原点按坐标空间解释。</meaning></fielddoc></param>
/// <param name="Y">热点纵坐标，原点按坐标空间解释。
/// <fielddoc><meaning>热点纵坐标，原点按坐标空间解释。</meaning></fielddoc></param>
/// <param name="Visible">远端光标是否可见。
/// <fielddoc><meaning>远端光标是否可见。</meaning></fielddoc></param>
/// <param name="Composited">帧内是否已合成光标。
/// <fielddoc><meaning>帧内是否已合成光标。</meaning></fielddoc></param>
/// <param name="CoordinateSpace">位置坐标系，未知值不用于定位。
/// <fielddoc><meaning>位置坐标系，未知值不用于定位。</meaning></fielddoc></param>
/// <param name="ShapeVersion">光标形状版本，零表示尚未取得形状。
/// <fielddoc><meaning>光标形状版本，零表示尚未取得形状。</meaning></fielddoc></param>
public sealed record ConsoleCursorState(int X,
    int Y,
    bool Visible,
    bool Composited,
    ConsoleCoordinateSpace CoordinateSpace,
    ulong ShapeVersion);
