using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;

namespace ExHyperV.App.UI.Shared.Appearance;

internal static unsafe partial class NativeTheme
{
    private static readonly AccessibilitySettings Accessibility = new();
    private static readonly UISettings SystemColors = new();

    internal static void Sync(nint window, ElementTheme requestedTheme, bool? highContrastOverride = null)
    {
        bool highContrast = highContrastOverride ?? IsHighContrast();
        bool systemDark = !highContrast && requestedTheme == ElementTheme.Default && IsSystemDark();
        SyncResolved(window, (int)requestedTheme, highContrast, systemDark);
    }

    private static bool IsHighContrast()
    {
        try { return Accessibility.HighContrast; }
        catch (COMException) { return false; }
    }

    private static bool IsSystemDark()
    {
        try
        {
            var background = SystemColors.GetColorValue(UIColorType.Background);
            return (5 * background.G + 2 * background.R + background.B) <= 8 * 128;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
