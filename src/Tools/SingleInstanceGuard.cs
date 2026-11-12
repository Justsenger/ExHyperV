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

    // 必须静态持有到进程退出：Mutex 一旦被 GC 回收或释放，内核句柄随之消失，
    // 下一个进程就会误判成首个实例。
    private static Mutex? _mutex;

    // RegisterWindowMessage 返回 0xC000~0xFFFF，转 int 不会溢出。返回 0 表示注册失败，
    // 此时只是无法唤醒（单实例判定仍由 Mutex 保证），不会造成多开。
    private static readonly uint ActivationMessage = RegisterWindowMessage(ActivationMessageName);

    /// <summary>供主窗口的 HwndSource 消息钩子比对。</summary>
    public static int ActivateMessage => (int)ActivationMessage;

    /// <summary>
    /// 尝试成为唯一实例。返回 false 表示已有实例在运行（唤醒广播已发出），调用方须立即退出。
    /// </summary>
    public static bool TryAcquire()
    {
        // 用构造函数的 createdNew 判定，不调 WaitOne：前一实例崩溃遗留的
        // abandoned mutex 既不会抛 AbandonedMutexException，也不影响判定。
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (createdNew) return true;

        _mutex.Dispose();
        _mutex = null;
        // 主窗口即便 Hide() 到托盘，HWND 仍然存在，广播消息照样能收到。
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
}
