using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Tray;
using WpfControls = System.Windows.Controls;

namespace ExHyperV.Tools.Controls;

/// <summary>主窗口隐藏到托盘；界面与 USB 始终属于同一个进程。</summary>
internal sealed class TrayIconController : NotifyIconService, IDisposable
{
    private readonly Application _application;
    private readonly Window _window;
    private readonly WpfControls.ContextMenu _menu = new();
    private WindowState _restoreState;
    private bool _exitRequested;
    private bool _disposed;

    public TrayIconController(Application application, Window window, ImageSource icon,
        string openText, string exitText)
    {
        _application = application;
        _window = window;
        _restoreState = window.WindowState == WindowState.Minimized
            ? WindowState.Normal : window.WindowState;

        try
        {
            var openItem = new WpfControls.MenuItem { Header = openText };
            openItem.Click += (_, _) => RestoreWindow();
            var exitItem = new WpfControls.MenuItem { Header = exitText };
            exitItem.Click += (_, _) => ExitApplication();
            _menu.Items.Add(openItem);
            _menu.Items.Add(new WpfControls.Separator());
            _menu.Items.Add(exitItem);
            TooltipText = "ExHyperV";
            Icon = icon;
            ContextMenu = _menu;

            // 不调用 SetParentWindow：它会在 Closing 时无条件销毁托盘，
            // 即使关闭已被取消。这里由 ExHyperV 管理隐藏和真正退出。
            _window.Loaded += OnWindowLoaded;
            _window.Closing += OnWindowClosing;
            _window.StateChanged += OnWindowStateChanged;
            _window.Closed += OnWindowClosed;
            _application.SessionEnding += OnSessionEnding;
            _application.Exit += OnApplicationExit;
            if (_window.IsLoaded) RegisterTray();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e) => RegisterTray();

    private void RegisterTray()
    {
        if (_disposed || _exitRequested || IsRegistered) return;
        try
        {
            // 此时主窗口已有 HWND。使用无参数 Register，不挂库的 Closing 处理。
            if (!Register())
                System.Diagnostics.Debug.WriteLine("[ExHyperV] Tray registration failed.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ExHyperV] Tray registration failed: {ex.Message}");
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested || _disposed || !IsRegistered) return;
        e.Cancel = true;
        // 只隐藏主窗口，不关闭控制台，也不停止 USB 通道和后台任务。
        _window.Hide();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window.WindowState != WindowState.Minimized)
            _restoreState = _window.WindowState;
    }

    protected override void OnLeftClick() => RestoreWindow();

    private void RestoreWindow()
    {
        if (_exitRequested || _disposed) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = _restoreState;
        _window.Activate();
    }

    private void ExitApplication()
    {
        if (_exitRequested || _disposed) return;
        _exitRequested = true;
        // 走原有 App.OnExit，先清理 USB 等资源，再结束整个进程。
        // 显式 Shutdown 也会关闭仍打开的控制台窗口。
        _application.Shutdown();
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // 系统注销/关机不能被“关闭到托盘”拦住。
        _exitRequested = true;
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Dispose();
    private void OnApplicationExit(object sender, ExitEventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Loaded -= OnWindowLoaded;
        _window.Closing -= OnWindowClosing;
        _window.StateChanged -= OnWindowStateChanged;
        _window.Closed -= OnWindowClosed;
        _application.SessionEnding -= OnSessionEnding;
        _application.Exit -= OnApplicationExit;
        _menu.IsOpen = false;
        Unregister();
        ContextMenu = null;
    }
}
