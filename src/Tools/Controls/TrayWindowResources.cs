using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ExHyperV.Tools.Controls;

/// <summary>退托盘时释放主窗口的渲染资源，保留窗口、页面状态与后台任务。</summary>
internal sealed class TrayWindowResources(Window window) : IDisposable
{
    private HwndSource? _source;
    private Visual? _root;
    private RenderMode _renderMode;
    private DispatcherOperation? _trimOperation;

    public void Suspend()
    {
        if (_source != null || window.IsVisible) return;
        var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        if (source?.CompositionTarget is not { } target || target.RootVisual == null) return;

        _source = source;
        _root = target.RootVisual;
        _renderMode = target.RenderMode;
        try
        {
            // Hide 仍会保留 WPF 的 GPU 渲染目标/纹理。两步缺一不可：释放可视树的
            // 合成资源，并让这个隐藏窗口退出硬件渲染；不能改进程级 RenderMode，
            // 否则会影响仍打开的控制台。保留 HwndSource.RootVisual，主题监听仍有效。
            target.RootVisual = null;
            target.RenderMode = RenderMode.SoftwareOnly;

            // 只在本次退托盘后回收一次，不设周期性 GC/工作集清理，避免后台反复缺页。
            _trimOperation = window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(TrimWhenIdle));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ExHyperV] Suspend tray rendering failed: {ex.Message}");
            Resume();
        }
    }

    public void Resume()
    {
        _trimOperation?.Abort();
        _trimOperation = null;
        var source = _source;
        var root = _root;
        _source = null;
        _root = null;
        if (source == null || source.IsDisposed || source.CompositionTarget is not { } target) return;

        // 必须在 Show 前恢复；轻量化/已有软件渲染窗口也要恢复自己的原始模式。
        try
        {
            target.RenderMode = _renderMode;
        }
        finally
        {
            target.RootVisual = root;
        }
    }

    private void TrimWhenIdle()
    {
        _trimOperation = null;
        if (_source == null || window.IsVisible ||
            Application.Current.Windows.Cast<Window>().Any(w => w.IsVisible)) return;

        try
        {
            // 先释放渲染资源，再归还可回收的托管内存和空闲物理页；工作集回收
            // 本身并不释放 GPU 分配。其他窗口可见时不回收整进程，避免影响控制台。
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
            using var process = Process.GetCurrentProcess();
            if (!SetProcessWorkingSetSize(process.Handle, new IntPtr(-1), new IntPtr(-1)))
                Debug.WriteLine($"[ExHyperV] Trim tray working set failed: {Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ExHyperV] Trim tray working set failed: {ex.Message}");
        }
    }

    public void Dispose() => Resume();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
}
