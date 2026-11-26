using ExHyperV.Services;

namespace ExHyperV.ConsoleIpc;

internal sealed record IpcAccess(ConsoleExtensionSnapshot Snapshot, IReadOnlyList<ConsoleDisplayMode> Modes,
    string? Guest, string? Domain, ulong SupportedMask, ulong GrantedMask, string Subject, Guid Instance, CancellationToken Revoked)
{
    internal static IpcAccess Offline(string diagnostic = "EXT-001", ConsoleExtensionStateCode host = ConsoleExtensionStateCode.Unreachable)
    {
        var unknown = new ConsoleExtensionStage(1);
        return new(new(0, new((int)host), unknown, unknown, unknown, 0, 0, 0, 0, new(false, false, 0), [], null, diagnostic),
            [], null, null, 0, 0, "", default, default);
    }
}

internal sealed class ConsoleIpcClient(ConsoleIpcDiscovery discovery, int role) : IDisposable
{
    private sealed class Entry
    {
        internal IpcAccess Current = IpcAccess.Offline();
        internal long Expires;
        internal long LastUsed;
        internal Task? Refresh;
        internal CancellationTokenSource Grant = new();
        internal long Revision;
    }

    private readonly object gate = new();
    private readonly Dictionary<(Guid Vm, string Subject), Entry> entries = [];
    private readonly SemaphoreSlim connect = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private ConsoleIpcChannel? control;
    private Timer? retry;
    internal event Action<Guid>? SnapshotChanged;

    private async Task<ConsoleIpcChannel> Control(CancellationToken token)
    {
        await connect.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (gate)
            {
                if (control is { Closed: false }) return control;
                if (control != null) { control = null; Invalidate(null); }
            }
            var pipe = await ConsoleIpcChannel.Open(discovery, role, false, OnPush, token).ConfigureAwait(false);
            lock (gate)
            {
                if (lifetime.IsCancellationRequested) { pipe.Dispose(); throw new ObjectDisposedException(nameof(ConsoleIpcClient)); }
                control = pipe;
            }
            _ = Observe(pipe);
            return pipe;
        }
        finally { connect.Release(); }
    }

    private async Task Observe(ConsoleIpcChannel pipe)
    {
        try { await pipe.Completion.ConfigureAwait(false); } catch (Exception) { }
        lock (gate)
        {
            if (control != pipe) return;
            control = null;
            Invalidate(null);
        }
        SnapshotChanged?.Invoke(Guid.Empty);
    }

    private void OnPush(IpcPacket packet)
    {
        if (packet.Session != 0 || packet.Type != IpcMessage.SnapshotChanged) throw new IpcFailure(IpcFailure.Incompatible);
        using var reader = new IpcReader(packet.Body);
        Guid vm = reader.Id();
        string? subject = reader.BaseStream.Position == reader.BaseStream.Length ? null : reader.Text();
        lock (gate) Invalidate(vm == Guid.Empty ? null : vm, subject);
        SnapshotChanged?.Invoke(vm);
    }

    private void Invalidate(Guid? vm, string? subject = null)
    {
        foreach (var (key, entry) in entries)
        {
            if (vm.HasValue && vm != key.Vm || subject != null && subject != key.Subject) continue;
            entry.Grant.Cancel();
            entry.Grant.Dispose();
            entry.Grant = new();
            entry.Current = IpcAccess.Offline();
            entry.Expires = 0;
            entry.Revision++;
        }
    }

    internal IpcAccess Inspect(Guid vm, string subject, bool wait = false, bool fresh = false)
    {
        Task refresh;
        Entry entry;
        lock (gate)
        {
            if (lifetime.IsCancellationRequested) return IpcAccess.Offline();
            if (role == 1) retry ??= new Timer(_ => RetryOffline(), null, 2000, 2000);
            if (!entries.TryGetValue((vm, subject), out entry!))
            {
                if (entries.Count >= 512)
                {
                    var oldest = entries.MinBy(e => e.Value.Expires);
                    oldest.Value.Revision++;
                    oldest.Value.Grant.Cancel();
                    oldest.Value.Grant.Dispose();
                    entries.Remove(oldest.Key);
                }
                entries.Add((vm, subject), entry = new());
            }
            entry.LastUsed = Environment.TickCount64;
            if (!fresh && entry.LastUsed < entry.Expires) return entry.Current;
            long revision = entry.Revision;
            refresh = entry.Refresh ??= Task.Run(() => Refresh(vm, subject, entry, revision));
        }
        if (wait) refresh.Wait(TimeSpan.FromSeconds(2));
        lock (gate) return Environment.TickCount64 - entry.Expires <= 2000 ? entry.Current : IpcAccess.Offline();
    }

    private void RetryOffline()
    {
        lock (gate)
        {
            long now = Environment.TickCount64;
            foreach (var (key, entry) in entries)
            {
                if (lifetime.IsCancellationRequested || now - entry.LastUsed > 60000 || entry.Current.Instance != Guid.Empty ||
                    entry.Refresh != null || entry.Expires > now) continue;
                long revision = entry.Revision;
                entry.Refresh = Task.Run(() => Refresh(key.Vm, key.Subject, entry, revision));
            }
        }
    }

    private async Task Refresh(Guid vm, string subject, Entry entry, long revision)
    {
        IpcAccess access;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var pipe = await Control(timeout.Token).ConfigureAwait(false);
            using var r = await pipe.Call(IpcMessage.InspectSnapshot, 0, w => Target(w, vm, subject), timeout.Token).ConfigureAwait(false);
            ulong version = r.ReadUInt64();
            ConsoleExtensionStage host = new(r.ReadInt32()), authorization = new(r.ReadInt32()), guest = new(r.ReadInt32()), transport = new(r.ReadInt32());
            var kind = (ConsoleTransportKind)r.ReadInt32();
            if (kind == ConsoleTransportKind.GpuShared && (pipe.Features & 2ul) == 0) throw new IpcFailure(IpcFailure.Incompatible);
            ulong epoch = r.ReadUInt64();
            ulong supportedMask = r.ReadUInt64();
            ulong grantedMask = r.ReadUInt64();
            var supported = (ConsoleCapabilities)(uint)supportedMask;
            var granted = (ConsoleCapabilities)(uint)grantedMask;
            var state = ReadControl(r);
            var modes = ReadModes(r);
            string? session = r.Text(), domain = r.Text(), reason = r.SafeReason(), diagnostic = r.Diagnostic();
            if (state.CanView && granted.HasFlag(ConsoleCapabilities.DisplayResize))
            {
                using var list = await pipe.Call(IpcMessage.GetModes, 0, w =>
                {
                    Target(w, vm, subject);
                    w.Write(version);
                }, timeout.Token).ConfigureAwait(false);
                modes = ReadModes(list);
            }
            access = new(new(version, host, authorization, guest, transport, kind, epoch, supported, granted, state, [], reason, diagnostic),
                modes, session, domain, supportedMask, grantedMask, subject, pipe.Instance, default);
        }
        catch (UnauthorizedAccessException e) { access = IpcAccess.Offline(e.Message == "EXT-TRUST-01" ? e.Message : "EXT-TRUST-02", ConsoleExtensionStateCode.AccessDenied); }
        catch (IpcFailure e) when (e.Code == IpcFailure.Incompatible) { access = IpcAccess.Offline("EXT-IPC-02", ConsoleExtensionStateCode.Incompatible); }
        catch (IpcFailure e) when (e.Code == IpcFailure.Denied) { access = IpcAccess.Offline("EXT-403", ConsoleExtensionStateCode.AccessDenied); }
        catch (Exception) { access = IpcAccess.Offline(); }
        bool changed;
        lock (gate)
        {
            entry.Refresh = null;
            if (entry.Revision != revision || lifetime.IsCancellationRequested) return;
            var prev = entry.Current.Snapshot;
            var next = access.Snapshot;
            changed = access.Instance != entry.Current.Instance || next.Version != prev.Version ||
                next.Host.RawCode != prev.Host.RawCode || next.Authorization.RawCode != prev.Authorization.RawCode ||
                next.Guest.RawCode != prev.Guest.RawCode || next.Transport.RawCode != prev.Transport.RawCode ||
                next.TransportKind != prev.TransportKind || next.SurfaceEpoch != prev.SurfaceEpoch ||
                access.SupportedMask != entry.Current.SupportedMask || access.GrantedMask != entry.Current.GrantedMask ||
                next.Control != prev.Control || next.Reason != prev.Reason || next.DiagnosticId != prev.DiagnosticId;
            if (changed)
            {
                entry.Grant.Cancel();
                entry.Grant.Dispose();
                entry.Grant = new();
            }
            entry.Current = access with { Revoked = entry.Grant.Token };
            entry.Expires = Environment.TickCount64 + 1000;
        }
        if (changed) SnapshotChanged?.Invoke(vm);
    }

    private void Target(BinaryWriter w, Guid vm, string subject)
    {
        w.Write(vm.ToByteArray());
        w.Text(subject);
        w.Write(role == 1);
    }

    internal static ConsoleControlState ReadControl(IpcReader r) => new(r.Flag(), r.Flag(), r.ReadUInt64());
    internal static ConsoleDisplayMode ReadMode(IpcReader r) => new(r.String(), r.Count(8192), r.Count(8192), r.ReadUInt32(), r.ReadUInt32());
    internal static ConsoleDisplayMode[] ReadModes(IpcReader r)
    {
        var modes = new ConsoleDisplayMode[r.Count(256)];
        for (int i = 0; i < modes.Length; i++) modes[i] = ReadMode(r);
        return modes;
    }

    internal bool AllowsFallback(Guid vm, string backend, string subject)
    {
        ConsoleIpcChannel? pipe;
        lock (gate) pipe = control;
        if (pipe == null || backend != "RdpEnhanced") return false;
        using var r = pipe.Call(IpcMessage.AllowsFallback, 0, w => { Target(w, vm, subject); w.Text(backend); }).GetAwaiter().GetResult();
        return r.Flag();
    }

    internal ConsoleIpcSession Session(ConsoleConnectionOptions options, IpcAccess access)
    {
        if (options.VmId == Guid.Empty || options.ConnectTimeout <= TimeSpan.Zero || options.ConnectTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(options));
        var status = access.Snapshot;
        if (access.Instance == Guid.Empty || access.Revoked.IsCancellationRequested || !status.Control.CanView ||
            status.Host.Code != ConsoleExtensionStateCode.Ready || status.Authorization.Code != ConsoleExtensionStateCode.Ready ||
            status.Guest.Code != ConsoleExtensionStateCode.Ready || !(status.Supported & status.Granted).HasFlag(ConsoleCapabilities.Video))
            throw new UnauthorizedAccessException();
        return new(discovery, role, options, access, next =>
        {
            if (role != 1) return;
            lock (gate)
            {
                if (!entries.TryGetValue((options.VmId, access.Subject), out var entry) ||
                    entry.Current.Instance != access.Instance || entry.Grant.Token != access.Revoked) return;
                var s = entry.Current.Snapshot;
                entry.Current = entry.Current with { Snapshot = new(s.Version, s.Host, s.Authorization, s.Guest, s.Transport,
                    s.TransportKind, s.SurfaceEpoch, s.Supported, s.Granted, next, s.AvailableActions, s.Reason, s.DiagnosticId) };
            }
            SnapshotChanged?.Invoke(options.VmId);
        });
    }

    public void Dispose()
    {
        lock (gate)
        {
            lifetime.Cancel();
            retry?.Dispose();
            control?.Dispose();
            Invalidate(null);
            foreach (var entry in entries.Values) entry.Grant.Dispose();
            entries.Clear();
        }
    }
}
