namespace ExHyperV.Services;

/// <summary>远端位置使用的坐标系。Cursor coordinate space.</summary>
public enum ConsoleCoordinateSpace
{
    /// <summary>坐标空间未知。</summary>
    Unknown = 0,
    /// <summary>来宾桌面物理像素。</summary>
    DesktopPixels = 1
}
