using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Properties;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;
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
                    Title = "MMIO空间过小",
                    Content = message,
                    PrimaryButtonText = "是",
                    CloseButtonText = "否",
                    DialogHost = ((MainWindow)Application.Current.MainWindow).ContentPresenterForDialogs
                };
                var result = await confirmDialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return false;

                var shutdownDialog = new ContentDialog
                {
                    Title = "请稍候",
                    Content = new TextBlock { Text = $"正在关闭虚拟机 '{targetVmName}' ...", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
                    DialogHost = ((MainWindow)Application.Current.MainWindow).ContentPresenterForDialogs,
                };
                var shutdownDialogTask = shutdownDialog.ShowAsync();
                bool updateSuccess = await _hyperVService.UpdateMmioSpaceAsync(targetVmName);
                shutdownDialog.Hide();
                await shutdownDialogTask;

                if (!updateSuccess)
                {
                    var errorDialog = new MessageBox { Title = "错误", Content = "更新MMIO空间失败。", CloseButtonText = "确定" };
                    await errorDialog.ShowDialogAsync();
                    await LoadDataCommand.ExecuteAsync(null);
                    return false;
                }
            }
            else if (resultType == MmioCheckResultType.Error)
            {
                var errorDialog = new MessageBox { Title = "错误", Content = $"检查MMIO空间时出错: {message}", CloseButtonText = "确定" };
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
                Content = new TextBlock { Text = "正在执行设备分配，请稍候...", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
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
                waitDialog.Title = "操作失败";
                waitDialog.Content = new ScrollViewer { Content = new TextBlock { Text = $"执行时遇到错误：\n\n{errorMessage ?? "未知错误"}", TextWrapping = TextWrapping.Wrap } };
                waitDialog.CloseButtonText = "关闭";
            }
            await dialogTask;
        }
    }
}