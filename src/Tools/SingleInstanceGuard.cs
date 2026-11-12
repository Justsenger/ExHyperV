using System.Runtime.InteropServices;

namespace ExHyperV.Tools;

/// <summary>
/// 进程级单实例门禁：命名 Mutex 判定唯一实例；后来的实例广播一条自定义消息，
/// 让已在运行的实例把主窗口（可能已隐藏到托盘）显示并前置。
/// </summary>
internal static class SingleInstanceGuard
{
    // Local\ 作用域 = 当前登录会话。RDP / 多用户会话各自独立，互不阻塞。
    // Global\ 会让整机只允许一个进程，但跨完整性级别可能抛 UnauthorizedAccessException。
    private const string MutexName = @"Local\ExHyperV.SingleInstance";
    private const string ActivationMessageName = "ExHyperV.ActivateMainWindow";

    private const int HwndBroadcast = 0xFFFF;
    // SW_SHOW：按当前大小/位置激活并显示。不用 SW_RESTORE，否则会把最大化窗口还原成普通大小。
    private const int SwShow = 5;
    // ASFW_ANY：把置前许可转交给任意进程（此处即已在运行的实例）。
    private const int AsfwAny = -1;

    // 必须静态持有到进程退出：Mutex 一旦被 GC 回收或释放，内核句柄随之消失，
    // 下一个进程就会误判成首个实例。
    private static Mutex? _mutex;

    // RegisterWindowMessage 成功时返回 0xC000~0xFFFF，转 int 不会溢出；失败返回 0。
    // 0 与 WM_NULL 同值，不能用来判定唤醒：注册失败时唤醒能力整体关闭，
    // 单实例判定仍由 Mutex 保证，不会因此多开。
    private static readonly uint ActivationMessage = RegisterWindowMessage(ActivationMessageName);

    /// <summary>消息号是否可用；注册失败时为 false，调用方须忽略唤醒消息。</summary>
    public static bool IsActivationAvailable => ActivationMessage != 0;

    /// <summary>供主窗口的 HwndSource 消息钩子比对。</summary>
    public static int ActivateMessage => (int)ActivationMessage;

    /// <summary>
    /// 尝试成为唯一实例。返回 false 表示已有实例在运行（唤醒广播已发出），调用方须立即退出。
    /// </summary>
    public static bool TryAcquire()
    {
        // 用构造函数的 createdNew 判定，不调 WaitOne：前一实例崩溃遗留的
        // abandoned mutex 既不会抛 AbandonedMutexException，也不影响判定。
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
            if (createdNew) return true;
        }
        catch (Exception ex)
        {
            // 同名 Mutex 被不同完整性级别/会话（如服务或旧版实例）先建时会抛
            // UnauthorizedAccessException。此时无法判定唯一实例，放弃门禁、
            // 照常启动，避免把用户挡在崩溃之外；单实例仅是体验增强，非安全边界。
            System.Diagnostics.Debug.WriteLine($"[ExHyperV] Single-instance mutex unavailable, proceeding without guard: {ex.Message}");
            return true;
        }

        _mutex.Dispose();
        _mutex = null;
        // 前台锁默认只把置前许可留给本进程：本进程由 shell 启动、刚拿到用户点击，
        // 须先把它转交给已在运行的实例，否则对方的 SetForegroundWindow 会被拦下，
        // 表现为窗口已显示但只闪任务栏。
        AllowSetForegroundWindow(AsfwAny);
        // 主窗口即便 Hide() 到托盘，HWND 仍然存在，广播消息照样能收到。
        // 消息号不可用时广播的是 WM_NULL，没有意义，直接跳过。
        if (IsActivationAvailable)
            PostMessage((IntPtr)HwndBroadcast, ActivationMessage, IntPtr.Zero, IntPtr.Zero);
        return false;
    }

    /// <summary>跨进程唤醒时单靠 Window.Activate() 常不足以置前，补一次 Win32 前置。</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        ShowWindow(hwnd, SwShow);
        SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
