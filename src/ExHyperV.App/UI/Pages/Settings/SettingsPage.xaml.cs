
using ExHyperV.App.Diagnostics;
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.Controls;
using ExHyperV.App.UI.Shared.Feedback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ExHyperV.App.UI.Pages.Settings;

public sealed partial class SettingsPage : Page
{
    private readonly AppAppearance appearance = ((App)Application.Current).Appearance;
    private readonly ComboBox language = new() { MinWidth = 152 };
    private readonly ComboBox theme = new() { MinWidth = 152 };
    private readonly ToggleSwitch accent = Switch(), autoConsole = Switch(), logging = Switch(), verbose = Switch();
    private readonly SettingsSegmentedPill startup = new() { Width = 264 }, close = new() { Width = 220 }, session = new() { Width = 196 };
    private readonly List<(string Hex, Border Ring, Button Button, FontIcon Check)> colors = [];
    private readonly List<StackPanel> sections = [];
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private bool updating = true;
    internal Func<Task>? PrepareLanguageChange { get; init; }

    public SettingsPage()
    {
        InitializeComponent();
        AddCategory("Settings_Appearance", "\uE790", AppearanceSection);
        AddCategory("Settings_System", "\uE770", SystemSection);
        AddCategory("Settings_Preferences", "\uE9E9", PreferencesSection);
        AddCategory("Settings_Diagnostics", "\uE9D9", LogsSection);
        AddCategory("Settings_About", "\uE946", AboutSection);
        AutomationProperties.SetName(Categories, L10n.Get("Settings_Title"));
        Categories.ItemContainerTransitions.Clear();
        Categories.SelectedIndex = 0;

        foreach (string key in new[] { "Settings_Language_System", "Settings_Language_ZhCn", "Settings_Language_En" })
            language.Items.Add(L10n.Get(key));
        foreach (string key in new[] { "Settings_Theme_System", "Settings_Theme_Light", "Settings_Theme_Dark" })
            theme.Items.Add(L10n.Get(key));
        AppearanceSection.Children.Insert(0, Card("Settings_Language", "Settings_LanguageHelp", language));
        AppearanceSection.Children.Insert(1, Card("Settings_Theme", "Settings_ThemeHelp", theme));
        AccentRows.Children.Insert(0, Row("Settings_Accent", "Settings_ColorHint", accent));
        BuildPalette();

        startup.SetOptions((SettingOption[])[Option(StartupMode.Off, "Common_Off"), Option(StartupMode.Normal, "Settings_NormalStart"),
            Option(StartupMode.Silent, "Settings_SilentStart")]);
        close.SetOptions((SettingOption[])[Option(WindowCloseBehavior.Exit, "Settings_Exit"), Option(WindowCloseBehavior.Tray, "Settings_ToTray")]);
        session.SetOptions((SettingOption[])[Option(ConsoleSessionMode.RdpBasic, "Settings_Basic"), Option(ConsoleSessionMode.RdpEnhanced, "Settings_Enhanced")]);
        SystemSection.Children.Add(Card("Settings_AutoStart", "Settings_AutoStartHelp", startup));
        SystemSection.Children.Add(Card("Settings_OnClose", "Settings_OnCloseHelp", close));
        PreferencesSection.Children.Add(Card("Settings_AutoConsole", "Host_Set_AutoConsole_Desc", autoConsole));
        PreferencesSection.Children.Add(Card("Settings_ConsoleSession", "Settings_ConsoleHelp", session));
        BuildLogs();
        BuildAbout();

        language.SelectionChanged += OnLanguageChanged;
        theme.SelectionChanged += (_, _) => Apply(appearance.Settings with
            { Theme = theme.SelectedIndex switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default } });
        accent.Toggled += (_, _) => Apply(appearance.Settings with { AccentEnabled = accent.IsOn });
        startup.SelectionChanged += value => Apply(appearance.Settings with { AutoStart = (StartupMode)value! });
        close.SelectionChanged += value => Apply(appearance.Settings with { CloseBehavior = (WindowCloseBehavior)value! });
        session.SelectionChanged += value => Apply(appearance.Settings with { DefaultConsoleSession = (ConsoleSessionMode)value! });
        autoConsole.Toggled += (_, _) => Apply(appearance.Settings with { AutoOpenConsole = autoConsole.IsOn });
        logging.Toggled += (_, _) => Apply(appearance.Settings with
            { LoggingEnabled = logging.IsOn, VerboseDiagnostics = logging.IsOn && appearance.Settings.VerboseDiagnostics });
        verbose.Toggled += (_, _) => Apply(appearance.Settings with { VerboseDiagnostics = logging.IsOn && verbose.IsOn });

        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Success.Hide(); };
        Unloaded += (_, _) => { toastTimer.Stop(); Success.Hide(); };
        Loaded += (_, _) => Refresh();
        ActualThemeChanged += (_, _) => RefreshPalette();
        Refresh();
    }

    private static ToggleSwitch Switch() => new() { OnContent = "", OffContent = "", MinWidth = 0 };
    private static SettingOption Option(object value, string key) => new(value, L10n.Get(key));

    private void AddCategory(string key, string glyph, StackPanel section)
    {
        var title = L10n.Get(key);
        var item = new ListViewItem { Content = IconLabel(glyph, title), MinHeight = 40, Padding = new(12, 6, 8, 6) };
        AutomationProperties.SetName(item, title);
        sections.Add(section);
        Categories.Items.Add(item);
    }

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        for (int i = 0; i < sections.Count; i++)
            sections[i].Visibility = i == Categories.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
        Body.ChangeView(null, 0, null, true);
    }

    private static StackPanel IconLabel(string glyph, string text)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 15 });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private static Grid Row(string key, string help, FrameworkElement editor)
    {
        string title = L10n.Get(key);
        var label = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.Medium, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new SettingInfoButton(title, L10n.Get(help)));
        AutomationProperties.SetName(editor, title);

        var grid = new Grid { ColumnSpacing = 12, MinHeight = 32 };
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.Children.Add(label);
        grid.Children.Add(editor);
        editor.VerticalAlignment = VerticalAlignment.Center;
        editor.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(editor, 1);
        // Stack the editor under its label when translated text would overlap.
        label.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        editor.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        double singleLineWidth = label.DesiredSize.Width + editor.DesiredSize.Width + grid.ColumnSpacing;
        grid.SizeChanged += (_, e) =>
        {
            bool stacked = e.NewSize.Width < singleLineWidth;
            Grid.SetColumn(editor, stacked ? 0 : 1);
            Grid.SetRow(editor, stacked ? 1 : 0);
            Grid.SetColumnSpan(label, stacked ? 2 : 1);
            Grid.SetColumnSpan(editor, stacked ? 2 : 1);
            editor.HorizontalAlignment = stacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        };
        return grid;
    }

    private static Border Card(string key, string help, FrameworkElement editor) => new()
    {
        Style = (Style)Application.Current.Resources["SectionBorderStyle"], Padding = new(17, 14, 17, 14),
        MinHeight = 68, VerticalAlignment = VerticalAlignment.Center, Child = Row(key, help, editor)
    };

    private void Refresh()
    {
        updating = true;
        var prefs = appearance.Settings;
        language.SelectedIndex = prefs.Language switch { "zh-CN" => 1, "en" => 2, _ => 0 };
        theme.SelectedIndex = prefs.Theme switch { ElementTheme.Light => 1, ElementTheme.Dark => 2, _ => 0 };
        accent.IsOn = prefs.AccentEnabled;
        startup.SelectedValue = prefs.AutoStart;
        close.SelectedValue = prefs.CloseBehavior;
        autoConsole.IsOn = prefs.AutoOpenConsole;
        session.SelectedValue = prefs.DefaultConsoleSession;
        logging.IsOn = prefs.LoggingEnabled;
        verbose.IsOn = prefs.VerboseDiagnostics;
        verbose.IsEnabled = prefs.LoggingEnabled;
        AutoUpdate.IsOn = prefs.AutoCheckUpdate;
        RefreshPalette();
        updating = false;
    }

    private void Apply(AppearanceSettings next)
    {
        if (updating || next == appearance.Settings) return;
        Notice.Hide();
        if (appearance.Set(next) is { } error) Notice.Show(error);
        Refresh();
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating) return;
        string preference = language.SelectedIndex switch { 1 => "zh-CN", 2 => "en", _ => "system" };
        if (preference == appearance.Settings.Language) return;
        try
        {
            if (PrepareLanguageChange != null) await PrepareLanguageChange();
            Apply(appearance.Settings with { Language = preference });
        }
        catch (Exception error) { Notice.Show(CoreMessageResolver.ResolveException(error)); }
        finally { Refresh(); }
    }

    private void OnAutoUpdate(object sender, RoutedEventArgs e) => Apply(appearance.Settings with { AutoCheckUpdate = AutoUpdate.IsOn });

    private void ShowSuccess(string text)
    {
        Notice.Hide();
        Success.Show(text, false);
        toastTimer.Stop();
        toastTimer.Start();
    }

    private void BuildPalette()
    {
        foreach (var (name, hex) in new[] { ("Blue", "#4CC2FF"), ("Purple", "#B69CFF"), ("Pink", "#F39BC1"),
            ("Orange", "#FFB36B"), ("Yellow", "#F2D66D"), ("Green", "#7BDCB5"), ("Cyan", "#64D8E4") })
        {
            AppearanceSettings.TryParseColor(hex, out var color);
            var fill = new SolidColorBrush(color);
            var check = new FontIcon { Glyph = "\uE73E", FontSize = 14, Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black) };
            var button = new Button { Content = check, Width = 30, Height = 30, MinWidth = 0, MinHeight = 0,
                CornerRadius = new(15), Padding = new(0), Background = fill };
            button.Resources["ButtonBackgroundPointerOver"] = fill;
            button.Resources["ButtonBackgroundPressed"] = fill;
            button.Click += (_, _) => Apply(appearance.Settings with { AccentColor = hex });
            NameButton(button, "Settings_Color_" + name);
            var ring = new Border { CornerRadius = new(19), BorderThickness = new(2), Padding = new(2), Child = button };
            Swatches.Children.Insert(colors.Count, ring);
            colors.Add((hex, ring, button, check));
        }
        AutomationProperties.SetName(Swatches, L10n.Get("Settings_ColorPaletteName"));
        NameButton(CustomColor, "Settings_CustomColor");
        NameButton(CopyColor, "Settings_CopyColor");
        CancelColor.Content = L10n.Get("Common_Cancel");
        ApplyColor.Content = L10n.Get("Common_Apply");
    }

    private static void NameButton(Button button, string key)
    {
        string title = L10n.Get(key);
        AutomationProperties.SetName(button, title);
        ToolTipService.SetToolTip(button, title);
    }

    private void RefreshPalette()
    {
        var prefs = appearance.Settings;
        var outline = ThemeHelper.ThemeBrush(this, "TextFillColorPrimaryBrush");
        bool preset = false;
        foreach (var (hex, ring, button, check) in colors)
        {
            bool selected = hex.Equals(prefs.AccentColor, StringComparison.OrdinalIgnoreCase);
            preset |= selected;
            ring.BorderBrush = selected ? outline : null;
            check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetItemStatus(button, L10n.Get(selected ? "Settings_Selected" : "Settings_Unselected"));
            button.IsEnabled = prefs.AccentEnabled;
        }
        AppearanceSettings.TryParseColor(prefs.AccentColor, out var color);
        var fill = new SolidColorBrush(color);
        CustomRing.BorderBrush = preset ? null : outline;
        CustomColor.Background = preset ? CustomGradient() : fill;
        CustomGlyph.Foreground = new SolidColorBrush(preset ? Microsoft.UI.Colors.White : ThemeHelper.ContrastingForeground(color));
        CustomColor.IsEnabled = CopyColor.IsEnabled = prefs.AccentEnabled;
        AutomationProperties.SetItemStatus(CustomColor, L10n.Get(preset ? "Settings_Unselected" : "Settings_Selected"));
        Hex.Text = prefs.AccentEnabled ? prefs.AccentColor.ToUpperInvariant() : L10n.Get("Settings_Gray");
        ColorDot.Fill = prefs.AccentEnabled ? fill : ThemeHelper.ThemeBrush(this, "TextFillColorSecondaryBrush");
        Palette.Opacity = prefs.AccentEnabled ? 1 : .42;
    }

    private static LinearGradientBrush CustomGradient()
    {
        var brush = new LinearGradientBrush { EndPoint = new(1, 1) };
        brush.GradientStops.Add(new GradientStop { Offset = 0, Color = Microsoft.UI.Colors.Coral });
        brush.GradientStops.Add(new GradientStop { Offset = .5, Color = Microsoft.UI.Colors.MediumPurple });
        brush.GradientStops.Add(new GradientStop { Offset = 1, Color = Microsoft.UI.Colors.DeepSkyBlue });
        return brush;
    }

    private void OnChooseColor(object sender, object e)
    {
        AppearanceSettings.TryParseColor(appearance.Settings.AccentColor, out var color);
        AccentPicker.Color = color;
    }

    private void OnApplyColor(object sender, RoutedEventArgs e)
    {
        Apply(appearance.Settings with { AccentColor = AppearanceSettings.ToHex(AccentPicker.Color) });
        ColorFlyout.Hide();
    }

    private void OnCancelColor(object sender, RoutedEventArgs e) => ColorFlyout.Hide();

    private void OnCopyColor(object sender, RoutedEventArgs e)
    {
        try
        {
            var data = new DataPackage();
            data.SetText(Hex.Text);
            Clipboard.SetContent(data);
            Clipboard.Flush();
            ShowSuccess(L10n.Format("Settings_ColorCopied", Hex.Text));
        }
        catch (Exception error) { Notice.Show(CoreMessageResolver.ResolveException(error)); }
    }
}
