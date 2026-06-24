using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Interaction;
using ExHyperV.Services;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using TextBlock = Wpf.Ui.Controls.TextBlock;
namespace ExHyperV.ViewModels
{
    public partial class PCIePageViewModel : ObservableObject
    {
        // ===== 绑定属性与命令 =====

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private bool _showServerError;

        [ObservableProperty]
        private bool _isUiEnabled = true;

        public ObservableCollection<DeviceViewModel> Devices { get; }

        // ===== 构造 =====

        public PCIePageViewModel()
        {
            Devices = new ObservableCollection<DeviceViewModel>();
            LoadDataCommand.Execute(null);
        }

        // ===== 业务方法 =====

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
                var serverCheckTask = PCIeService.IsServerOperatingSystemAsync();
                var pcieInfoTask = PCIeService.GetPCIeInfoAsync();
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

        [RelayCommand]
        private async Task ChangeAssignmentAsync(object parameter)
        {
            if (parameter is not object[] parameters || parameters.Length < 2 ||
                parameters[0] is not DeviceViewModel deviceViewModel ||
                parameters[1] is not string selectedTarget)
                return;

            if (deviceViewModel.Status == selectedTarget) return;

            IsUiEnabled = false;
            try
            {
                // MMIO空间检查流程
                if (selectedTarget != Properties.Resources.Host)
                {
                    bool canProceed = await HandleMmioCheckAsync(selectedTarget);
                    if (!canProceed) return;
                }

                // ── 最后一张显卡警告：直通主机当前唯一的显卡 → 主机将失去视频输出（物理屏幕黑屏风险）──
                if (selectedTarget != Properties.Resources.Host
                    && deviceViewModel.Status == Properties.Resources.Host
                    && string.Equals(deviceViewModel.ClassType, "Display", StringComparison.OrdinalIgnoreCase)
                    && Devices.Count(d => string.Equals(d.ClassType, "Display", StringComparison.OrdinalIgnoreCase)
                                          && d.Status == Properties.Resources.Host) <= 1)
                {
                    bool proceed = await Dialogs.ShowConfirmAsync(
                        Properties.Resources.PCIePage_Title_LastGpuWarning,
                        string.Format(Properties.Resources.PCIePage_Msg_LastGpuWarning, deviceViewModel.FriendlyName),
                        Properties.Resources.Button_Yes, Properties.Resources.Button_No,
                        isDanger: true, showIcon: false, maxWidth: 340);
                    if (!proceed) return;
                }

                // ── 关联设备捆绑：移动显卡时，把与它当前在同一处的同卡设备（板载声卡等）一并带往目标 ──
                // 覆盖 主机→VM / VM→VM / VM→主机 三个方向；不做反向（仅显卡发起，声卡不反拉显卡）
                var companions = new List<DeviceViewModel>();
                if (string.Equals(deviceViewModel.ClassType, "Display", StringComparison.OrdinalIgnoreCase))
                {
                    string cardKey = PCIeService.CardKey(deviceViewModel.Path);
                    if (!string.IsNullOrEmpty(cardKey))
                    {
                        var siblings = Devices.Where(d =>
                                !string.Equals(d.InstanceId, deviceViewModel.InstanceId, StringComparison.OrdinalIgnoreCase)
                                && d.Status == deviceViewModel.Status        // 与显卡当前在同一处，才能一起移动
                                && !string.IsNullOrEmpty(d.Path)
                                && PCIeService.CardKey(d.Path) == cardKey)
                            .ToList();

                        if (siblings.Count > 0)
                        {
                            string list = string.Join("\n\n", siblings.Select(s => "• " + s.FriendlyName));
                            bool alsoAssign = await Dialogs.ShowConfirmAsync(
                                Properties.Resources.PCIePage_Title_CompanionDevice,
                                string.Format(Properties.Resources.PCIePage_Msg_CompanionDevice, deviceViewModel.FriendlyName, list, selectedTarget),
                                Properties.Resources.Button_Yes, Properties.Resources.Button_No, showIcon: false);
                            if (alsoAssign) companions = siblings;
                        }
                    }
                }

                // 先直通显卡本体，再逐个直通伴随设备，各自单独报告结果
                var errors = new List<string>();
                var (success, errorMessage) = await PCIeService.ExecutePCIeOperationAsync(
                    selectedTarget, deviceViewModel.Status, deviceViewModel.InstanceId, deviceViewModel.Path);
                if (!success)
                    errors.Add($"{deviceViewModel.FriendlyName}: {errorMessage ?? Properties.Resources.Error_Unknown}");

                if (success)
                {
                    foreach (var companion in companions)
                    {
                        var (cSuccess, cError) = await PCIeService.ExecutePCIeOperationAsync(
                            selectedTarget, companion.Status, companion.InstanceId, companion.Path);
                        if (!cSuccess)
                            errors.Add($"{companion.FriendlyName}: {cError ?? Properties.Resources.Error_Unknown}");
                    }
                }

                if (errors.Count > 0)
                {
                    var errorDialog = new MessageBox
                    {
                        Title = Properties.Resources.Dialog_Title_OperationFailed,
                        Content = new TextBlock
                        {
                            Text = string.Format(Properties.Resources.PCIePage_Error_ExecutionGeneric, string.Join("\n", errors)),
                            TextWrapping = System.Windows.TextWrapping.Wrap,
                            MaxWidth = 400
                        },
                        CloseButtonText = Properties.Resources.Btn_Confirm
                    };
                    await errorDialog.ShowDialogAsync();
                }

                // 操作完成后自动刷新
                await LoadDataCommand.ExecuteAsync(null);
            }
            finally
            {
                IsUiEnabled = true;
            }
        }

        private async Task<bool> HandleMmioCheckAsync(string targetVmName)
        {
            var (resultType, message) = await PCIeService.CheckMmioSpaceAsync(targetVmName);

            if (resultType == MmioCheckResultType.NeedsConfirmation)
            {
                bool confirmed = await Dialogs.ShowConfirmAsync(
                    Properties.Resources.PCIePage_Title_MmioSpaceTooSmall, message,
                    Properties.Resources.Button_Yes, Properties.Resources.Button_No);
                if (!confirmed) return false;

                bool updateSuccess = await PCIeService.UpdateMmioSpaceAsync(targetVmName);
                if (!updateSuccess)
                {
                    var errorDialog = new MessageBox
                    {
                        Title = Properties.Resources.Error_Title,
                        Content = Properties.Resources.PCIePage_Error_UpdateMmioFailed,
                        CloseButtonText = Properties.Resources.Btn_Confirm
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
                    Title = Properties.Resources.Error_Title,
                    Content = Properties.Resources.PCIePage_Error_CheckMmioGeneric,
                    CloseButtonText = Properties.Resources.Btn_Confirm
                };
                await errorDialog.ShowDialogAsync();
                return false;
            }

            return true;
        }
    }
}