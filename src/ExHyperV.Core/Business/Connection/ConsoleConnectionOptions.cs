namespace ExHyperV.Services;

/// <summary>连接目标、初始桌面尺寸、缩放比例和连接超时。</summary>
/// <remarks>
/// <para>后端由 <see cref="ConsoleService.CreateConnection"/> 的 backendId 参数选择。
/// 当前 RDP 后端连接本机 Hyper-V 虚拟机。</para>
/// <para>Width、Height、DesktopScale 是请求值，实际画面尺寸以收到的 <see cref="ConsoleFrame"/> 为准。
/// 基本会话使用来宾已有的显示模式，不能通过这些参数保证改变来宾分辨率。</para>
/// </remarks>
public sealed record ConsoleConnectionOptions
{
    /// <summary>要连接的本机虚拟机标识。</summary>
    /// <fielddoc>
    /// <meaning>要连接的本机虚拟机标识。</meaning>
    /// <values kind="format">非空 GUID，例如 <c>6f0c2e91-8a45-4b37-9d60-c1257e3a8f42</c>。</values>
    /// </fielddoc>
    public Guid VmId { get; init; }
    /// <summary>请求的来宾桌面宽度。</summary>
    /// <fielddoc>
    /// <meaning>请求的来宾桌面宽度。</meaning>
    /// <values kind="format">200～8192 像素，默认 1024。Width × Height × 4 不超过 64 MiB 减 64 字节。</values>
    /// </fielddoc>
    public int Width { get; init; } = 1024;
    /// <summary>请求的来宾桌面高度。</summary>
    /// <fielddoc>
    /// <meaning>请求的来宾桌面高度。</meaning>
    /// <values kind="format">200～8192 像素，默认 768。Width × Height × 4 不超过 64 MiB 减 64 字节。</values>
    /// </fielddoc>
    public int Height { get; init; } = 768;
    /// <summary>请求的来宾桌面缩放比例。</summary>
    /// <fielddoc>
    /// <meaning>请求的来宾桌面缩放比例。</meaning>
    /// <values kind="format">100～500%，默认 100%。</values>
    /// </fielddoc>
    public uint DesktopScale { get; init; } = 100;
    /// <summary>等待底层连接建立的最长时间。</summary>
    /// <fielddoc>
    /// <meaning>等待底层连接建立的最长时间。</meaning>
    /// <values kind="format">大于零、不超过 2 分钟，默认 2 秒。</values>
    /// </fielddoc>
    /// <remarks>从原生连接对象创建完成后计时；不包含原生库初始化、来宾登录和断开清理。超时由 Disconnected 事件报告。</remarks>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        if (VmId == Guid.Empty) throw new ArgumentException("A VM ID is required.", nameof(VmId));
        if (!ConsolePayload.IsDesktopSize(Width, Height)) throw new ArgumentOutOfRangeException(nameof(Width));
        if (DesktopScale is < 100 or > 500) throw new ArgumentOutOfRangeException(nameof(DesktopScale));
        if (ConnectTimeout <= TimeSpan.Zero || ConnectTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
    }
}
