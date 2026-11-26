using ExHyperV.Services;

namespace ExHyperV.Server.Hosting;

internal sealed class ConsoleSources(ConsoleDirectory directory)
{
    private sealed record Desktop(Guid Vm, string Backend, string Guest, string Domain);
    private sealed record Key(Desktop Desktop, bool HostCursor, int Width, int Height, uint Scale,
        bool Clipboard, bool AudioOutput, bool AudioInput);
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<Key, ConsoleSource> sources = [];
    private readonly Dictionary<Desktop, ConsoleController> controllers = [];

    internal async Task Serve(SocketPeer peer, ConsoleRequest request, SocketAuthorization grant, ExtensionAccess? access)
    {
        var o = request.Options;
        string unique = Guid.NewGuid().ToString("N");
        string guest = request.Backend == "RdpBasic" ? "basic" : unique;
        string domain = grant.Subject == "loopback" ? "loopback" : "authenticated";
        if (access != null)
        {
            guest = string.IsNullOrWhiteSpace(access.GuestSession) ? unique : access.GuestSession;
            domain = string.IsNullOrWhiteSpace(access.IsolationDomain) ? unique : access.IsolationDomain;
        }
        var desktop = new Desktop(o.VmId, request.Backend, guest, domain);
        var key = new Key(desktop, o.HostCursor, o.Width, o.Height, o.DesktopScale, o.RedirectClipboard, o.RedirectAudioOutput, o.RedirectAudioInput);
        ConsoleSocket? viewer = null;
        ConsoleSource? source = null;
        try
        {
            await gate.WaitAsync(peer.Token);
            try
            {
                peer.Token.ThrowIfCancellationRequested();
                if (!sources.TryGetValue(key, out source))
                {
                    var connection = directory.Create(request, access);
                    if (!controllers.TryGetValue(desktop, out var controller)) controllers.Add(desktop, controller = new());
                    sources.Add(key, source = new(connection, controller));
                }
                source.Users++;
                viewer = new(peer, source, request, grant);
                viewer.Attach();
                source.Start();
            }
            finally { gate.Release(); }
            await viewer.Run();
        }
        finally
        {
            if (source != null)
            {
                await gate.WaitAsync();
                try
                {
                    try { if (viewer != null) await viewer.Detach(); }
                    finally
                    {
                        if (--source.Users == 0)
                        {
                            sources.Remove(key);
                            if (!source.Controller.ResetPending && !sources.Keys.Any(k => k.Desktop == desktop)) controllers.Remove(desktop);
                            try { await source.Connection.DisconnectAsync(); }
                            finally { await source.Connection.DisposeAsync(); }
                        }
                    }
                }
                finally { gate.Release(); }
            }
        }
    }
}

internal sealed class ConsoleController
{
    private readonly object gate = new();
    private ConsoleSocket? holder;
    private static long nextGeneration;
    private long generation, lastInput;
    private bool resetFailed;
    private readonly HashSet<ConsoleSocket> viewers = [];

    internal bool ResetPending { get { lock (gate) return resetFailed; } }

    internal void Join(ConsoleSocket viewer)
    {
        lock (gate)
        {
            if (resetFailed && viewers.Count == 0 && viewer.Has(ConsoleRights.Control))
                resetFailed = viewer.Connection.Input?.ReleaseAllInputs() != true;
            viewers.Add(viewer);
            if (holder == null && !resetFailed && viewer.Has(ConsoleRights.Control)) Assign(viewer);
            else viewer.ControlState(false, generation);
        }
    }
    internal void Leave(ConsoleSocket viewer)
    {
        lock (gate)
        {
            viewers.Remove(viewer);
            if (holder == viewer) Assign(null);
        }
    }
    private void Assign(ConsoleSocket? next)
    {
        var old = holder;
        holder = null;
        generation = Interlocked.Increment(ref nextGeneration);
        lastInput = 0;
        if (old != null)
        {
            resetFailed = true;
            resetFailed = !old.ReleaseHeld();
        }
        if (!resetFailed) holder = next;
        foreach (var viewer in viewers) viewer.ControlState(holder == viewer, generation);
    }
    internal void Take(ConsoleSocket viewer, long expected)
    {
        lock (gate)
        {
            if (!viewers.Contains(viewer) || !viewer.Has(ConsoleRights.Control) || resetFailed || expected != generation)
                throw new UnauthorizedAccessException("无法接管控制权。");
            if (holder != viewer) Assign(viewer);
        }
    }
    internal void Input(ConsoleSocket viewer, ConsoleRights right, long expected, long sequence, Action submit)
    {
        lock (gate)
        {
            if (holder != viewer || !viewer.Has(right) || viewer.V2 && (expected != generation || sequence <= lastInput))
                throw new UnauthorizedAccessException("当前观众没有此控制权限或输入已过期。");
            if (viewer.V2) lastInput = sequence;
            submit();
        }
    }
}

internal sealed class ConsoleSource(IConsoleConnection connection, ConsoleController controller)
{
    internal IConsoleConnection Connection => connection;
    internal ConsoleController Controller => controller;
    internal int Users;
    private bool started;
    private readonly object gate = new();
    // All remaining codec parameters are fixed by ConsoleH264Encoder for the current frame size.
    private readonly Dictionary<(string Encoding, bool Regions), ConsoleMediaGroup> groups = [];

    private Action<ConsoleCursor?>? cursorListeners;
    private ConsoleCursor? lastCursor;
    private bool cursorKnown;

    internal void ObserveCursor(Action<ConsoleCursor?> listener, bool add)
    {
        lock (gate)
        {
            if (connection.Video == null) return;
            if (add)
            {
                if (cursorListeners == null) connection.Video.CursorChanged += CursorChanged;
                cursorListeners += listener;
                if (cursorKnown) listener(lastCursor);
            }
            else
            {
                cursorListeners -= listener;
                if (cursorListeners == null) connection.Video.CursorChanged -= CursorChanged;
            }
        }
    }

    private void CursorChanged(ConsoleCursor? cursor)
    {
        lock (gate) { cursorKnown = true; lastCursor = cursor; cursorListeners?.Invoke(cursor); }
    }

    internal void Start()
    {
        lock (gate)
        {
            if (started) return;
            connection.Start();
            started = true;
            foreach (var group in groups.Values) group.Start();
        }
    }

    internal ConsoleMediaGroup Subscribe(ConsoleSocket viewer, string encoding)
    {
        lock (gate)
        {
            var key = (encoding, viewer.V2 && encoding != "h264");
            if (!groups.TryGetValue(key, out var group))
                groups.Add(key, group = new(connection.Video, encoding, key.Item2));
            viewer.SetGroup(group);
            group.Join(viewer);
            if (started) group.Start();
            return group;
        }
    }

    internal async Task Unsubscribe(ConsoleSocket viewer, ConsoleMediaGroup group)
    {
        Task? stopped = null;
        lock (gate)
        {
            if (group.Leave(viewer))
            {
                groups.Remove((group.Encoding, group.Regions));
                stopped = group.Stop();
            }
        }
        if (stopped != null) await stopped;
    }
}
