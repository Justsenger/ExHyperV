using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Properties;
using ExHyperV.Interaction;
using ExHyperV.Services;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using TextBlock = Wpf.Ui.Controls.TextBlock;

using ExHyperV.Views;
namespace ExHyperV.ViewModels
{
    public partial class PCIePageViewModel : ObservableObject
    {
        // ===== 字段 =====

        private readonly PCIeService _pcieService;

        // ===== 绑定属性与命令 =====

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private bool _showServerError;

        [ObservableProperty]
        private bool _isUiEnabled = true;

        public ObservableCollection<DeviceViewModel> Devices { get; }
        public IAsyncRelayCommand LoadDataCommand { get; }
        public IAsyncRelayCommand<object> ChangeAssignmentCommand { get; }

        // ===== 构造 =====

        public PCIePageViewModel()
        {
            _pcieService = new PCIeService();
            Devices = new ObservableCollection<DeviceViewModel>();
            LoadDataCommand = new AsyncRelayCommand(LoadDataAsync);
            ChangeAssignmentCommand = new AsyncRelayCommand<object>(ChangeAssignmentAsync);
            LoadDataCommand.Execute(null);
        }

        // ===== 业务方法 =====

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
                var serverCheckTask = _pcieService.IsServerOperatingSystemAsync();
                var pcieInfoTask = _pcieService.GetPCIeInfoAsync();
                await Task.WhenAll(serverCheckTask, pcieInfoTask);

                Devices.Clear();

                ShowServerError = !await serverCheckTask;
                var (devices, vmNames) = await pcieInfoTask;

                if (devices != null)
                {
                    foreach (var deviceInfo in devices)
                        Devices.Add(new DeviceViewModel(deviceInfo, vmNames));
                }
            }
            finally
            {
                IsLoading = false;
                IsUiEnabled = true;
            }
        }

        private async Task ChangeAssignmentAsync(object parameter)
        {
            if (parameter is not object[] parameters || parameters.Length < 2 ||
                parameters[0] is not DeviceViewModel deviceViewModel ||
                parameters[1] is not string selectedTarget)
                return;

            if (deviceViewModel.Status == selectedTarget) return;

            IsUiEnabled = false;

            // MMIO空间检查流程
            if (selectedTarget != Resources.Host)
            {
                bool canProceed = await HandleMmioCheckAsync(selectedTarget);
                if (!canProceed)
                {
                    IsUiEnabled = true;
                    return;
                }
            }

            // 直接执行，不显示等待弹窗
            var (success, errorMessage) = await _pcieService.ExecutePCIeOperationAsync(
                selectedTarget,
                deviceViewModel.Status,
                deviceViewModel.InstanceId,
                deviceViewModel.Path
            );

            if (!success)
            {
                var errorDialog = new MessageBox
                {
                    Title = Properties.Resources.Dialog_Title_OperationFailed,
                    Content = new TextBlock
                    {
                        Text = string.Format(Properties.Resources.PCIePage_Error_ExecutionGeneric, errorMessage ?? Properties.Resources.Error_Unknown),
                        TextWrapping = System.Windows.TextWrapping.Wrap,
                        MaxWidth = 400
                    },
                    CloseButtonText = Resources.Btn_Confirm
                };
                await errorDialog.ShowDialogAsync();
            }

            // 操作完成后自动刷新
            await LoadDataCommand.ExecuteAsync(null);
        }

        private async Task<bool> HandleMmioCheckAsync(string targetVmName)
        {
            var (resultType, message) = await _pcieService.CheckMmioSpaceAsync(targetVmName);

            if (resultType == MmioCheckResultType.NeedsConfirmation)
            {
                bool confirmed = await Dialogs.ShowConfirmAsync(
                    ExHyperV.Properties.Resources.PCIePage_Title_MmioSpaceTooSmall, message,
                    ExHyperV.Properties.Resources.Button_Yes, ExHyperV.Properties.Resources.Button_No);
                if (!confirmed) return false;

                bool updateSuccess = await _pcieService.UpdateMmioSpaceAsync(targetVmName);
                if (!updateSuccess)
                {
                    var errorDialog = new MessageBox
                    {
                        Title = Properties.Resources.Error_Title,
                        Content = Resources.PCIePage_Error_UpdateMmioFailed,
                        CloseButtonText = Resources.Btn_Confirm
                    };
                    await errorDialog.ShowDialogAsync();
                    await LoadDataCommand.ExecuteAsync(null);
                    return false;
                }
            }
            else if (resultType == MmioCheckResultType.Error)
            {
                var errorDialog = new MessageBox
                {
                    Title = Resources.Error_Title,
                    Content = ExHyperV.Properties.Resources.PCIePage_Error_CheckMmioGeneric,
                    CloseButtonText = Resources.Btn_Confirm
                };
                await errorDialog.ShowDialogAsync();
                return false;
            }

            return true;
        }
    }
}