using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Services;
using ExHyperV.Properties;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using MessageBox = Wpf.Ui.Controls.MessageBox;

namespace ExHyperV.ViewModels
{
    /// <summary>
    /// DDAPage页面的ViewModel，负责管理DDA设备数据和用户交互逻辑。
    /// </summary>
    public partial class DDAPageViewModel : ObservableObject
    {
        private readonly IHyperVService _hyperVService;

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
            _hyperVService = new HyperVService();
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
                var serverCheckTask = _hyperVService.IsServerOperatingSystemAsync();
                var ddaInfoTask = _hyperVService.GetDdaInfoAsync();
                await Task.WhenAll(serverCheckTask, ddaInfoTask);

                Devices.Clear();

                ShowServerError = !await serverCheckTask;
                var (devices, vmNames) = await ddaInfoTask;

                if (devices != null)
                {
                    foreach (var deviceInfo in devices)
                    {
                        Devices.Add(new DeviceViewModel(deviceInfo, vmNames));
                    }
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
            {
                return;
            }

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

            // DDA设备分配流程
            await PerformDdaAssignmentAsync(deviceViewModel, selectedTarget);

            // 最终刷新
            await LoadDataCommand.ExecuteAsync(null);
        }

        /// <summary>
        /// 管理MMIO空间检查和用户确认流程。
        /// </summary>
        private async Task<bool> HandleMmioCheckAsync(string targetVmName)
        {
            var (resultType, message) = await _hyperVService.CheckMmioSpaceAsync(targetVmName);

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

                var shutdownDialog = new ContentDialog
                {
                    Title = ExHyperV.Properties.Resources.Dialog_Title_PleaseWait,
                    Content = new TextBlock { Text = ExHyperV.Properties.Resources.DdaPage_Status_ShuttingDownVm, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                    DialogHost = ((MainWindow)Application.Current.MainWindow).ContentPresenterForDialogs,
                };
                var shutdownDialogTask = shutdownDialog.ShowAsync();
                bool updateSuccess = await _hyperVService.UpdateMmioSpaceAsync(targetVmName);
                shutdownDialog.Hide();
                await shutdownDialogTask;

                if (!updateSuccess)
                {
                    var errorDialog = new MessageBox { Title = Properties.Resources.error, Content = Resources.DdaPage_Error_UpdateMmioFailed, CloseButtonText = Resources.sure };
                    await errorDialog.ShowDialogAsync();
                    await LoadDataCommand.ExecuteAsync(null);
                    return false;
                }
            }
            else if (resultType == MmioCheckResultType.Error)
            {
                var errorDialog = new MessageBox { Title = Resources.error, Content = ExHyperV.Properties.Resources.DdaPage_Error_CheckMmioGeneric, CloseButtonText = Resources.sure };
                await errorDialog.ShowDialogAsync();
                return false;
            }

            return true;
        }

        /// <summary>
        /// 执行DDA分配并显示一个等待对话框。
        /// </summary>
        private async Task PerformDdaAssignmentAsync(DeviceViewModel device, string target)
        {
            var waitDialog = new ContentDialog
            {
                Title = Resources.setting,
                Content = new TextBlock { Text = ExHyperV.Properties.Resources.DdaPage_Status_AssigningDevice, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                DialogHost = ((MainWindow)Application.Current.MainWindow).ContentPresenterForDialogs,
            };
            var dialogTask = waitDialog.ShowAsync();

            var (success, errorMessage) = await _hyperVService.ExecuteDdaOperationAsync(target, device.Status, device.InstanceId, device.Path);

            if (success)
            {
                waitDialog.Hide();
            }
            else
            {
                waitDialog.Title = ExHyperV.Properties.Resources.Dialog_Title_OperationFailed;
                waitDialog.Content = new ScrollViewer { Content = new TextBlock { Text = string.Format(Properties.Resources.DdaPage_Error_ExecutionGeneric, errorMessage ?? Properties.Resources.Error_Unknown) } };
                waitDialog.CloseButtonText = ExHyperV.Properties.Resources.Close;
            }
            await dialogTask;
        }
    }
}