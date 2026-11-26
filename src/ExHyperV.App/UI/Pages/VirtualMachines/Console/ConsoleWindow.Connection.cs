using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.App.UI.Shared.ConsoleHost;
using ExHyperV.Models;
using ExHyperV.Services;
using Microsoft.UI.Xaml;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Console;

public sealed partial class ConsoleWindow
{
    private enum View { Connecting, Connected, Stopped, Disconnected, Disabled }
    private View view = View.Connecting;
    private IConsoleConnection? connection;
    private FramePresenter? frames;
    private ConsoleClipboardBridge? clipboard;
    private ConsolePlayback? playback;
    private ConsoleCapture? capture;
    private CancellationTokenSource? pollLifetime;
    private readonly SemaphoreSlim refresh = new(0, 1);
    private Task monitor = Task.CompletedTask;
    private const int StartupRetryLimit = 6;
    private VmState? powerState;
    private VmPowerAction? activePowerAction;
    private long powerGeneration;
    private readonly ConsoleSessionState sessionState = new();
    private static ConsoleExtensionCatalog Catalog => ConsoleExtensionCatalog.Local;
    private bool IsEnhanced => sessionState.ActiveBackend == ConsoleService.RdpEnhancedBackendId;
    private bool enhancedAvailable, reconnect, busyPower, muted, retryPending;
    private bool? firstGenKnown;
    private bool extensionStatus;
    private Guid connectedVmId;
    private readonly ConsoleSessionLifetime sessions = new();
    private CancellationTokenSource? opening;
    private Action? detachSessionEvents;
    private int retryBudget;
    private (int Width, int Height) requestedSize = (1280, 720);
    private (int Width, int Height)? liveSize;
    private int zoom;
    private int desktopScale;
    private VmConsoleFeatures enhancedFeatures = new();
    private sealed record DisplayPreferences((int Width, int Height) Size, int Zoom, int DesktopScale, bool Muted, ConsoleSessionMode Mode);
    private static readonly Dictionary<Guid, DisplayPreferences> Preferences = [];

    private bool TargetEnhanced => sessionState.Target == ConsoleSessionMode.RdpEnhanced;

    private int HostDisplayScalePercent()
    {
        double scale = layoutScale > 0 ? layoutScale : (Root.XamlRoot?.RasterizationScale ?? 1.0);
        return Math.Clamp((int)Math.Round(scale * 4.0) * 25, 100, 300);
    }

    private uint EffectiveDesktopScale(int? width = null) =>
        desktopScale >= 100
            ? (uint)Math.Clamp(desktopScale, 100, 500)
            : (uint)((width ?? requestedSize.Width) >= 1920 ? HostDisplayScalePercent() : 100);

    private void RememberDisplay() => Preferences[vm.Id] = new(requestedSize, zoom, desktopScale, muted, sessionState.Preference);

    private void BeginMonitoring()
    {
        powerState = null;
        firstGenKnown = null;
        sessionState.Clear();
        enhancedAvailable = false;
        liveSize = null;
        Preferences.TryGetValue(vm.Id, out var preferences);
        requestedSize = preferences?.Size ?? (1280, 720);
        zoom = preferences?.Zoom ?? 0;
        desktopScale = preferences?.DesktopScale ?? 0;
        muted = preferences?.Muted ?? false;
        sessionState.Preference = preferences?.Mode ?? Appearance.Settings.DefaultConsoleSession;
        extensionStatus = false;
        reconnect = true;
        retryPending = false;
        sessionState.ResetFallback();
        manualWindowSize = false;
        retryBudget = StartupRetryLimit;
        Notice.Visibility = Visibility.Collapsed;
        SetView(View.Connecting);
        FitWindowToAspect();
        pollLifetime = new();
        monitor = MonitorAsync(vm.Id, pollLifetime.Token);
    }

    private async Task StopMonitoringAsync()
    {
        var lifetime = pollLifetime;
        if (lifetime == null) return;
        lifetime.Cancel();
        SetView(View.Disconnected);
        await monitor;
        lifetime.Dispose();
        pollLifetime = null;
    }

    private static Task<IReadOnlyList<VmQueryRow>> ReadRowsAsync(VmQueryRequest request, CancellationToken token) =>
        Task.Run(async () =>
        {
            await using var query = new VmQueryService();
            var result = await query.QueryAsync(request, token).ConfigureAwait(false);
            if (!result.HasData) throw new InvalidOperationException(CoreMessageResolver.ResolveError(result));
            return result.Data!.VirtualMachines;
        }, token);

    private void RefreshSoon()
    {
        if (refresh.CurrentCount == 0) refresh.Release();
    }

    private async Task MonitorAsync(Guid id, CancellationToken token)
    {
        var request = new VmQueryRequest((VmQueryField[])[VmIdentityFields.State, VmIdentityFields.Generation,
            VmConsoleFields.BasicEnabled, VmConsoleFields.EnhancedAvailable, VmSecurityFields.Shielded,
            VmConsoleFields.EnhancedClipboard, VmConsoleFields.EnhancedAudioOutput, VmConsoleFields.EnhancedAudioInput], (Guid[])[id]);
        var stateOnly = new VmQueryRequest((VmQueryField[])[VmIdentityFields.State, VmConsoleFields.EnhancedAvailable], (Guid[])[id]);
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (reconnect && connection != null)
                    {
                        await DropConnectionAsync();
                        token.ThrowIfCancellationRequested();
                    }
                    // Console hardware cannot be changed on a running VM. Avoid re-reading
                    // configuration and security from WMI during a live connection.
                    bool live = view == View.Connected && !reconnect && !retryPending;
                    var row = (await ReadRowsAsync(live ? stateOnly : request, token)).FirstOrDefault();
                    token.ThrowIfCancellationRequested();
                    if (row == null || row.Get(VmIdentityFields.State) is not { HasValue: true } state)
                        throw new InvalidOperationException(L10n.Get("VmList_ReadFailed"));
                    if (live && (reconnect || view != View.Connected)) continue;
                    if (live && state.Value == VmState.Running)
                    {
                        bool avail = firstGenKnown != true && row.Get(VmConsoleFields.EnhancedAvailable) is { HasValue: true, Value: true };
                        if (sessionState.ActiveBackend == ConsoleService.RdpBasicBackendId && TargetEnhanced && avail)
                        {
                            enhancedAvailable = true;
                            reconnect = true;
                            retryBudget = StartupRetryLimit;
                            await DropConnectionAsync();
                            token.ThrowIfCancellationRequested();
                            continue;
                        }
                        if (avail != enhancedAvailable)
                        {
                            enhancedAvailable = avail;
                            Render();
                        }
                        await refresh.WaitAsync(TimeSpan.FromSeconds(3), token);
                        continue;
                    }
                    if (live)
                    {
                        await DropConnectionAsync();
                        token.ThrowIfCancellationRequested();

                        enhancedAvailable = false;
                        retryPending = false;
                        retryBudget = StartupRetryLimit;
                        sessionState.ResetFallback();
                        powerState = state.Value;
                        reconnect = true;
                        SetView(View.Stopped);
                        continue;
                    }
                    if (row.Get(VmIdentityFields.Generation) is not { HasValue: true } generation)
                        throw new InvalidOperationException(L10n.Get("VmList_ReadFailed"));
                    bool firstGen = generation.Value == 1;
                    firstGenKnown = firstGen;
                    enhancedAvailable = !firstGen && row.Get(VmConsoleFields.EnhancedAvailable) is { HasValue: true, Value: true };
                    if (!live)
                        enhancedFeatures = new(
                            row.Get(VmConsoleFields.EnhancedClipboard) is not { HasValue: true, Value: false },
                            row.Get(VmConsoleFields.EnhancedAudioOutput) is not { HasValue: true, Value: false },
                            row.Get(VmConsoleFields.EnhancedAudioInput) is not { HasValue: true, Value: false });
                    var support = row.Get(VmConsoleFields.BasicEnabled);
                    var shielded = row.Get(VmSecurityFields.Shielded);
                    bool disabled = shielded is { HasValue: true, Value: true } ||
                        !firstGen && (support is not { HasValue: true, Value: true } || !shielded.HasValue);
                    var previous = powerState;
                    powerState = state.Value;
                    if (disabled || powerState != VmState.Running)
                    {
                        await DropConnectionAsync();
                        token.ThrowIfCancellationRequested();
                        reconnect = true;
                        retryPending = false;
                        retryBudget = StartupRetryLimit;

                        enhancedAvailable = false;
                        sessionState.ResetFallback();
                        SetView(disabled ? View.Disabled : View.Stopped);
                    }
                    else if (retryPending)
                    {
                        retryPending = false;
                        retryBudget = Math.Max(0, retryBudget - 1);
                        await DropConnectionAsync();
                        token.ThrowIfCancellationRequested();
                        await Task.Delay(700, token);
                        token.ThrowIfCancellationRequested();
                        reconnect = true;
                        continue;
                    }
                    else if (reconnect || previous is not (null or VmState.Running))
                    {
                        if (previous is not (null or VmState.Running))
                        {
                            retryBudget = StartupRetryLimit;
                            sessionState.ResetFallback();
                        }
                        reconnect = false;
                        await DropConnectionAsync();
                        token.ThrowIfCancellationRequested();
                        var mode = sessionState.Target;
                        if (mode == ConsoleSessionMode.RdpEnhanced && !enhancedAvailable)
                        {
                            // Only the initial native preference can use the basic boot console.
                            if (sessionState.Preference != ConsoleSessionMode.RdpEnhanced || sessionState.Fallback != null)
                            {
                                SetView(View.Disconnected);
                                continue;
                            }
                            mode = ConsoleSessionMode.RdpBasic;
                        }
                        if (sessionState.Fallback != null && sessionState.Preference == ConsoleSessionMode.External &&
                            !Catalog.AllowsFallback(id, mode.ToString())) { SetView(View.Disconnected); continue; }
                        await StartConnectionAsync(id, mode, token);
                    }
                    else if (view == View.Disconnected) await DropConnectionAsync();
                    Render();
                }
                catch (Exception) when (token.IsCancellationRequested) { break; }
                catch (Exception) when (sessionState.ActiveBackend == ConsoleExtensionCatalog.External)
                {
                    reconnect = retryPending = false;
                    extensionStatus = true;
                    SetView(View.Disconnected);
                    await DropConnectionAsync();
                }
                catch (Exception ex)
                {
                    if (connection?.State != ConsoleConnectionState.Connected)
                    {
                        powerState = null;
                        await DropConnectionAsync();
                        if (!token.IsCancellationRequested && view != View.Disconnected)
                        {
                            retryPending = false;
                            SetView(View.Disconnected);
                            Notify(CoreMessageResolver.ResolveException(ex));
                        }
                    }
                }
                await refresh.WaitAsync(TimeSpan.FromSeconds(3), token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) when (sessions.Drained.IsFaulted)
        {
            reconnect = retryPending = false;
            SetView(View.Disconnected);
        }
        finally
        {
            try { await DropConnectionAsync(); }
            catch (Exception) when (sessions.Drained.IsFaulted)
            {
                Diagnostics.AppDiagnostics.Write("Console", "DisconnectFailed", $"vmId={id:D}");
            }
        }
    }

    private async Task StartConnectionAsync(Guid id, ConsoleSessionMode mode, CancellationToken token)
    {
        SetView(View.Connecting);
        var features = enhancedFeatures;
        string backend = mode.ToString();
        var snapshot = mode == ConsoleSessionMode.External ? Catalog.Inspect(id) : null;
        extensionStatus = snapshot != null && !ConsoleExtensionCatalog.Ready(snapshot);
        if (extensionStatus) { SetView(View.Disconnected); return; }
        var options = new ConsoleConnectionOptions
        {
            VmId = id, Width = requestedSize.Width, Height = requestedSize.Height,
            DesktopScale = mode == ConsoleSessionMode.RdpEnhanced ? EffectiveDesktopScale(requestedSize.Width) : 100u,
            HostCursor = true, RedirectClipboard = features.Clipboard && sessionState.FallbackLimit.HasFlag(ConsoleCapabilities.Clipboard),
            RedirectAudioOutput = features.AudioOutput && sessionState.FallbackLimit.HasFlag(ConsoleCapabilities.AudioOutput),
            RedirectAudioInput = features.AudioInput && sessionState.FallbackLimit.HasFlag(ConsoleCapabilities.AudioInput)
        };
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        opening = attempt;
        IConsoleConnection session;
        try { session = await sessions.OpenAsync(t => Catalog.ConnectAsync(backend, options, t), attempt.Token); }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested) { return; }
        catch (Exception) when (snapshot != null)
        {
            extensionStatus = true;
            SetView(View.Disconnected);
            return;
        }
        finally { opening = null; }
        connection = session;
        connectedVmId = id;
        token.ThrowIfCancellationRequested();
        if (reconnect) return;
        if (session.BackendId != backend) throw new InvalidOperationException(L10n.Get("Console_Disconnected"));
        var policy = ConsoleCapabilities.Video | ConsoleCapabilities.Input | ConsoleCapabilities.DisplayResize;
        if (features.Clipboard) policy |= ConsoleCapabilities.Clipboard;
        if (features.AudioOutput) policy |= ConsoleCapabilities.AudioOutput;
        if (features.AudioInput) policy |= ConsoleCapabilities.AudioInput;
        var caps = snapshot == null ? ConsoleService.GetBackends().Single(b => b.BackendId == backend).Capabilities
            : snapshot.Granted & snapshot.Supported;
        policy &= sessionState.FallbackLimit;
        sessionState.Activate(backend, policy, caps, snapshot?.Control ?? new(true, true, 0));
        if (snapshot != null) sessionState.SetTransport(snapshot.TransportKind);
        void Post(Action action) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!token.IsCancellationRequested && connection == session) action();
        });
        bool guestEstablished = false;
        void GrantChanged(Guid change)
        {
            if (change == Guid.Empty || change == id) Post(() =>
            {
                if (closing || reconnect || view == View.Disconnected) return;
                var updated = Catalog.Inspect(id);
                bool changedOwner = sessionState.Control.Generation != updated.Control.Generation;
                sessionState.UpdateGrant(updated);
                if (changedOwner || !sessionState.CanInput) ForgetInput();
                if (updated.TransportKind == ConsoleTransportKind.CpuShared && sessionState.Transport == ConsoleTransportKind.GpuShared)
                    frames?.RequestCpu();
                if (!sessionState.Allows(ConsoleCapabilities.Video))
                {
                    reconnect = retryPending = false;
                    extensionStatus = true;
                    SetView(View.Disconnected);
                    RefreshSoon();
                }
                else { BindChannels(); UpdateCursor(); Render(); }
            });
        }
        void Connected() => Post(() =>
        {
            if (reconnect || extensionStatus || !Granted(ConsoleCapabilities.Video)) return;
            guestEstablished = true;
            retryBudget = 0;
            retryPending = false;
            SetView(View.Connected);
            frames?.Start();
            UpdateCursor();
        });
        void Login() => Post(() => { if (Surface.FocusState != FocusState.Unfocused) SyncLocks(); });
        void Disconnected(ConsoleDisconnectInfo info) => Post(() =>
        {
            Diagnostics.AppDiagnostics.Write("Console", "Disconnected", $"vmId={id:D} backend={backend} code={info.ErrorCode}");
            if (reconnect || closing) return;
            var failure = ConsoleSessionState.Classify(backend, info.ErrorCode);
            // Core cannot prove that a live RDP logon and the basic console are the same guest session.
            bool allowed = snapshot == null ? !guestEstablished : Catalog.AllowsFallback(id, "RdpEnhanced");
            if (snapshot != null)
            {
                var latest = Catalog.Inspect(id);
                allowed &= latest.Control.Generation == sessionState.Control.Generation;
                sessionState.UpdateGrant(latest);
            }
            if (sessionState.TryFallback(failure, allowed))
            {
                retryPending = false;
                reconnect = true;
                SetView(View.Connecting);
            }
            else
            {
                retryPending = view == View.Connecting && retryBudget > 0 && failure == ConsoleBackendFailure.BackendUnavailable;
                extensionStatus = snapshot != null;
                SetView(View.Disconnected);
            }
            RefreshSoon();
        });
        void BindChannels()
        {
            if (sessionState.Allows(ConsoleCapabilities.Clipboard) && (session.Clipboard ?? session as IConsoleClipboardProvider) is { } channel)
                clipboard ??= new(channel, DispatcherQueue, WinRT.Interop.WindowNative.GetWindowHandle(this), id,
                    () => active && !token.IsCancellationRequested && connection == session && Granted(ConsoleCapabilities.Clipboard), Notify);
            else { clipboard?.Dispose(); clipboard = null; }
            if (sessionState.Allows(ConsoleCapabilities.AudioOutput) && session.AudioOutput is { } output)
                playback ??= new(output, DispatcherQueue, muted, Render, Notify);
            else { playback?.Dispose(); playback = null; }
            if (sessionState.CanInput && sessionState.Allows(ConsoleCapabilities.AudioInput) && session.AudioInput is { } input)
                capture ??= new(input, DispatcherQueue, Render, Notify);
            else { capture?.Dispose(); capture = null; }
        }

        var video = session.Video;
        var cursorSource = session as IConsoleCursorStateSource ?? video as IConsoleCursorStateSource;
        void CursorShape(ConsoleCursor? shape) => Post(() => { cursorShape = shape; receivedShape = true; UpdateCursorShape(); });
        void CursorPosition(ConsoleCursorState state) => Post(() => { cursorState = state; UpdateCursor(); });
        void Rejected(uint _) => Post(ResizeRejected);
        session.Connected += Connected;
        session.LoginCompleted += Login;
        session.Disconnected += Disconnected;
        if (snapshot != null) Catalog.Changed += GrantChanged;
        detachSessionEvents = () =>
        {
            session.Connected -= Connected;
            session.LoginCompleted -= Login;
            session.Disconnected -= Disconnected;
            Catalog.Changed -= GrantChanged;
            if (cursorSource != null) cursorSource.CursorStateChanged -= CursorPosition;
            if (video != null)
            {
                if (frames != null) video.FrameReceived -= frames.Receive;
                video.CursorChanged -= CursorShape;
                video.ResizeRejected -= Rejected;
            }
        };
        frames = new(work => DispatcherQueue.TryEnqueue(() => work()), ShowFrame, session as IConsoleGpuSource ?? video as IConsoleGpuSource, ShowGpuFrame, OnCpuRequested);
        if (video != null)
        {
            video.FrameReceived += frames.Receive;
            video.CursorChanged += CursorShape;
            video.ResizeRejected += Rejected;
        }
        if (cursorSource != null)
        {
            cursorSource.CursorStateChanged += CursorPosition;
            cursorState = cursorSource.CursorState;
        }
        if (snapshot != null)
        {
            var latest = Catalog.Inspect(id);
            sessionState.UpdateGrant(latest);
            if (!ConsoleExtensionCatalog.Ready(latest))
            {
                extensionStatus = true;
                SetView(View.Disconnected);
                RefreshSoon();
                return;
            }
        }
        BindChannels();
        session.Start();
    }

    private bool Granted(ConsoleCapabilities capability)
    {
        if (!sessionState.Allows(capability)) return false;
        if (sessionState.ActiveBackend != ConsoleExtensionCatalog.External) return true;
        var current = Catalog.Inspect(connectedVmId);
        return ConsoleExtensionCatalog.Ready(current) && (current.Supported & current.Granted).HasFlag(capability) &&
            (capability != ConsoleCapabilities.Input || current.Control.HasControl && current.Control.Generation == sessionState.Control.Generation);
    }

    private Task DropConnectionAsync()
    {
        var session = connection;
        if (session == null) return sessions.Drained;
        StopInput();
        connection = null;
        detachSessionEvents?.Invoke();
        detachSessionEvents = null;
        sessionState.Clear();
        appliedModeId = null;
        frames?.Dispose();
        frames = null;
        clipboard?.Dispose();
        clipboard = null;
        playback?.Dispose();
        playback = null;
        capture?.Dispose();
        capture = null;
        cursorState = null;
        cursorShape = null;
        receivedShape = false;
        UpdateCursorShape();
        ClearFrame();
        return sessions.CloseAsync(session);
    }

    private void SetView(View value)
    {
        if (value != View.Connected) StopInput();
        view = value;
        if (value != View.Connected) { ClearFrame(); Surface.ResetCursor(); }
        Render();
    }

    private void SwitchSessionMode(ConsoleSessionMode mode)
    {
        if (closing || !switchWork.IsCompleted) return;
        if (mode == ConsoleSessionMode.RdpEnhanced && !enhancedAvailable) return;
        opening?.Cancel();
        sessionState.ResetFallback();
        sessionState.Preference = mode;
        RememberDisplay();
        if (sessionState.ActiveBackend == mode.ToString() && view == View.Connected) { Render(); return; }
        extensionStatus = false;
        manualWindowSize = false;
        retryPending = false;
        retryBudget = StartupRetryLimit;
        reconnect = true;
        if (powerState == VmState.Running && view != View.Disabled)
        {
            SetView(View.Connecting);
            RefreshSoon();
        }
        else Render();
    }

    private void ResizeRejected()
    {
        if (liveSize is { } size) requestedSize = size;
        RememberDisplay();
        Notify(L10n.Get("Console_ResizeRejected"));
        Render();
    }
}
