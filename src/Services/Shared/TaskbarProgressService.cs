using System.Windows;
using System.Windows.Shell;

namespace ExHyperV.Services;

internal enum TaskbarProgressOperation
{
    VmImport,
    VmExport
}

internal static class TaskbarProgressService
{
    private static readonly object SyncRoot = new();
    private static TaskbarProgressOperation? _currentOperation;
    private static long _generation;
    private static double _progressValue;

    public static void Start(TaskbarProgressOperation operation)
    {
        long generation;
        lock (SyncRoot)
        {
            _currentOperation = operation;
            _progressValue = 0;
            generation = ++_generation;
        }

        Apply(TaskbarItemProgressState.Indeterminate, 0, generation);
    }

    public static void Report(TaskbarProgressOperation operation, int percentage)
    {
        double progress = Math.Clamp(percentage, 0, 100) / 100d;
        long generation;
        lock (SyncRoot)
        {
            if (_currentOperation != operation) return;
            _progressValue = progress;
            generation = _generation;
        }

        Apply(TaskbarItemProgressState.Normal, progress, generation);
    }

    public static void Complete(TaskbarProgressOperation operation) =>
        Finish(operation, TaskbarItemProgressState.Normal, 1, TimeSpan.FromSeconds(1.5));

    public static void Fail(TaskbarProgressOperation operation)
    {
        double progress;
        lock (SyncRoot)
        {
            if (_currentOperation != operation) return;
            progress = Math.Max(_progressValue, 0.01);
        }

        Finish(operation, TaskbarItemProgressState.Error, progress, TimeSpan.FromSeconds(3));
    }

    private static void Finish(
        TaskbarProgressOperation operation,
        TaskbarItemProgressState state,
        double progress,
        TimeSpan clearDelay)
    {
        long generation;
        lock (SyncRoot)
        {
            if (_currentOperation != operation) return;
            _progressValue = progress;
            generation = ++_generation;
        }

        Apply(state, progress, generation);
        _ = ClearAfterDelayAsync(operation, generation, clearDelay);
    }

    private static async Task ClearAfterDelayAsync(
        TaskbarProgressOperation operation,
        long generation,
        TimeSpan delay)
    {
        await Task.Delay(delay);

        long clearGeneration;
        lock (SyncRoot)
        {
            if (_currentOperation != operation || _generation != generation) return;
            _currentOperation = null;
            _progressValue = 0;
            clearGeneration = ++_generation;
        }

        Apply(TaskbarItemProgressState.None, 0, clearGeneration);
    }

    private static void Apply(
        TaskbarItemProgressState state,
        double progress,
        long generation)
    {
        Application? application = Application.Current;
        if (application == null || application.Dispatcher.HasShutdownStarted) return;

        void Update()
        {
            lock (SyncRoot)
            {
                if (_generation != generation) return;
            }

            Window? window = application.MainWindow;
            if (window == null) return;

            window.TaskbarItemInfo ??= new TaskbarItemInfo();
            window.TaskbarItemInfo.ProgressValue = progress;
            window.TaskbarItemInfo.ProgressState = state;
        }

        if (application.Dispatcher.CheckAccess())
            Update();
        else
            application.Dispatcher.BeginInvoke(Update);
    }
}
