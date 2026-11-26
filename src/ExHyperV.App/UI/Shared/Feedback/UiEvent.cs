using System.Diagnostics;
using System.Runtime.CompilerServices;
using ExHyperV.App.Diagnostics;
using ExHyperV.App.Localization;

namespace ExHyperV.App.UI.Shared.Feedback;

internal static class UiEvent
{
    // WinUI cannot observe a Task returned by an event handler.
    internal static async Task RunAsync(Func<Task> action, Action<string>? reportError = null,
        [CallerMemberName] string caller = "")
    {
        try
        {
            AppDiagnostics.Write("UI", "ActionStarted", $"caller={caller}", TraceEventType.Verbose);
            await action();
        }
        catch (OperationCanceledException error)
        {
            Trace.TraceInformation("UI operation canceled: {0}", error);
        }
        catch (Exception error)
        {
            Trace.TraceError("UI event failed: {0}", error);
            try { reportError?.Invoke(CoreMessageResolver.ResolveException(error)); }
            catch (Exception feedbackError)
            {
                // The window may have closed while the operation was awaiting completion.
                Trace.TraceError("Could not display UI event failure: {0}", feedbackError);
            }
        }
    }
}
