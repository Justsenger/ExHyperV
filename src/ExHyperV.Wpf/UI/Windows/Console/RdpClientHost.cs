using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExHyperV.Services;
using Image = System.Windows.Controls.Image;
using Brushes = System.Windows.Media.Brushes;
using Cursors = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ExHyperV.UI.Windows.Console;

/// <summary>Core 图像、输入和剪贴板流的 WPF 适配；不实现 RDP 协议或 Hyper-V 认证。</summary>
public sealed class RdpClientHost : System.Windows.Controls.UserControl, IDisposable
{
    private readonly Image _image = new() { Stretch = Stretch.Fill, Focusable = false };
    private readonly ScrollViewer _scroll;
    private readonly ConsoleFrameStatistics _frameStatistics = new(Stopwatch.GetTimestamp());
    private readonly TextBlock _frameStatisticsText = new()
    {
        Foreground = Brushes.Lime, FontFamily = new FontFamily("Consolas"), FontSize = 13,
        Text = "FPS —", IsHitTestVisible = false
    };
    private readonly Border _frameStatisticsOverlay;
    private readonly DispatcherTimer _frameStatisticsTimer;
    private volatile bool _frameStatisticsEnabled;
    private readonly object _frameGate = new();
    private readonly HashSet<(ushort Scan, bool Extended)> _pressedKeys = new();
    private readonly HashSet<(ushort Scan, bool Extended)> _physicalKeys = new();
    private readonly List<Task> _retiredConnections = new();
    private readonly LowLevelKeyboardProc _keyboardCallback;
    private IConsoleConnection? _connection;
    private ConsoleFrame? _pendingFrame;
    private ConsoleFrameRegion _pendingDamage;
    private long _pendingBaseSequence;
    private WriteableBitmap? _bitmap;
    private int _generation, _state, _remoteWidth, _remoteHeight;
    private long _renderedSequence = -1;
    private bool _frameQueued;
    private volatile bool _disposed;
    private bool _enhanced, _manageDesktopPresentation, _topAlignedWhenFits, _preferNativePixels;
    private bool _smartSizing, _fullScreen, _shortcutDown, _pauseDown;
    private uint _zoom = 100;
    private int _fullScreenHotKey = 0x0D;
    private Window? _owner;
    private HwndSource? _windowSource;
    private IntPtr _keyboardHook;
    private bool _inputActive;
    private ConsolePointerButton _pressedButtons;
    private ushort _pointerX, _pointerY;
    private bool _clipboardAvailable;
    private uint _clipboardSequence;
    private DispatcherTimer? _clipboardRetry;
    private Task? _shutdownTask;
    private bool _inputFailureQueued;
    private static readonly uint HtmlClipboardFormat = RegisterClipboardFormat("HTML Format");
    private const uint UnicodeTextFormat = 13, DibFormat = 8;
    private const int MaxClipboardFormatBytes = 16 * 1024 * 1024;

    public event Action? Connected;
    public event Action? LoginCompleted;
    public event Action<int>? Disconnected;
    public event Action<int, int>? RemoteSizeChanged;
    public event Action<bool>? FullScreenRequested;
    public event Action? MinimizeRequested;
    public event Action? CloseRequested;
    public event Action<int>? FatalError;
    public event Action? ResolutionRejected;

    /// <summary>兼容窗口原入口：0 未连接，1 已连接，2 正在连接或停止。</summary>
    public int ConnectionState => _state == 3 ? 2 : _state;

    public static readonly DependencyProperty ShowFrameStatisticsProperty = DependencyProperty.Register(
        nameof(ShowFrameStatistics), typeof(bool), typeof(RdpClientHost),
        new PropertyMetadata(false, (owner, _) => ((RdpClientHost)owner).UpdateFrameStatisticsVisibility()));

    /// <summary>Counts received frames and completed bitmap updates, not monitor scan-out.</summary>
    public bool ShowFrameStatistics
    {
        get => (bool)GetValue(ShowFrameStatisticsProperty);
        set => SetValue(ShowFrameStatisticsProperty, value);
    }

    public RdpClientHost()
    {
        Background = Brushes.Black;
        Focusable = true;
        ClipToBounds = true;
        _keyboardCallback = KeyboardHook;
        _scroll = new ScrollViewer
        {
            Content = _image, Focusable = false, Background = Brushes.Black, CanContentScroll = false,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        _frameStatisticsOverlay = new Border
        {
            Child = _frameStatisticsText, Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(192, 0, 0, 0)),
            Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(8), CornerRadius = new CornerRadius(3),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false, Visibility = Visibility.Collapsed
        };
        var viewport = new Grid();
        viewport.Children.Add(_scroll);
        viewport.Children.Add(_frameStatisticsOverlay);
        Content = viewport;
        _frameStatisticsTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _frameStatisticsTimer.Tick += (_, _) => RefreshFrameStatistics();
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => UpdateDesktopPresentation();
        GotKeyboardFocus += (_, _) => UpdateInputFocus();
        LostKeyboardFocus += (_, _) => UpdateInputFocus();
        PreviewKeyDown += OnFallbackKeyDown;
        PreviewKeyUp += OnFallbackKeyUp;
        _image.MouseDown += OnImageMouseDown;
        _image.MouseUp += OnImageMouseUp;
        _image.MouseMove += OnImageMouseMove;
        _image.PreviewMouseWheel += OnImageMouseWheel;
        _image.LostMouseCapture += (_, _) => ReleasePointerButtons();
        Dispatcher.ShutdownStarted += OnDispatcherShutdownStarted;
    }

    public void Connect(RdpConnectionSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != 0) return;
        int generation = Interlocked.Increment(ref _generation);
        _enhanced = settings.Enhanced;
        _fullScreenHotKey = settings.FullScreenHotKeyVirtualKey;
        _manageDesktopPresentation = false;
        _smartSizing = settings.Enhanced;
        _clipboardAvailable = false;
        _remoteWidth = _remoteHeight = 0;
        _renderedSequence = -1;
        ClearFrame();
        _state = 2;
        try
        {
            var connection = ConsoleService.CreateConnection(new ConsoleConnectionOptions
            {
                VmId = settings.VmId, Enhanced = settings.Enhanced,
                Width = settings.DesktopWidth > 0 ? settings.DesktopWidth & ~1 : 1024,
                Height = settings.DesktopHeight > 0 ? settings.DesktopHeight : 768,
                DesktopScale = settings.DesktopScaleFactor,
                ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, settings.ConnectionTimeoutSeconds))
            });
            _connection = connection;
            var video = connection.Video ?? throw new NotSupportedException("所选控制台来源不提供画面。");
            connection.Connected += () => Dispatch(generation, () =>
            {
                if (_state == 3) return; // 用户已开始停止，迟到的成功回调不能重新启用输入。
                _state = 1; UpdateInputFocus(); Connected?.Invoke();
            });
            connection.LoginCompleted += () => Dispatch(generation, () => LoginCompleted?.Invoke());
            video.ResizeRejected += _ => Dispatch(generation, () => ResolutionRejected?.Invoke());
            connection.Disconnected += info => Dispatch(generation, () =>
            {
                if (!string.IsNullOrEmpty(info.Error)) Debug.WriteLine($"[Console] {info.Error}");
                HandleDisconnected(generation, unchecked((int)info.ErrorCode));
            });
            video.FrameReceived += frame => QueueFrame(generation, frame);
            if (connection.Clipboard is { } clipboard)
            {
                clipboard.ClipboardAvailabilityChanged += available => Dispatch(generation, () =>
                {
                    _clipboardAvailable = available;
                    if (available) PublishLocalClipboard(force: true);
                });
                clipboard.ClipboardReceived += data =>
                {
                    // 在 Core 回调发生时记住本地版本，不能等 Dispatcher 排到这条消息后才取。
                    uint sequence = GetClipboardSequenceNumber();
                    // 本地已复制但 WM_CLIPBOARDUPDATE 尚未处理时，先让本地内容同步到 Core。
                    if (!IsClipboardVersionCurrent(Volatile.Read(ref _clipboardSequence), sequence)) return;
                    Dispatch(generation, () => ReceiveClipboard(data, sequence));
                };
            }
            connection.Start();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Console] 连接初始化失败：{ex}");
            Dispatch(generation, () => { FatalError?.Invoke(ex.HResult); HandleDisconnected(generation, ex.HResult); });
        }
    }

    public void Disconnect()
    {
        if (_connection == null || _state == 0 || _state == 3) return;
        ReleaseInput();
        _state = 3;
        _ = StopConnectionAsync(_connection, _generation);
    }

    private async Task StopConnectionAsync(IConsoleConnection connection, int generation)
    {
        try { await connection.DisconnectAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Console] 断开失败：{ex}");
            Dispatch(generation, () => HandleDisconnected(generation, ex.HResult));
        }
    }

    private void HandleDisconnected(int generation, int reason)
    {
        if (generation != _generation || _disposed) return;
        ReleaseInput();
        _clipboardAvailable = false;
        var previous = _connection;
        _connection = null;
        _state = 0;
        Interlocked.Increment(ref _generation); // 旧帧和剪贴板不得覆盖下一条连接。
        ClearFrame();
        if (previous != null)
        {
            _retiredConnections.RemoveAll(task => task.IsCompleted);
            _retiredConnections.Add(RetireAsync(previous));
        }
        Disconnected?.Invoke(reason);
    }

    private static async Task RetireAsync(IConsoleConnection connection)
    {
        try { await connection.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Debug.WriteLine($"[Console] 连接收尾失败：{ex}"); }
    }

    /// <summary>先解绑界面，再异步等待 Core 归还原生连接；重复关闭共享同一结果。</summary>
    public Task ShutdownAndDisposeAsync()
    {
        if (_shutdownTask != null) return _shutdownTask;
        ReleaseInput();
        _disposed = true;
        _frameStatisticsEnabled = false;
        _frameStatisticsTimer.Stop();
        Interlocked.Increment(ref _generation);
        Dispatcher.ShutdownStarted -= OnDispatcherShutdownStarted;
        DetachWindow();
        ClearFrame();
        var connection = _connection;
        _connection = null;
        _state = 0;
        if (connection != null) _retiredConnections.Add(RetireAsync(connection));
        return _shutdownTask = Task.WhenAll(_retiredConnections);
    }

    public void ShutdownAndDispose() => ShutdownAndDisposeAsync().GetAwaiter().GetResult();
    public void Dispose() => ShutdownAndDispose();
    public bool Resize(int width, int height, double dpiScale) => _connection?.Video?.Resize(width & ~1, height,
        (uint)Math.Clamp(Math.Round(dpiScale * 100), 100, 500)) == true;

    public bool TryGetRemoteDesktopSize(out int width, out int height)
    {
        width = _remoteWidth; height = _remoteHeight;
        return _state == 1 && width > 0 && height > 0;
    }

    public void SetSmartSizing(bool on)
    {
        _manageDesktopPresentation = false; _smartSizing = on; UpdateDesktopPresentation();
    }

    public void ConfigureDesktopPresentation(bool preferNativePixels, int desktopWidth, int desktopHeight, bool topAlignedWhenFits = false)
    {
        _manageDesktopPresentation = true; _preferNativePixels = preferNativePixels;
        _topAlignedWhenFits = topAlignedWhenFits; UpdateDesktopPresentation();
    }

    public void SetZoomLevel(uint percent) { _zoom = Math.Clamp(percent, 1u, 500u); UpdateDesktopPresentation(); }
    public void SetFullScreen(bool fullScreen) => _fullScreen = fullScreen;
    public void RequestMinimize() => MinimizeRequested?.Invoke();
    public void RequestClose() => CloseRequested?.Invoke();

    private void Dispatch(int generation, Action action)
    {
        if (_disposed || generation != Volatile.Read(ref _generation) || Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
        {
            if (!_disposed && generation == _generation) action();
        }));
    }

    private void QueueFrame(int generation, ConsoleFrame frame)
    {
        ConsoleFrame? owned = frame.Retain();
        ConsoleFrame? previous = null;
        bool queued = false;
        try
        {
            lock (_frameGate)
            {
                if (!_disposed && generation == _generation && !Dispatcher.HasShutdownStarted
                    && frame.Sequence > _renderedSequence && (_pendingFrame == null || frame.Sequence > _pendingFrame.Sequence))
                {
                    if (_frameStatisticsEnabled) _frameStatistics.RecordReceived(Stopwatch.GetTimestamp());
                    var pending = _pendingFrame;
                    if (pending != null && pending.Width == frame.Width && pending.Height == frame.Height
                        && frame.BaseSequence == pending.Sequence)
                        _pendingDamage = _pendingDamage.Union(frame.Damage);
                    else
                    {
                        _pendingBaseSequence = frame.BaseSequence;
                        _pendingDamage = frame.Damage;
                        if (pending != null) _pendingBaseSequence = -1; // Missed damage or a resize requires a complete copy.
                    }
                    previous = pending;
                    _pendingFrame = owned;
                    owned = null;
                    if (!_frameQueued) { _frameQueued = true; queued = true; }
                }
            }
        }
        finally { owned?.Dispose(); previous?.Dispose(); }
        if (!queued) return;
        try
        {
            if (Dispatcher.HasShutdownStarted) { ReleasePendingFrame(generation); return; }
            var operation = Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => RenderLatestFrame(generation)));
            if (operation.Status == DispatcherOperationStatus.Aborted) ReleasePendingFrame(generation);
        }
        catch (InvalidOperationException) { ReleasePendingFrame(generation); }
        catch { ReleasePendingFrame(generation); throw; }
    }

    private unsafe void RenderLatestFrame(int generation)
    {
        ConsoleFrame? frame;
        ConsoleFrameRegion damage;
        long baseSequence;
        lock (_frameGate)
        {
            if (_disposed || generation != _generation) return;
            frame = _pendingFrame; _pendingFrame = null; _frameQueued = false;
            damage = _pendingDamage; baseSequence = _pendingBaseSequence;
        }
        if (frame == null) return;
        try
        {
            if (frame.Sequence <= _renderedSequence || frame.Width <= 0 || frame.Height <= 0
                || frame.Stride < (long)frame.Width * 4 || frame.ByteLength < (long)frame.Stride * frame.Height) return;
            bool changed = frame.Width != _remoteWidth || frame.Height != _remoteHeight;
            bool full = _bitmap == null || changed || frame.HasFullDamage || baseSequence != _renderedSequence;
            if (_bitmap == null || changed)
            {
                // RDP 画面不透明；忽略 FreeRDP 缓冲中未定义的 alpha 字节。
                _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                _image.Source = _bitmap;
            }
            if (full) damage = new(0, 0, frame.Width, frame.Height);
            if (!damage.IsEmpty)
            {
                fixed (byte* pixels = frame.Pixels)
                    _bitmap.WritePixels(new Int32Rect(damage.X, damage.Y, damage.Width, damage.Height),
                        (IntPtr)pixels, frame.ByteLength, frame.Stride, damage.X, damage.Y);
                if (_frameStatisticsEnabled) _frameStatistics.RecordPresented(Stopwatch.GetTimestamp());
            }
            _remoteWidth = frame.Width; _remoteHeight = frame.Height; _renderedSequence = frame.Sequence;
            _image.Cursor = Cursors.None; // Core 帧已经绘入远端光标，不叠加第二个本地箭头。
            UpdateDesktopPresentation();
            if (changed) RemoteSizeChanged?.Invoke(frame.Width, frame.Height);
        }
        finally { frame.Dispose(); }
    }

    private void OnDispatcherShutdownStarted(object? sender, EventArgs e)
    {
        _frameStatisticsEnabled = false;
        _frameStatisticsTimer.Stop();
        ReleasePendingFrame();
    }

    private void ReleasePendingFrame(int? generation = null)
    {
        ConsoleFrame? pending;
        lock (_frameGate)
        {
            if (generation.HasValue && generation.Value != _generation) return;
            pending = _pendingFrame; _pendingFrame = null; _frameQueued = false;
            _pendingDamage = default; _pendingBaseSequence = -1;
        }
        pending?.Dispose();
    }

    private void ClearFrame()
    {
        ReleasePendingFrame();
        _bitmap = null; _image.Source = null; _image.Cursor = Cursors.Arrow;
        _frameStatistics.Reset(Stopwatch.GetTimestamp());
        if (ShowFrameStatistics && !_disposed) RefreshFrameStatistics();
    }

    private void UpdateFrameStatisticsVisibility()
    {
        _frameStatisticsEnabled = false;
        _frameStatisticsTimer.Stop();
        _frameStatistics.Reset(Stopwatch.GetTimestamp());
        bool visible = ShowFrameStatistics && !_disposed;
        _frameStatisticsOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        RefreshFrameStatistics();
        if (!IsLoaded) return;
        _frameStatisticsEnabled = true;
        _frameStatisticsTimer.Start();
    }

    private void RefreshFrameStatistics()
    {
        var sample = _frameStatistics.Snapshot(Stopwatch.GetTimestamp());
        _frameStatisticsText.Text = string.Format(Properties.Resources.ConsoleWindow_FrameStatisticsFormat,
            sample.PresentedFps, sample.ReceivedFps, sample.MaxGapMilliseconds, sample.LongGapCount);
    }

    private void UpdateDesktopPresentation()
    {
        if (_remoteWidth <= 0 || _remoteHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        bool fit = _manageDesktopPresentation || _smartSizing;
        var size = GetPresentationSize(ActualWidth, ActualHeight, _remoteWidth, _remoteHeight, dpi, fit, _zoom);
        _image.Width = size.Width; _image.Height = size.Height;
        bool scroll = !fit && _zoom > 100;
        _scroll.HorizontalScrollBarVisibility = scroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = scroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        bool fits = _remoteWidth / dpi <= ActualWidth && _remoteHeight / dpi <= ActualHeight;
        _scroll.VerticalContentAlignment = _manageDesktopPresentation && _topAlignedWhenFits && fits
            ? VerticalAlignment.Top : VerticalAlignment.Center;
        RenderOptions.SetBitmapScalingMode(_image, _preferNativePixels && fits && fit
            ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    internal static System.Windows.Size GetPresentationSize(double availableWidth, double availableHeight,
        int pixelWidth, int pixelHeight, double dpiScale, bool fit, uint zoom)
    {
        if (!double.IsFinite(availableWidth) || !double.IsFinite(availableHeight)
            || availableWidth <= 0 || availableHeight <= 0 || pixelWidth <= 0 || pixelHeight <= 0
            || !double.IsFinite(dpiScale) || dpiScale <= 0) return new System.Windows.Size(0, 0);
        double scale = fit ? Math.Min(1, Math.Min(availableWidth * dpiScale / pixelWidth, availableHeight * dpiScale / pixelHeight))
            : Math.Clamp(zoom, 1u, 500u) / 100.0;
        return new System.Windows.Size(pixelWidth * scale / dpiScale, pixelHeight * scale / dpiScale);
    }

    internal static bool TryMapPointer(Point position, double displayWidth, double displayHeight, int pixelWidth, int pixelHeight,
        bool captured, out ushort x, out ushort y)
    {
        x = y = 0;
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y) || !double.IsFinite(displayWidth) || !double.IsFinite(displayHeight)
            || displayWidth <= 0 || displayHeight <= 0
            || pixelWidth <= 0 || pixelHeight <= 0 || pixelWidth > ushort.MaxValue || pixelHeight > ushort.MaxValue) return false;
        if (!captured && (position.X < 0 || position.Y < 0 || position.X >= displayWidth || position.Y >= displayHeight)) return false;
        x = (ushort)Math.Clamp(Math.Floor(position.X * pixelWidth / displayWidth), 0, pixelWidth - 1);
        y = (ushort)Math.Clamp(Math.Floor(position.Y * pixelHeight / displayHeight), 0, pixelHeight - 1);
        return true;
    }

    private bool UpdatePointer(MouseEventArgs e)
    {
        if (_state != 1 || _connection?.Input is null || !TryMapPointer(e.GetPosition(_image), _image.ActualWidth, _image.ActualHeight,
            _remoteWidth, _remoteHeight, _image.IsMouseCaptured, out var x, out var y)) return false;
        _pointerX = x; _pointerY = y; return true;
    }

    private static ConsolePointerButton MapButton(MouseButton button) => button switch
    {
        MouseButton.Left => ConsolePointerButton.Left, MouseButton.Right => ConsolePointerButton.Right,
        MouseButton.Middle => ConsolePointerButton.Middle, _ => ConsolePointerButton.None
    };

    private void OnImageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_state == 1 && _connection?.Input is null)
        {
            Focus(); Keyboard.Focus(this); UpdateInputFocus();
            return;
        }
        if (!UpdatePointer(e)) return;
        Focus(); Keyboard.Focus(this); UpdateInputFocus();
        var button = MapButton(e.ChangedButton);
        if (button == ConsolePointerButton.None) return;
        if (_connection?.Input?.SendPointer(_pointerX, _pointerY, button, true) == true)
        {
            _pressedButtons |= button; _image.CaptureMouse();
        }
        e.Handled = true;
    }

    private void OnImageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!UpdatePointer(e)) return;
        var button = MapButton(e.ChangedButton);
        if (button == ConsolePointerButton.None) return;
        if (_connection?.Input?.SendPointer(_pointerX, _pointerY, button, false) != true) QueueInputFailure();
        _pressedButtons &= ~button;
        if (_pressedButtons == ConsolePointerButton.None) _image.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnImageMouseMove(object sender, MouseEventArgs e)
    {
        if (UpdatePointer(e)) _connection?.Input?.SendPointer(_pointerX, _pointerY);
    }

    private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!UpdatePointer(e)) return;
        foreach (short delta in SplitWheelDelta(e.Delta)) _connection?.Input?.SendPointer(_pointerX, _pointerY, wheel: delta);
        e.Handled = true;
    }

    internal static IEnumerable<short> SplitWheelDelta(int delta)
    {
        while (delta != 0)
        {
            short part = (short)Math.Clamp(delta, -255, 255);
            yield return part;
            delta -= part;
        }
    }

    private void ReleasePointerButtons()
    {
        foreach (var button in new[] { ConsolePointerButton.Left, ConsolePointerButton.Right, ConsolePointerButton.Middle })
            if ((_pressedButtons & button) != 0 && _connection?.Input?.SendPointer(_pointerX, _pointerY, button, false) != true) QueueInputFailure();
        _pressedButtons = ConsolePointerButton.None;
        if (_image.IsMouseCaptured) _image.ReleaseMouseCapture();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        UpdateFrameStatisticsVisibility();
        DetachWindow();
        _owner = Window.GetWindow(this);
        if (_owner != null)
        {
            _owner.Activated += OnOwnerActivationChanged; _owner.Deactivated += OnOwnerActivationChanged;
            _windowSource = PresentationSource.FromVisual(_owner) as HwndSource;
            if (_windowSource != null)
            {
                _windowSource.AddHook(ClipboardWindowHook); AddClipboardFormatListener(_windowSource.Handle);
            }
        }
        UpdateInputFocus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _frameStatisticsEnabled = false;
        _frameStatisticsTimer.Stop();
        ReleaseInput(); DetachWindow();
    }
    private void OnOwnerActivationChanged(object? sender, EventArgs e) => UpdateInputFocus();

    private void DetachWindow()
    {
        if (_owner != null) { _owner.Activated -= OnOwnerActivationChanged; _owner.Deactivated -= OnOwnerActivationChanged; }
        if (_windowSource != null)
        {
            RemoveClipboardFormatListener(_windowSource.Handle); _windowSource.RemoveHook(ClipboardWindowHook);
        }
        _windowSource = null; _owner = null;
    }

    private void UpdateInputFocus()
    {
        bool active = HasConsoleFocus && _connection?.Input is not null;
        if (!active)
        {
            ReleaseInput();
            if (HasConsoleFocus) PublishLocalClipboard(force: true);
            return;
        }
        if (_inputActive) return;
        _inputActive = true;
        _keyboardHook = SetWindowsHookEx(13, _keyboardCallback, GetModuleHandle(null), 0);
        if (_keyboardHook == IntPtr.Zero) Debug.WriteLine($"[Console] 系统键捕获不可用：{Marshal.GetLastWin32Error()}");
        var toggles = MapKeyboardToggles(GetKeyState(0x14), GetKeyState(0x90), GetKeyState(0x91), GetKeyState(0x15));
        if (_connection?.Input?.SynchronizeKeyboard(toggles) != true) QueueInputFailure();
        PublishLocalClipboard(force: true);
    }

    internal static ConsoleKeyboardToggleState MapKeyboardToggles(short capsLock, short numLock, short scrollLock, short kanaLock) =>
        ((capsLock & 1) != 0 ? ConsoleKeyboardToggleState.CapsLock : ConsoleKeyboardToggleState.None)
        | ((numLock & 1) != 0 ? ConsoleKeyboardToggleState.NumLock : ConsoleKeyboardToggleState.None)
        | ((scrollLock & 1) != 0 ? ConsoleKeyboardToggleState.ScrollLock : ConsoleKeyboardToggleState.None)
        | ((kanaLock & 1) != 0 ? ConsoleKeyboardToggleState.KanaLock : ConsoleKeyboardToggleState.None);

    private void ReleaseInput()
    {
        _inputActive = false; _clipboardRetry?.Stop();
        if (_keyboardHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = IntPtr.Zero; }
        ReleaseKeys(); ReleasePointerButtons(); _physicalKeys.Clear(); _shortcutDown = false; _pauseDown = false;
    }

    private void ReleaseKeys()
    {
        foreach (var key in _pressedKeys)
            if (_connection?.Input?.SendKey(key.Scan, key.Extended, false) != true) QueueInputFailure();
        _pressedKeys.Clear();
    }

    private void QueueInputFailure()
    {
        if (_inputFailureQueued || _disposed || _state != 1) return;
        _inputFailureQueued = true;
        int generation = _generation;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _inputFailureQueued = false;
            if (!_disposed && generation == _generation) Disconnect();
        }));
    }

    private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _inputActive && _state == 1)
        {
            int message = wParam.ToInt32();
            if (message is 0x100 or 0x104 or 0x101 or 0x105)
            {
                try
                {
                    var key = Marshal.PtrToStructure<KeyboardData>(lParam);
                    HandleKey((int)key.VirtualKey, (ushort)key.ScanCode, (key.Flags & 1) != 0, message is 0x100 or 0x104);
                }
                catch (Exception ex)
                {
                    // 托管异常不能穿越 Win32 钩子回调；断开后按原窗口轮询重连。
                    Debug.WriteLine($"[Console] 键盘输入失败：{ex}");
                    QueueInputFailure();
                }
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void HandleKey(int virtualKey, ushort scan, bool extended, bool down)
    {
        // Pause 使用包含释放的 E1 序列，不能把它的 0x45 当作 NumLock 发送。
        // Ctrl+Break 的 VK_CANCEL 仍走下面的普通扫描码路径。
        if (virtualKey == 0x13)
        {
            if (down && !_pauseDown && _connection?.Input?.SendPause() != true) QueueInputFailure();
            _pauseDown = down;
            return;
        }
        var key = (scan, extended);
        if (down) _physicalKeys.Add(key); else _physicalKeys.Remove(key);
        // 被低级钩子拦下的修饰键不会更新 Windows 异步键状态，必须用本控件实际收到的按键判定。
        if (virtualKey == _fullScreenHotKey && (_shortcutDown || IsFullScreenChord(down,
            _physicalKeys.Any(k => k.Scan == 0x1D), _physicalKeys.Any(k => k.Scan == 0x38))))
        {
            if (down && !_shortcutDown)
            {
                _shortcutDown = true; ReleaseKeys(); _fullScreen = !_fullScreen; FullScreenRequested?.Invoke(_fullScreen);
            }
            if (!down) _shortcutDown = false;
            return;
        }
        if (scan == 0) return;
        if (down) { if (_connection?.Input?.SendKey(scan, extended, true) == true) _pressedKeys.Add(key); }
        else if (_pressedKeys.Remove(key) && _connection?.Input?.SendKey(scan, extended, false) != true) QueueInputFailure();
    }

    internal static bool IsFullScreenChord(bool down, bool controlDown, bool altDown) => down && controlDown && altDown;

    private void OnFallbackKeyDown(object sender, KeyEventArgs e) => ForwardFallbackKey(e, true);
    private void OnFallbackKeyUp(object sender, KeyEventArgs e) => ForwardFallbackKey(e, false);
    private void ForwardFallbackKey(KeyEventArgs e, bool down)
    {
        if (!_inputActive || _keyboardHook != IntPtr.Zero) return;
        int vk = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        uint mapped = MapVirtualKey((uint)vk, 4);
        HandleKey(vk, (ushort)(mapped & 0xFF), (mapped & 0xFF00) == 0xE000, down);
        e.Handled = true;
    }

    private bool HasConsoleFocus => !_disposed && _state == 1 && IsLoaded && IsKeyboardFocusWithin && _owner?.IsActive == true;
    private bool CanSyncClipboard => HasConsoleFocus && _clipboardAvailable && _connection?.Clipboard?.IsAvailable == true;
    private IntPtr ClipboardWindowHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x031D && CanSyncClipboard) PublishLocalClipboard();
        return IntPtr.Zero;
    }

    private void PublishLocalClipboard(bool force = false)
    {
        if (!CanSyncClipboard) return;
        if (!force && GetClipboardSequenceNumber() == _clipboardSequence) return;
        RetryClipboard(() =>
        {
            if (!TryReadClipboard(out var data, out uint sequence)) return false;
            if (_connection?.Clipboard?.SetClipboard(data!) != true) return false;
            Volatile.Write(ref _clipboardSequence, sequence); return true;
        });
    }

    private void ReceiveClipboard(ConsoleClipboardData data, uint expectedSequence)
    {
        if (!CanSyncClipboard || !IsClipboardVersionCurrent(expectedSequence, GetClipboardSequenceNumber())) return;
        RetryClipboard(() =>
        {
            // 首次执行和每次重试均沿用 Core 回调时的版本，不覆盖随后复制的本地内容。
            if (!IsClipboardVersionCurrent(expectedSequence, GetClipboardSequenceNumber())) return true;
            if (!TryWriteClipboard(data, expectedSequence, out uint? writtenSequence)) return false;
            if (writtenSequence.HasValue) Volatile.Write(ref _clipboardSequence, writtenSequence.Value);
            return true;
        });
    }

    internal static bool IsClipboardVersionCurrent(uint expectedSequence, uint currentSequence) => expectedSequence == currentSequence;

    private void RetryClipboard(Func<bool> attempt)
    {
        _clipboardRetry?.Stop();
        if (attempt()) return;
        int generation = _generation, remaining = 5;
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(60) };
        _clipboardRetry = timer;
        timer.Tick += (_, _) =>
        {
            if (generation != _generation || !CanSyncClipboard || --remaining <= 0 || attempt()) timer.Stop();
        };
        timer.Start();
    }

    private bool TryReadClipboard(out ConsoleClipboardData? data, out uint sequence)
    {
        data = null;
        sequence = 0;
        if (!OpenClipboard(_windowSource?.Handle ?? IntPtr.Zero)) return false;
        try
        {
            var formats = new List<ConsoleClipboardFormat>();
            foreach (uint id in new[] { UnicodeTextFormat, DibFormat, HtmlClipboardFormat })
            {
                if (id == 0 || !IsClipboardFormatAvailable(id)) continue;
                IntPtr handle = GetClipboardData(id);
                ulong length = GlobalSize(handle).ToUInt64();
                if (handle == IntPtr.Zero || length == 0 || length > MaxClipboardFormatBytes) continue;
                IntPtr memory = GlobalLock(handle);
                if (memory == IntPtr.Zero) continue;
                try
                {
                    var bytes = new byte[(int)length]; Marshal.Copy(memory, bytes, 0, bytes.Length);
                    if (id == UnicodeTextFormat) bytes = NormalizeUnicodeClipboard(bytes);
                    formats.Add(new ConsoleClipboardFormat(id, id == HtmlClipboardFormat ? "HTML Format" : null, bytes));
                }
                finally { GlobalUnlock(handle); }
            }
            data = new ConsoleClipboardData(formats); sequence = GetClipboardSequenceNumber(); return true;
        }
        finally { CloseClipboard(); }
    }

    internal static byte[] NormalizeUnicodeClipboard(byte[] bytes)
    {
        // GlobalSize 包含分配器尾部空间，CF_UNICODETEXT 仅到首个 UTF-16 NUL 为止。
        for (int i = 0; i + 1 < bytes.Length; i += 2)
            if (bytes[i] == 0 && bytes[i + 1] == 0) return bytes.AsSpan(0, i + 2).ToArray();
        Array.Resize(ref bytes, bytes.Length + 2 + bytes.Length % 2);
        return bytes;
    }

    private bool TryWriteClipboard(ConsoleClipboardData data, uint expectedSequence, out uint? writtenSequence)
    {
        writtenSequence = null;
        var formats = data.Formats.Where(format => format.Data.Length > 0 && format.Data.Length <= MaxClipboardFormatBytes
            && (format.Id is UnicodeTextFormat or DibFormat || string.Equals(format.Name, "HTML Format", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (formats.Length == 0) return true;
        if (!OpenClipboard(_windowSource?.Handle ?? IntPtr.Zero)) return false;
        try
        {
            // OpenClipboard 前仍可能有其他程序复制；获得锁后再核对一次。
            if (!IsClipboardVersionCurrent(expectedSequence, GetClipboardSequenceNumber())) return true;
            if (!EmptyClipboard()) return false;
            foreach (var format in formats)
            {
                uint id = string.Equals(format.Name, "HTML Format", StringComparison.OrdinalIgnoreCase) ? HtmlClipboardFormat : format.Id;
                if (id == 0) continue;
                byte[] bytes = format.Data.ToArray();
                if (id == UnicodeTextFormat && (bytes.Length < 2 || bytes.Length % 2 != 0 || bytes[^1] != 0 || bytes[^2] != 0))
                    Array.Resize(ref bytes, bytes.Length + 2 + bytes.Length % 2);
                IntPtr handle = GlobalAlloc(0x0002, (UIntPtr)bytes.Length);
                if (handle == IntPtr.Zero) continue;
                IntPtr memory = GlobalLock(handle);
                if (memory == IntPtr.Zero) { GlobalFree(handle); continue; }
                try { Marshal.Copy(bytes, 0, memory, bytes.Length); }
                finally { GlobalUnlock(handle); }
                // 成功后内存归 Windows；失败仍由本适配器释放。
                if (SetClipboardData(id, handle) == IntPtr.Zero) GlobalFree(handle);
            }
            writtenSequence = GetClipboardSequenceNumber();
            return true;
        }
        finally { CloseClipboard(); }
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint VirtualKey, ScanCode, Flags, Time; public UIntPtr ExtraInfo; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelKeyboardProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr handle);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);
}
