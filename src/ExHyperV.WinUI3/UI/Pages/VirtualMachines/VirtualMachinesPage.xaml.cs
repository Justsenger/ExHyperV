using ExHyperV.WinUI3.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ExHyperV.WinUI3.UI.Pages.VirtualMachines;

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
        if (loadedOnce) { await Dashboard.SelectAsync(ViewModel.Selected); return; }
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
        await (ViewModel.RefreshCommand.ExecutionTask ?? Task.CompletedTask);
    }
    public Task<bool> PrepareToLeaveAsync() => Dashboard.TryCloseSettingsAsync();

    internal void SetImmersiveMode(bool immersive)
    {
        VmListPanel.Visibility = immersive ? Visibility.Collapsed : Visibility.Visible;
        VmListColumn.Width = immersive ? new GridLength(0) : new GridLength(220);
        PageRoot.ColumnSpacing = immersive ? 0 : 12;
        PageRoot.Margin = immersive ? new Thickness(0) : new Thickness(12);
        ImmersiveRequested?.Invoke(immersive);
    }
}
