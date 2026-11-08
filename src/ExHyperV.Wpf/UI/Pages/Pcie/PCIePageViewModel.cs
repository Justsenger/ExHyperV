using ExHyperV.UI.Shared;
using ExHyperV.UI.Shared.Interaction;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using TextBlock = Wpf.Ui.Controls.TextBlock;
namespace ExHyperV.UI.Pages.Pcie
{
    public partial class PCIePageViewModel : PageViewModelBase
    {

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private bool _showServerError;

        [ObservableProperty]
        private bool _isUiEnabled = true;

        public ObservableCollection<DeviceViewModel> Devices { get; }


        public PCIePageViewModel()
        {
            Devices = new ObservableCollection<DeviceViewModel>();
            LoadDataCommand.Execute(null);
        }


        [RelayCommand]
        private async Task LoadDataAsync()
        {
            if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(new System.Windows.DependencyObject()))
            {
                IsLoading = false;
                IsUiEnabled = true;
                return;
            }

            IsUiEnabled = false;
            IsLoading = true;
            try
            {
                bool isServerSystem = HostProductTypeService.IsServerSystem();
                var result = await HostPcieService.GetPCIeInfoAsync();
                ShowServerError = !isServerSystem;
                if (!result.HasData)
                {
                    ShowError(string.IsNullOrWhiteSpace(result.Error) ? Properties.Resources.Error_Unknown : result.Error);
                    return;
                }
                Devices.Clear();
                foreach (var deviceInfo in result.Data.Devices)
                    Devices.Add(new DeviceViewModel(deviceInfo, result.Data.VirtualMachines));
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally
            {
                IsLoading = false;
                IsUiEnabled = true;
            }
        }

        [RelayCommand]
        private async Task ChangeAssignmentAsync(object parameter)
        {
            if (parameter is not object[] parameters || parameters.Length < 2 ||
                parameters[0] is not DeviceViewModel device ||
                parameters[1] is not AssignmentTarget target) return;
            if (!IsUiEnabled || device.Assignment == target.Target) return;

            IsUiEnabled = false;
            try
            {
                var session = new PcieAssignmentSession(Snapshot(device), Devices.Select(Snapshot), target.Target);
                var state = await session.AdvanceAsync();
                while (state.RequiresDecision)
                {
                    bool proceed = state.Step == PcieAssignmentStep.ConfirmLastGpu
                        ? await Dialogs.ShowConfirmAsync(state.Title, state.Message,
                            Properties.Resources.Button_Yes, Properties.Resources.Button_No,
                            isDanger: true, showIcon: false, maxWidth: 340)
                        : await Dialogs.ShowConfirmAsync(state.Title, state.Message,
                            Properties.Resources.Button_Yes, Properties.Resources.Button_No, showIcon: false);
                    state = await session.AdvanceAsync(new PcieAssignmentDecision(state.RequestId!.Value, proceed));
                }

                if (state.Error != PcieAssignmentError.None)
                {
                    object content = state.Error is PcieAssignmentError.SystemDisk or PcieAssignmentError.Execution
                        ? new TextBlock
                        {
                            Text = state.Message,
                            TextWrapping = System.Windows.TextWrapping.Wrap,
                            MaxWidth = state.Error == PcieAssignmentError.SystemDisk ? 360 : 400
                        }
                        : state.Message;
                    await new MessageBox
                    {
                        Title = state.Title, Content = content,
                        CloseButtonText = Properties.Resources.Btn_Confirm
                    }.ShowDialogAsync();
                }
                if (state.RefreshRequired) await LoadDataCommand.ExecuteAsync(null);
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { IsUiEnabled = true; }
        }

        private static ExHyperV.Models.PcieDeviceInfo Snapshot(DeviceViewModel device) => new()
        {
            FriendlyName = device.FriendlyName, ClassType = device.ClassType,
            InstanceId = device.InstanceId, Path = device.Path, Assignment = device.Assignment
        };
    }
}
