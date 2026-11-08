using ExHyperV.UI.Shared.Interaction;

namespace ExHyperV.UI.Pages.VirtualMachines;

public partial class VirtualMachinesPageViewModel
{
    private void OnPowerActionSucceeded(VmInstanceViewModel instance, string action, bool openConsoleOnStart)
    {
        if (openConsoleOnStart && action == "Start" && instance.IsRunning)
            Navigation.OpenConsoleWindow(instance.Id.ToString(), instance.Name);
    }
}
