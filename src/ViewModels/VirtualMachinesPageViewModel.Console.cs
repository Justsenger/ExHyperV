using ExHyperV.Interaction;

namespace ExHyperV.ViewModels;

public partial class VirtualMachinesPageViewModel
{
    private void OnPowerActionSucceeded(VmInstanceViewModel instance, string action, bool openConsoleOnStart)
    {
        if (action == "Start" || action == "Restart") TryApplyAffinityForRootScheduler(instance);
        if (openConsoleOnStart && action == "Start" && instance.IsRunning)
            Navigation.OpenConsoleWindow(instance.Id.ToString(), instance.Name);
    }
}
