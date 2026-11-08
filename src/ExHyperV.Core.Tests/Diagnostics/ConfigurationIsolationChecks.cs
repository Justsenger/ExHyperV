using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using ExHyperV.Services;

internal static class ConfigurationIsolationChecks
{
    private const string WorkerArgument = "--configuration-worker";
    private const int WorkerCount = 3;
    private const int UpdatesPerWorker = 8;

    // Program must call this before running any ordinary checks. A worker only
    // opens its parent's unique temporary directory, never AppDataPaths.
    public static int? RunWorker(string[] args)
    {
        if (args.Length == 0 || args[0] != WorkerArgument) return null;
        try
        {
            if (args.Length != 4 || !Guid.TryParseExact(args[3], "N", out _) ||
                !int.TryParse(args[2], out int workerId) || workerId < 0 || workerId >= WorkerCount)
                throw new ArgumentException("Invalid configuration check worker arguments.");
            string directory = OwnedDirectory(args[3]);
            if (!string.Equals(Path.GetFullPath(args[1]), directory, StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(directory))
                throw new ArgumentException("Worker directory is not the parent-owned test directory.");

            File.WriteAllText(Path.Combine(directory, "ready-" + workerId), "ready");
            var timer = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(directory, "go")))
            {
                if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Worker start barrier timed out.");
                Thread.Sleep(20);
            }
            Increment(new XmlConfigurationFile(Path.Combine(directory, "cross-process.xml")), workerId);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        string runId = Guid.NewGuid().ToString("N");
        string directory = OwnedDirectory(runId);
        Directory.CreateDirectory(directory);
        try
        {
            CheckSnapshotsAndFailures(directory, check);
            await CheckIndependentInstancesAsync(directory, check);
            await CheckCrossProcessAsync(directory, runId, check);
        }
        finally
        {
            // Only flat files created in this exact run's unique scratch folder.
            if (!string.Equals(directory, OwnedDirectory(runId), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing cleanup outside the owned test directory.");
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private static string OwnedDirectory(string runId) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ExHyperV-ConfigurationChecks-" + runId));

    private static void CheckSnapshotsAndFailures(string directory, Action<bool, string> check)
    {
        string path = Path.Combine(directory, "snapshots.xml");
        var first = new XmlConfigurationFile(path);
        var second = new XmlConfigurationFile(path);
        check(first.Load().Root?.Name == "Config" && !File.Exists(path),
            "loading a missing configuration returns defaults without writing a file");
        first.Update(root =>
        {
            root.SetElementValue("Value", "saved");
            root.Add(new XElement("UnknownField", new XAttribute("keep", "yes"), new XElement("Nested", "value")));
        });

        var snapshot = first.Load();
        snapshot.Root!.SetElementValue("Value", "caller edit");
        check(second.Load().Root!.Element("Value")?.Value == "saved",
            "returned configuration documents cannot mutate stored state without an update");
        second.Update(root => root.SetElementValue("NewField", "later"));
        check(snapshot.Root.Element("NewField") == null && snapshot.Root.Element("Value")?.Value == "caller edit" &&
            second.Load().Root!.Element("UnknownField")?.ToString(SaveOptions.DisableFormatting) ==
                "<UnknownField keep=\"yes\"><Nested>value</Nested></UnknownField>",
            "later updates do not mutate earlier snapshots and preserve unrelated nested configuration");

        byte[] original = File.ReadAllBytes(path);
        bool callbackFailed = false;
        try
        {
            first.Update(root =>
            {
                root.SetElementValue("Value", "uncommitted");
                throw new InvalidOperationException("expected callback failure");
            });
        }
        catch (InvalidOperationException error) when (error.Message == "expected callback failure") { callbackFailed = true; }
        check(callbackFailed && File.ReadAllBytes(path).SequenceEqual(original) &&
            !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "a failing configuration callback leaves the original bytes and no temporary file");

        bool replaceFailed = false;
        FileStream? replacementBlocker = null;
        try
        {
            first.Update(root =>
            {
                root.SetElementValue("Value", "uncommitted replacement");
                replacementBlocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            });
        }
        catch (IOException) { replaceFailed = true; }
        finally { replacementBlocker?.Dispose(); }
        check(replaceFailed && File.ReadAllBytes(path).SequenceEqual(original) &&
            !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "a failed atomic replacement preserves the old configuration and cleans its temporary file");

        bool nestedFailed = false;
        try
        {
            first.Update(root =>
            {
                root.SetElementValue("Value", "outer uncommitted");
                second.Update(nested => nested.SetElementValue("Value", "inner uncommitted"));
            });
        }
        catch (InvalidOperationException) { nestedFailed = true; }
        bool nestedKeptOriginal = File.ReadAllBytes(path).SequenceEqual(original);
        second.Update(root => root.SetElementValue("AfterNestedFailure", "saved"));
        check(nestedFailed && nestedKeptOriginal && first.Load().Root!.Element("AfterNestedFailure")?.Value == "saved" &&
            first.Load().Root!.Element("Value")?.Value == "saved",
            "same-file nested updates leave the original intact and release the guard for the next update");

        File.WriteAllText(path, "<Config><Broken>");
        original = File.ReadAllBytes(path);
        bool loadFailed = false, updateFailed = false, callbackRan = false;
        try { first.Load(); }
        catch (XmlException) { loadFailed = true; }
        try { second.Update(root => { callbackRan = true; root.SetElementValue("Value", "replacement"); }); }
        catch (XmlException) { updateFailed = true; }
        check(loadFailed && updateFailed && !callbackRan && File.ReadAllBytes(path).SequenceEqual(original) &&
            !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "malformed configuration is reported and never overwritten with fallback defaults");
    }

    private static async Task CheckIndependentInstancesAsync(string directory, Action<bool, string> check)
    {
        string path = Path.Combine(directory, "in-process.xml");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = Enumerable.Range(0, WorkerCount).Select(workerId => Task.Run(async () =>
        {
            var independent = new XmlConfigurationFile(path);
            await ready.Task;
            Increment(independent, workerId);
        })).ToArray();
        ready.SetResult();
        await Task.WhenAll(updates).WaitAsync(TimeSpan.FromSeconds(20));
        check(HasEveryUpdate(new XmlConfigurationFile(path).Load()),
            "independent in-process configuration stores preserve every concurrent shared and per-caller update");
    }

    private static void Increment(XmlConfigurationFile store, int workerId)
    {
        for (int index = 0; index < UpdatesPerWorker; index++)
        {
            store.Update(root =>
            {
                int total = (int?)root.Element("Total") ?? 0;
                string field = "Worker" + workerId;
                int own = (int?)root.Element(field) ?? 0;
                // Widen the read/modify/write race so independent stale documents
                // would lose updates if the shared file mutex were missing.
                Thread.Sleep(3);
                root.SetElementValue("Total", total + 1);
                root.SetElementValue(field, own + 1);
            });
        }
    }

    private static bool HasEveryUpdate(XDocument document) =>
        (int?)document.Root?.Element("Total") == WorkerCount * UpdatesPerWorker &&
        Enumerable.Range(0, WorkerCount).All(id => (int?)document.Root?.Element("Worker" + id) == UpdatesPerWorker);

    private static async Task CheckCrossProcessAsync(string directory, string runId, Action<bool, string> check)
    {
        var workers = new List<(Process Process, Task<string> Error, Task<string> Output)>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            for (int workerId = 0; workerId < WorkerCount; workerId++)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("No test executable path."))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = directory
                };
                // Test Explorer / dotnet exec may host the test DLL via dotnet.exe.
                if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
                foreach (string argument in new[] { WorkerArgument, directory, workerId.ToString(), runId })
                    start.ArgumentList.Add(argument);
                var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start configuration worker.");
                workers.Add((process, process.StandardError.ReadToEndAsync(), process.StandardOutput.ReadToEndAsync()));
            }
            while (Enumerable.Range(0, WorkerCount).Any(id => !File.Exists(Path.Combine(directory, "ready-" + id))))
            {
                if (workers.Any(worker => worker.Process.HasExited))
                    throw new InvalidOperationException("Configuration worker exited before the start barrier.");
                await Task.Delay(20, timeout.Token);
            }
            File.WriteAllText(Path.Combine(directory, "go"), "go");
            await Task.WhenAll(workers.Select(worker => worker.Process.WaitForExitAsync(timeout.Token)));
            foreach (var worker in workers)
            {
                string error = await worker.Error;
                await worker.Output;
                if (worker.Process.ExitCode != 0)
                    throw new InvalidOperationException("Configuration worker failed: " + error);
            }
            check(HasEveryUpdate(new XmlConfigurationFile(Path.Combine(directory, "cross-process.xml")).Load()),
                "separate processes in the same Windows session preserve every concurrent configuration update");
        }
        finally
        {
            Exception? cleanupFailure = null;
            foreach (var worker in workers)
            {
                try
                {
                    if (!worker.Process.HasExited) worker.Process.Kill(entireProcessTree: true);
                    await worker.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    await Task.WhenAll(worker.Error, worker.Output).WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (Exception error) { cleanupFailure ??= error; }
                finally { worker.Process.Dispose(); }
            }
            if (cleanupFailure != null) throw new InvalidOperationException("Configuration worker cleanup failed.", cleanupFailure);
        }
    }
}
