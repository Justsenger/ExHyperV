using ExHyperV.App.Localization;
using ExHyperV.App.UI.Pages.VirtualMachines.Dashboard;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.ConsoleHost;
using ExHyperV.App.UI.Shared.Feedback;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

public sealed partial class ConsoleWindow
{
    private ContentDialog? confirmation;
    private string? appliedModeId;
    private static readonly (int Width, int Height)[] Resolutions =
        [(3840, 2160), (2560, 1440), (1920, 1080), (1600, 900), (1280, 720), (1024, 768), (800, 600)];
    private static readonly SolidColorBrush SelectedFill = new(Color.FromArgb(38, 150, 150, 150));
    private static readonly SolidColorBrush ReadoutForeground = new(Color.FromArgb(184, 255, 255, 255));
    private bool SavePrimary => Appearance.Settings.DefaultPowerAction == "Save";
    private VmPowerAction PrimaryPower => powerState != VmState.Running ? VmPowerAction.Start :
        SavePrimary ? VmPowerAction.Save : VmPowerAction.Stop;

    private static void Tip(FrameworkElement control, string text)
    {
        ToolTipService.SetToolTip(control, text);
        AutomationProperties.SetName(control, text);
    }

    private void Localize()
    {
        Root.Language = System.Globalization.CultureInfo.CurrentUICulture.Name;
        Tip(PowerDrop, L10n.Get("Dash_MorePowerActions"));
        Tip(Resolution, L10n.Get("Console_Resolution"));
        Tip(Zoom, L10n.Get("Console_Zoom"));
        Tip(SessionMode, L10n.Get("Console_SessionMode"));
        Tip(CapsuleMode, L10n.Get("Console_SessionMode"));
        Tip(Sas, L10n.Get("Console_SendSas"));
        Tip(CapsuleSas, L10n.Get("Console_SendSas"));
        Tip(Screenshot, L10n.Get("Console_Capture"));
        Tip(Pin, L10n.Get("Console_Pin"));
        SasLabel.Text = L10n.Get("Console_Sas");
        ExitLabel.Text = L10n.Get("Console_ExitFullscreen");
        ConnectingText.Text = L10n.Get("Console_Connecting");
        StateHint.Text = L10n.Get("Console_DisabledHint");
    }

    private void Render()
    {
        bool running = powerState == VmState.Running;
        bool disabled = view == View.Disabled;
        bool connected = view == View.Connected && connection != null;
        Title = L10n.Format("Console_Title", vm.Name);
        SwitchName.Text = CapsuleName.Text = vm.Name;
        SwitchIcon.Source = StateIcon.Source = OsIconSource.Get(vm.OsIconName);
        Tip(Switcher, vm.Name);
        PowerIcon.FontFamily = running ? AppFonts.FluentSystem : AppFonts.FluentFilled;
        PowerIcon.Glyph = !running ? "\uF610" : SavePrimary ? "\uF680" : "\uF60F";
        bool turnOffPending = busyPower && activePowerAction == VmPowerAction.TurnOff;
        Power.IsEnabled = !busyPower && powerState is VmState.PoweredOff or VmState.Saved or VmState.Suspended or VmState.Running;
        PowerDrop.IsEnabled = running && !turnOffPending;
        Tip(Power, L10n.Get(!running ? "Dash_Power_Start" : SavePrimary ? "Dash_Power_SaveState" : "Dash_Power_ShutDown"));
        var displayedSize = connected ? liveSize ?? (IsEnhanced ? requestedSize : null) : (IsEnhanced ? requestedSize : null);
        ResolutionLabel.Text = displayedSize is { } size ? L10n.Format("Console_Size", size.Width, size.Height) : "-";
        bool canResize = running && connected && CanChangeDisplay && (DisplayModes != null || connection?.Video?.SupportsResize == true);
        Resolution.IsEnabled = canResize;
        if (!canResize && displayedSize != null) ResolutionLabel.Foreground = ReadoutForeground;
        else ResolutionLabel.ClearValue(TextBlock.ForegroundProperty);
        Tip(Resolution, L10n.Get(sessionState.ActiveBackend != "RdpBasic" || displayedSize == null ? "Console_Resolution" : "Console_BasicResolution"));
        ZoomLabel.Text = ZoomText(IsEnhanced ? desktopScale : zoom);
        Tip(Zoom, L10n.Get(IsEnhanced ? "Console_EnhancedScale" : "Console_Zoom"));
        string modeText = L10n.Get((sessionState.ActiveBackend ?? sessionState.Target.ToString()) switch
        {
            ConsoleExtensionCatalog.External => "Settings_SuperSession", "RdpEnhanced" => "Settings_Enhanced", _ => "Settings_Basic"
        });
        SessionMode.IsEnabled = running && !disabled;
        SessionModeLabel.Text = modeText;
        Tip(SessionMode, L10n.Get("Console_SessionMode") + " / " + modeText);
        CapsuleMode.IsEnabled = running && !disabled;
        CapsuleModeLabel.Text = modeText;
        CapsuleMode.Background = sessionState.ActiveBackend is "RdpEnhanced" or ConsoleExtensionCatalog.External ? SelectedFill : null;
        Tip(CapsuleMode, L10n.Get("Console_SessionMode") + " / " + modeText);
        Audio.IsEnabled = connected && playback != null;
        AudioIcon.Glyph = muted ? "\uEB4B" : "\uEB43";
        Audio.Background = muted ? SelectedFill : null;
        Tip(Audio, L10n.Get(connected && playback == null
            ? "Console_AudioDisabledInSettings"
            : muted ? "Console_Unmute" : "Console_Mute"));
        bool micReady = connected && sessionState.CanInput && capture?.IsAvailable == true;
        bool micLive = micReady && capture?.IsCapturing == true;
        Mic.IsEnabled = micReady;
        MicIcon.Glyph = micLive ? "\uE720" : "\uF781";
        Mic.Background = micLive ? SelectedFill : null;
        Tip(Mic, L10n.Get(!connected || sessionState.ActiveBackend == ConsoleService.RdpBasicBackendId
            ? "Console_MicEnhancedOnly"
            : capture == null
                ? "Console_MicDisabledInSettings"
                : !capture.IsAvailable
                    ? "Console_MicWaitingGuest"
                    : micLive ? "Console_MicMute" : "Console_MicUnmute"));
        Sas.IsEnabled = CapsuleSas.IsEnabled = connected && sessionState.CanInput;
        Screenshot.IsEnabled = connected && sessionState.Allows(ConsoleCapabilities.Video);
        var status = new List<string>();
        if (connected && !sessionState.CanInput) status.Add(L10n.Get("Console_ViewOnly"));
        if (connected && sessionState.Transport is ConsoleTransportKind.GpuShared or ConsoleTransportKind.CpuShared)
            status.Add(sessionState.Transport.ToString());
        AccessText.Text = string.Join(" / ", status);
        AccessBadge.Visibility = ShowWhen(status.Count != 0);
        Surface.IsTabStop = Sas.IsEnabled;
        ExtensionDetails.Visibility = ShowWhen(extensionStatus && view == View.Disconnected);
        if (ExtensionDetails.Visibility == Visibility.Visible)
            ExtensionDetails.Content ??= new ConsoleExtensionPanel(vm.Id, vm.Name, () => SwitchSessionMode(ConsoleSessionMode.External));
        else ExtensionDetails.Content = null;
        FullscreenIcon.Glyph = fullscreen ? "\uE688" : "\uE685";
        Tip(Fullscreen, L10n.Get(fullscreen ? "Console_ExitFullscreen" : "Console_Fullscreen"));
        Pin.Background = pinned ? SelectedFill : null;
        Scroll.Visibility = ShowWhen(connected);
        Overlay.Visibility = ShowWhen(!connected);
        Connecting.Visibility = ShowWhen(view == View.Connecting);
        Spinner.IsActive = view == View.Connecting;
        StateCard.Visibility = ShowWhen(view != View.Connecting && !connected);
        StateTitle.Text = L10n.Get(view switch
        {
            View.Disconnected => "Console_Disconnected", View.Disabled => "Console_Disabled", _ => "Console_Stopped"
        });
        StateHint.Visibility = ShowWhen(view == View.Disabled);
        StateAction.Visibility = ShowWhen((!extensionStatus && view == View.Disconnected) || view == View.Stopped);
        StateAction.Content = L10n.Get(view == View.Stopped ? "Console_StartConnect" : "Console_Reconnect");
        StateAction.IsEnabled = view != View.Stopped || Power.IsEnabled;
    }

    private static string ZoomText(int value) => value == 0 ? L10n.Get("Common_Auto") : L10n.Format("Console_Percent", value);

    private sealed record CompactMenuOption(string Label, bool Selected, bool Enabled, Action OnClick, string? Tooltip = null, bool SeparatorAfter = false);

    private static void ShowCompactFlyout(FrameworkElement anchor, IEnumerable<CompactMenuOption> options)
    {
        var panel = new StackPanel { Spacing = 2 };
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        presenterStyle.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShouldConstrainToRootBounds = false,
            FlyoutPresenterStyle = presenterStyle,
            Content = panel
        };
        var itemStyle = Application.Current.Resources["VmSelectorButtonStyle"] as Style;
        var dividerBrush = Application.Current.Resources["DividerStrokeColorDefaultBrush"] as Brush;
        foreach (var option in options)
        {
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = option.Label,
                    FontSize = 12.5,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Style = itemStyle,
                Height = 30,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(12, 0, 12, 0),
                Background = option.Selected ? SelectedFill : null,
                IsEnabled = option.Enabled,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            if (!string.IsNullOrWhiteSpace(option.Tooltip)) Tip(button, option.Tooltip);
            button.Click += (_, _) => { flyout.Hide(); option.OnClick(); };
            panel.Children.Add(button);
            if (option.SeparatorAfter)
                panel.Children.Add(new Border { Height = 1, Margin = new Thickness(4, 2, 4, 2), Background = dividerBrush });
        }
        flyout.ShowAt(anchor);
    }

    private IConsoleDisplayModes? DisplayModes => connection as IConsoleDisplayModes ?? connection?.Video as IConsoleDisplayModes;
    private bool CanChangeDisplay => sessionState.CanInput && Granted(ConsoleCapabilities.Input) && Granted(ConsoleCapabilities.DisplayResize);

    private void OnResolutionMenu(object sender, RoutedEventArgs args)
    {
        if (!CanChangeDisplay) return;
        var current = liveSize ?? requestedSize;
        var source = connection;
        if (DisplayModes is { } modes)
        {
            IReadOnlyList<ConsoleDisplayMode> choices;
            try { choices = modes.GetModes(); }
            catch (Exception) { ResizeRejected(); return; }
            ShowCompactFlyout(Resolution, choices.Select(mode => new CompactMenuOption(
                L10n.Format("Console_DisplayMode", mode.Width, mode.Height, (double)mode.RefreshNumerator / mode.RefreshDenominator),
                mode.Id == appliedModeId, true, () =>
                {
                    if (!CanChangeDisplay || source != connection) return;
                    ConsoleDisplayModeResult result;
                    try { result = modes.SetMode(mode.Id); }
                    catch (Exception) { ResizeRejected(); return; }
                    if (!result.Accepted || result.ActiveMode is not { } applied) { ResizeRejected(); return; }
                    appliedModeId = applied.Id;
                    requestedSize = (applied.Width, applied.Height);
                    SetFrameSize(applied.Width, applied.Height);
                    RememberDisplay();
                    Render();
                })));
            return;
        }
        ShowCompactFlyout(Resolution, Resolutions.Select(size => new CompactMenuOption(
            L10n.Format("Console_SizeChoice", size.Width, size.Height),
            size == current,
            true,
            () =>
            {
                if (!CanChangeDisplay || source != connection) return;
                if (connection?.Video?.Resize(size.Width, size.Height, EffectiveDesktopScale(size.Width)) != true) { ResizeRejected(); return; }
                requestedSize = size;
                manualWindowSize = false;
                RememberDisplay();
                Render();
            })));
    }

    private void OnZoomMenu(object sender, RoutedEventArgs args)
    {
        if (IsEnhanced)
        {
            ShowCompactFlyout(Zoom, ((int[])[0, 300, 250, 200, 175, 150, 125, 100]).Select(value => new CompactMenuOption(
                ZoomText(value),
                value == desktopScale,
                true,
                () =>
                {
                    if (!CanChangeDisplay) return;
                    desktopScale = value;
                    RememberDisplay();
                    if (zoom != 0) { zoom = 0; SizeSurface(); }
                    var current = liveSize ?? requestedSize;
                    if (connection?.Video?.SupportsResize == true
                        && !connection.Video.Resize(current.Width, current.Height, EffectiveDesktopScale(current.Width)))
                    {
                        ResizeRejected();
                    }
                    Render();
                },
                SeparatorAfter: value == 0)));
            return;
        }
        ShowCompactFlyout(Zoom, ((int[])[0, 200, 150, 125, 100, 75, 50]).Select(value => new CompactMenuOption(
            ZoomText(value),
            value == zoom,
            true,
            () => { zoom = value; RememberDisplay(); if (value == 0 && !manualWindowSize) FitWindowToAspect(); SizeSurface(); Render(); },
            SeparatorAfter: value == 0)));
    }

    private void OnSessionModeMenu(object sender, RoutedEventArgs args)
    {
        bool canEnhanced = powerState == VmState.Running && view != View.Disabled && firstGenKnown != true && enhancedAvailable;
        ShowCompactFlyout((FrameworkElement)sender, (CompactMenuOption[])[
            new(L10n.Get("Settings_Basic"), sessionState.ActiveBackend == "RdpBasic", true, () => SwitchSessionMode(ConsoleSessionMode.RdpBasic)),
            new(L10n.Get("Settings_Enhanced"), IsEnhanced, canEnhanced, () => SwitchSessionMode(ConsoleSessionMode.RdpEnhanced),
                canEnhanced ? null : L10n.Get("Console_EnhancedUnavailable")),
            new(L10n.Get("Settings_SuperSession"), sessionState.ActiveBackend == ConsoleExtensionCatalog.External, true,
                () => SwitchSessionMode(ConsoleSessionMode.External))
        ]);
    }

    private void OnCapsuleModeToggle(object sender, RoutedEventArgs args) => OnSessionModeMenu(sender, args);

    private bool CanRunPower(VmPowerAction action) =>
        !busyPower || (activePowerAction != VmPowerAction.TurnOff
            && action != activePowerAction
            && action is VmPowerAction.TurnOff or VmPowerAction.Restart);

    private void OnPowerMenu(object sender, RoutedEventArgs args)
    {
        var menu = new MenuFlyout();
        void Add(VmPowerAction action, string key, string glyph, bool filled = false)
        {
            var icon = new FontIcon { Glyph = glyph, FontFamily = filled ? AppFonts.FluentFilled : AppFonts.FluentSystem, FontSize = 16 };
            var item = new MenuFlyoutItem { Text = L10n.Get(key), Icon = icon, IsEnabled = CanRunPower(action) };
            if (action == VmPowerAction.TurnOff) item.Foreground = icon.Foreground = new SolidColorBrush(Color.FromArgb(255, 232, 17, 35));
            item.Click += (_, _) => RunPower(action);
            menu.Items.Add(item);
        }
        if (SavePrimary) Add(VmPowerAction.Stop, "Dash_Power_ShutDown", "\uF60F");
        else Add(VmPowerAction.Save, "Dash_Power_SaveState", "\uF680");
        Add(VmPowerAction.Restart, "Dash_Power_Restart", "\uF13E");
        Add(VmPowerAction.Suspend, "Dash_Power_Pause", "\uF5AC", true);
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(VmPowerAction.TurnOff, "Dash_Power_ForceTurnOff", "\uF60F");
        menu.ShowAt(PowerDrop);
    }

    private void OnStateAction(object sender, RoutedEventArgs e)
    {
        if (view == View.Stopped) RunPower(VmPowerAction.Start);
        else
        {
            reconnect = true;
            retryPending = false;
            retryBudget = StartupRetryLimit;
            sessionState.ResetFallback();
            SetView(View.Connecting);
            RefreshSoon();
        }
    }

    private void OnPrimaryPower(object sender, RoutedEventArgs e) => RunPower(PrimaryPower);

    private void RunPower(VmPowerAction action)
    {
        if (closing || !switchWork.IsCompleted || !CanRunPower(action)) return;
        _ = UiEvent.RunAsync(() => ChangePowerAsync(vm.Id, action, ++powerGeneration), Notify);
    }

    private async Task ChangePowerAsync(Guid id, VmPowerAction action, long generation)
    {
        busyPower = true;
        activePowerAction = action;
        Render();
        bool CurrentOperation() => generation == powerGeneration && !closing && vm.Id == id;
        try
        {
            if (action == VmPowerAction.TurnOff)
            {
                string label = L10n.Get("Dash_Power_ForceTurnOff");
                confirmation = new ContentDialog
                {
                    XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme,
                    Title = L10n.Format("Dash_PowerConfirmTitle", label, vm.Name),
                    Content = L10n.Get("Dash_PowerConfirm_TurnOffBody"),
                    PrimaryButtonText = label, CloseButtonText = L10n.Get("Common_Cancel"), DefaultButton = ContentDialogButton.Close
                };
                var decision = await confirmation.ShowAsync();
                if (!CurrentOperation() || decision != ContentDialogResult.Primary) return;
            }
            var result = await Task.Run(() => VmPowerService.ExecuteControlActionAsync(id, action));
            if (!CurrentOperation()) return;
            bool escalatedRestart = false;
            if (!result.Success)
            {
                if (action == VmPowerAction.Restart)
                {
                    var (escalated, resetResult) = await VmPowerFallback.TryForceResetAsync(id, result);
                    if (!CurrentOperation()) return;
                    escalatedRestart = escalated;
                    if (!escalated) { Notify(CoreMessageResolver.ResolveError(resetResult)); RefreshSoon(); return; }
                }
                else if (action == VmPowerAction.Stop && VmPowerFallback.CanEscalate(result))
                {
                    string reason = CoreMessageResolver.ResolveError(result);
                    if (!await VmPowerFallback.ConfirmForceTurnOffAsync(Root.XamlRoot, Root.ActualTheme, vm.Name, reason, d => confirmation = d)
                        || !CurrentOperation())
                    {
                        if (CurrentOperation()) RefreshSoon();
                        return;
                    }
                    var turnOff = await Task.Run(() => VmPowerService.ExecuteControlActionAsync(id, VmPowerAction.TurnOff));
                    if (!CurrentOperation()) return;
                    if (!turnOff.Success) Notify(CoreMessageResolver.ResolveError(turnOff));
                    RefreshSoon();
                    return;
                }
                else
                {
                    Notify(CoreMessageResolver.ResolveError(result));
                    RefreshSoon();
                    return;
                }
            }
            if (action is VmPowerAction.Start or VmPowerAction.Restart)
            {
                await DropConnectionAsync();
                if (!CurrentOperation()) return;
                reconnect = true;
                retryPending = false;
                retryBudget = StartupRetryLimit;
                sessionState.ResetFallback();
                SetView(View.Connecting);
                if (escalatedRestart) Notify(L10n.Get("Dash_Power_RestartEscalated"));
            }
            RefreshSoon();
        }
        finally
        {
            if (generation == powerGeneration)
            {
                confirmation = null;
                busyPower = false;
                activePowerAction = null;
                if (!closing) Render();
            }
        }
    }

    private void OnMute(object sender, RoutedEventArgs args)
    {
        muted = !muted;
        RememberDisplay();
        playback?.SetMuted(muted);
        Render();
    }

    private bool CanSendMicrophone => sessionState.CanInput && Granted(ConsoleCapabilities.AudioInput) && Granted(ConsoleCapabilities.Input);

    private void OnMicToggle(object sender, RoutedEventArgs args)
    {
        if (!CanSendMicrophone || capture is not { IsAvailable: true }) return;
        capture.SetEnabled(!capture.IsCapturing);
        Render();
    }

    private async void OnSwitcher(object sender, RoutedEventArgs args) => await UiEvent.RunAsync(async () =>
    {
        if (pollLifetime == null) return;
        var token = pollLifetime.Token;
        var request = new VmQueryRequest((VmQueryField[])[VmIdentityFields.Name, VmIdentityFields.State, VmMetadataFields.Key, VmMetadataFields.Value]);
        var list = new StackPanel { MinWidth = 220, MaxWidth = 420, Spacing = 2 };
        var flyout = new Flyout { Content = new ScrollViewer { MaxHeight = 400, Content = list } };
        Switcher.IsEnabled = false;
        IReadOnlyList<VmQueryRow> rows;
        try { rows = await ReadRowsAsync(request, token); }
        finally { if (!closing) Switcher.IsEnabled = true; }
        token.ThrowIfCancellationRequested();
        foreach (var row in rows.OrderBy(r => r.Get(VmIdentityFields.Name).Value, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!Guid.TryParse(row.Key, out var id)) continue;
            var state = row.Get(VmIdentityFields.State);
            var target = new VmListItem(id, row.Get(VmIdentityFields.Name).Value ?? row.Key, state.HasValue ? state.Value : null, "")
                { OsIconName = OsIconSource.ReadName(row) };
            bool current = id == vm.Id, open = WindowsByVm.ContainsKey(id);
            var grid = new Grid { ColumnSpacing = 2 };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var content = new Grid { ColumnSpacing = 8 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(new Image { Width = 16, Height = 16, Source = OsIconSource.Get(target.OsIconName) });
            var name = new TextBlock { Text = target.Name, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(name, 1);
            content.Children.Add(name);
            var status = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Opacity = .7,
                Text = current ? L10n.Get("Console_Current") : open ? L10n.Get("Console_Open") : target.State };
            Grid.SetColumn(status, 2);
            content.Children.Add(status);
            var select = new Button { Content = content, Padding = new Thickness(6, 3, 6, 3), Background = current ? SelectedFill : null,
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            Tip(select, target.Name);
            select.Click += (_, _) => { flyout.Hide(); switchWork = SwitchAsync(target); };
            grid.Children.Add(select);
            if (!open)
            {
                var pop = new Button { Content = new FontIcon { Glyph = "\uE8A7", FontSize = 16, FontFamily = AppFonts.SegoeFluent }, Padding = new Thickness(5) };
                Tip(pop, L10n.Get("Console_NewWindow"));
                pop.Click += (_, _) => { flyout.Hide(); Open(target); };
                Grid.SetColumn(pop, 1);
                grid.Children.Add(pop);
            }
            list.Children.Add(grid);
        }
        flyout.ShowAt(Switcher);
    }, Notify);
}
