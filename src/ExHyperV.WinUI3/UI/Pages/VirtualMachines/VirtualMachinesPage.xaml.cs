using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

public sealed partial class VirtualMachinesPage : Page
{
    public VirtualMachinesPageViewModel ViewModel { get; } = new();
    private bool loadedOnce;
    private bool restoringSelection;
    private int selectionVersion;

    public VirtualMachinesPage()
    {
        InitializeComponent();
        Dashboard.SetCache(ViewModel.Cache);
        Loaded += OnLoaded;
        Unloaded += (_, _) => { ViewModel.Cancel(); Dashboard.Stop(); };
        ViewModel.PropertyChanged += async (_, args) =>
        {
            if (args.PropertyName != nameof(ViewModel.Selected) || !IsLoaded || restoringSelection) return;
            int version = ++selectionVersion;
            if (!await Dashboard.SelectAsync(ViewModel.Selected) && version == selectionVersion)
            {
                restoringSelection = true;
                try { ViewModel.Selected = Dashboard.SelectedVm; }
                finally { restoringSelection = false; }
            }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (loadedOnce) { ViewModel.ResumePreload(); await Dashboard.SelectAsync(ViewModel.Selected); return; }
        loadedOnce = true;
        await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    private async void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (ViewModel.RefreshCommand.CanExecute(null)) await ViewModel.RefreshCommand.ExecuteAsync(null);
    }

    public async Task StopAsync()
    {
        // 设置页可能因待核实或待清理的操作拒绝离开，此前不能终止仍需使用的查询缓存。
        await Dashboard.StopAsync();
        ViewModel.Cancel();
        await Task.WhenAll(ViewModel.RefreshCommand.ExecutionTask ?? Task.CompletedTask, ViewModel.Cache.StopAsync());
    }
    public Task<bool> PrepareToLeaveAsync() => Dashboard.TryCloseSettingsAsync();
}
