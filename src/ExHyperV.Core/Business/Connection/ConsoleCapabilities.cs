namespace ExHyperV.Services;

/// <summary>控制台后端实现的功能，可使用按位运算组合和检查。</summary>
/// <remarks>例如 (capabilities &amp; ConsoleCapabilities.Clipboard) != 0 表示实现了剪贴板。
/// 这份列表描述后端能力；某次连接的通道是否就绪，由对应能力接口报告。</remarks>
[Flags]
public enum ConsoleCapabilities
{
    /// <summary>没有已接入能力。</summary>
    None = 0,
    /// <summary>接收画面。</summary>
    Video = 1,
    /// <summary>发送键鼠输入。</summary>
    Input = 2,
    /// <summary>与来宾交换剪贴板内容。</summary>
    Clipboard = 4,
    /// <summary>请求调整来宾桌面的分辨率和缩放比例。</summary>
    DisplayResize = 8,
    /// <summary>预留的音频接收标志；当前内置后端不声明此能力。</summary>
    AudioOutput = 16,
    /// <summary>历史预留标志，不属于当前功能范围；内置后端不声明此能力。</summary>
    AudioInput = 32
}
