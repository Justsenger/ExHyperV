using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ExHyperV.Services;

/// <summary>A bounded transport probe. Input is opt-in; VM power and host clipboard are never accessed.</summary>
internal static class ConsoleProbe
{
    internal static async Task RunAsync(string[] args)
    {
        int vmArg = Array.IndexOf(args, "--console-probe");
        int reportArg = Array.IndexOf(args, "--console-report");
        if (vmArg < 0 || vmArg + 1 >= args.Length || !Guid.TryParse(args[vmArg + 1], out var vmId) || vmId == Guid.Empty ||
            reportArg < 0 || reportArg + 1 >= args.Length)
            throw new ArgumentException("Use --console-probe <vm-guid> [--enhanced] [--console-exercise] --console-report <json-path>.");
        string reportPath = Path.GetFullPath(args[reportArg + 1]);
        int frameArg = Array.IndexOf(args, "--console-frame");
        if (frameArg >= 0 && frameArg + 1 >= args.Length) throw new ArgumentException("--console-frame requires a PNG output path.");
        string? framePath = frameArg >= 0 ? Path.GetFullPath(args[frameArg + 1]) : null;
        if (string.Equals(framePath, reportPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Frame and report paths must differ.");
        int timeoutArg = Array.IndexOf(args, "--console-timeout-ms");
        int connectTimeoutMilliseconds = 2000;
        if (timeoutArg >= 0 && (timeoutArg + 1 >= args.Length || !int.TryParse(args[timeoutArg + 1], out connectTimeoutMilliseconds) ||
            connectTimeoutMilliseconds is < 1 or > 120000))
            throw new ArgumentException("--console-timeout-ms must be an integer from 1 through 120000.");
        double observationLimitSeconds = Math.Min(122, Math.Max(10, connectTimeoutMilliseconds / 1000d + 2));
        bool enhanced = args.Contains("--enhanced");
        bool exerciseRequested = args.Contains("--console-exercise");
        if (exerciseRequested && !enhanced) throw new ArgumentException("--console-exercise requires --enhanced.");
        var exercise = new ExerciseResult();
        byte[]? markerBytes = exerciseRequested ? Encoding.Unicode.GetBytes($"ExHyperV Core 剪贴板探针 {Guid.NewGuid():N}\0") : null;
        var clock = Stopwatch.StartNew();
        object gate = new();
        int frames = 0, width = 0, height = 0, stride = 0, logins = 0;
        long lastSequence = 0;
        int nonUniformFrames = 0, allBlackFrames = 0, lastNonBlackPixels = 0, maxNonBlackPixels = 0;
        string? firstFrameSha256 = null, lastFrameSha256 = null;
        var distinctFrameHashes = new HashSet<string>(StringComparer.Ordinal);
        bool connected = false, clipboardAvailable = false, clipboardEverAvailable = false;
        double? firstFrameMilliseconds = null;
        ConsoleDisconnectInfo? disconnect = null;
        string? error = null;
        string? frameSaveError = null;
        bool frameSaved = false;
        ConsoleFrame? latestFrame = null;
        var events = new List<object>();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new FreeRdpConsoleConnection(new ConsoleConnectionOptions
        {
            VmId = vmId, ConnectTimeout = TimeSpan.FromMilliseconds(connectTimeoutMilliseconds)
        }, enhanced);
        void Event(string name, object? value = null)
        {
            if (events.Count < 64) events.Add(new { name, value, milliseconds = Math.Round(clock.Elapsed.TotalMilliseconds, 2) });
        }
        connection.Connected += () => { lock (gate) { connected = true; Event("connected"); } };
        connection.LoginCompleted += () => { lock (gate) { logins++; Event("login-completed"); } };
        connection.FrameReceived += frame =>
        {
            var pixels = frame.Pixels;
            int nonBlack = 0;
            bool nonUniform = false;
            for (int offset = 0; offset + 3 < pixels.Length; offset += 4)
            {
                if ((pixels[offset] | pixels[offset + 1] | pixels[offset + 2]) != 0) nonBlack++;
                if (pixels[offset] != pixels[0] || pixels[offset + 1] != pixels[1] || pixels[offset + 2] != pixels[2]) nonUniform = true;
            }
            string digest = Convert.ToHexString(SHA256.HashData(pixels));
            GC.KeepAlive(frame);
            lock (gate)
            {
                frames++; width = frame.Width; height = frame.Height; stride = frame.Stride; lastSequence = frame.Sequence;
                firstFrameMilliseconds ??= Math.Round(clock.Elapsed.TotalMilliseconds, 2);
                if (nonUniform) nonUniformFrames++;
                if (nonBlack == 0) allBlackFrames++;
                lastNonBlackPixels = nonBlack; maxNonBlackPixels = Math.Max(maxNonBlackPixels, nonBlack);
                firstFrameSha256 ??= digest; lastFrameSha256 = digest;
                if (distinctFrameHashes.Count < 64) distinctFrameHashes.Add(digest);
                if (framePath is not null)
                {
                    var previous = latestFrame;
                    latestFrame = frame.Retain();
                    previous?.Dispose();
                }
                // Only an explicitly requested PNG retains one lease beyond this callback.
            }
        };
        connection.ClipboardAvailabilityChanged += available =>
        {
            lock (gate) { clipboardAvailable = available; clipboardEverAvailable |= available; Event("clipboard-availability", available); }
        };
        if (exerciseRequested)
        {
            connection.ClipboardReceived += snapshot =>
            {
                // Compare only our own marker; never retain or report unrelated guest clipboard bytes.
                bool matches = snapshot.Formats.Any(format => format.Id == 13 && format.Data.Span.SequenceEqual(markerBytes));
                lock (gate)
                {
                    exercise.RemoteClipboardSnapshots++;
                    if (exercise.ClipboardWriteAttempts > 0 && matches) exercise.ClipboardMarkerReturned = true;
                }
            };
            connection.ResizeRejected += code => { lock (gate) { exercise.ResizeRejections++; Event("resize-rejected", code); } };
        }
        connection.Disconnected += result =>
        {
            lock (gate) { disconnect = result; Event("disconnected", result.ErrorCode); }
            disconnected.TrySetResult();
        };
        try
        {
            connection.Start();
            if (exerciseRequested) await ExerciseAsync().ConfigureAwait(false);
            double remainingSeconds = Math.Max(0, observationLimitSeconds - clock.Elapsed.TotalSeconds);
            await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromSeconds(remainingSeconds))).ConfigureAwait(false);
        }
        catch (Exception failure) { error = failure.ToString(); }
        finally
        {
            // Stop after the bounded observation interval; allow native teardown to finish honestly.
            await connection.DisconnectAsync().ConfigureAwait(false);
            if (connected || connection.State == ConsoleConnectionState.Disconnected)
                await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }
        if (framePath is not null)
        {
            try
            {
                if (latestFrame is null) throw new InvalidOperationException("No frame was received to save.");
                SaveFramePng(latestFrame, framePath);
                frameSaved = true;
            }
            catch (Exception failure) { frameSaveError = failure.ToString(); }
            finally { latestFrame?.Dispose(); latestFrame = null; }
        }
        object report;
        lock (gate)
        {
            report = new
            {
                mode = exerciseRequested ? "console-core-explicit-exercise" : "console-core-readonly-probe", vmId, enhanced, connected, frames, width, height, stride, lastSequence,
                firstFrameMilliseconds, logins, clipboardAvailable, clipboardEverAvailable,
                nonUniformFrames, allBlackFrames, lastNonBlackPixels, maxNonBlackPixels, firstFrameSha256, lastFrameSha256,
                distinctFrameHashesObserved = distinctFrameHashes.Count, distinctHashCountCappedAt = 64,
                stateAfterCleanup = connection.State.ToString(), disconnect, error,
                elapsedMilliseconds = Math.Round(clock.Elapsed.TotalMilliseconds, 2),
                observationLimitSeconds, connectTimeoutMilliseconds, inputOperations = exercise.PointerAttempts, keyboardOperations = 0,
                resizeOperations = exercise.ResizeAttempts + exercise.RestoreAttempts, clipboardWriteOperations = exercise.ClipboardWriteAttempts,
                exercise = exerciseRequested ? exercise : null, vmPowerOperations = 0,
                hostClipboardAccess = false, rawPixelsPersisted = frameSaved, frameFile = framePath, frameSaveError, events,
                coreHasDesktopReference = typeof(FreeRdpConsoleConnection).Assembly.GetReferencedAssemblies().Any(a =>
                    a.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase" or "System.Windows.Forms"),
                wpfLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name is "PresentationFramework" or "PresentationCore")
            };
            Environment.ExitCode = error is null && frameSaveError is null && connected && frames > 0 ? 0 : 2;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(reportPath, json).ConfigureAwait(false);
        Console.WriteLine(json);

        async Task<bool> WaitUntilAsync(Func<bool> condition, double deadlineSeconds)
        {
            while (true)
            {
                lock (gate) { if (condition()) return true; }
                if (disconnected.Task.IsCompleted || clock.Elapsed.TotalSeconds >= deadlineSeconds) return false;
                double milliseconds = Math.Min(50, (deadlineSeconds - clock.Elapsed.TotalSeconds) * 1000);
                if (milliseconds <= 0) return false;
                await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromMilliseconds(milliseconds))).ConfigureAwait(false);
            }
        }

        async Task ExerciseAsync()
        {
            // Reserve the final two seconds for restoring the original desktop size.
            double operationDeadline = Math.Max(0, observationLimitSeconds - 2);
            if (!await WaitUntilAsync(() => connected && frames > 0, operationDeadline).ConfigureAwait(false))
            {
                exercise.SkippedReason = "No connected frame within the exercise window.";
                return;
            }
            int initialWidth, initialHeight;
            long resizeSequence;
            lock (gate)
            {
                initialWidth = width; initialHeight = height; resizeSequence = lastSequence;
                exercise.Started = true; exercise.InitialWidth = width; exercise.InitialHeight = height;
                exercise.RequestedWidth = width >= 240 ? (width - 32) & ~1 : (width + 32) & ~1;
                exercise.RequestedHeight = height;
            }
            try
            {
                // One ordinary move only: no buttons, wheel, keys, shell commands or login actions.
                exercise.PointerAttempts++;
                exercise.PointerQueueAccepted = connection.SendPointer((ushort)(initialWidth / 2), (ushort)(initialHeight / 2));
                lock (gate) Event("exercise-pointer-queued", exercise.PointerQueueAccepted);
                exercise.ResizeAttempts++;
                exercise.ResizeQueueAccepted = connection.Resize(exercise.RequestedWidth, exercise.RequestedHeight);
                lock (gate) Event("exercise-resize-queued", exercise.ResizeQueueAccepted);
                if (exercise.ResizeQueueAccepted)
                    exercise.ResizeConfirmedByFrame = await WaitUntilAsync(() => width == exercise.RequestedWidth && height == exercise.RequestedHeight &&
                        lastSequence > resizeSequence, Math.Min(operationDeadline, clock.Elapsed.TotalSeconds + 3)).ConfigureAwait(false);

                if (await WaitUntilAsync(() => clipboardAvailable, operationDeadline).ConfigureAwait(false))
                {
                    lock (gate) exercise.ClipboardWriteAttempts++;
                    exercise.ClipboardQueueAccepted = connection.SetClipboard(new ConsoleClipboardData(
                        [new ConsoleClipboardFormat(13, null, markerBytes!)]));
                    lock (gate) Event("exercise-clipboard-queued", exercise.ClipboardQueueAccepted);
                    if (exercise.ClipboardQueueAccepted)
                        await WaitUntilAsync(() => exercise.ClipboardMarkerReturned, operationDeadline).ConfigureAwait(false);
                }
                else exercise.ClipboardSkippedReason = "Clipboard channel did not become available within the exercise window.";
            }
            finally
            {
                if (exercise.ResizeQueueAccepted && connection.State == ConsoleConnectionState.Connected)
                {
                    long restoreSequence;
                    lock (gate) restoreSequence = lastSequence;
                    exercise.RestoreAttempts++;
                    exercise.RestoreQueueAccepted = connection.Resize(initialWidth, initialHeight);
                    lock (gate) Event("exercise-restore-queued", exercise.RestoreQueueAccepted);
                    if (exercise.RestoreQueueAccepted)
                        exercise.RestoreConfirmedByFrame = await WaitUntilAsync(() => width == initialWidth && height == initialHeight &&
                            lastSequence > restoreSequence, observationLimitSeconds).ConfigureAwait(false);
                }
            }
        }
    }

    private sealed class ExerciseResult
    {
        public bool Started { get; set; }
        public string? SkippedReason { get; set; }
        public int InitialWidth { get; set; }
        public int InitialHeight { get; set; }
        public int RequestedWidth { get; set; }
        public int RequestedHeight { get; set; }
        public int PointerAttempts { get; set; }
        public bool PointerQueueAccepted { get; set; }
        public bool PointerEffectVerified => false;
        public int ResizeAttempts { get; set; }
        public bool ResizeQueueAccepted { get; set; }
        public bool ResizeConfirmedByFrame { get; set; }
        public int ResizeRejections { get; set; }
        public int RestoreAttempts { get; set; }
        public bool RestoreQueueAccepted { get; set; }
        public bool RestoreConfirmedByFrame { get; set; }
        public int ClipboardWriteAttempts { get; set; }
        public bool ClipboardQueueAccepted { get; set; }
        public int RemoteClipboardSnapshots { get; set; }
        public bool ClipboardMarkerReturned { get; set; }
        public bool ClipboardRoundTripVerified => ClipboardQueueAccepted && ClipboardMarkerReturned;
        public string? ClipboardSkippedReason { get; set; }
        public string ClipboardResult => ClipboardRoundTripVerified ? "Marker returned through Core." : "Bidirectional clipboard not verified; accepting the local queue alone is not proof.";
        public bool GuestClipboardRestored => false;
    }

    private static void SaveFramePng(ConsoleFrame frame, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new Bitmap(frame.Width, frame.Height, PixelFormat.Format32bppArgb);
        var locked = bitmap.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels = frame.CopyPixels();
            for (int y = 0; y < frame.Height; y++)
                Marshal.Copy(pixels, y * frame.Stride, IntPtr.Add(locked.Scan0, y * locked.Stride), frame.Width * 4);
        }
        finally { bitmap.UnlockBits(locked); }
        bitmap.Save(path, ImageFormat.Png);
    }
}
