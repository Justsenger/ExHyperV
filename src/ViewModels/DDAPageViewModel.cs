using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Properties;
using ExHyperV.Services;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using TextBlock = Wpf.Ui.Controls.TextBlock;

namespace ExHyperV.ViewModels
{
    public partial class DDAPageViewModel : ObservableObject
    {
        private readonly HyperVDDAService _DDAService;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private bool _showServerError;

        [ObservableProperty]
        private bool _isUiEnabled = true;

        public ObservableCollection<DeviceViewModel> Devices { get; }
        public IAsyncRelayCommand LoadDataCommand { get; }
        public IAsyncRelayCommand<object> ChangeAssignmentCommand { get; }

        public DDAPageViewModel()
        {
            _DDAService = new HyperVDDAService();
            Devices = new ObservableCollection<DeviceViewModel>();
            LoadDataCommand = new AsyncRelayCommand(LoadDataAsync);
            ChangeAssignmentCommand = new AsyncRelayCommand<object>(ChangeAssignmentAsync);
            LoadDataCommand.Execute(null);
        }

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
                var serverCheckTask = _DDAService.IsServerOperatingSystemAsync();
                var ddaInfoTask = _DDAService.GetDdaInfoAsync();
                await Task.WhenAll(serverCheckTask, ddaInfoTask);

                Devices.Clear();

                ShowServerError = !await serverCheckTask;
                var (devices, vmNames) = await ddaInfoTask;

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
            var (success, errorMessage) = await _DDAService.ExecuteDdaOperationAsync(
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
                        Text = string.Format(Properties.Resources.DdaPage_Error_ExecutionGeneric, errorMessage ?? Properties.Resources.Error_Unknown),
                        TextWrapping = System.Windows.TextWrapping.Wrap,
                        MaxWidth = 400
                    },
                    CloseButtonText = Resources.sure
                };
                await errorDialog.ShowDialogAsync();
            }

            // 操作完成后自动刷新
            await LoadDataCommand.ExecuteAsync(null);
        }

        private async Task<bool> HandleMmioCheckAsync(string targetVmName)
        {
            var (resultType, message) = await _DDAService.CheckMmioSpaceAsync(targetVmName);

            if (resultType == MmioCheckResultType.NeedsConfirmation)
            {
                var confirmDialog = new ContentDialog
                {
                    Title = ExHyperV.Properties.Resources.DdaPage_Title_MmioSpaceTooSmall,
                    Content = message,
                    PrimaryButtonText = ExHyperV.Properties.Resources.Button_Yes,
                    CloseButtonText = ExHyperV.Properties.Resources.Button_No,
                    DialogHost = ((MainWindow)Application.Current.MainWindow).ContentPresenterForDialogs
                };
                var result = await confirmDialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return false;

                bool updateSuccess = await _DDAService.UpdateMmioSpaceAsync(targetVmName);
                if (!updateSuccess)
                {
                    var errorDialog = new MessageBox
                    {
                        Title = Properties.Resources.error,
                        Content = Resources.DdaPage_Error_UpdateMmioFailed,
                        CloseButtonText = Resources.sure
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
                    Title = Resources.error,
                    Content = ExHyperV.Properties.Resources.DdaPage_Error_CheckMmioGeneric,
                    CloseButtonText = Resources.sure
                };
                await errorDialog.ShowDialogAsync();
                return false;
            }

            return true;
        }
    }
}