using System.Runtime.InteropServices.WindowsRuntime;
using ExHyperV.App.Diagnostics;
using ExHyperV.App.Localization;
using WinRT;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.App.UI.Shared.ConsoleHost;
using ExHyperV.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

public sealed partial class ConsoleWindow
{
    private ConsoleSwapChainRenderer? gpuRenderer;
    private WriteableBitmap? bitmap;
    private Stream? pixelStream;
    private long sequence;
    private long bitmapSequence;
    private uint? capturedPointer;
    private bool keyboardCaptured;
    private IConsoleInput? Input => active && view == View.Connected && sessionState.CanInput && Granted(ConsoleCapabilities.Input) ? connection?.Input : null;

    private ConsoleSwapChainRenderer CreateRenderer() => new(BindSwapChain,
        () => (GpuScreen.CompositionScaleX, GpuScreen.CompositionScaleY),
        (name, detail) => AppDiagnostics.Write("Console", name, detail));

    private unsafe int BindSwapChain(nint chain)
    {
        Guid iid = new("63aad0b8-7c24-40ff-85a8-640d944cc325");
        int hr = ((IWinRTObject)GpuScreen).NativeObject.TryAs(iid, out nint native);
        if (hr < 0) return hr;
        try { return ((delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)native)[3])(native, chain); }
        finally { System.Runtime.InteropServices.Marshal.Release(native); }
    }

    private bool CanDisplay => !closing && view == View.Connected && Granted(ConsoleCapabilities.Video);

    private void SetFrameSize(int width, int height)
    {
        if (liveSize == (width, height)) return;
        liveSize = (width, height);
        if ((IsEnhanced || zoom == 0) && !manualWindowSize) FitWindowToAspect(width, height);
        SizeSurface();
        Render();
    }

    private void ShowFrame(ConsoleFrame frame, ConsoleFrameDamage damage)
    {
        if (!CanDisplay) return;
        if (liveSize != (frame.Width, frame.Height) || sequence != damage.BaseSequence ||
            sessionState.Transport == ConsoleTransportKind.GpuShared) damage = ConsoleFrameDamage.FullFrame;
        SetFrameSize(frame.Width, frame.Height);
        if (sessionState.ActiveBackend == ConsoleExtensionCatalog.External) SetPath(ConsoleTransportKind.CpuShared);
        gpuRenderer ??= CreateRenderer();
        if (!gpuRenderer.IsDisabled && gpuRenderer.TryPresent(frame, damage, Surface.Width, Surface.Height))
            UseSwapChain();
        else ShowBitmapFallback(frame, damage);
        sequence = frame.Sequence;
    }

    private bool ShowGpuFrame(ConsoleGpuFrame frame)
    {
        if (!CanDisplay) return true;
        SetFrameSize(frame.Width, frame.Height);
        gpuRenderer ??= CreateRenderer();
        if (!gpuRenderer.TryPresent(frame, Surface.Width, Surface.Height)) return false;
        UseSwapChain();
        sequence = 0;
        SetPath(ConsoleTransportKind.GpuShared);
        return true;
    }

    private void UseSwapChain()
    {
        GpuScreen.Visibility = Visibility.Visible;
        Screen.Visibility = Visibility.Collapsed;
        Screen.Source = null;
        pixelStream?.Dispose();
        pixelStream = null;
        bitmap = null;
        bitmapSequence = 0;
    }

    private void SetPath(ConsoleTransportKind path)
    {
        if (sessionState.Transport == path) return;
        sessionState.SetTransport(path);
        Render();
    }

    private void OnCpuRequested()
    {
        gpuRenderer?.Clear();
        sequence = 0;
        SetPath(ConsoleTransportKind.CpuShared);
    }

    private void ShowBitmapFallback(ConsoleFrame frame, ConsoleFrameDamage damage)
    {
        GpuScreen.Visibility = Visibility.Collapsed;
        Screen.Visibility = Visibility.Visible;

        bool newBitmap = bitmap == null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height;
        if (newBitmap)
        {
            pixelStream?.Dispose();
            bitmap = new WriteableBitmap(frame.Width, frame.Height);
            pixelStream = bitmap.PixelBuffer.AsStream();
        }

        ConsoleFrameRegion[] dirty = newBitmap || damage.Full || bitmapSequence != damage.BaseSequence
            ? [new(0, 0, frame.Width, frame.Height)] : [.. damage.Dirty, .. damage.Moves.Select(move => move.Destination)];
        foreach (var region in dirty)
        {
            var dest = pixelStream!;
            int pitch = frame.Width * 4, count = region.Width * 4;
            if (pitch == frame.Stride && count == pitch)
            {
                dest.Position = region.Y * pitch;
                dest.Write(frame.Pixels.Slice(region.Y * pitch, region.Height * pitch));
            }
            else
            {
                for (int y = region.Y; y < region.Y + region.Height; y++)
                {
                    dest.Position = y * pitch + region.X * 4;
                    dest.Write(frame.Pixels.Slice(y * frame.Stride + region.X * 4, count));
                }
            }
        }
        if (dirty.Length != 0) bitmap!.Invalidate();

        if (!ReferenceEquals(Screen.Source, bitmap)) Screen.Source = bitmap;
        bitmapSequence = frame.Sequence;
    }

    private void ClearFrame()
    {
        GpuScreen.Visibility = Visibility.Collapsed;
        Screen.Visibility = Visibility.Collapsed;
        Screen.Source = null;
        gpuRenderer?.Clear();
        pixelStream?.Dispose();
        pixelStream = null;
        bitmap = null;
        bitmapSequence = 0;
        liveSize = null;
        sequence = 0;
    }

    private void DisposeDisplay()
    {
        ClearFrame();
        gpuRenderer?.Dispose();
        gpuRenderer = null;
    }

    private void OnGpuScreenScaleChanged(SwapChainPanel sender, object args)
    {
        if (!closing) SizeSurface();
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        SizeSurface();
        if (verifyFit && Viewport.ActualWidth > 0 && Viewport.ActualHeight > 0)
        {
            verifyFit = false;
            FitWindowToAspect();
        }
    }

    private void SizeSurface()
    {
        if (liveSize is not { } size || Viewport.ActualWidth <= 0 || Viewport.ActualHeight <= 0) return;
        bool fit = IsEnhanced || zoom == 0;
        var bars = fit ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        Scroll.HorizontalScrollBarVisibility = Scroll.VerticalScrollBarVisibility = bars;
        double factor = fit
            ? Math.Min(Viewport.ActualWidth / size.Width, Viewport.ActualHeight / size.Height)
            : zoom / (100 * (Root.XamlRoot?.RasterizationScale ?? 1));
        double width = size.Width * factor;
        double height = size.Height * factor;
        if (fit && Math.Abs(width - Viewport.ActualWidth) <= 1.5 && Math.Abs(height - Viewport.ActualHeight) <= 1.5)
        {
            width = Viewport.ActualWidth;
            height = Viewport.ActualHeight;
        }
        Surface.Width = width;
        Surface.Height = height;
        CursorClip.Rect = new(0, 0, width, height);
        if (gpuRenderer?.HasSurface(size.Width, size.Height) == true && !gpuRenderer.UpdateDisplaySize(width, height) &&
            sessionState.Transport == ConsoleTransportKind.GpuShared)
            frames?.RequestCpu();
        UpdateCursor();
    }

    private ConsoleCursor? cursorShape;
    private ConsoleCursorState? cursorState;
    private bool hasNativeShape, receivedShape;

    private void UpdateCursorShape()
    {
        hasNativeShape = false;
        RemoteCursor.Source = null;
        if (cursorShape is { } shape && ValidShape(shape) && cursorState != null)
        {
            var image = new WriteableBitmap(shape.Width, shape.Height);
            using var pixels = image.PixelBuffer.AsStream();
            // WriteableBitmap uses premultiplied BGRA.
            var data = shape.Pixels.ToArray();
            for (int i = 0; i < data.Length; i += 4)
                for (int c = 0; c < 3; c++) data[i + c] = (byte)((data[i + c] * data[i + 3] + 127) / 255);
            pixels.Write(data);
            image.Invalidate();
            RemoteCursor.Source = image;
        }
        UpdateCursor();
    }

    private static bool ValidShape(ConsoleCursor s) => s.Width is > 0 and <= 384 && s.Height is > 0 and <= 384 &&
        s.HotX >= 0 && s.HotX < s.Width && s.HotY >= 0 && s.HotY < s.Height && s.Pixels.Length == s.Width * s.Height * 4;

    private void UpdateCursor()
    {
        RemoteCursor.Visibility = Visibility.Collapsed;
        bool interactive = Input != null;
        if (view != View.Connected) { Surface.ResetCursor(); hasNativeShape = false; return; }
        if (cursorState is { } state)
        {
            Surface.SetBlankCursor(interactive && (state.Composited || !state.Visible || (cursorShape is { } s && ValidShape(s))));
            hasNativeShape = false;
            if (state.Composited || !state.Visible || state.CoordinateSpace != ConsoleCoordinateSpace.DesktopPixels ||
                liveSize is not { } size || cursorShape is not { } shape || !ValidShape(shape)) return;
            if (RemoteCursor.Source == null) { UpdateCursorShape(); return; }
            double sx = Surface.Width / size.Width, sy = Surface.Height / size.Height;
            RemoteCursor.Width = shape.Width * sx;
            RemoteCursor.Height = shape.Height * sy;
            Canvas.SetLeft(RemoteCursor, (state.X - (double)shape.HotX) * sx);
            Canvas.SetTop(RemoteCursor, (state.Y - (double)shape.HotY) * sy);
            RemoteCursor.Visibility = Visibility.Visible;
        }
        else if (interactive && cursorShape is { } legacy && ValidShape(legacy))
        {
            if (!hasNativeShape) Surface.SetRemoteCursor(legacy.Width, legacy.Height, legacy.HotX, legacy.HotY, legacy.Pixels);
            hasNativeShape = true;
        }
        else { Surface.SetBlankCursor(interactive && receivedShape); hasNativeShape = false; }
    }

    private async Task<ConsoleFrame?> CaptureFrameAsync(IConsoleConnection? source)
    {
        if (!CanDisplay) return null;
        if (sessionState.Transport != ConsoleTransportKind.GpuShared) return source?.Video?.CaptureFrame();
        using var readback = gpuRenderer?.BeginReadback();
        for (int i = 0; readback != null && i < 100 && source == connection && CanDisplay; i++)
        {
            if (readback.Read() is { } frame) return frame;
            await Task.Delay(10);
        }
        return null;
    }

    private async void OnScreenshot(object sender, RoutedEventArgs e) => await UiEvent.RunAsync(async () =>
    {
        var source = connection;
        int width, height;
        byte[] pixels;
        using (var frame = await CaptureFrameAsync(source))
        {
            if (frame == null) { Notify(L10n.Get("Console_NoFrame")); return; }
            width = frame.Width;
            height = frame.Height;
            if (frame.Stride == width * 4) pixels = frame.CopyPixels();
            else
            {
                pixels = new byte[width * height * 4];
                for (int y = 0; y < height; y++) frame.Pixels.Slice(y * frame.Stride, width * 4).CopyTo(pixels.AsSpan(y * width * 4));
            }
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        if (!CanDisplay || source != connection) return;
        stream.Seek(0);
        var data = new DataPackage();
        data.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        Clipboard.SetContent(data);
        Clipboard.Flush();
        Notify(L10n.Get("Console_Copied"));
    }, Notify);

    private void OnSas(object sender, RoutedEventArgs e)
    {
        if (Input?.SendKeys((ConsoleKeyEvent[])[new(0x1D, false, true), new(0x38, false, true), new(0x53, true, true),
            new(0x53, true, false), new(0x38, false, false), new(0x1D, false, false)]) != true)
            Notify(L10n.Get("Console_InputRejected"));
    }

    private void OnSurfaceFocus(object sender, RoutedEventArgs e) { keyboardCaptured = Input != null; SyncLocks(); }
    private void OnSurfaceBlur(object sender, RoutedEventArgs e) => ReleaseInput();

    private void SyncLocks()
    {
        var flags = ConsoleKeyboardToggleState.None;
        void Add(VirtualKey key, ConsoleKeyboardToggleState flag)
        {
            if ((InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Locked) != 0) flags |= flag;
        }
        Add(VirtualKey.CapitalLock, ConsoleKeyboardToggleState.CapsLock);
        Add(VirtualKey.NumberKeyLock, ConsoleKeyboardToggleState.NumLock);
        Add(VirtualKey.Scroll, ConsoleKeyboardToggleState.ScrollLock);
        Add(VirtualKey.Kana, ConsoleKeyboardToggleState.KanaLock);
        Input?.SynchronizeKeyboard(flags);
    }

    private void StopInput()
    {
        bool currentOwner = Granted(ConsoleCapabilities.Input);
        if (sessionState.StopInputs() && currentOwner) connection?.Input?.ReleaseAllInputs();
        ForgetInput();
    }

    private void ReleaseInput()
    {
        if ((keyboardCaptured || capturedPointer != null) && sessionState.CanInput && Granted(ConsoleCapabilities.Input))
            connection?.Input?.ReleaseAllInputs();
        ForgetInput();
    }

    private void ForgetInput()
    {
        keyboardCaptured = false;
        capturedPointer = null;
        Surface.ReleasePointerCaptures();
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        if (keyboardCaptured)
        {
            ReleaseInput();
            if (fullscreen) { Capsule.Visibility = Visibility.Visible; Pin.Focus(FocusState.Programmatic); }
            else Switcher.Focus(FocusState.Programmatic);
        }
        else if (fullscreen) SetFullscreen(false);
        else return;
        e.Handled = true;
    }

    private void OnGuestKeyDown(object sender, KeyRoutedEventArgs e) => GuestKey(e, true);
    private void OnGuestKeyUp(object sender, KeyRoutedEventArgs e) => GuestKey(e, false);

    private void GuestKey(KeyRoutedEventArgs e, bool down)
    {
        if (!keyboardCaptured || Input is not { } input) return;
        var status = e.KeyStatus;
        bool accepted = e.OriginalKey == VirtualKey.Pause ? !down || input.SendPause()
            : input.SendKey((ushort)status.ScanCode, status.IsExtendedKey, down);
        if (!accepted && !down) Input?.ReleaseAllInputs();
        e.Handled = true;
    }

    private bool PointOnScreen(PointerRoutedEventArgs e, out ushort x, out ushort y)
    {
        x = y = 0;
        if (liveSize is not { } size || Input == null || Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0) return false;
        var p = e.GetCurrentPoint(Surface).Position;
        if (capturedPointer != e.Pointer.PointerId &&
            (p.X < 0 || p.Y < 0 || p.X >= Surface.ActualWidth || p.Y >= Surface.ActualHeight)) return false;
        x = (ushort)Math.Clamp(p.X * size.Width / Surface.ActualWidth, 0, size.Width - 1);
        y = (ushort)Math.Clamp(p.Y * size.Height / Surface.ActualHeight, 0, size.Height - 1);
        return true;
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (PointOnScreen(e, out var x, out var y)) Input?.SendPointer(x, y);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // WinUI reports additional button transitions during a chord as movements.
        var kind = e.GetCurrentPoint(Surface).Properties.PointerUpdateKind;
        if (PointerButton(kind) != ConsolePointerButton.None)
        {
            PointerButtonChanged(e, kind is PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.RightButtonPressed);
            return;
        }
        if (PointOnScreen(e, out var x, out var y)) Input?.SendPointer(x, y);
    }

    private static ConsolePointerButton PointerButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => ConsolePointerButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => ConsolePointerButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => ConsolePointerButton.Middle,
        _ => ConsolePointerButton.None
    };

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e) => PointerButtonChanged(e, true);
    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) => PointerButtonChanged(e, false);

    private void PointerButtonChanged(PointerRoutedEventArgs e, bool down)
    {
        if (!PointOnScreen(e, out var x, out var y) || Input is not { } input) return;
        var properties = e.GetCurrentPoint(Surface).Properties;
        var button = PointerButton(properties.PointerUpdateKind);
        if (button == ConsolePointerButton.None) return;
        if (down)
        {
            Surface.Focus(FocusState.Programmatic);
            if (!keyboardCaptured) { keyboardCaptured = true; SyncLocks(); }
            if (Surface.CapturePointer(e.Pointer)) capturedPointer = e.Pointer.PointerId;
        }
        if (!input.SendPointer(x, y, button, down) && !down) Input?.ReleaseAllInputs();
        if (!down && !properties.IsLeftButtonPressed && !properties.IsRightButtonPressed && !properties.IsMiddleButtonPressed)
        {
            capturedPointer = null;
            Surface.ReleasePointerCapture(e.Pointer);
        }
        e.Handled = true;
    }

    private void OnCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (capturedPointer == e.Pointer.PointerId) { capturedPointer = null; Input?.ReleaseAllInputs(); }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(Surface).Properties;
        if (properties.IsHorizontalMouseWheel || !PointOnScreen(e, out var x, out var y)) return;
        int delta = properties.MouseWheelDelta;
        while (delta != 0)
        {
            short step = (short)Math.Clamp(delta, -120, 120);
            if (Input?.SendPointer(x, y, wheel: step) != true) break;
            delta -= step;
        }
        e.Handled = true;
    }
}
