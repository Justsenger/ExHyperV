namespace ExHyperV.Services;

/// <summary>一条虚拟机桌面连接，提供画面、输入和其他受支持的交互能力。</summary>
/// <remarks>
/// <para>先订阅事件，再调用 Start。Connected 表示连接已建立；LoginCompleted 表示后端报告来宾登录完成；
/// 剪贴板通道另有自己的 IsAvailable。按实际需要等待相应状态。</para>
/// <para>Video、Input 等属性在对象存活期间保持不变；null 表示后端未实现该能力。
/// 例如 RdpBasic 的 Clipboard 为 null；RdpEnhanced 的 Clipboard 非 null，但需要等待通道就绪。</para>
/// <para>关闭时优先使用 await DisconnectAsync() 或 await DisposeAsync()，等待底层退出。
/// Dispose() 只发出停止请求。断开后若要重连，请新建对象。</para>
/// <para>事件在后台线程触发；界面更新需调度到界面线程，耗时工作应移出回调。
/// 回调中同步等待连接退出或其他依赖本连接的线程，可能造成死锁。
/// Disconnected 在底层清理完成后触发，可以在该通知中完成释放。各订阅者抛出的异常不会继续向外传播。</para>
/// </remarks>
/// <example>
/// <para>连接并监听状态，调用方决定何时结束。此例不显示画面；接收画面的示例见 IConsoleVideoSource。</para>
/// <code>
/// using ExHyperV.Services;
/// public static class ConnectionExample
/// {
///     public static async Task RunAsync(Guid vmId, CancellationToken stop)
///     {
///         await using var connection = ConsoleService.CreateConnection(
///             new ConsoleConnectionOptions { VmId = vmId },
///             ConsoleService.RdpEnhancedBackendId);
///         var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
///         connection.Connected += () => Console.WriteLine("控制台已连接");
///         connection.Disconnected += info =>
///         {
///             Console.WriteLine($"连接结束：{info.ErrorCode:X8} {info.Error}");
///             ended.TrySetResult();
///         };
///         connection.Start();
///         try { await ended.Task.WaitAsync(stop); }
///         catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
///         await connection.DisconnectAsync();
///     }
/// }
/// </code>
/// </example>
public interface IConsoleConnection : IDisposable, IAsyncDisposable
{
    /// <summary>当前连接使用的后端标识。</summary>
    /// <fielddoc>
    /// <meaning>当前连接使用的后端标识。</meaning>
    /// <values kind="options"><c>RdpBasic</c> 或 <c>RdpEnhanced</c>；与 ConsoleService.GetBackends 返回的 BackendId 对应。</values>
    /// </fielddoc>
    string BackendId { get; }
    /// <summary>当前连接所处的阶段。</summary>
    /// <fielddoc>
    /// <meaning>当前连接所处的阶段。</meaning>
    /// </fielddoc>
    /// <remarks>Connected 表示底层连接建立；来宾登录和剪贴板通道就绪分别由相应事件通知。</remarks>
    ConsoleConnectionState State { get; }
    /// <summary>接收来宾桌面画面的接口。</summary>
    /// <fielddoc>
    /// <meaning>接收来宾桌面画面的接口。</meaning>
    /// <values kind="format">未提供视频能力时为 null。</values>
    /// </fielddoc>
    IConsoleVideoSource? Video { get; }
    /// <summary>向来宾发送键盘和鼠标输入的接口。</summary>
    /// <fielddoc>
    /// <meaning>向来宾发送键盘和鼠标输入的接口。</meaning>
    /// <values kind="format">未提供输入能力时为 null。</values>
    /// </fielddoc>
    IConsoleInput? Input { get; }
    /// <summary>收发来宾剪贴板内容的接口。</summary>
    /// <fielddoc>
    /// <meaning>收发来宾剪贴板内容的接口。</meaning>
    /// <values kind="format">RdpBasic 为 null；RdpEnhanced 提供接口，通道就绪后 IsAvailable 为 true。</values>
    /// </fielddoc>
    IConsoleClipboard? Clipboard { get; }
    /// <summary>底层连接建立时通知。</summary>
    event Action? Connected;
    /// <summary>后端报告用户登录完成时通知；并非所有后端都会报告此事件。</summary>
    event Action? LoginCompleted;
    /// <summary>连接结束并完成底层清理时通知，参数包含诊断码和可选的错误说明。</summary>
    /// <remarks>仅对已经启动的连接通知。DisconnectAsync 等待的是底层清理完成，不等待此事件的订阅者执行完毕。</remarks>
    event Action<ConsoleDisconnectInfo>? Disconnected;
    /// <summary>开始连接虚拟机，立即返回；连接结果由事件通知。</summary>
    /// <remarks>连接前先启动虚拟机。每个对象只能启动一次，重连需新建对象。
    /// 启动后发生的连接失败通过 Disconnected 报告。</remarks>
    /// <exception cref="InvalidOperationException">已经启动过，或对象已请求关闭。</exception>
    void Start();
    /// <summary>断开连接，并等待底层资源释放完成。</summary>
    /// <returns>在底层清理完成时结束的任务；不等待 Disconnected 事件处理完毕。</returns>
    /// <remarks>可重复调用。尚未 Start 的对象也可以关闭，但关闭后不能再启动。</remarks>
    Task DisconnectAsync();
}
