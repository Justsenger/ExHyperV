namespace ExHyperV.WinUI3.Composition;

/// <summary>产品构建在 App 启动时提供的配置。开源构建不依赖任何商业模块。</summary>
public sealed partial class ProductConfiguration
{
    public string DisplayName { get; init; } = "ExHyperV";
    public IDesktopWindowProvider? DesktopWindows { get; init; }
}

/// <summary>
/// 可选的桌面窗口提供者。全部方法在 WinUI 主线程调用。
/// 此接口只管理窗口，不表示实现了 Core 的无窗口视频、音频和输入接口。
/// </summary>
public interface IDesktopWindowProvider
{
    string DisplayName { get; }
    /// <summary>打开或激活窗口。不启动虚拟机，不自动改用其他连接方式。实现须检查授权。</summary>
    void Open(Guid vmId, string displayName);
    /// <summary>关闭所属窗口，并在返回前结束相关输入和帧资源使用。须可重复调用；失败时允许重试。</summary>
    void CloseAll();
}
