using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ExHyperV.App.UI.Shared.Appearance;

internal static unsafe partial class NativeTheme
{
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    internal enum PreferredAppMode
    {
        Default = 0,
        AllowDark = 1,
        ForceDark = 2,
        ForceLight = 3
    }

    private static readonly Lock Gate = new();
    private static bool resolved;
    private static delegate* unmanaged[Stdcall]<void> refreshImmersiveColorPolicyState;
    private static delegate* unmanaged[Stdcall]<nint, int, int> allowDarkModeForWindow;
    private static delegate* unmanaged[Stdcall]<int, int> setPreferredAppMode;
    private static delegate* unmanaged[Stdcall]<void> flushMenuThemes;

    internal static (PreferredAppMode Mode, bool AllowDarkWindow) ResolvePreferredAppMode(
        int themeValue,
        bool highContrast,
        bool systemDark)
    {
        if (highContrast) return (PreferredAppMode.Default, false);
        bool dark = themeValue switch
        {
            2 => true,
            1 => false,
            _ => systemDark
        };
        return (dark ? PreferredAppMode.ForceDark : PreferredAppMode.ForceLight, dark);
    }

    internal static void SyncResolved(nint window, int themeValue, bool highContrast, bool systemDark)
    {
        if (!EnsureEntryPoints()) return;
        var (mode, allowDark) = ResolvePreferredAppMode(themeValue, highContrast, systemDark);
        lock (Gate)
        {
            if (setPreferredAppMode != null) setPreferredAppMode((int)mode);
            if (window != 0 && allowDarkModeForWindow != null) allowDarkModeForWindow(window, allowDark ? 1 : 0);
            if (refreshImmersiveColorPolicyState != null) refreshImmersiveColorPolicyState();
            if (flushMenuThemes != null) flushMenuThemes();
        }
    }

    private static bool EnsureEntryPoints()
    {
        if (Volatile.Read(ref resolved)) return setPreferredAppMode != null || allowDarkModeForWindow != null;
        lock (Gate)
        {
            if (resolved) return setPreferredAppMode != null || allowDarkModeForWindow != null;
            nint module = LoadLibraryExW("uxtheme.dll", 0, LoadLibrarySearchSystem32);
            if (module != 0)
            {
                refreshImmersiveColorPolicyState = (delegate* unmanaged[Stdcall]<void>)GetProcAddress(module, 104);
                allowDarkModeForWindow = (delegate* unmanaged[Stdcall]<nint, int, int>)GetProcAddress(module, 133);
                setPreferredAppMode = (delegate* unmanaged[Stdcall]<int, int>)GetProcAddress(module, 135);
                flushMenuThemes = (delegate* unmanaged[Stdcall]<void>)GetProcAddress(module, 136);
            }
            Volatile.Write(ref resolved, true);
            return setPreferredAppMode != null || allowDarkModeForWindow != null;
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryExW(string lpLibFileName, nint hFile, uint dwFlags);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetProcAddress(nint hModule, nint lpProcName);
}
