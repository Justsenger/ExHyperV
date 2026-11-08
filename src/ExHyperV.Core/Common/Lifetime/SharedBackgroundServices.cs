namespace ExHyperV.Services;

/// <summary>集中执行 Core 进程级 ARP 后台资源的最终清理。</summary>
public static class SharedBackgroundServices
{
    /// <summary>释放共享 ARP 捕获；用于宿主程序最终退出。</summary>
    /// <remarks>清理后不支持在同一进程重新启动捕获；不关闭虚拟机。</remarks>
    public static Task ShutdownAsync()
    {
        ArpSnoopService.Instance.Dispose();
        return Task.CompletedTask;
    }
}
