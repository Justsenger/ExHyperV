using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using ExHyperV.App.UI.Shared.Appearance;
using ExHyperV.Services;
using ExHyperV.Tools;

namespace ExHyperV.App.Diagnostics;

internal static class AppDiagnostics
{
    internal static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExHyperV");
    internal static string DirectoryPath => Path.Combine(DataDirectory, "Logs");
    private static RollingLog? log;

    private static LocalTraceListener? recorder;

    internal static void Start(AppearanceSettings settings)
    {
        log = new RollingLog(DirectoryPath, settings.LoggingEnabled, settings.VerboseDiagnostics);
        recorder = new LocalTraceListener(log);
        if (settings.LoggingEnabled) CoreDiagnostics.Reported += recorder.OnCoreEvent;
        Trace.Listeners.Add(recorder);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Crash("UnhandledException", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => Crash("UnobservedTaskException", args.Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Write("App", "ProcessExit");
            log.Dispose();
        };
    }

    internal static void Configure(AppearanceSettings settings)
    {
        log!.Configure(settings.LoggingEnabled, settings.VerboseDiagnostics);
        CoreDiagnostics.Reported -= recorder!.OnCoreEvent;
        if (settings.LoggingEnabled) CoreDiagnostics.Reported += recorder!.OnCoreEvent;
    }

    internal static void StartupHeader(AppearanceSettings settings)
    {
        if (settings.LoggingEnabled) Write("App", "Startup", EnvironmentHeader(settings));
    }

    internal static void Write(string category, string name, string? fields = null, TraceEventType level = TraceEventType.Information) =>
        log?.Write(level, category, name, fields);

    internal static void Failure(string category, string name, Exception? error, TraceEventType level = TraceEventType.Error) =>
        recorder?.Failure(category, name, error, level);

    internal static void Crash(string name, Exception? error) => Failure("App", name, error, TraceEventType.Critical);

    private static string EnvironmentHeader(AppearanceSettings settings)
    {
        string admin;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator).ToString();
        }
        catch (Exception ex)
        {
            Failure("Environment", "AdminQueryFailed", ex);
            admin = "Unknown";
        }
        return $"appVersion={typeof(App).Assembly.GetName().Version} coreVersion={typeof(ApiResponse).Assembly.GetName().Version}" +
            $" windowsBuild={Environment.OSVersion.Version} osArchitecture={RuntimeInformation.OSArchitecture} processArchitecture={RuntimeInformation.ProcessArchitecture}" +
            $" administrator={admin} language={CultureInfo.CurrentUICulture.Name} logging={settings.LoggingEnabled} verbose={settings.VerboseDiagnostics}" +
            $" theme={settings.Theme} accentEnabled={settings.AccentEnabled} autoOpenConsole={settings.AutoOpenConsole} powerAction={AppearanceSettings.NormalizePowerAction(settings.DefaultPowerAction)}";
    }

    internal static async Task ExportAsync(string destination, AppearanceSettings settings)
    {
        string header = EnvironmentHeader(settings);
        await Task.Run(async () =>
        {
            string hypervisor = "Unknown", vmms = "Unknown", scheduler = "Unknown";
            try
            {
                var status = await HyperVHostService.GetHyperVDiagnosticsAsync().ConfigureAwait(false);
                hypervisor = status.Hypervisor.State.ToString();
                vmms = status.Vmms.State == HyperVCheckState.NotChecked
                    ? HyperVHostService.GetVmmsStatus() switch { 1 => "Running", 2 => "NotRunning", _ => "UnknownOrMissing" }
                    : $"{status.Vmms.State} ({status.Vmms.DiagnosticKind})";
            }
            catch (Exception ex) { Failure("Environment", "HyperVQueryFailed", ex); }
            try { scheduler = HyperVSchedulerService.GetSchedulerType().ToString(); }
            catch (Exception ex) { Failure("Environment", "SchedulerQueryFailed", ex); }

            DiagnosticBundle.Export(log!, Path.Combine(DataDirectory, "Config.xml"), destination,
                $"{header}\nhypervisor={hypervisor}\nvmms={vmms}\nscheduler={scheduler}");
        });
    }

    internal static Task ClearAsync() => Task.Run(() => log!.Clear());
}
