using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace ExHyperV.Services;

/// <summary>
///     Handles system theme changes and applies them to the application when in Auto mode
/// </summary>
public static class SystemThemeHandler
{
    private static bool _isWatching;
    private static Window? _watchedWindow;
    private static DispatcherTimer? _themeCheckTimer;
    private static SystemTheme _lastKnownTheme = SystemTheme.Light;

    /// <summary>
    ///     Starts watching for system theme changes
    /// </summary>
    /// <param name="window">Main window to watch</param>
    public static void StartWatching(Window window)
    {
        if (_isWatching)
            return;

        _isWatching = true;
        _watchedWindow = window;

        // Use WPF-UI's SystemThemeWatcher to monitor system theme changes
        SystemThemeWatcher.Watch(window);

        // Store initial theme
        _lastKnownTheme = SystemThemeManager.GetCachedSystemTheme();

        // Set up timer to periodically check for theme changes
        _themeCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1) // Check every second
        };
        _themeCheckTimer.Tick += OnThemeCheckTimer;
        _themeCheckTimer.Start();
    }

    /// <summary>
    ///     Stops watching for system theme changes
    /// </summary>
    public static void StopWatching()
    {
        if (!_isWatching)
            return;

        _isWatching = false;

        _themeCheckTimer?.Stop();
        _themeCheckTimer = null;

        if (_watchedWindow is null) return;
        SystemThemeWatcher.UnWatch(_watchedWindow);
        _watchedWindow = null;
    }

    /// <summary>
    ///     Timer event handler to check for theme changes
    /// </summary>
    private static void OnThemeCheckTimer(object? sender, EventArgs e)
    {
        var currentTheme = SystemThemeManager.GetCachedSystemTheme();
        if (currentTheme == _lastKnownTheme) return;
        _lastKnownTheme = currentTheme;
        // Only apply theme change if user preference is set to "Auto"
        ThemeService.OnSystemThemeChanged();
    }
}