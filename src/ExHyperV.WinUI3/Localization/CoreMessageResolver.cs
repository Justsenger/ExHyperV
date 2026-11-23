using ExHyperV.Models;
using ExHyperV.Tools;

namespace ExHyperV.WinUI3.Localization;

internal static class CoreMessageResolver
{
    internal static string? ResolveConditionReason(VmModificationCondition condition)
    {
        if (condition.CanModify) return null;
        return condition.ReasonCode switch
        {
            ConditionReasonCode.PoweredOffRequired => L10n.Get("Cond_PoweredOff"),
            ConditionReasonCode.DynamicMemoryRunningStartupLocked => L10n.Get("Cond_DynRamStartup"),
            ConditionReasonCode.HostSettingMissing => L10n.Get("Cond_HostSettingMissing"),
            ConditionReasonCode.UnsupportedVmVersion => L10n.Get("Cond_UnsupportedVmVersion"),
            ConditionReasonCode.RequiresX64 => L10n.Get("Cond_RequiresX64"),
            ConditionReasonCode.RequiresArm64 => L10n.Get("Cond_RequiresArm64"),
            ConditionReasonCode.RequiresIntel => L10n.Get("Cond_RequiresIntel"),
            ConditionReasonCode.RequiresHardwareIsolation => L10n.Get("Cond_RequiresHardwareIsolation"),
            ConditionReasonCode.RequiresPerfmonPmu => L10n.Get("Cond_RequiresPerfmonPmu"),
            ConditionReasonCode.UnknownSetting => L10n.Get("Cond_UnknownSetting"),
            _ => string.IsNullOrWhiteSpace(condition.Reason)
                ? L10n.Get("Cond_UnsupportedVmVersion")
                : condition.Reason
        };
    }

    internal static string ResolveError(ApiResponse response)
        => ResolveDiagnostic(response.DiagnosticCode, response.DiagnosticArgs, response.Exception, response.Error);

    internal static string ResolveError<T>(ApiResponse<T> response)
        => ResolveDiagnostic(response.DiagnosticCode, response.DiagnosticArgs, response.Exception, response.Error);

    internal static string ResolveException(Exception exception)
    {
        if (exception is CoreOperationException core)
            return ResolveError(core.Result);
        var code = CoreErrorClassifier.ClassifyException(exception);
        return ResolveDiagnostic(code, Array.Empty<string>(), exception, exception.Message);
    }

    private static string ResolveDiagnostic(
        CoreErrorCode diagnosticCode,
        IReadOnlyList<string> args,
        Exception? exception,
        string? rawError)
    {
        switch (diagnosticCode)
        {
            case CoreErrorCode.VmmsCrashedCxlRecovered:
                return L10n.Get("Err_VmmsCrashedCxlRecovered");
            case CoreErrorCode.VmmsCrashedRecovered:
                return L10n.Get("Err_VmmsCrashedRecovered");
            case CoreErrorCode.VmmsCrashedVnumaUnrestored:
                return L10n.Get("Err_VmmsCrashedVnumaUnrestored");
            case CoreErrorCode.VmmsRecoveryFailed:
                return args.Count > 0 && !string.IsNullOrWhiteSpace(args[0])
                    ? L10n.Format("Err_VmmsRecoveryFailedWithDetail", args[0])
                    : L10n.Get("Err_VmmsRecoveryFailed");
            case CoreErrorCode.VmmsRecoveredOutcomeUnknown:
                return L10n.Get("Err_VmmsRecoveredOutcomeUnknown");
            case CoreErrorCode.VmmsUnavailableOrShuttingDown:
                return L10n.Get("Err_VmmsUnavailable");
            case CoreErrorCode.AccessDenied:
                return L10n.Get("Err_AccessDenied");
            case CoreErrorCode.NotFound:
                return L10n.Get("Err_NotFound");
            case CoreErrorCode.Unsupported:
                return L10n.Get("Err_Unsupported");
            case CoreErrorCode.InvalidState:
                return L10n.Get("Err_InvalidState");
            case CoreErrorCode.OperationTimedOut:
                return L10n.Get("Err_TimedOut");
            case CoreErrorCode.OperationCancelled:
                return L10n.Get("Err_Cancelled");
            case CoreErrorCode.ReadBackUnavailable:
                return L10n.Get("Err_ReadBackUnavailable");
            case CoreErrorCode.ReadBackMismatch:
                return args.Count >= 3
                    ? L10n.Format("Err_ReadBackMismatch", args[0], args[1], args[2])
                    : Innermost(rawError, exception);
            case CoreErrorCode.InsufficientMemory:
                return ResolveInsufficientMemoryMessage(args);
            case CoreErrorCode.FileInUse:
                return L10n.Get("Err_FileInUse");
            case CoreErrorCode.FileNotFound:
                return L10n.Get("Err_FileNotFound");
            case CoreErrorCode.DiskFull:
                return L10n.Get("Err_DiskFull");
            case CoreErrorCode.DifferencingParentNotFound:
                return L10n.Get("Err_DifferencingParentNotFound");
            case CoreErrorCode.VirtualSwitchNotFound:
                return L10n.Get("Err_VirtualSwitchNotFound");
            case CoreErrorCode.StartFailedProcessorTopology:
                return args.Count >= 2 && !string.IsNullOrWhiteSpace(args[0]) && !string.IsNullOrWhiteSpace(args[1])
                    ? L10n.Format("Err_StartFailedProcessorTopology", args[0], args[1])
                    : L10n.Get("Err_StartFailedUnsupported");
            case CoreErrorCode.RuntimeMemoryResizeFailed:
                return L10n.Get("Err_RuntimeMemoryResizeFailed");
            case CoreErrorCode.StartFailedGen1OnArm64:
                return L10n.Get("Err_StartFailedGen1OnArm64");
            case CoreErrorCode.StartUnsupportedWithSuspects:
                if (args.Count > 0)
                {
                    string list = string.Join(L10n.Get("Common_ListSeparator"),
                        args.Where(a => !string.IsNullOrWhiteSpace(a)).Select(ResolveSuspectName));
                    if (!string.IsNullOrWhiteSpace(list))
                        return L10n.Format("Err_StartUnsupportedWithSuspects", list);
                }
                return L10n.Get("Err_StartFailedUnsupported");
            case CoreErrorCode.StartFailedUnsupported:
                return args.Count > 0 && !string.IsNullOrWhiteSpace(args[0])
                    ? $"{L10n.Get("Err_StartFailedUnsupported")}\n{args[0]}"
                    : L10n.Get("Err_StartFailedUnsupported");
            case CoreErrorCode.PartialVnumaDisableFailed:
                return L10n.Format("Err_PartialVnumaDisable",
                    args.Count > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : Innermost(rawError, exception));
            case CoreErrorCode.PartialVnumaRestoreFailed:
                return L10n.Format("Err_PartialVnumaRestore",
                    args.Count > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : Innermost(rawError, exception));
            case CoreErrorCode.InvalidParameter:
                return args.Count > 0 && !string.IsNullOrWhiteSpace(args[0])
                    ? args[0]
                    : L10n.Get("Err_InvalidParameter");
            case CoreErrorCode.MethodFailed:
                return args.Count > 0 && !string.IsNullOrWhiteSpace(args[0])
                    ? args[0]
                    : L10n.Get("Err_MethodFailed");
            default:
                return Innermost(rawError, exception);
        }
    }

    private static string ResolveInsufficientMemoryMessage(IReadOnlyList<string> args)
    {
        if (args.Count >= 4
            && long.TryParse(args[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long requiredMiB)
            && long.TryParse(args[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long availableMiB)
            && long.TryParse(args[2], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long totalMiB)
            && long.TryParse(args[3], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long deficitMiB)
            && totalMiB > 0)
        {
            if (requiredMiB > totalMiB)
            {
                return L10n.Format("Err_InsufficientMemoryExceedsTotal",
                    Math.Max(1L, deficitMiB), availableMiB, totalMiB);
            }

            if (deficitMiB > 0)
            {
                return L10n.Format("Err_InsufficientMemoryDeficit",
                    deficitMiB, availableMiB, totalMiB);
            }

            return L10n.Format("Err_InsufficientMemoryOverhead",
                availableMiB, totalMiB);
        }

        return args.Count > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? L10n.Format("Err_InsufficientMemoryWithSize", args[0])
            : L10n.Get("Err_InsufficientMemory");
    }

    private static string ResolveSuspectName(string token) => token switch
    {
        "SGX" => L10n.Get("Suspect_SGX"),
        "CXL" => L10n.Get("Suspect_CXL"),
        "HugePages1Gb" => L10n.Get("Suspect_HugePages1Gb"),
        "MemEncryption" => L10n.Get("Suspect_MemEncryption"),
        "IntelPerfmon" => L10n.Get("Suspect_IntelPerfmon"),
        "HwIsolation" => L10n.Get("Suspect_HwIsolation"),
        "ApicMode" => L10n.Get("Suspect_ApicMode"),
        "LpiMode" => L10n.Get("Suspect_LpiMode"),
        "NestedVirt" => L10n.Get("Suspect_NestedVirt"),
        _ => token
    };

    private static string Innermost(string? rawError, Exception? exception)
    {
        if (!string.IsNullOrWhiteSpace(rawError)) return rawError.Trim();
        var ex = exception;
        while (ex?.InnerException != null) ex = ex.InnerException;
        if (!string.IsNullOrWhiteSpace(ex?.Message)) return ex.Message.Trim();
        return L10n.Get("Err_GenericDefault");
    }
}
