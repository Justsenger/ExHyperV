using System.Diagnostics;
using ExHyperV.App.Diagnostics;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared.ConsoleHost;
using ExHyperV.App.UI.Shared.Feedback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ExHyperV.App.UI.Pages.Settings;

public sealed partial class SettingsPage
{
    internal Func<(Guid Id, string? Name)>? ExtensionTarget { get; init; }

    private void UpdateSessionOptions(AppearanceSettings prefs)
    {
        bool includeExternal = prefs.ShowExtensionEntries || prefs.DefaultConsoleSession == ConsoleSessionMode.External;
        if (includeExternal == sessionIncludesExternal) return;
        sessionIncludesExternal = includeExternal;
        SettingOption[] modes = includeExternal
            ? [Option(ConsoleSessionMode.RdpBasic, "Settings_Basic"), Option(ConsoleSessionMode.RdpEnhanced, "Settings_Enhanced"),
                Option(ConsoleSessionMode.External, "Settings_SuperSession")]
            : [Option(ConsoleSessionMode.RdpBasic, "Settings_Basic"), Option(ConsoleSessionMode.RdpEnhanced, "Settings_Enhanced")];
        session.SetOptions(modes);
        double widest = 0;
        foreach (var mode in modes)
        {
            var label = new TextBlock { Text = mode.Label, FontSize = 13 };
            label.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, label.DesiredSize.Width);
        }
        session.MinWidth = Math.Ceiling(widest + 18) * modes.Length + 16;
    }

    private void RefreshExtensions()
    {
        var target = ExtensionTarget?.Invoke() ?? (Guid.Empty, null);
        ExtensionsSection.Children.Clear();
        ExtensionsSection.Children.Add(Card("Settings_ShowExtensionEntries", "Settings_ShowExtensionEntriesHelp", showExtensions));
        ExtensionsSection.Children.Add(new Border
        {
            Style = (Style)Application.Current.Resources["SectionBorderStyle"], Padding = new(17, 14, 17, 14),
            Child = new ConsoleExtensionPanel(target.Item1, target.Item2)
        });
    }

    private void BuildLogs()
    {
        LogsSection.Children.Add(Card("Settings_EnableLogging", "Settings_LoggingHelp", logging));
        var verboseRow = Card("Settings_VerboseDiagnostics", "Settings_VerboseHelp", verbose);
        verbose.IsEnabledChanged += (_, _) => verboseRow.Opacity = verbose.IsEnabled ? 1 : .55;
        LogsSection.Children.Add(verboseRow);
        var actions = new WrapPanel();
        actions.Children.Add(ActionButton("Settings_ExportLogs", ExportLogsAsync));
        actions.Children.Add(ActionButton("Settings_OpenLogs", () => OpenDirectoryAsync(AppDiagnostics.DirectoryPath)));
        actions.Children.Add(ActionButton("Settings_ClearLogs", ClearLogsAsync));
        LogsSection.Children.Add(Card("Settings_LogManagement", "Settings_LogHelp", actions));

        var location = new WrapPanel();
        var path = new TextBlock { Text = @"%LocalAppData%\ExHyperV", FontFamily = new FontFamily("Consolas"),
            FontSize = 13, Margin = new(0, 7, 0, 0), IsTextSelectionEnabled = true };
        ToolTipService.SetToolTip(path, AppDiagnostics.DataDirectory);
        location.Children.Add(path);
        location.Children.Add(ActionButton("Settings_OpenLogs", () => OpenDirectoryAsync(AppDiagnostics.DataDirectory)));
        LogsSection.Children.Add(Card("Settings_DataFolder", "Settings_DataHelp", location));
    }

    private Button ActionButton(string key, Func<Task> action, string? glyph = null)
    {
        string title = L10n.Get(key);
        var button = new Button { Content = glyph == null ? title : IconLabel(glyph, title) };
        AutomationProperties.SetName(button, title);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await UiEvent.RunAsync(action, message => Notice.Show(message)); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private static Task OpenDirectoryAsync(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }

    private async Task ExportLogsAsync()
    {
        string? path = await StoragePickers.SaveAsync($"ExHyperV-Diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            L10n.Get("Settings_ZipType"), ".zip");
        if (path == null) return;
        await AppDiagnostics.ExportAsync(path, appearance.Settings);
        ShowSuccess(L10n.Get("Settings_Exported"));
    }

    private async Task ClearLogsAsync()
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
            Title = L10n.Get("Settings_LogManagement"), Content = L10n.Get("Settings_ClearConfirm"),
            PrimaryButtonText = L10n.Get("Settings_ClearLogs"), CloseButtonText = L10n.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await AppDiagnostics.ClearAsync();
        ShowSuccess(L10n.Get("Settings_Cleared"));
    }

    private void BuildAbout()
    {
        VersionBadge.Text = AppUpdates.CurrentVersion;
        AboutDescription.Text = L10n.Get("Settings_AboutDescription");
        AutoUpdateLabel.Text = L10n.Get("Settings_AutoUpdate");
        AutomationProperties.SetName(AutoUpdate, AutoUpdateLabel.Text);

        var check = ActionButton("Settings_CheckUpdate", CheckUpdateAsync, "\uE72C");
        check.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        AboutActions.Children.Add(check);
        foreach (var (key, glyph, url) in new[] {
            ("Settings_Docs", "\uE8A7", "https://exhyperv.com"),
            ("Settings_Feedback", "\uE71B", AppUpdates.Repository + "/issues"),
            ("Settings_GitHub", "\uE943", AppUpdates.Repository),
            ("Settings_Sponsor", "\uEB52", "https://afdian.com/a/saniye") })
        {
            var button = ActionButton(key, () => AppUpdates.OpenLinkAsync(url), glyph);
            if (key == "Settings_Sponsor" && button.Content is StackPanel content && content.Children[0] is FontIcon heart)
                heart.Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
            AboutActions.Children.Add(button);
        }
        foreach (Button button in AboutActions.Children)
        {
            button.Height = 34;
            button.Padding = new(16, 0, 16, 0);
        }
    }

    private async Task CheckUpdateAsync()
    {
        string? update;
        try { update = await AppUpdates.CheckAsync(); }
        catch (OperationCanceledException) { throw new TimeoutException(L10n.Get("Settings_UpdateTimeout")); }
        if (!IsLoaded) return;
        if (update == null) ShowSuccess(L10n.Format("Settings_UpToDate", AppUpdates.CurrentVersion));
        else Notice.Show(L10n.Format("Settings_UpdateFound", update), InfoBarSeverity.Informational,
            ActionButton("Settings_ViewRelease", () => AppUpdates.OpenLinkAsync(AppUpdates.ReleasePage)));
    }
}
