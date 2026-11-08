namespace ExHyperV.UI.Windows.Console;

/// <summary>窗口传给 Core 的控制台选择；Hyper-V 协议和认证配方由 Core 持有。</summary>
public sealed class RdpConnectionSettings
{
    public Guid VmId { get; set; }
    public bool Enhanced { get; set; }
    public int DesktopWidth { get; set; }
    public int DesktopHeight { get; set; }
    public uint DesktopScaleFactor { get; set; } = 100;
    public int ConnectionTimeoutSeconds { get; set; } = 2;
    public int FullScreenHotKeyVirtualKey { get; set; } = 0x0D;
}
