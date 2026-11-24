
using ExHyperV.App.Localization;
using ExHyperV.App.UI.Shared.Feedback;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml;

namespace ExHyperV.App.UI.Pages.VirtualMachines;

public sealed partial class VirtualMachinesPage : Page
{
    public VirtualMachinesPageViewModel ViewModel { get; } = new();
    private bool loadedOnce;
    private bool restoringSelection;
    private int selectionVersion;

    internal event Action<bool>? ImmersiveRequested;

    public VirtualMachinesPage()
    {
        InitializeComponent();
        SearchBox.PlaceholderText = L10n.Get("VmList_SearchPlaceholder");
        AutomationProperties.SetName(SearchBox, L10n.Get("VmList_SearchAria"));
        AutomationProperties.SetName(VmList, L10n.Get("VmList_ListAria"));
        Dashboard.SetCache(ViewModel.Cache);
        Dashboard.ImmersiveRequested += SetImmersiveMode;
        Loaded += OnLoaded;
        Unloaded += (_, _) => { ViewModel.Cancel(); Dashboard.Stop(); };
        ViewModel.PropertyChanged += async (_, args) => await UiEvent.RunAsync(async () =>
        {
            if (args.PropertyName != nameof(ViewModel.Selected) || !IsLoaded || restoringSelection) return;
            int version = ++selectionVersion;
            if (!await Dashboard.SelectAsync(ViewModel.Selected) && version == selectionVersion)
            {
                restoringSelection = true;
                try { ViewModel.Selected = Dashboard.SelectedVm; }
                finally { restoringSelection = false; }
            }
        }, ViewModel.ReportError);
    }

    private async void OnLoaded(object sender, RoutedEventArgs args) => await UiEvent.RunAsync(async () =>
    {
        if (loadedOnce)
        {
            if (ViewModel.Selected is { } vm && ReferenceEquals(vm, Dashboard.SelectedVm))
            {
                SetImmersiveMode(Dashboard.IsSettingsImmersive);
                Dashboard.StartMonitoring(vm.Id);
                return;
            }
        }
        if (loadedOnce) { await Dashboard.SelectAsync(ViewModel.Selected); return; }
        loadedOnce = true;
        await ViewModel.RefreshCommand.ExecuteAsync(null);
    }, ViewModel.ReportError);

    private async void Refresh_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => await UiEvent.RunAsync(async () =>
    {
        args.Handled = true;
        if (ViewModel.RefreshCommand.CanExecute(null)) await ViewModel.RefreshCommand.ExecuteAsync(null);
    }, ViewModel.ReportError);

    public async Task StopAsync()
    {
        // 设置页可能因待核实或待清理的操作拒绝离开，此前不能终止仍需使用的查询缓存。
        await Dashboard.StopAsync();
        ViewModel.Cancel();
        await (ViewModel.RefreshCommand.ExecutionTask ?? Task.CompletedTask);
    }
    internal void SetImmersiveMode(bool immersive)
    {
        VmListPanel.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        VmListColumn.Width = immersive ? new GridLength(0) : new GridLength(220);
        PageRoot.ColumnSpacing = immersive ? 0 : 12;
        PageRoot.Margin = immersive ? new Thickness(0) : new Thickness(12);
        ImmersiveRequested?.Invoke(immersive);
    }
}
