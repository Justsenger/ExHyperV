using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ExHyperV.App.Diagnostics;
using ExHyperV.App.Localization;

namespace ExHyperV.App.UI.Shell;

// The icon exists only while the window is hidden. Explorer can recreate the taskbar at any time.
internal sealed unsafe partial class WindowTrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 71;
    private readonly nint window;
    private readonly Action restore;
    private readonly Action exit;
    private readonly Action? syncTheme;
    private readonly uint taskbarCreated;
    private GCHandle handle;
    private IconData data;

    internal WindowTrayIcon(nint window, Action restore, Action exit, Action? syncTheme = null)
    {
        this.window = window;
        this.restore = restore;
        this.exit = exit;
        this.syncTheme = syncTheme;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        data.Size = (uint)sizeof(IconData);
        data.Window = window;
        data.Id = 1;
        data.Flags = 1 | 2 | 4 | 0x80; // message, icon, tip, show standard tooltip
        data.Message = CallbackMessage;
        data.Icon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"), 1, 0, 0, 0x10 | 0x40);
        fixed (char* tip = data.Tip) "ExHyperV".AsSpan().CopyTo(new Span<char>(tip, 128));
    }

    internal bool Show()
    {
        if (data.Icon == 0 || taskbarCreated == 0) return false;
        handle = GCHandle.Alloc(this);
        if (SetWindowSubclass(window, &WindowProc, 1, GCHandle.ToIntPtr(handle)) == 0) return false;
        ChangeWindowMessageFilterEx(window, CallbackMessage, 1, 0);
        ChangeWindowMessageFilterEx(window, taskbarCreated, 1, 0);
        return AddIcon();
    }

    private bool AddIcon()
    {
        if (ShellNotifyIcon(0, ref data) == 0) return false;
        data.Version = 4;
        ShellNotifyIcon(4, ref data);
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nint reference)
    {
        var tray = (WindowTrayIcon)GCHandle.FromIntPtr(reference).Target!;
        try
        {
            if (message == tray.taskbarCreated && !tray.AddIcon()) tray.restore();
            if (message == CallbackMessage)
            {
                switch ((uint)lParam & 0xffff)
                {
                    case 0x400: case 0x401: case 0x202: // NIN_SELECT, NIN_KEYSELECT, legacy left button up
                        tray.restore();
                        break;
                    case 0x7b: case 0x205: // context menu, legacy right button up
                        tray.ShowMenu();
                        break;
                }
                return 0;
            }
        }
        catch (Exception error) { AppDiagnostics.Failure("Tray", "CallbackFailed", error); }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        syncTheme?.Invoke();
        nint menu = CreatePopupMenu();
        if (menu == 0) { restore(); return; }
        try
        {
            AppendMenu(menu, 0, 1, L10n.Get("Settings_ShowWindow"));
            AppendMenu(menu, 0, 2, L10n.Get("Settings_Exit"));
            GetCursorPos(out var point);
            SetForegroundWindow(window);
            uint command = TrackPopupMenu(menu, 0x100 | 2, point.X, point.Y, 0, window, 0);
            PostMessage(window, 0, 0, 0);
            if (command == 1) restore();
            if (command == 2) exit();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (handle.IsAllocated)
        {
            ShellNotifyIcon(2, ref data);
            RemoveWindowSubclass(window, &WindowProc, 1);
            handle.Free();
        }
        if (data.Icon != 0) { DestroyIcon(data.Icon); data.Icon = 0; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconData
    {
        internal uint Size;
        internal nint Window;
        internal uint Id, Flags, Message;
        internal nint Icon;
        internal fixed char Tip[128];
        internal uint State, StateMask;
        internal fixed char Info[256];
        internal uint Version;
        internal fixed char InfoTitle[64];
        internal uint InfoFlags;
        internal Guid Guid;
        internal nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { internal int X, Y; }

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    private static partial int ShellNotifyIcon(uint command, ref IconData data);
    [LibraryImport("comctl32.dll")]
    private static partial int SetWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nint, nint> proc, nuint id, nint reference);
    [LibraryImport("comctl32.dll")]
    private static partial int RemoveWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nuint, nint, nint> proc, nuint id);
    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessage(string message);
    [LibraryImport("user32.dll")]
    private static partial int ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint change);
    [LibraryImport("user32.dll", EntryPoint = "LoadImageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [LibraryImport("user32.dll")]
    private static partial int DestroyIcon(nint icon);
    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();
    [LibraryImport("user32.dll", EntryPoint = "AppendMenuW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int AppendMenu(nint menu, uint flags, nuint id, string text);
    [LibraryImport("user32.dll")]
    private static partial int DestroyMenu(nint menu);
    [LibraryImport("user32.dll")]
    private static partial int GetCursorPos(out Point point);
    [LibraryImport("user32.dll")]
    private static partial int SetForegroundWindow(nint hwnd);
    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    private static partial int PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")]
    private static partial uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
}
