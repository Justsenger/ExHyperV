using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ExHyperV.Services;
using ExHyperV.Tools;

internal static class ConsoleConnectionChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static ConsoleConnectionOptions Options() => new() { VmId = Guid.NewGuid() };

    public static async Task RunAsync(Action<bool, string> check)
    {
        CheckValidation(check);
        CheckClipboardPayloads(check);
        await CheckConsoleStateQuery(check);
        await CheckIndependentConnections(check);
        await CheckFrameLeases(check);
        await CheckInput(check);
        await CheckShutdown(check);
        await CheckStopDuringCreation(check);
        await CheckFailures(check);
        await CheckCallbackDisposal(check);
        await CheckSendShutdownRace(check);
        await CheckConnectTimeout(check);
        await CheckResizeRejection(check);
        await CheckClipboardRevision(check);
        await CheckSpecialKeyboardInput(check);
    }

    internal static async Task CheckConsoleStateQuery(Action<bool, string> check)
    {
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();
        int enhancedQueries = 0;
        Guid enhancedId = Guid.Empty;
        Task<ApiResponse<bool>> Enhanced(Guid id) { enhancedQueries++; enhancedId = id; return Task.FromResult(ApiResponse<bool>.Ok(true)); }
        Task<ApiResponse<List<(Guid Id, string Name, ushort State)>>> Rows(params (Guid Id, string Name, ushort State)[] rows)
            => Task.FromResult(ApiResponse<List<(Guid Id, string Name, ushort State)>>.Ok(rows.ToList()));

        var renamed = await VmConsoleSupportService.GetConsoleStateAsync(target, () => Rows((target, "Renamed", 2)), Enhanced);
        check(renamed.HasData && renamed.Data is { Name: "Renamed", IsRunning: true, IsEnhancedAvailable: true } && enhancedId == target,
            "Console state resolves the current display name while enhanced availability uses the fixed GUID");
        var duplicateName = await VmConsoleSupportService.GetConsoleStateAsync(target,
            () => Rows((other, "SAME NAME", 2), (target, "Same name", 3)), Enhanced);
        check(duplicateName.HasData && duplicateName.Data is { Name: "Same name", IsRunning: false } && duplicateName.Data.VmId == target,
            "An active VM with the same name cannot replace the requested identity");
        var alphabetical = await VmConsoleSupportService.GetConsoleStateAsync(target,
            () => Rows((other, "Alpha", 2), (target, "Zulu", 2)), Enhanced);
        check(alphabetical.HasData && alphabetical.Data.Name == "Zulu", "Alphabetical ordering cannot replace the requested identity");

        bool activityMatches = true;
        foreach (ushort code in new ushort[] { 0, 2, 3, 4, 6, 10, 32768, 32770, 32771, 32772, 32773, 32774, 32776, 32777, 32780, 32781, 32782, 32783 })
        {
            int before = enhancedQueries;
            var state = await VmConsoleSupportService.GetConsoleStateAsync(target, () => Rows((target, "Current", code)), Enhanced);
            bool active = VmMapper.IsActiveState(code);
            activityMatches &= state.HasData && state.Data.IsRunning == active && state.Data.IsEnhancedAvailable == active
                && enhancedQueries - before == (active ? 1 : 0);
        }
        check(activityMatches, "Console state retains the active-state whitelist and queries enhanced availability only for active VMs");

        int beforeMissing = enhancedQueries;
        var missing = await VmConsoleSupportService.GetConsoleStateAsync(target, () => Rows((other, "Unrelated", 2)), Enhanced);
        var empty = await VmConsoleSupportService.GetConsoleStateAsync(target, () => Rows(), Enhanced);
        var failed = await VmConsoleSupportService.GetConsoleStateAsync(target,
            () => Task.FromResult(ApiResponse<List<(Guid Id, string Name, ushort State)>>.Fail("query failure", 5, ApiErrorSource.Wmi, kind: ApiErrorKind.AccessDenied)), Enhanced);
        var duplicateId = await VmConsoleSupportService.GetConsoleStateAsync(target, () => Rows((target, "A", 2), (target, "B", 3)), Enhanced);
        check(missing.ErrorKind == ApiErrorKind.NotFound && empty.ErrorKind == ApiErrorKind.NotFound
            && failed.ErrorKind == ApiErrorKind.AccessDenied && failed.Code == 5 && failed.ErrorSource == ApiErrorSource.Wmi
            && duplicateId.ErrorKind == ApiErrorKind.Conflict && enhancedQueries == beforeMissing,
            "Missing identity, duplicate identity and query failure preserve distinct structured errors");
        var unavailable = await VmConsoleSupportService.GetConsoleStateAsync(target, () => Rows((target, "Current", 2)), _ => Task.FromResult(ApiResponse<bool>.Ok(false)));
        var enhancedFailure = await VmConsoleSupportService.GetConsoleStateAsync(target, () => Rows((target, "Current", 2)), _ => Task.FromResult(ApiResponse<bool>.Fail("enhanced query failed", kind: ApiErrorKind.AccessDenied)));
        check(unavailable.HasData && unavailable.Data is { IsRunning: true, IsEnhancedAvailable: false }
            && !enhancedFailure.Success && enhancedFailure.ErrorKind == ApiErrorKind.AccessDenied,
            "False enhanced availability remains distinct from a failed query");

        var savedContext = SynchronizationContext.Current;
        var savedCulture = CultureInfo.CurrentCulture;
        var savedUiCulture = CultureInfo.CurrentUICulture;
        SynchronizationContext? queryContext = new();
        string? queryCulture = null, queryUiCulture = null;
        Task<ApiResponse<VmConsoleState>> pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-CN");
            pending = VmConsoleSupportService.GetConsoleStateAsync(target, () =>
            {
                queryContext = SynchronizationContext.Current;
                queryCulture = CultureInfo.CurrentCulture.Name;
                queryUiCulture = CultureInfo.CurrentUICulture.Name;
                return Rows((target, "Current", 2));
            }, Enhanced);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(savedContext);
            CultureInfo.CurrentCulture = savedCulture;
            CultureInfo.CurrentUICulture = savedUiCulture;
        }
        await pending;
        check(queryContext == null, "Console status work leaves the caller synchronization context");
        check(queryCulture == "tr-TR" && queryUiCulture == "zh-CN", "Background console polling preserves caller culture");
    }
    internal static void CheckValidation(Action<bool, string> check)
    {
        var descriptor = typeof(FreeRdpNative).GetNestedType("FrameDescriptor", BindingFlags.NonPublic)!;
        check(Marshal.SizeOf(descriptor) == 64 && Marshal.OffsetOf(descriptor, "Pixels") == 24
            && Marshal.OffsetOf(descriptor, "Sequence") == 32 && Marshal.OffsetOf(descriptor, "BaseSequence") == 40
            && Marshal.OffsetOf(descriptor, "Left") == 48 && Marshal.OffsetOf(descriptor, "Bottom") == 60,
            "Console retained-frame descriptor matches the 64-byte native ABI on the current architecture");
        var backend = new FakeBackend();
        check(Throws<ArgumentException>(() => new FreeRdpConsoleConnection(Options() with { VmId = Guid.Empty }, true, backend)),
            "Console requires an explicit VM identity without starting native code");
        check(Throws<ArgumentOutOfRangeException>(() => new FreeRdpConsoleConnection(Options() with { Width = int.MaxValue }, true, backend)) &&
            Throws<ArgumentOutOfRangeException>(() => new FreeRdpConsoleConnection(Options() with { Width = 8192, Height = 8192 }, true, backend)) &&
            Throws<ArgumentOutOfRangeException>(() => new FreeRdpConsoleConnection(Options() with { Height = 0 }, true, backend)),
            "Console rejects invalid and over-budget initial frame dimensions");
        check(Throws<ArgumentOutOfRangeException>(() => new FreeRdpConsoleConnection(Options() with { DesktopScale = 99 }, true, backend)) &&
            Throws<ArgumentOutOfRangeException>(() => new FreeRdpConsoleConnection(Options() with { DesktopScale = 501 }, true, backend)) &&
            Throws<ArgumentOutOfRangeException>(() => new FreeRdpConsoleConnection(Options() with { ConnectTimeout = TimeSpan.Zero }, true, backend)) &&
            Throws<ArgumentOutOfRangeException>(() => new FreeRdpConsoleConnection(Options() with { ConnectTimeout = TimeSpan.FromMinutes(3) }, true, backend)),
            "Console validates DPI and finite connection timeout before allocating native resources");
        check(!ConsolePayload.IsFrame(new byte[16], 1, 1) && !ConsolePayload.IsFrame([], uint.MaxValue, uint.MaxValue) &&
            !ConsolePayload.IsFrame([], 8192, 8192) && !ConsolePayload.IsFrame([], 0, 1) && ConsolePayload.IsFrame(new byte[16], 2, 2),
            "Console frame admission requires exact bounded BGRA storage and checks multiplication before use");
    }

    internal static void CheckClipboardPayloads(Action<bool, string> check)
    {
        var text = Encoding.Unicode.GetBytes("第一行\r\nclipboard\0");
        var dib = new byte[] { 40, 0, 0, 0, 1, 2, 3, 4 };
        var html = Encoding.UTF8.GetBytes("Version:0.9\r\n<!--StartFragment-->文本<!--EndFragment-->");
        var input = new ConsoleClipboardData([new(13, null, text), new(8, null, dib), new(0xc001, "HTML Format", html)]);
        check(ConsolePayload.TryWriteClipboard(input, out var encoded) && ConsolePayload.TryReadClipboard(encoded, out var decoded) &&
            decoded.Formats.Count == 3 && decoded.Formats[0].Data.Span.SequenceEqual(text) &&
            decoded.Formats[1].Data.Span.SequenceEqual(dib) && decoded.Formats[2].Data.Span.SequenceEqual(html) && decoded.Formats[2].Name == "HTML Format",
            "Console clipboard preserves Unicode, bitmap and registered HTML bytes without a host clipboard API");
        var independent = encoded.ToArray();
        text[0] = 0;
        check(encoded.SequenceEqual(independent), "Console outgoing clipboard encoding owns its snapshot independently of caller buffers");
        check(ConsolePayload.TryWriteClipboard(new([]), out var empty) && empty.SequenceEqual(new byte[4]) &&
            ConsolePayload.TryReadClipboard(empty, out var emptyClipboard) && emptyClipboard.Formats.Count == 0,
            "Console empty clipboard snapshots have an explicit valid wire representation");
        check(!ConsolePayload.TryWriteClipboard(new([new(13, null, new byte[] { 65, 0 })]), out _) &&
            !ConsolePayload.TryWriteClipboard(new([new(13, null, new byte[] { 0 })]), out _) &&
            !ConsolePayload.TryWriteClipboard(new([new(13, null, Array.Empty<byte>())]), out _),
            "Console refuses unterminated, odd-length and missing Unicode clipboard payloads");
        check(!ConsolePayload.TryWriteClipboard(new([new(15, null, new byte[4])]), out _) &&
            !ConsolePayload.TryWriteClipboard(new([new(13, "HTML Format", new byte[2])]), out _) &&
            !ConsolePayload.TryWriteClipboard(new([new(0xc000, "arbitrary format", new byte[2])]), out _),
            "Console explicitly rejects unsupported file-drop and arbitrary clipboard formats");
        check(!ConsolePayload.TryWriteClipboard(new([new(13, null, new byte[2]), new(13, null, new byte[2])]), out _) &&
            !ConsolePayload.TryWriteClipboard(new([new(0xc001, "HTML Format", html), new(0xc002, "HTML Format", html)]), out _),
            "Console rejects duplicate standard and duplicate registered clipboard categories");
        check(!ConsolePayload.TryReadClipboard(encoded[..^1], out _) && !ConsolePayload.TryReadClipboard([.. encoded, 0], out _) &&
            !ConsolePayload.TryReadClipboard([4, 0, 0, 0], out _) && !ConsolePayload.TryReadClipboard([], out _),
            "Console clipboard parser rejects truncation, trailing bytes and excess format count");
        var broken = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(broken, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(4), 13);
        BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(broken.AsSpan(12), uint.MaxValue);
        check(!ConsolePayload.TryReadClipboard(broken, out _), "Console clipboard length overflow is rejected without allocating the claimed payload");
        var invalidUtf8 = new byte[17];
        BinaryPrimitives.WriteUInt32LittleEndian(invalidUtf8, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(invalidUtf8.AsSpan(4), 0xc000);
        BinaryPrimitives.WriteUInt32LittleEndian(invalidUtf8.AsSpan(8), 1);
        invalidUtf8[16] = 0xff;
        check(!ConsolePayload.TryReadClipboard(invalidUtf8, out _), "Console rejects invalid UTF-8 names instead of aliasing an unsupported format");
    }

    internal static async Task CheckIndependentConnections(Action<bool, string> check)
    {
        var aBackend = new FakeBackend();
        var bBackend = new FakeBackend();
        await using var a = new FreeRdpConsoleConnection(Options(), true, aBackend);
        await using var b = new FreeRdpConsoleConnection(Options(), true, bBackend);
        var aFrames = new List<ConsoleFrame>(); var bFrames = new List<ConsoleFrame>();
        var aClipboard = new List<ConsoleClipboardData>(); var bClipboard = new List<ConsoleClipboardData>();
        a.FrameReceived += frame => aFrames.Add(frame.Retain()); b.FrameReceived += frame => bFrames.Add(frame.Retain());
        a.ClipboardReceived += aClipboard.Add; b.ClipboardReceived += bClipboard.Add;
        await Start(a, aBackend); await Start(b, bBackend);
        aBackend.Emit(3, [1, 2, 3, 255], 1, 1);
        bBackend.Emit(3, [9, 8, 7, 255, 6, 5, 4, 255], 2, 1);
        aBackend.Emit(3, [3, 2, 1, 255], 1, 1);
        check(aFrames.Count == 2 && bFrames.Count == 1 && aFrames[0].Pixels[0] == 1 &&
            aFrames[0].Sequence < aFrames[1].Sequence && bFrames[0].Sequence == 1 && bFrames[0].Stride == 8,
            "Console simultaneous connections retain independent frames, sequence counters and dimensions");
        ConsolePayload.TryWriteClipboard(new([new(13, null, Encoding.Unicode.GetBytes("A\0"))]), out var clipboard);
        aBackend.Emit(4, clipboard);
        check(aClipboard.Count == 1 && bClipboard.Count == 0 && Encoding.Unicode.GetString(aClipboard[0].Formats[0].Data.Span) == "A\0",
            "Console remote clipboard contents are delivered only to their owning connection");
        await a.DisconnectAsync().WaitAsync(Timeout);
        check(aBackend.Native.Disposals == 1 && bBackend.Native.Disposals == 0 && b.SendKey(30, false, true),
            "Console stopping one VM connection leaves another connection and its input stream usable");
        aBackend.Emit(1, []); aBackend.Emit(2, []); aBackend.Emit(3, new byte[4], 1, 1); aBackend.Emit(4, clipboard);
        check(a.State == ConsoleConnectionState.Disconnected && aFrames.Count == 2 && aClipboard.Count == 1,
            "Console callbacks arriving after teardown cannot revive a connection or publish stale content");
        foreach (var frame in aFrames.Concat(bFrames)) frame.Dispose();
        check(aBackend.FramesReleased == aBackend.FramesCreated && bBackend.FramesReleased == bBackend.FramesCreated,
            "Console independent consumers return every explicitly retained frame");
    }

    internal static async Task CheckFrameLeases(Action<bool, string> check)
    {
        var backend = new FakeBackend();
        await using var connection = new FreeRdpConsoleConnection(Options(), true, backend);
        ConsoleFrame? borrowed = null, first = null, second = null;
        byte[]? snapshot = null;
        bool secondObserverRead = false;
        connection.FrameReceived += frame =>
        {
            borrowed = frame; first = frame.Retain(); snapshot = frame.CopyPixels();
            frame.Dispose();
            throw new InvalidOperationException("An observer disposed its own borrowed wrapper");
        };
        connection.FrameReceived += frame => { secondObserverRead = frame.Pixels[0] == 17; second = frame.Retain(); };
        await Start(connection, backend);
        backend.Emit(3, [17, 23, 31, 255], 1, 1);
        try
        {
            check(secondObserverRead && first!.Pixels[0] == 17 && second!.Pixels[0] == 17 && backend.FramesReleased == 0,
                "Console observer disposal and exceptions cannot invalidate another observer or its retained pixels");
            check(Throws<ObjectDisposedException>(() => _ = borrowed!.Pixels.Length) &&
                Throws<ObjectDisposedException>(() => borrowed!.Retain()),
                "Console callback-borrowed wrappers cannot be read or retained after callback completion");
            snapshot![0] = 99;
            check(first!.Pixels[0] == 17, "Console CopyPixels returns an independent explicitly allocated snapshot");
            await connection.DisconnectAsync().WaitAsync(Timeout);
            check(first.Pixels[0] == 17 && second!.Pixels[0] == 17,
                "Console retained pixels remain readable after native connection cleanup");
            first.Dispose(); first.Dispose();
            check(backend.FramesReleased == 0 && second!.Pixels[0] == 17,
                "Console duplicate disposal of one lease cannot release another lease's buffer");
            second!.Dispose();
            check(backend.FramesReleased == 1 && snapshot[0] == 99,
                "Console last lease returns the buffer exactly once while copied snapshots survive");
            backend.Emit(3, [1, 2, 3, 255], 1, 1);
            check(backend.FramesReleased == 2, "Console a late frame after shutdown is promptly returned without publication");
        }
        finally { first?.Dispose(); second?.Dispose(); }

        var unobserved = new FakeBackend();
        await using var noSubscriber = new FreeRdpConsoleConnection(Options(), true, unobserved);
        await Start(noSubscriber, unobserved);
        unobserved.Emit(3, [1, 2, 3, 255], 1, 1);
        unobserved.Emit(3, [1, 2, 3, 255], uint.MaxValue, 1);
        check(unobserved.FramesCreated == 2 && unobserved.FramesReleased == 2,
            "Console unobserved and malformed frames return their buffers without allocating managed snapshots");

        int releases = 0;
        using var raced = new ConsoleFrame(1, 1, 4, new byte[4], 1, 0, new(0, 0, 1, 1), true,
            new FrameOwner(() => Interlocked.Increment(ref releases)));
        Parallel.For(0, 100, index =>
        {
            try { using var retained = raced.Retain(); if (index == 50) raced.Dispose(); _ = retained.Pixels.Length; }
            catch (ObjectDisposedException) { }
        });
        raced.Dispose();
        check(releases == 1, "Console concurrent retain/dispose never frees a buffer more than once");

        int nativeReleases = 0;
        var nativeOwner = new TestFrameHandle([51, 61, 71, 255], () => Interlocked.Increment(ref nativeReleases));
        using var nativeFrame = new ConsoleFrame(1, 1, 4, nativeOwner.DangerousGetHandle(), 4, nativeOwner,
            1, 0, new(0, 0, 1, 1), true);
        using var nativeRetained = nativeFrame.Retain();
        nativeFrame.Dispose();
        check(nativeRetained.Pixels[1] == 61 && nativeRetained.CopyPixels().SequenceEqual(new byte[] { 51, 61, 71, 255 })
            && nativeReleases == 0, "Console native-backed spans and explicit copies remain valid until the last lease is released");
        nativeRetained.Dispose();
        check(nativeReleases == 1 && nativeOwner.IsClosed, "Console the last lease closes its native SafeHandle exactly once");

        int finalized = 0;
        var forgotten = ForgetNativeLease(() => Interlocked.Increment(ref finalized));
        for (int attempt = 0; attempt < 3 && Volatile.Read(ref finalized) == 0; attempt++)
        { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
        check(!forgotten.IsAlive && finalized == 1,
            "Console unreachable forgotten leases eventually return their native buffer through SafeHandle finalization");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ForgetNativeLease(Action release)
    {
        var owner = new TestFrameHandle([1, 2, 3, 255], release);
        using var frame = new ConsoleFrame(1, 1, 4, owner.DangerousGetHandle(), 4, owner, 1, 0, new(0, 0, 1, 1), true);
        return new WeakReference(frame.Retain());
    }

    private sealed class TestFrameHandle : SafeHandle
    {
        private readonly Action release;
        internal TestFrameHandle(byte[] bytes, Action release) : base(0, true)
        { this.release = release; SetHandle(Marshal.AllocHGlobal(bytes.Length)); Marshal.Copy(bytes, 0, handle, bytes.Length); }
        public override bool IsInvalid => handle == 0;
        protected override bool ReleaseHandle() { Marshal.FreeHGlobal(handle); release(); return true; }
    }

    internal static async Task CheckInput(Action<bool, string> check)
    {
        var backend = new FakeBackend();
        await using var connection = new FreeRdpConsoleConnection(Options(), true, backend);
        check(!connection.SendKey(30, false, true) && !connection.SetClipboard(new([])), "Console rejects input before a connection exists");
        await Start(connection, backend);
        check(!connection.SendPointer(0, 0), "Console does not guess pointer coordinates before the first actual frame");
        backend.Emit(3, new byte[4 * 4 * 4], 4, 4);
        check(connection.SendKey(0x1d, true, true) && connection.SendKey(0x1d, true, false),
            "Console forwards both extended key down and key up through Core");
        var keys = backend.Native.Sent.Where(s => s.Kind == 1).ToArray();
        check(keys.Length == 2 && keys[0].Bytes.SequenceEqual(new byte[] { 0x1d, 0, 1, 1 }) && keys[1].Bytes.SequenceEqual(new byte[] { 0x1d, 0, 1, 0 }),
            "Console input preserves scan code and release flags without converting them to text");
        int before = backend.Native.Sent.Count;
        check(!connection.SendKey(0, false, true) && !connection.SendKey(256, false, true) && !connection.SendPointer(4, 0) &&
            !connection.SendPointer(0, 4) && !connection.SendPointer(0, 0, (ConsolePointerButton)3) &&
            !connection.SendPointer(0, 0, down: true) && !connection.SendPointer(0, 0, ConsolePointerButton.Left, wheel: 120) &&
            !connection.SendPointer(0, 0, wheel: 256) && before == backend.Native.Sent.Count,
            "Console rejects out-of-bounds pointer events and invalid key/button/wheel combinations before native dispatch");
        check(connection.SendPointer(3, 2, ConsolePointerButton.Right, true) && connection.SendPointer(3, 2, wheel: -120),
            "Console accepts valid button and signed wheel input at remote pixel coordinates");
        var pointer = backend.Native.Sent.Where(s => s.Kind == 2).ToArray();
        check(BinaryPrimitives.ReadUInt16LittleEndian(pointer[0].Bytes.AsSpan(4)) == 10 &&
            BinaryPrimitives.ReadInt16LittleEndian(pointer[1].Bytes.AsSpan(6)) == -120,
            "Console pointer payload preserves the right-button down and negative wheel direction");
        backend.Native.AcceptInput = false;
        int sentBeforeRelease = backend.Native.Sent.Count;
        check(connection.SendKey(30, false, false) && backend.Native.Sent.Count == sentBeforeRelease, "Console accepts a refused release into the bounded retry queue");
        backend.Native.AcceptInput = true;
        check(connection.SendKey(31, false, true) && backend.Native.Sent.Skip(sentBeforeRelease).Take(2).Select(s => s.Bytes[0]).SequenceEqual(new byte[] { 30, 31 }), "A queued release is sent before the next key press");
        check(connection.Resize(1920, 1080, 150) && !connection.Resize(8192, 8192) && !connection.Resize(1920, 1080, 501),
            "Console enhanced resize validates bounds and forwards the requested DPI independently of frame confirmation");
        var resize = backend.Native.Sent.Last(s => s.Kind == 3).Bytes;
        check(BinaryPrimitives.ReadUInt32LittleEndian(resize) == 1920 && BinaryPrimitives.ReadUInt32LittleEndian(resize.AsSpan(4)) == 1080 &&
            BinaryPrimitives.ReadUInt32LittleEndian(resize.AsSpan(8)) == 150 && !connection.SendPointer(5, 2),
            "Console queued resize does not change accepted pointer bounds before the guest sends a new frame");
        check(!connection.SetClipboard(new([])), "Console clipboard remains unavailable until the enhanced guest channel reports ready");
        backend.Emit(5, [], 1, 0);
        check(connection.SetClipboard(new([new(13, null, Encoding.Unicode.GetBytes("local\0"))])) &&
            backend.Native.Sent.Last().Kind == 4, "Console publishes an explicitly supplied clipboard snapshot through the native channel");
        backend.Emit(5, [], 0, 0);
        check(!connection.SetClipboard(new([])), "Console immediately refuses clipboard writes after guest channel closure");

        var basicBackend = new FakeBackend();
        await using var basic = new FreeRdpConsoleConnection(Options(), false, basicBackend);
        int received = 0; basic.ClipboardReceived += _ => received++;
        await Start(basic, basicBackend); basicBackend.Emit(5, [], 1, 0); basicBackend.Emit(4, new byte[4]);
        check(!basic.Resize(1280, 720) && !basic.SetClipboard(new([])) && received == 0,
            "Console basic mode does not advertise enhanced display or clipboard capabilities");
    }

    internal static async Task CheckShutdown(Action<bool, string> check)
    {
        var backend = new FakeBackend { DelayExit = true };
        await using var connection = new FreeRdpConsoleConnection(Options(), true, backend);
        int lateFrames = 0; connection.FrameReceived += _ => lateFrames++;
        var disconnected = NewSignal<bool>();
        connection.Disconnected += _ => disconnected.TrySetResult(backend.Native.Disposals == 1 && connection.State == ConsoleConnectionState.Disconnected);
        await Start(connection, backend);
        try
        {
            var first = connection.DisconnectAsync(); var repeated = connection.DisconnectAsync();
            check(ReferenceEquals(first, repeated) && !first.IsCompleted && backend.Native.Disposals == 0 && connection.State == ConsoleConnectionState.Disconnecting,
                "Console repeated shutdown shares completion and waits while the native transport is still running");
            backend.Emit(3, new byte[4], 1, 1);
            check(!connection.SendKey(30, false, true) && lateFrames == 0,
                "Console stopping blocks new input and ignores callbacks before native teardown completes");
            backend.Native.Exit.Set();
            await first.WaitAsync(Timeout);
            check(await disconnected.Task.WaitAsync(Timeout), "Console emits disconnection only after releasing the native connection");
            connection.Dispose(); await connection.DisposeAsync();
            check(backend.Native.Disposals == 1 && Throws<InvalidOperationException>(connection.Start),
                "Console disposal is idempotent and a stopped connection cannot silently reopen");
        }
        finally { backend.Native.Exit.Set(); }
        using var unused = new FreeRdpConsoleConnection(Options(), true, new FakeBackend());
        await unused.DisposeAsync();
        check(unused.State == ConsoleConnectionState.Disconnected && Throws<InvalidOperationException>(unused.Start),
            "Console disposing before Start allocates no connection and permanently closes the object");
    }

    internal static async Task CheckStopDuringCreation(Action<bool, string> check)
    {
        var backend = new FakeBackend { DelayCreate = true };
        await using var connection = new FreeRdpConsoleConnection(Options(), true, backend);
        connection.Start(); await backend.CreateEntered.Task.WaitAsync(Timeout);
        try
        {
            var closing = connection.DisconnectAsync();
            check(!closing.IsCompleted && connection.State == ConsoleConnectionState.Disconnecting,
                "Console cancellation during native initialization waits for the owned initialization to return");
            backend.CreateRelease.Set();
            await closing.WaitAsync(Timeout);
            check(backend.Native.Stops > 0 && backend.Native.Disposals == 1 && connection.State == ConsoleConnectionState.Disconnected,
                "Console a connection created after cancellation is stopped and released before shutdown completes");
        }
        finally { backend.CreateRelease.Set(); backend.Native.Exit.Set(); }
    }

    internal static async Task CheckFailures(Action<bool, string> check)
    {
        var failed = new FakeBackend { CreateFailure = true };
        await using var connection = new FreeRdpConsoleConnection(Options(), true, failed);
        var result = NewSignal<ConsoleDisconnectInfo>(); connection.Disconnected += r => result.TrySetResult(r);
        connection.Start(); var error = await result.Task.WaitAsync(Timeout);
        check(error.ErrorCode != 0 && error.Error?.Contains("expected create failure", StringComparison.Ordinal) == true &&
            connection.State == ConsoleConnectionState.Disconnected, "Console native-load failure becomes a completed connection error instead of a stranded connecting state");
        var backend = new FakeBackend { RunFailure = true };
        await using var running = new FreeRdpConsoleConnection(Options(), true, backend);
        var runResult = NewSignal<ConsoleDisconnectInfo>(); running.Disconnected += r => runResult.TrySetResult(r);
        running.Start(); var runError = await runResult.Task.WaitAsync(Timeout);
        check(runError.ErrorCode != 0 && backend.Native.Disposals == 1,
            "Console a transport exception still releases its native connection exactly once");
        var observers = new FakeBackend();
        await using var observed = new FreeRdpConsoleConnection(Options(), true, observers);
        int connected = 0, loggedIn = 0, frames = 0, clipboards = 0;
        observed.Connected += () => throw new InvalidOperationException("observer"); observed.Connected += () => connected++;
        observed.LoginCompleted += () => loggedIn++;
        observed.FrameReceived += _ => throw new InvalidOperationException("observer"); observed.FrameReceived += _ => frames++;
        observed.ClipboardReceived += _ => clipboards++;
        await Start(observed, observers); observers.Emit(1, []); observers.Emit(2, []);
        observers.Emit(3, new byte[4], 1, 1); observers.Emit(3, new byte[4], uint.MaxValue, 1); observers.Emit(4, [255, 0, 0, 0]);
        check(connected == 1 && loggedIn == 1 && frames == 1 && clipboards == 0 && observed.State == ConsoleConnectionState.Connected,
            "Console isolates observer exceptions, deduplicates connected events and rejects malformed frame/clipboard callbacks");
    }

    internal static async Task CheckCallbackDisposal(Action<bool, string> check)
    {
        var backend = new FakeBackend();
        await using var connection = new FreeRdpConsoleConnection(Options(), true, backend);
        var disposed = NewSignal<bool>();
        connection.Connected += () => { connection.Dispose(); disposed.TrySetResult(true); };
        connection.Start(); await backend.Native.RunEntered.Task.WaitAsync(Timeout);
        backend.Emit(1, []);
        check(await disposed.Task.WaitAsync(Timeout), "Console Dispose can request stop from an event callback without waiting on that callback thread");
        await connection.DisconnectAsync().WaitAsync(Timeout);
        check(backend.Native.Disposals == 1, "Console callback-triggered stop still completes native cleanup");
    }

    internal static async Task CheckSendShutdownRace(Action<bool, string> check)
    {
        var backend = new FakeBackend();
        await using var connection = new FreeRdpConsoleConnection(Options(), true, backend);
        await Start(connection, backend);
        backend.Native.DelaySend = true;
        var sending = Task.Run(() => connection.SendKey(30, false, false));
        await backend.Native.SendEntered.Task.WaitAsync(Timeout);
        var closing = Task.Run(() => connection.DisconnectAsync());
        try
        {
            check(!sending.IsCompleted && backend.Native.Disposals == 0,
                "Console teardown cannot dispose native storage while an admitted input call is using it");
            backend.Native.SendRelease.Set();
            await sending.WaitAsync(Timeout); await closing.WaitAsync(Timeout);
            check(backend.Native.Disposals == 1 && !backend.Native.SendAfterDisposal,
                "Console concurrent input and stop drain without sending through a freed native connection");
        }
        finally { backend.Native.SendRelease.Set(); backend.Native.Exit.Set(); }
    }

    internal static async Task CheckConnectTimeout(Action<bool, string> check)
    {
        var backend = new FakeBackend();
        await using var connection = new FreeRdpConsoleConnection(Options() with { ConnectTimeout = TimeSpan.FromMilliseconds(50) }, true, backend);
        var disconnected = NewSignal<ConsoleDisconnectInfo>();
        connection.Disconnected += result => disconnected.TrySetResult(result);
        connection.Start();
        var result = await disconnected.Task.WaitAsync(Timeout);
        check(result.ErrorCode == 0xfffffffe && result.Error?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true && backend.Native.Stops > 0,
            "Console a handshake that never reaches Connected is stopped by the Core connection deadline with a distinct timeout result");
        check(connection.State == ConsoleConnectionState.Disconnected && backend.Native.Disposals == 1 &&
            connection.DisconnectAsync().IsCompleted, "Console deadline cancellation waits for native cleanup before completing disconnection");
        var connectedBackend = new FakeBackend { ConnectOnRun = true };
        await using var connected = new FreeRdpConsoleConnection(Options() with { ConnectTimeout = TimeSpan.FromMilliseconds(50) }, true, connectedBackend);
        await Start(connected, connectedBackend);
        await Task.Delay(150);
        check(connected.State == ConsoleConnectionState.Connected && connectedBackend.Native.Stops == 0 && connectedBackend.Native.Disposals == 0,
            "Console reaching Connected disables the handshake deadline so established sessions are not timed out");
    }

    internal static async Task CheckResizeRejection(Action<bool, string> check)
    {
        var backend = new FakeBackend();
        await using var connection = new FreeRdpConsoleConnection(Options(), true, backend);
        var rejections = new List<uint>();
        connection.ResizeRejected += _ => throw new InvalidOperationException("observer");
        connection.ResizeRejected += rejections.Add;
        await Start(connection, backend);
        check(connection.Resize(1281, 721) && BinaryPrimitives.ReadUInt32LittleEndian(backend.Native.Sent.Last().Bytes) == 1280,
            "Console rounds an odd resize width down to the display protocol's required even width");
        backend.Emit(6, [], 3, 87); backend.Emit(6, [], 1, 99);
        check(rejections.SequenceEqual(new uint[] { 87 }) && connection.State == ConsoleConnectionState.Connected,
            "Console reports a rejected queued resize to every observer without disconnecting or confusing other input failures with resize failure");
        await connection.DisconnectAsync().WaitAsync(Timeout); backend.Emit(6, [], 3, 88);
        check(rejections.Count == 1, "Console stopped connections ignore late resize rejections from an old transport");
        var basicBackend = new FakeBackend();
        await using var basic = new FreeRdpConsoleConnection(Options(), false, basicBackend);
        int basicRejections = 0; basic.ResizeRejected += _ => basicRejections++;
        await Start(basic, basicBackend); basicBackend.Emit(6, [], 3, 87);
        check(basicRejections == 0, "Console basic mode never exposes an enhanced-only resize rejection");
    }

    internal static async Task CheckClipboardRevision(Action<bool, string> check)
    {
        var backend = new FakeBackend();
        await using var connection = new FreeRdpConsoleConnection(Options(), true, backend);
        int received = 0; connection.ClipboardReceived += _ => received++;
        await Start(connection, backend); backend.Emit(5, [], 1, 0);
        var local = new ConsoleClipboardData([new(13, null, Encoding.Unicode.GetBytes("new local\0"))]);
        ConsolePayload.TryWriteClipboard(new([new(13, null, Encoding.Unicode.GetBytes("remote\0"))]), out var remote);
        check(connection.SetClipboard(local), "Console an accepted local clipboard publication advances its local generation");
        backend.Emit(4, remote, 0, 0);
        check(received == 0, "Console suppresses a completed old remote snapshot queued before a newer local clipboard publication");
        backend.Emit(4, remote, 1, 0);
        check(received == 1, "Console accepts a remote snapshot advertised after the latest local clipboard generation");
        backend.Native.AcceptInput = false;
        bool refused = !connection.SetClipboard(local);
        backend.Emit(4, remote, 1, 0);
        check(refused && received == 2, "Console a refused local clipboard send does not advance the generation or suppress a still-current remote snapshot");
        backend.Native.AcceptInput = true;
        check(connection.SetClipboard(local), "Console the next accepted clipboard publication remains usable after a refused send");
        backend.Emit(4, remote, 1, 0); backend.Emit(4, remote, 2, 0);
        check(received == 3, "Console successive clipboard generations reject prior content while forwarding the newest remote snapshot");
    }

    internal static async Task CheckSpecialKeyboardInput(Action<bool, string> check)
    {
        var backend = new FakeBackend();
        await using var connection = new FreeRdpConsoleConnection(Options(), true, backend);
        check(!connection.SendPause() && !connection.SynchronizeKeyboard(ConsoleKeyboardToggleState.NumLock) && backend.Native.Sent.IsEmpty,
            "Console Pause and lock-key synchronization are refused before a connection exists");
        await Start(connection, backend);
        check(connection.SendPause() && backend.Native.Sent.Single() is { Kind: 6, Bytes.Length: 0 },
            "Console Pause uses its dedicated empty command rather than an ordinary NumLock key event");
        bool accepted = true;
        for (uint bits = 0; bits <= 15; bits++) accepted &= connection.SynchronizeKeyboard((ConsoleKeyboardToggleState)bits);
        var toggles = backend.Native.Sent.Where(item => item.Kind == 5).ToArray();
        check(accepted && toggles.Length == 16 && toggles.Select((item, index) => item.Bytes.Length == 4 &&
                BinaryPrimitives.ReadUInt32LittleEndian(item.Bytes) == (uint)index).All(value => value),
            "Console preserves all sixteen combinations of explicit Scroll, Num, Caps and Kana lock states as a four-byte focus command");
        int before = backend.Native.Sent.Count;
        check(!connection.SynchronizeKeyboard((ConsoleKeyboardToggleState)16) && !connection.SynchronizeKeyboard((ConsoleKeyboardToggleState)uint.MaxValue) &&
            backend.Native.Sent.Count == before, "Console rejects undefined keyboard toggle bits before native dispatch");
        backend.Native.AcceptInput = false;
        check(!connection.SendPause() && !connection.SynchronizeKeyboard(ConsoleKeyboardToggleState.None) && backend.Native.Sent.Count == before,
            "Console exposes native refusal of Pause and lock synchronization without claiming either command was accepted");
        backend.Native.AcceptInput = true;
        await connection.DisconnectAsync().WaitAsync(Timeout);
        check(!connection.SendPause() && !connection.SynchronizeKeyboard(ConsoleKeyboardToggleState.CapsLock),
            "Console stopped connections cannot send Pause or change the guest lock-key state");
    }

    private static async Task Start(FreeRdpConsoleConnection connection, FakeBackend backend)
    {
        connection.Start(); await backend.Native.RunEntered.Task.WaitAsync(Timeout); backend.Emit(1, []);
    }
    private static bool Throws<T>(Action action) where T : Exception { try { action(); return false; } catch (T) { return true; } }
    private static TaskCompletionSource<T> NewSignal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class FakeBackend : IConsoleNativeBackend
    {
        internal readonly FakeNative Native;
        internal readonly ManualResetEventSlim CreateRelease = new();
        internal readonly TaskCompletionSource<bool> CreateEntered = NewSignal<bool>();
        internal bool DelayCreate, DelayExit, CreateFailure, RunFailure, ConnectOnRun;
        private Action<uint, byte[], uint, uint>? callback;
        private Action<ConsoleFrame>? frameCallback;
        private long frameSequence;
        internal int FramesCreated, FramesReleased;
        internal FakeBackend() => Native = new(this);
        public IConsoleNativeConnection Create(ConsoleConnectionOptions options, Action<uint, byte[], uint, uint> callback, Action<ConsoleFrame> frameCallback)
        {
            this.callback = callback; this.frameCallback = frameCallback; CreateEntered.TrySetResult(true);
            if (DelayCreate && !CreateRelease.Wait(Timeout)) throw new TimeoutException("Create did not unblock");
            if (CreateFailure) throw new InvalidOperationException("expected create failure");
            return Native;
        }
        internal void Emit(uint kind, byte[] bytes, uint a = 0, uint b = 0)
        {
            if (kind != 3) { callback!(kind, bytes, a, b); return; }
            int width = unchecked((int)a), height = unchecked((int)b);
            long sequence = Interlocked.Increment(ref frameSequence);
            Interlocked.Increment(ref FramesCreated);
            using var frame = new ConsoleFrame(width, height, width is > 0 and <= 8192 ? width * 4 : 0,
                bytes, sequence, sequence - 1, new(0, 0, width, height), true,
                new FrameOwner(() => Interlocked.Increment(ref FramesReleased)));
            frameCallback!(frame);
        }
    }

    private sealed class FrameOwner(Action release) : IDisposable { public void Dispose() => release(); }

    internal sealed class FakeNative(FakeBackend owner) : IConsoleNativeConnection
    {
        internal readonly TaskCompletionSource<bool> RunEntered = NewSignal<bool>();
        internal readonly TaskCompletionSource<bool> SendEntered = NewSignal<bool>();
        internal readonly ManualResetEventSlim Exit = new();
        internal readonly ManualResetEventSlim SendRelease = new();
        internal readonly ConcurrentQueue<(uint Kind, byte[] Bytes)> Sent = new();
        internal int Disposals, Stops;
        internal ConsoleFrame? FrameToCapture;
        public ConsoleFrame? CaptureFrame() => FrameToCapture?.Retain();
        internal bool AcceptInput = true, DelaySend, SendAfterDisposal;
        public uint Run()
        {
            if (owner.ConnectOnRun) owner.Emit(1, []);
            RunEntered.TrySetResult(true);
            if (owner.RunFailure) throw new IOException("expected run failure");
            if (!Exit.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Mock transport did not exit");
            return 0;
        }
        public void Stop() { Interlocked.Increment(ref Stops); if (!owner.DelayExit) Exit.Set(); }
        public bool Send(uint kind, ReadOnlySpan<byte> payload)
        {
            SendEntered.TrySetResult(true);
            if (DelaySend && !SendRelease.Wait(Timeout)) throw new TimeoutException("Mock send did not unblock");
            if (Disposals != 0) SendAfterDisposal = true;
            if (!AcceptInput) return false;
            Sent.Enqueue((kind, payload.ToArray())); return true;
        }
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
