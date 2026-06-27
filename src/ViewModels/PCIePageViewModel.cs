using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Interaction;
using ExHyperV.Services;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using TextBlock = Wpf.Ui.Controls.TextBlock;
namespace ExHyperV.ViewModels
{
    public partial class PCIePageViewModel : PageViewModelBase
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
                parameters[1] is not AssignmentTarget target)
                return;

            string selectedTarget = target.Key; // Key 是身份（主机=HostKey、虚拟机=名）；Display 仅用于界面显示
            if (deviceViewModel.Status == selectedTarget) return;

            IsUiEnabled = false;
            try
            {
                // MMIO 空间不足时按算法自动扩展（不询问用户）
                if (selectedTarget != PCIeService.HostKey)
                {
                    bool canProceed = await EnsureMmioSpaceAsync(selectedTarget);
                    if (!canProceed) return;
                }

                // ── 最后一张显卡警告：直通主机当前唯一的显卡 → 主机将失去视频输出（物理屏幕黑屏风险）──
                if (selectedTarget != PCIeService.HostKey
                    && deviceViewModel.Status == PCIeService.HostKey
                    && string.Equals(deviceViewModel.ClassType, "Display", StringComparison.OrdinalIgnoreCase)
                    && Devices.Count(d => string.Equals(d.ClassType, "Display", StringComparison.OrdinalIgnoreCase)
                                          && d.Status == PCIeService.HostKey) <= 1)
                {
                    bool proceed = await Dialogs.ShowConfirmAsync(
                        Properties.Resources.PCIePage_Title_LastGpuWarning,
                        string.Format(Properties.Resources.PCIePage_Msg_LastGpuWarning, deviceViewModel.FriendlyName),
                        Properties.Resources.Button_Yes, Properties.Resources.Button_No,
                        isDanger: true, showIcon: false, maxWidth: 340);
                    if (!proceed) return;
                }

                // ── 存储控制器：直通到 VM 前处理名下磁盘（系统/启动盘拒绝；在线数据盘先脱机）──
                // 记录本次为直通而脱机的盘号；直通失败时控制器仍在主机，需把它们重新联机
                var offlinedDisks = new List<int>();
                if (selectedTarget != PCIeService.HostKey
                    && deviceViewModel.Status == PCIeService.HostKey
                    && (string.Equals(deviceViewModel.ClassType, "SCSIAdapter", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(deviceViewModel.ClassType, "HDC", StringComparison.OrdinalIgnoreCase)))
                {
                    var disks = await PCIeService.GetControllerDisksAsync(deviceViewModel.InstanceId);

                    // 系统盘/启动盘 → 硬拒绝（直通会使宿主无法启动）
                    var critical = disks.Where(d => d.IsSystem || d.IsBoot).ToList();
                    if (critical.Count > 0)
                    {
                        var dlg = new MessageBox
                        {
                            Title = Properties.Resources.Error_Title,
                            Content = new TextBlock
                            {
                                Text = string.Format(Properties.Resources.PCIePage_Msg_StorageHasSystemDisk,
                                    string.Join("\n", critical.Select(d => $"• #{d.Number} {d.FriendlyName}"))),
                                TextWrapping = System.Windows.TextWrapping.Wrap,
                                MaxWidth = 360
                            },
                            CloseButtonText = Properties.Resources.Btn_Confirm
                        };
                        await dlg.ShowDialogAsync();
                        return;
                    }

                    // 在线数据盘 → 确认后脱机
                    var onlineDisks = disks.Where(d => !d.IsOffline).ToList();
                    if (onlineDisks.Count > 0)
                    {
                        string diskList = string.Join("\n", onlineDisks.Select(d => $"• #{d.Number} {d.FriendlyName}"));
                        bool proceed = await Dialogs.ShowConfirmAsync(
                            Properties.Resources.PCIePage_Title_StorageOffline,
                            string.Format(Properties.Resources.PCIePage_Msg_StorageOffline, diskList),
                            Properties.Resources.Button_Yes, Properties.Resources.Button_No, showIcon: false);
                        if (!proceed) return;

                        foreach (var d in onlineDisks)
                        {
                            await HostDiskService.SetDiskOfflineStatusAsync(d.Number, true);
                            offlinedDisks.Add(d.Number);
                        }
                    }
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

                // 直通失败：控制器仍在主机，把先前为直通而脱机的数据盘重新联机，避免主机丢失访问
                if (!success && offlinedDisks.Count > 0)
                    foreach (var num in offlinedDisks)
                        await HostDiskService.SetDiskOfflineStatusAsync(num, false);

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

                // 存储控制器返还主机后：等磁盘随控制器重新枚举（轮询最多 ~6 秒），把之前脱机的盘重新联机
                if (success && selectedTarget == PCIeService.HostKey
                    && (string.Equals(deviceViewModel.ClassType, "SCSIAdapter", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(deviceViewModel.ClassType, "HDC", StringComparison.OrdinalIgnoreCase)))
                {
                    List<ControllerDisk> disks = new();
                    for (int i = 0; i < 4 && disks.Count == 0; i++)
                    {
                        await Task.Delay(1500);
                        disks = await PCIeService.GetControllerDisksAsync(deviceViewModel.InstanceId);
                    }
                    foreach (var d in disks.Where(d => d.IsOffline && !d.IsSystem && !d.IsBoot))
                        await HostDiskService.SetDiskOfflineStatusAsync(d.Number, false);
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

        private async Task<bool> EnsureMmioSpaceAsync(string targetVmName)
        {
            var result = await PCIeService.CheckMmioSpaceAsync(targetVmName);

            if (result == MmioCheckResultType.NeedsExpansion)
            {
                // MMIO 不足直接按算法扩展（需 VM 关机，与后续 DDA 步骤一致），不再询问用户
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
            else if (result == MmioCheckResultType.Error)
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