using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Properties;
using ExHyperV.Services;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using TextBlock = Wpf.Ui.Controls.TextBlock;

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
            _hyperVService = new DDAService();
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
                    Content = new TextBlock { Text = string.Format(ExHyperV.Properties.Resources.DdaPage_Status_ShuttingDownVm, targetVmName), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
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
        /// <summary>
        /// 执行DDA分配并显示一个可以报告详细进度的等待对话框。
        /// </summary>
        private async Task PerformDdaAssignmentAsync(DeviceViewModel device, string target)
        {
            var statusTextBlock = new TextBlock
            {
                Text = "", // 初始文本
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var waitDialog = new ContentDialog
            {
                Title = Resources.setting,
                Content = statusTextBlock,
                DialogHost = ((MainWindow)Application.Current.MainWindow).ContentPresenterForDialogs,
                CloseButtonText = ExHyperV.Properties.Resources.Close
            };
            bool isOperationInProgress = true;

            waitDialog.Closing += (sender, args) =>
            {
                if (isOperationInProgress)
                {
                    args.Cancel = true;
                }
            };

            var progressReporter = new Progress<string>(message =>
            {
                statusTextBlock.Text = message;
            });
            var dialogTask = waitDialog.ShowAsync();
            var (success, errorMessage) = await _hyperVService.ExecuteDdaOperationAsync(
                target,
                device.Status,
                device.InstanceId,
                device.Path,
                progressReporter 
            );
            isOperationInProgress = false;
            if (success)
            {
                waitDialog.Hide();
            }
            else
            {
                waitDialog.Title = ExHyperV.Properties.Resources.Dialog_Title_OperationFailed;
                waitDialog.Content = new ScrollViewer
                {
                    Content = new TextBlock
                    {
                        Text = string.Format(Properties.Resources.DdaPage_Error_ExecutionGeneric, errorMessage ?? Properties.Resources.Error_Unknown),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
            }
            await dialogTask;
        }
    }
}