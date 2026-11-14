namespace ExHyperV.Services;

/// <summary>选择连接方式，创建用于查看和操作虚拟机桌面的控制台连接。</summary>
/// <remarks>
/// <para>先用 <see cref="ConsoleService.GetBackends"/> 查看可用后端，再用 <see cref="ConsoleService.CreateConnection"/> 创建连接。
/// 创建后订阅画面、连接状态等事件，调用 <see cref="IConsoleConnection.Start"/> 开始连接。
/// 连接的用法见 <see cref="IConsoleConnection"/>。</para>
/// <para>RdpBasic 提供基本画面和键鼠；RdpEnhanced 还提供剪贴板和分辨率调整，需要来宾支持增强会话。
/// 两者目前均由 FreeRDP 实现。</para>
/// </remarks>
/// <businessdoc category="连接">
/// <summary>选择后端，建立和关闭虚拟机通信连接。</summary>
/// <para>连接对象提供状态通知和视频、控制、剪贴板等接口。控制包含键盘和鼠标。</para>
/// <para>调用顺序：选择后端 → 创建连接 → 订阅事件 → Start → 使用所需能力 → DisconnectAsync 或 DisposeAsync。</para>
/// <para>RdpBasic 已提供视频和键鼠；RdpEnhanced 还提供剪贴板和分辨率调整。</para>
/// <para>从 <see cref="ConsoleService"/> 选择后端；完整连接示例见 <see cref="IConsoleConnection"/>。
/// 连接建立、来宾登录完成、剪贴板通道就绪是三个不同的时点。</para>
/// </businessdoc>
public static class ConsoleService
{
    /// <summary>RDP 基本会话的后端标识，提供视频和键鼠输入。</summary>
    /// <fielddoc>
    /// <meaning>RDP 基本会话的后端标识，提供视频和键鼠输入。</meaning>
    /// <values kind="format"><c>RdpBasic</c>。</values>
    /// </fielddoc>
    public const string RdpBasicBackendId = "RdpBasic";
    /// <summary>RDP 增强会话的后端标识，另提供剪贴板和分辨率调整，需要来宾支持增强会话。</summary>
    /// <fielddoc>
    /// <meaning>RDP 增强会话的后端标识，另提供剪贴板和分辨率调整，需要来宾支持增强会话。</meaning>
    /// <values kind="format"><c>RdpEnhanced</c>。</values>
    /// </fielddoc>
    public const string RdpEnhancedBackendId = "RdpEnhanced";
    private static readonly IConsoleBackend[] Backends =
        [new FreeRdpBackend(enhanced: false), new FreeRdpBackend(enhanced: true)];
    private static readonly IReadOnlyList<ConsoleBackendInfo> Descriptions =
        Array.AsReadOnly(Backends.Select(backend => backend.Info).ToArray());

    /// <summary>列出当前版本实现的控制台后端及其能力。</summary>
    /// <returns>只读列表，包含 RdpBasic 和 RdpEnhanced。</returns>
    /// <remarks>读取内置后端目录。
    /// 按 Capabilities 选择所需的功能。</remarks>
    public static IReadOnlyList<ConsoleBackendInfo> GetBackends() => Descriptions;

    /// <summary>为指定虚拟机创建控制台连接。</summary>
    /// <param name="options">目标虚拟机、初始桌面尺寸、缩放比例和连接超时。</param>
    /// <param name="backendId">后端标识，默认 RdpBasic。使用本类的标识常量或 GetBackends 返回的 BackendId；区分大小写。</param>
    /// <returns>尚未启动的连接。先订阅事件，再调用 Start；用完后由调用方释放。</returns>
    /// <remarks>此时只创建连接对象并检查参数，Start 才开始连接虚拟机。
    /// 后端选择决定基本或增强会话。</remarks>
    /// <exception cref="ArgumentNullException">options 或 backendId 为 null。</exception>
    /// <exception cref="ArgumentException">后端标识未知，或连接参数无效。</exception>
    public static IConsoleConnection CreateConnection(ConsoleConnectionOptions options, string backendId = RdpBasicBackendId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backendId);
        var backend = Backends.FirstOrDefault(candidate => candidate.Info.BackendId == backendId)
            ?? throw new ArgumentException("Unknown console backend.", nameof(backendId));
        return backend.Create(options);
    }

    private sealed class FreeRdpBackend(bool enhanced) : IConsoleBackend
    {
        public ConsoleBackendInfo Info { get; } = new(
            enhanced ? RdpEnhancedBackendId : RdpBasicBackendId,
            enhanced ? "RDP Enhanced" : "RDP Basic",
            ConsoleCapabilities.Video | ConsoleCapabilities.Input |
            (enhanced ? ConsoleCapabilities.Clipboard | ConsoleCapabilities.DisplayResize : ConsoleCapabilities.None));
        public IConsoleConnection Create(ConsoleConnectionOptions options) => new FreeRdpConsoleConnection(options, enhanced);
    }

}
