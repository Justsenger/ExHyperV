
using ExHyperV.App.Localization;
using ExHyperV.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace ExHyperV.App.UI.Pages.VirtualMachines.Settings.Pcie;

internal static class PcieSettingsChange
{
    internal static async Task<VmPcieChangeResult> ApplyAsync(FrameworkElement owner,
        VmPcieChangeSession session, CancellationToken token)
    {
        bool confirmed = false;
        if (session.RequiresConfirmation)
        {
            token.ThrowIfCancellationRequested();
            var dialog = new ContentDialog
            {
                XamlRoot = owner.XamlRoot,
                RequestedTheme = owner.ActualTheme,
                Title = L10n.Get("Pcie_Emulation"),
                Content = L10n.Get("Pcie_PermanentConfirmation"),
                PrimaryButtonText = L10n.Get("Common_Confirm"),
                CloseButtonText = L10n.Get("Common_Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            using var registration = token.Register(() => owner.DispatcherQueue.TryEnqueue(dialog.Hide));
            confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        token.ThrowIfCancellationRequested();
        return await Task.Run(() => session.ApplyAsync(confirmed));
    }
}
