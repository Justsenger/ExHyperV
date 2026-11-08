using System.Buffers.Binary;

namespace ExHyperV.Services;

/// <summary>
/// 由调用方管理生命周期的单个 FreeRDP 控制台连接。
/// </summary>
/// <remarks>
/// 事件在后台线程触发；调用方须自行调度界面更新，不得阻塞回调线程。
/// 此对象不读取或写入宿主剪贴板。连接停止后如需重连，应创建新对象。
/// </remarks>
internal sealed class FreeRdpConsoleConnection : IConsoleConnection, IConsoleVideoSource, IConsoleInput, IConsoleClipboard
{
    private readonly object gate = new();
    private readonly ConsoleConnectionOptions options;
    private readonly bool enhanced;
    private readonly IConsoleNativeBackend backend;
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IConsoleNativeConnection? native;
    private ConsoleConnectionState state;
    private bool started, stopping, clipboardAvailable;
    private bool connectTimedOut;
    private int desktopWidth, desktopHeight;
    private uint clipboardRevision;
    private readonly ConsoleInputBuffer inputs;
    private Timer? inputRetry;

    /// <summary>创建尚未启动的控制台连接并同步校验选项；原生组件在 Start 后加载。</summary>
    /// <param name="options">本地虚拟机连接选项；不能为空且须满足尺寸、缩放和超时限制。</param>
    /// <param name="enhanced">由所选连接标识确定的 FreeRDP 模式。</param>
    /// <exception cref="ArgumentNullException">options 为 null。</exception>
    /// <exception cref="ArgumentException">虚拟机 GUID 为空，或尺寸、缩放、超时不在允许范围内。</exception>
    public FreeRdpConsoleConnection(ConsoleConnectionOptions options, bool enhanced) : this(options, enhanced, new FreeRdpNative(enhanced)) { }
    internal FreeRdpConsoleConnection(ConsoleConnectionOptions options, bool enhanced, IConsoleNativeBackend backend)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        options.Validate();
        this.options = options;
        this.enhanced = enhanced;
        this.backend = backend;
        inputs = new((kind, data) => native is not null && native.Send(kind, data));
    }

    /// <summary>线程安全读取的连接状态。</summary>
    public ConsoleConnectionState State { get { lock (gate) return state; } }
    /// <summary>此连接的选择标识；基本与增强模式分别报告自己的标识。</summary>
    public string BackendId => enhanced ? ConsoleService.RdpEnhancedBackendId : ConsoleService.RdpBasicBackendId;
    /// <summary>画面能力；与原 FrameReceived 共用同一条帧租约路径，不复制像素。</summary>
    public IConsoleVideoSource Video => this;
    /// <summary>键鼠输入能力。</summary>
    public IConsoleInput Input => this;
    /// <summary>增强会话提供剪贴板能力；基本会话为 null，就绪状态须另查 IsAvailable。</summary>
    public IConsoleClipboard? Clipboard => enhanced ? this : null;
    /// <summary>当前 FreeRDP 接入未实现音频接收，为 null。</summary>
    public IConsoleAudioOutput? AudioOutput => null;
    /// <summary>当前 FreeRDP 接入未实现音频发送，为 null。</summary>
    public IConsoleAudioInput? AudioInput => null;
    bool IConsoleVideoSource.SupportsResize => enhanced;
    bool IConsoleClipboard.IsAvailable
    {
        get { lock (gate) return clipboardAvailable && !stopping && state == ConsoleConnectionState.Connected; }
    }
    /// <summary>底层连接建立后在后台线程通知；来宾登录状态由来宾管理。</summary>
    public event Action? Connected;
    /// <summary>底层报告登录完成时在后台线程通知；不要求 Core 读取来宾密码。</summary>
    public event Action? LoginCompleted;
    /// <summary>底层清理结束后在后台线程通知，可在此通知中等待 DisposeAsync；事件异常会被隔离。</summary>
    public event Action<ConsoleDisconnectInfo>? Disconnected;
    /// <summary>收到一帧借用像素，仅在回调期间有效；异步处理前须调用 Retain，并在处理完成后释放保留的租约。</summary>
    public event Action<ConsoleFrame>? FrameReceived;
    /// <summary>增强会话收到支持格式的数据时在后台线程通知；调用方负责宿主剪贴板交互，回调不可阻塞。</summary>
    public event Action<ConsoleClipboardData>? ClipboardReceived;
    /// <summary>增强会话剪贴板通道可用性变化时在后台线程通知；false 时 SetClipboard 不可发送。</summary>
    public event Action<bool>? ClipboardAvailabilityChanged;
    /// <summary>已入队的尺寸调整请求在显示通道发送失败；这不是来宾返回的调整结果。</summary>
    public event Action<uint>? ResizeRejected;

    /// <summary>启动后台连接并立即返回；同一对象只能启动一次，已经断开或请求停止后不能再次启动。</summary>
    /// <remarks>订阅事件应先于此调用。原生加载、连接和超时错误通过 Disconnected 报告；重复启动会同步抛出 InvalidOperationException。</remarks>
    /// <exception cref="InvalidOperationException">此对象已启动过或已请求停止。</exception>
    public void Start()
    {
        lock (gate)
        {
            if (started || stopping) throw new InvalidOperationException("This console connection has already been started or stopped.");
            started = true;
            state = ConsoleConnectionState.Connecting;
            _ = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
    }

    private void Run()
    {
        uint error = 0;
        string? message = null;
        IConsoleNativeConnection? owned = null;
        try
        {
            owned = backend.Create(options, OnNativeEvent, OnNativeFrame);
            lock (gate)
            {
                native = owned;
                if (stopping) owned.Stop();
            }
            using var deadline = new Timer(_ =>
            {
                lock (gate)
                {
                    if (stopping || state != ConsoleConnectionState.Connecting) return;
                    connectTimedOut = true;
                    stopping = true;
                    state = ConsoleConnectionState.Disconnecting;
                    native?.Stop();
                }
            }, null, options.ConnectTimeout, Timeout.InfiniteTimeSpan);
            error = owned.Run();
        }
        catch (Exception ex) { error = 0xffffffff; message = ex.Message; }
        finally
        {
            lock (gate) { native = null; stopping = true; inputRetry?.Dispose(); inputRetry = null; inputs.Clear(); }
            try { owned?.Dispose(); }
            catch (Exception ex) { error = 0xffffffff; message ??= ex.Message; }
            bool notifyClipboard;
            lock (gate)
            {
                if (connectTimedOut) { error = 0xfffffffe; message = "The Hyper-V console connection timed out."; }
                state = ConsoleConnectionState.Disconnected;
                notifyClipboard = clipboardAvailable;
                clipboardAvailable = false;
            }
            // A Disconnected subscriber can safely await/perform disposal: native cleanup is finished.
            completion.TrySetResult();
            if (notifyClipboard) Raise(ClipboardAvailabilityChanged, false);
            Raise(Disconnected, new ConsoleDisconnectInfo(error, message));
        }
    }

    private void OnNativeEvent(uint kind, byte[] data, uint a, uint b)
    {
        lock (gate) { if (stopping || !started) return; }
        switch (kind)
        {
            case 1:
                lock (gate) { if (stopping || state == ConsoleConnectionState.Connected) return; state = ConsoleConnectionState.Connected; }
                Raise(Connected);
                break;
            case 2:
                if (State == ConsoleConnectionState.Connected) Raise(LoginCompleted);
                break;
            case 4:
                if (enhanced && ConsolePayload.TryReadClipboard(data, out var clipboard))
                {
                    lock (gate)
                    {
                        if (stopping || a != clipboardRevision) return;
                        Raise(ClipboardReceived, clipboard);
                    }
                }
                break;
            case 5:
                bool available = enhanced && a != 0;
                lock (gate) { if (stopping || clipboardAvailable == available) return; clipboardAvailable = available; }
                Raise(ClipboardAvailabilityChanged, available);
                break;
            case 6:
                if (a == 3 && enhanced) Raise(ResizeRejected, b);
                break;
        }
    }

    public ConsoleFrame? CaptureFrame()
    {
        // Drain this call before Run's finally clears/disposes native. Do not keep
        // a permanent managed lease: that would prevent in-place cursor updates.
        lock (gate)
            return !stopping && state == ConsoleConnectionState.Connected ? native?.CaptureFrame() : null;
    }

    private void OnNativeFrame(ConsoleFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.Width > 8192 || frame.Height > 8192
            || frame.Stride != (long)frame.Width * 4 || frame.ByteLength != (long)frame.Stride * frame.Height
            || frame.ByteLength > ConsolePayload.MaxFrameBytes || frame.Sequence <= 0 || frame.BaseSequence >= frame.Sequence
            || !frame.Damage.IsWithin(frame.Width, frame.Height)) return;
        lock (gate)
        {
            if (stopping || !started) return;
            desktopWidth = frame.Width; desktopHeight = frame.Height;
            inputs.Width = desktopWidth; inputs.Height = desktopHeight;
        }
        var handlers = FrameReceived;
        if (handlers is null) return;
        foreach (Action<ConsoleFrame> handler in handlers.GetInvocationList())
        {
            // Each observer borrows its own wrapper: disposal or exceptions in one
            // observer cannot invalidate another observer's independently retained lease.
            using var borrowed = frame.Retain();
            try { handler(borrowed); }
            catch { /* Observer failures cannot unwind into the native output thread. */ }
        }
    }

    /// <summary>提交一个键盘扫描码的按下或释放事件。</summary>
    /// <param name="scanCode">扫描码，范围 1 至 255，不是字符码或虚拟键码。</param>
    /// <param name="extended">是否使用扩展扫描码标记。</param>
    /// <param name="down">true 为按下，false 为释放。</param>
    /// <returns>已加入发送队列时为 true；无效输入、未连接或队列拒绝时为 false。</returns>
    public bool SendKey(ushort scanCode, bool extended, bool down)
    {
        lock (gate)
        {
            if (!CanSendInput) return false;
            bool accepted = inputs.Key(scanCode, extended, down);
            ScheduleInputRetry();
            return accepted;
        }
    }

    /// <summary>发送 Pause 键专用的 E1 输入序列；该序列不同于 Num Lock 的普通扫描码。</summary>
    /// <returns>是否已将 Pause 的特殊 E1 输入序列加入发送队列。</returns>
    public bool SendPause() => Send(6, ReadOnlySpan<byte>.Empty);

    /// <summary>获得输入焦点时同步键盘锁定状态；状态由调用方提供，Core 不读取宿主键盘。</summary>
    /// <param name="flags">由调用方取得的锁定键组合，只允许枚举定义的低四位。</param>
    /// <returns>是否已加入发送队列；无效标志、未连接或队列拒绝时为 false。</returns>
    public bool SynchronizeKeyboard(ConsoleKeyboardToggleState flags)
    {
        if (((uint)flags & ~15u) != 0) return false;
        Span<byte> data = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)flags);
        return Send(5, data);
    }

    /// <summary>提交桌面坐标中的移动、单个按键或滚轮事件。</summary>
    /// <param name="x">来宾桌面横坐标，单位为物理像素，须小于最近收到的桌面宽度。</param>
    /// <param name="y">来宾桌面纵坐标，单位为物理像素，须小于最近收到的桌面高度。</param>
    /// <param name="button">单个按键；移动或滚轮使用 None，不能按位组合。</param>
    /// <param name="down">按键是否按下；button 为 None 时必须为 false。</param>
    /// <param name="wheel">滚轮增量，范围 -255 至 255；非零时 button 必须为 None。</param>
    /// <returns>是否已入队；首帧前没有桌面尺寸，越界、参数不合法或连接不可发送时均为 false。</returns>
    public bool SendPointer(ushort x, ushort y, ConsolePointerButton button = ConsolePointerButton.None, bool down = false, short wheel = 0)
    {
        lock (gate)
        {
            if (!CanSendInput) return false;
            bool accepted = inputs.Pointer(x, y, button, down, wheel);
            ScheduleInputRetry();
            return accepted;
        }
    }

    /// <summary>请求调整增强会话的桌面尺寸和缩放；返回成功仅表示入队，实际尺寸以收到的帧为准。</summary>
    /// <param name="width">目标宽度，200 至 8192 像素；发送时向下取偶数，总像素大小还须满足连接缓冲限制。</param>
    /// <param name="height">目标高度，200 至 8192 像素。</param>
    /// <param name="desktopScale">缩放百分比，100 至 500，默认 100。</param>
    /// <returns>是否已入显示通道队列；基本会话或无效尺寸返回 false。来宾最终尺寸以收到的帧为准。</returns>
    public bool Resize(int width, int height, uint desktopScale = 100)
    {
        if (!enhanced || !ConsolePayload.IsDesktopSize(width, height) || desktopScale is < 100 or > 500) return false;
        width &= ~1; // MS-RDPEDISP requires an even desktop width.
        Span<byte> data = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(data[8..], desktopScale);
        return Send(3, data);
    }

    /// <summary>发送调用方显式提供的剪贴板内容，需要增强会话及可用的来宾剪贴板通道。</summary>
    /// <param name="clipboard">显式提供的支持格式集合；发送前校验并复制，不会从宿主读取内容。</param>
    /// <returns>已加入发送队列时为 true；格式无效、通道不可用或连接停止时为 false。</returns>
    public bool SetClipboard(ConsoleClipboardData clipboard)
    {
        if (!ConsolePayload.TryWriteClipboard(clipboard, out var data)) return false;
        lock (gate)
        {
            if (!clipboardAvailable || stopping || state != ConsoleConnectionState.Connected || native is null || !native.Send(4, data)) return false;
            unchecked { clipboardRevision++; }
            return true;
        }
    }

    public bool SendKeys(IReadOnlyList<ConsoleKeyEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (gate)
        {
            if (!CanSendInput) return false;
            bool accepted = inputs.Keys(events);
            ScheduleInputRetry();
            return accepted;
        }
    }

    public bool SendClick(ushort x, ushort y, ConsolePointerButton button = ConsolePointerButton.Left)
    {
        lock (gate)
        {
            if (!CanSendInput) return false;
            bool accepted = inputs.Click(x, y, button);
            ScheduleInputRetry();
            return accepted;
        }
    }

    public bool ReleaseAllInputs()
    {
        lock (gate)
        {
            if (!CanSendInput) return false;
            inputs.ReleaseAll();
            ScheduleInputRetry();
            return true;
        }
    }

    private bool CanSendInput => !stopping && state == ConsoleConnectionState.Connected && native is not null;

    private void ScheduleInputRetry()
    {
        if (inputs.HasPending)
        {
            inputRetry ??= new Timer(_ => RetryInputs(), null, Timeout.Infinite, Timeout.Infinite);
            inputRetry.Change(10, Timeout.Infinite);
        }
        else inputRetry?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void RetryInputs()
    {
        lock (gate)
        {
            if (!CanSendInput) return;
            try { inputs.Flush(); ScheduleInputRetry(); }
            catch { _ = DisconnectAsync(); } // Never let a retry timer exception escape a ThreadPool callback.
        }
    }

    private bool Send(uint kind, ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            if (!CanSendInput) return false;
            if (kind is 5 or 6)
            {
                inputs.Flush();
                ScheduleInputRetry();
                if (inputs.HasPending) return false;
            }
            return native!.Send(kind, data);
        }
    }

    /// <summary>请求停止连接，并等待原生连接清理结束；可重复调用。</summary>
    /// <returns>等待底层清理完成的任务；Disconnected 订阅者独立执行。</returns>
    /// <remarks>未启动时调用会关闭该对象的启动资格。不要在普通帧或输入事件回调中同步阻塞等待断开，否则可能阻塞底层退出。</remarks>
    public Task DisconnectAsync()
    {
        lock (gate)
        {
            if (!stopping)
            {
                stopping = true;
                if (!started) completion.TrySetResult();
                else { state = ConsoleConnectionState.Disconnecting; native?.Stop(); }
            }
            return completion.Task;
        }
    }

    /// <summary>异步请求停止并等待原生资源清理。</summary>
    /// <returns>底层清理完成时结束的异步操作；不使其他仍有效的帧租约失效。</returns>
    public ValueTask DisposeAsync() => new(DisconnectAsync());
    /// <summary>请求清理并立即返回，不阻塞事件线程；需要等待原生资源释放时使用 DisposeAsync。</summary>
    public void Dispose() => _ = DisconnectAsync();

    // An observer exception must never unwind through a native FreeRDP callback.
    private static void Raise(Action? handlers)
    { if (handlers is not null) foreach (Action handler in handlers.GetInvocationList()) { try { handler(); } catch { } } }
    private static void Raise<T>(Action<T>? handlers, T value)
    { if (handlers is not null) foreach (Action<T> handler in handlers.GetInvocationList()) { try { handler(value); } catch { } } }
}
