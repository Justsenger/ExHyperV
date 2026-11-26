namespace ExHyperV.Services;

/// <summary>可选的视频源能力，按稳定标识协商模式。Optional display mode negotiation.</summary>
public interface IConsoleDisplayModes
{
    /// <summary>读取当前可用的模式列表。</summary>
    /// <returns>按后端顺序排列的模式，未就绪时可为空。</returns>
    IReadOnlyList<ConsoleDisplayMode> GetModes();

    /// <summary>请求切换到已枚举的模式。</summary>
    /// <param name="id">当前后端提供的稳定模式标识。</param>
    /// <returns>是否接受请求和当前已确认的实际模式。</returns>
    ConsoleDisplayModeResult SetMode(string id);
}
