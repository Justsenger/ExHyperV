namespace ExHyperV.Services;

/// <summary>宿主可按方向选择此接口挂接到连接。Optional clipboard provider.</summary>
public interface IConsoleClipboardProvider : IConsoleClipboard
{
    /// <summary>读取当前提供方、目标、就绪状态和方向。</summary>
    /// <fielddoc><meaning>每次切换提供方时重新核对目标和方向。</meaning></fielddoc>
    ConsoleClipboardProviderInfo ProviderInfo { get; }
}
