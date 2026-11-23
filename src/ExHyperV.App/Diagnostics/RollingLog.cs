using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ExHyperV.App.Diagnostics;

// A single consumer owns each stream. The directory mutex covers a whole batch,
// including rotation, snapshots and clear, across installed and portable instances.
internal sealed class RollingLog : IDisposable
{
    private readonly record struct Entry(string? Line, Action? Control, TaskCompletionSource? Completion);
    private readonly BlockingCollection<Entry> queue = new(1024);
    private readonly object admission = new();
    private readonly Thread worker;
    private readonly Mutex directoryLock;
    private readonly int maxBytes;
    private readonly int fileCount;
    private readonly string session = Guid.NewGuid().ToString("N")[..8];
    private FileStream? output;
    private int? lastWriteError;
    private bool enabled;
    private bool verbose;
    private bool closed;
    internal string DirectoryPath { get; }

    internal RollingLog(string directory, bool enabled, bool verbose, int maxBytes = 2 * 1024 * 1024, int fileCount = 6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(fileCount, 1);
        this.maxBytes = maxBytes;
        this.fileCount = fileCount;
        this.enabled = enabled;
        this.verbose = verbose;
        DirectoryPath = Path.GetFullPath(directory);
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(DirectoryPath.ToUpperInvariant()));
        directoryLock = new Mutex(false, @"Local\ExHyperV.Logs." + Convert.ToHexString(key));
        worker = new Thread(Consume) { IsBackground = true, Name = "ExHyperV log writer" };
        worker.Start();
    }

    internal void Configure(bool enable, bool detail)
    {
        lock (admission)
        {
            if (closed) return;
            enabled = enable;
            verbose = detail;
            if (!enable) Barrier();
        }
    }

    // Callers supply only reviewed, payload-free metadata. Free-form exception messages,
    // native callback buffers and UI text must never cross this boundary.
    internal void Write(TraceEventType level, string category, string eventName, string? fields = null,
        DateTimeOffset? timestamp = null)
    {
        TaskCompletionSource? completion = null;
        lock (admission)
        {
            if (closed || !enabled || (level == TraceEventType.Verbose && !verbose)) return;
            string line = $"{timestamp ?? DateTimeOffset.UtcNow:O} {level} session={session} module={category} event={eventName}";
            if (!string.IsNullOrEmpty(fields)) line += " " + fields;
            line = line.Replace("\r", "").Replace("\n", @"\n").Replace("\0", "");
            if (level is TraceEventType.Error or TraceEventType.Critical) completion = NewCompletion();
            queue.Add(new(line, null, completion));
        }
        // Diagnostics must not change the outcome of a UI, Core or crash callback.
        completion?.Task.GetAwaiter().GetResult();
    }

    private string PathAt(int index) => Path.Combine(DirectoryPath, $"runtime-{index}.log");
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal void Flush()
    {
        lock (admission) { if (!closed) Barrier(); }
    }

    private void Barrier(Action? action = null)
    {
        var done = NewCompletion();
        queue.Add(new(null, action, done));
        done.Task.GetAwaiter().GetResult();
    }

    internal IReadOnlyList<(string Name, byte[] Bytes)> Snapshot(out int? lastWriteHResult)
    {
        List<(string, byte[])> result = [];
        int? error = null;
        lock (admission)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            Barrier(() =>
            {
                error = lastWriteError;
                for (int i = fileCount - 1; i >= 0; i--)
                {
                    string path = PathAt(i);
                    if (File.Exists(path)) result.Add((Path.GetFileName(path), File.ReadAllBytes(path)));
                }
            });
        }
        lastWriteHResult = error;
        return result;
    }

    internal void Clear()
    {
        lock (admission)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            Barrier(() =>
            {
                if (Directory.Exists(DirectoryPath))
                    for (int i = 0; i < fileCount; i++) File.Delete(PathAt(i));
                lastWriteError = null;
            });
        }
    }

    private void Consume()
    {
        while (queue.TryTake(out var first, Timeout.Infinite))
        {
            try { directoryLock.WaitOne(); }
            catch (AbandonedMutexException) { /* The previous writer exited while holding the mutex. */ }
            try
            {
                var entry = first;
                for (int count = 1; ; count++)
                {
                    Process(entry);
                    if (count == 128 || !queue.TryTake(out entry)) break;
                }
            }
            finally
            {
                try { CloseFile(); }
                catch (IOException ex) { lastWriteError = ex.HResult; }
                directoryLock.ReleaseMutex();
            }
        }
    }

    private void Process(Entry entry)
    {
        try
        {
            if (entry.Line != null) Append(entry.Line);
            if (entry.Completion != null)
            {
                if (entry.Control == null && output == null && File.Exists(PathAt(0))) OpenFile();
                output?.Flush(flushToDisk: true);
                CloseFile();
                entry.Control?.Invoke();
            }
        }
        catch (Exception ex)
        {
            if (entry.Control != null) { entry.Completion!.SetException(ex); return; }
            lastWriteError = ex.HResult;
            try { CloseFile(); } catch (IOException) { /* Preserve the first failure. */ }
        }
        entry.Completion?.SetResult();
    }

    private void OpenFile()
    {
        Directory.CreateDirectory(DirectoryPath);
        output = new FileStream(PathAt(0), FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    private void CloseFile()
    {
        var previous = output;
        output = null;
        previous?.Dispose();
    }

    private void Append(string line)
    {
        int limit = Math.Min(maxBytes, 32 * 1024);
        byte[] bytes;
        if (Encoding.UTF8.GetByteCount(line) + 1 <= limit)
            bytes = Encoding.UTF8.GetBytes(line + "\n");
        else
        {
            bytes = new byte[limit];
            var suffix = " [truncated]\n"u8;
            var target = bytes.AsSpan(0, limit - suffix.Length);
            Encoding.UTF8.GetEncoder().Convert(line, target, false, out _, out int used, out _);
            suffix.CopyTo(bytes.AsSpan(used));
            Array.Resize(ref bytes, used + suffix.Length);
        }
        if (output == null) OpenFile();
        if (output!.Length + bytes.Length > maxBytes)
        {
            output.Flush(flushToDisk: true);
            CloseFile();
            File.Delete(PathAt(fileCount - 1));
            for (int i = fileCount - 2; i >= 0; i--)
                if (File.Exists(PathAt(i))) File.Move(PathAt(i), PathAt(i + 1), overwrite: true);
            OpenFile();
        }
        output!.Write(bytes);
    }

    public void Dispose()
    {
        lock (admission)
        {
            if (closed) return;
            closed = true;
            Barrier();
            queue.CompleteAdding();
            worker.Join();
            queue.Dispose();
            directoryLock.Dispose();
        }
    }
}
