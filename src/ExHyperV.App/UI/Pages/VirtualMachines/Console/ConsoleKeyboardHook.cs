using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

// Captures hardware and shell keys exclusively while the guest console owns focus.
internal sealed unsafe partial class ConsoleKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint LlkhfExtended = 0x01;
    private const uint LlkhfUp = 0x80;
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkPause = 0x13;
    private const uint VkCapital = 0x14;
    private const uint VkKana = 0x15;
    private const uint VkLeft = 0x25;
    private const uint VkNumLock = 0x90;
    private const uint VkScroll = 0x91;
    private const uint VkLControl = 0xA2;
    private const uint VkRControl = 0xA3;
    private const uint VkLMenu = 0xA4;
    private const uint VkRMenu = 0xA5;

    private static ConsoleKeyboardHook? activeInstance;
    private readonly nint hwnd;
    private readonly Func<ushort, bool, bool, bool> sendKey;
    private readonly Func<bool> sendPause;
    private readonly Action releaseCapture;
    private nint hookHandle;
    private bool disposed, leftCtrl, rightCtrl, leftAlt, rightAlt;

    internal ConsoleKeyboardHook(
        nint hwnd,
        Func<ushort, bool, bool, bool> sendKey,
        Func<bool> sendPause,
        Action releaseCapture)
    {
        this.hwnd = hwnd;
        this.sendKey = sendKey;
        this.sendPause = sendPause;
        this.releaseCapture = releaseCapture;
    }

    internal bool IsActive => hookHandle != 0 && !disposed;

    internal void SetEnabled(bool enabled)
    {
        if (disposed || hwnd == 0) enabled = false;
        if (enabled == (hookHandle != 0)) return;
        if (enabled)
        {
            if (activeInstance != null && activeInstance != this) activeInstance.SetEnabled(false);
            ResetModifiers();
            hookHandle = SetWindowsHookExW(WhKeyboardLl, &LowLevelKeyboardProc, GetModuleHandleW(nint.Zero), 0);
            if (hookHandle != 0) activeInstance = this;
        }
        else
        {
            Uninstall();
        }
    }

    public void Dispose()
    {
        disposed = true;
        Uninstall();
    }

    private void Uninstall()
    {
        ResetModifiers();
        nint handle = hookHandle;
        hookHandle = 0;
        if (ReferenceEquals(activeInstance, this)) activeInstance = null;
        if (handle != 0) UnhookWindowsHookEx(handle);
    }

    private void ResetModifiers() => leftCtrl = rightCtrl = leftAlt = rightAlt = false;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint LowLevelKeyboardProc(int nCode, nuint wParam, nint lParam)
    {
        var current = activeInstance;
        nint nextTarget = current?.hookHandle ?? 0;
        if (nCode < 0 || current == null || current.disposed)
            return CallNextHookEx(nextTarget, nCode, wParam, lParam);
        try
        {
            if (GetForegroundWindow() != current.hwnd)
                return CallNextHookEx(nextTarget, nCode, wParam, lParam);

            uint msg = (uint)wParam;
            if (msg is not (WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp))
                return CallNextHookEx(nextTarget, nCode, wParam, lParam);

            var info = *(KbdLlHookStruct*)lParam;
            uint vk = info.vkCode;
            if (IsPassThroughKey(vk))
                return CallNextHookEx(nextTarget, nCode, wParam, lParam);

            bool down = (info.flags & LlkhfUp) == 0 && (msg is WmKeyDown or WmSysKeyDown);
            current.TrackModifiers(vk, down);

            if (down && vk == VkLeft && current.IsCtrlAltHeld())
            {
                current.SetEnabled(false);
                current.releaseCapture();
                return (nint)1;
            }

            if (vk == VkPause)
            {
                if (down) current.sendPause();
                return (nint)1;
            }

            uint rawScan = info.scanCode != 0 ? info.scanCode : MapVirtualKeyW(vk, 0);
            ushort scanCode = (ushort)(rawScan & 0xFF);
            if (scanCode == 0)
                return CallNextHookEx(nextTarget, nCode, wParam, lParam);

            bool extended = (info.flags & LlkhfExtended) != 0 || (rawScan & 0xFF00) == 0xE000;
            current.sendKey(scanCode, extended, down);
            return (nint)1;
        }
        catch
        {
            return CallNextHookEx(nextTarget, nCode, wParam, lParam);
        }
    }

    private void TrackModifiers(uint vk, bool down)
    {
        switch (vk)
        {
            case VkLControl: leftCtrl = down; break;
            case VkRControl: rightCtrl = down; break;
            case VkLMenu: leftAlt = down; break;
            case VkRMenu: rightAlt = down; break;
        }
    }

    private bool IsCtrlAltHeld() =>
        (leftCtrl || rightCtrl || (GetKeyState((int)VkControl) & 0x8000) != 0) &&
        (leftAlt || rightAlt || (GetKeyState((int)VkMenu) & 0x8000) != 0);

    private static bool IsPassThroughKey(uint vk) =>
        vk is VkCapital or VkNumLock or VkScroll or VkKana or (>= 0xA6 and <= 0xB7);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetModuleHandleW(nint moduleName);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetWindowsHookExW(int idHook, delegate* unmanaged[Stdcall]<int, nuint, nint, nint> lpfn, nint hmod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    private static partial nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int nVirtKey);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint uCode, uint uMapType);
}
