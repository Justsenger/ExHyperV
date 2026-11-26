using ExHyperV.App.Localization;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ExHyperV.App.UI.Shared.Feedback;

/// <summary>桌面端虚拟机电源降级协助逻辑，保留 Core 显式安全契约。</summary>
internal static class VmPowerFallback
{
    internal static bool CanEscalate(ApiResponse? response) =>
        response is { Success: false }
        && response.ErrorKind is not (ApiErrorKind.OutcomeUnknown or ApiErrorKind.Cancelled)
        && response.DiagnosticCode is not (
            CoreErrorCode.AccessDenied
            or CoreErrorCode.NotFound
            or CoreErrorCode.VmmsUnavailableOrShuttingDown);

    internal static async Task<(bool Escalated, ApiResponse Result)> TryForceResetAsync(Guid vmId, ApiResponse failure)
    {
        if (!CanEscalate(failure)) return (false, failure);
        var turnOff = await Task.Run(() => VmPowerService.ExecuteControlActionAsync(vmId, VmPowerAction.TurnOff));
        if (!turnOff.Success) return (false, turnOff);
        var start = await Task.Run(() => VmPowerService.ExecuteControlActionAsync(vmId, VmPowerAction.Start));
        return (start.Success, start);
    }

    internal static async Task<bool> ConfirmForceTurnOffAsync(
        XamlRoot xamlRoot,
        ElementTheme theme,
        string vmName,
        string failureReason,
        Action<ContentDialog?>? trackDialog = null)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            RequestedTheme = theme,
            Title = L10n.Format("Dash_Power_StopFallbackTitle", vmName),
            Content = L10n.Format("Dash_Power_StopFallbackBody", failureReason),
            PrimaryButtonText = L10n.Get("Dash_Power_ForceTurnOff"),
            CloseButtonText = L10n.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        trackDialog?.Invoke(dialog);
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            trackDialog?.Invoke(null);
        }
    }
}
