using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public partial class VirtualMachinesPageViewModel
    {
        // ===== 视图模型属性 - GPU 管理 =====
        [ObservableProperty] private ObservableCollection<GpuInfo> _hostGpus = new();
        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ConfirmAddGpuCommand))] private GpuInfo _selectedHostGpu;
        [ObservableProperty] private bool _autoInstallDrivers = true;
        [ObservableProperty] private ObservableCollection<TaskItem> _gpuTasks = new();
        [ObservableProperty] private bool _showPartitionSelector = false;
        [ObservableProperty] private ObservableCollection<PartitionInfo> _detectedPartitions = new();
        [ObservableProperty] private PartitionInfo? _selectedPartition;
        [ObservableProperty] private bool _showSshForm = false;
        private string? _currentProcessingGpuAdapterId;
        private bool _needConfig = false;

        // Linux SSH 凭据
        [ObservableProperty] private string _sshHost = "";
        [ObservableProperty] private string _sshUsername = "root";
        [ObservableProperty] private string _sshPassword = "";
        [ObservableProperty] private int _sshPort = 22;
        [ObservableProperty] private bool _installGraphics = true;
        [ObservableProperty] private bool _useSshProxy = false;
        [ObservableProperty] private string _sshProxyHost = "";
        [ObservableProperty] private string _sshProxyPort = "";
        private CancellationTokenSource? _gpuDeploymentCts;

        // 日志与控制台
        [ObservableProperty] private string _gpuDeploymentLog = string.Empty;
        [ObservableProperty] private bool _showLogConsole = false;


        // ===== GPU 管理模块 - 列表与基础操作 =====

        // 导航至 GPU 管理页面
        [RelayCommand]
        private async Task GoToGpuSettingsAsync()
        {
            if (SelectedVm == null) return;

            CurrentViewType = VmDetailViewType.GpuSettings;
            IsLoadingSettings = true;
            try
            {
                await RefreshCurrentVmGpuAssignments();
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Common_LoadFail, Properties.Resources.Error_Gpu_ReadInfo + ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 刷新当前虚拟机的显卡分配情况
        private async Task RefreshCurrentVmGpuAssignments()
        {
            if (SelectedVm == null) return;
            try
            {
                var vmAdapters = await _vmGpuService.GetVmGpuAdaptersAsync(SelectedVm.Name);
                var hostGpus = await _vmGpuService.GetHostGpusAsync();

                var tempList = new List<VmGpuAssignment>();

                foreach (var adapter in vmAdapters)
                {
                    var matchedHostGpu = hostGpus.FirstOrDefault(h =>
                        !string.IsNullOrEmpty(h.InstanceId) &&
                        !string.IsNullOrEmpty(adapter.InstancePath) &&
                        (adapter.InstancePath.Contains(h.InstanceId, StringComparison.OrdinalIgnoreCase) ||
                         NormalizeDeviceId(h.InstanceId) == NormalizeDeviceId(adapter.InstancePath)));

                    var assignment = new VmGpuAssignment { AdapterId = adapter.Id };

                    if (matchedHostGpu != null)
                    {
                        assignment.Name = matchedHostGpu.Name;
                        assignment.Manu = matchedHostGpu.Manu;
                        assignment.Vendor = matchedHostGpu.Vendor;
                        assignment.DriverVersion = matchedHostGpu.DriverVersion;
                        assignment.Ram = matchedHostGpu.Ram;
                        assignment.PName = matchedHostGpu.Pname;
                    }
                    else
                    {
                        assignment.Name = "Unknown Device";
                        assignment.Manu = "Default";
                    }
                    tempList.Add(assignment);
                }

                Application.Current.Dispatcher.Invoke(() => {
                    bool isHardwareSame = SelectedVm.AssignedGpus.Count == tempList.Count &&
                                         SelectedVm.AssignedGpus.Select(x => x.AdapterId)
                                                      .SequenceEqual(tempList.Select(x => x.AdapterId));

                    if (isHardwareSame)
                    {
                        for (int i = 0; i < tempList.Count; i++)
                        {
                            var target = SelectedVm.AssignedGpus[i];
                            var source = tempList[i];
                            target.Name = source.Name;
                            target.Manu = source.Manu;
                            target.Vendor = source.Vendor;
                            target.DriverVersion = source.DriverVersion;
                            target.Ram = source.Ram;
                            target.PName = source.PName;
                        }
                    }
                    else
                    {
                        SelectedVm.AssignedGpus.Clear();
                        foreach (var item in tempList) SelectedVm.AssignedGpus.Add(item);
                    }

                    SelectedVm.RefreshGpuSummary();
                });
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Gpu_RefreshFail, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }

        // 移除 GPU 分区
        [RelayCommand]
        private async Task RemoveGpuAsync(string adapterId)
        {
            if (SelectedVm == null || string.IsNullOrEmpty(adapterId)) return;

            var itemToRemove = SelectedVm.AssignedGpus.FirstOrDefault(x => x.AdapterId == adapterId);
            if (itemToRemove == null) return;

            IsLoadingSettings = true;
            try
            {
                bool success = await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, adapterId);

                if (success)
                {
                    Application.Current.Dispatcher.Invoke(() => {
                        SelectedVm.AssignedGpus.Remove(itemToRemove);
                        if (SelectedVm.AssignedGpus.Count == 0)
                        {
                            SelectedVm.GpuName = string.Empty;
                        }
                    });

                    ShowSnackbar(Properties.Resources.Common_Success, Properties.Resources.Msg_Gpu_PartitionRemoved, ControlAppearance.Success, SymbolRegular.Checkmark24);

                    await Task.Delay(2000);
                    await RefreshCurrentVmGpuAssignments();
                }
                else
                {
                    ShowSnackbar(Properties.Resources.Error_Storage_RemoveFail, Properties.Resources.Error_Gpu_RemoveFail, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Common_OpException, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }


        // ===== GPU 管理模块 - 部署向导与自动化 =====

        // 导航至添加 GPU 向导
        [RelayCommand]
        private async Task GoToAddGpuAsync()
        {
            if (SelectedVm == null) return;

            IsLoadingSettings = true;
            try
            {
                // 1. 加载 GPU 列表
                var gpus = await _vmGpuService.GetHostGpusAsync();
                HostGpus = new ObservableCollection<GpuInfo>(gpus);
                SelectedHostGpu = null;

                // 2. 加载 Linux 脚本列表 (重写部分)
                var scripts = await _vmGpuService.GetAvailableScriptsAsync();
                AvailableLinuxScripts = new ObservableCollection<LinuxScriptItem>(scripts);
                SelectedLinuxScript = AvailableLinuxScripts.FirstOrDefault(); // 默认选中第一个（通常是本地脚本）

                CurrentViewType = VmDetailViewType.AddGpuSelect;
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Common_Error, "Failed to load GPU or Scripts: " + ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 取消添加 GPU
        [RelayCommand]
        private async Task CancelAddGpuAsync() // 【修改为 async Task】
        {
            // 【新增：处理中途取消的回滚】
            if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId) && SelectedVm != null)
            {
                try
                {
                    await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                    _currentProcessingGpuAdapterId = null;
                }
                catch { } // 静默清理
            }

            CurrentViewType = VmDetailViewType.GpuSettings;
            GpuTasks.Clear();
        }

        partial void OnSelectedPartitionChanged(PartitionInfo? value)
        {
            if (value == null) return;
            _ = SelectPartitionAndContinueCommand.ExecuteAsync(value);
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                _selectedPartition = null;
                OnPropertyChanged(nameof(SelectedPartition));
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // 检查是否可以确认添加
        private bool CanConfirmAddGpu() => SelectedHostGpu != null;

        // 确认添加 GPU 并开始流程
        [RelayCommand(CanExecute = nameof(CanConfirmAddGpu))]
        private async Task ConfirmAddGpu()
        {
            if (SelectedHostGpu == null) return;

            CurrentViewType = VmDetailViewType.AddGpuProgress;
            ShowPartitionSelector = false;

            GpuDeploymentLog = string.Empty;
            ShowLogConsole = true;

            AppendLog(string.Format(Properties.Resources.Msg_Gpu_WorkStart, SelectedVm.Name));
            AppendLog(string.Format(Properties.Resources.Msg_Gpu_Selected, SelectedHostGpu.Name));
            AppendLog(string.Format(Properties.Resources.Msg_Gpu_Path, SelectedHostGpu.Pname));

            GpuTasks.Clear();

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.Prepare,
                Name = Properties.Resources.Task_Gpu_Prepare,
                Description = Properties.Resources.Msg_Gpu_PreparingHost,
                Status = GpuTaskStatus.Pending
            });

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.ConfigCheck,
                Name = Properties.Resources.Task_Gpu_Config,
                Description = Properties.Resources.Msg_Gpu_CheckingConfig,
                Status = GpuTaskStatus.Pending
            });

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.PowerCheck,
                Name = Properties.Resources.Task_Gpu_Power,
                Description = Properties.Resources.Msg_Gpu_CheckingPower,
                Status = GpuTaskStatus.Pending
            });

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.Optimization,
                Name = Properties.Resources.Task_Gpu_Opt,
                Description = Properties.Resources.Msg_Gpu_Mmio,
                Status = GpuTaskStatus.Pending
            });

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.Assign,
                Name = Properties.Resources.Task_Gpu_Assign,
                Description = Properties.Resources.Msg_Gpu_Creating,
                Status = GpuTaskStatus.Pending
            });
            if (AutoInstallDrivers)
            {
                GpuTasks.Add(new TaskItem
                {
                    TaskType = GpuTaskType.Driver,
                    Name = Properties.Resources.Task_Gpu_Driver,
                    Description = Properties.Resources.Msg_Gpu_WaitingScan,
                    Status = GpuTaskStatus.Pending
                });
            }

            await RunRealGpuWorkflowAsync(0);
        }

        // 执行 GPU 部署工作流
        private async Task RunRealGpuWorkflowAsync(int startIndex)
        {
            var tasks = GpuTasks;
            _currentProcessingGpuAdapterId = null;

            for (int i = startIndex; i < tasks.Count; i++)
            {
                if (CurrentViewType != VmDetailViewType.AddGpuProgress || SelectedHostGpu == null)
                {
                    Debug.WriteLine("GPU Workflow aborted: UI state or SelectedHostGpu has been reset.");
                    return;
                }

                var task = tasks[i];
                task.Status = GpuTaskStatus.Running;
                AppendLog(string.Format(Properties.Resources.Msg_Gpu_ExecTask, task.Name));
                try
                {
                    switch (task.TaskType)
                    {
                        case GpuTaskType.Prepare:
                            await _vmGpuService.PrepareHostEnvironmentAsync();
                            task.Description = Properties.Resources.Msg_Gpu_Policy;
                            break;

                        case GpuTaskType.ConfigCheck:
                            _needConfig = !(await _vmGpuService.CheckVmForGpuAsync(SelectedVm.Name));
                            task.Description = _needConfig ? Properties.Resources.Msg_Gpu_ConfigNeeded : Properties.Resources.Msg_Gpu_ConfigOk;
                            break;

                        case GpuTaskType.PowerCheck:
                            if (_needConfig || AutoInstallDrivers)
                            {
                                var (isOff, state) = await _queryService.IsVmPoweredOffAsync(SelectedVm.Name);
                                if (!isOff)
                                {
                                    task.Description = string.Format(Properties.Resources.Msg_Gpu_ForceOff, state);
                                    AppendLog(task.Description);
                                    await VmPowerService.ExecuteControlActionAsync(SelectedVm.Name, "TurnOff");
                                    var offDeadline = DateTime.UtcNow.AddSeconds(30);
                                    while (!(await _queryService.IsVmPoweredOffAsync(SelectedVm.Name)).IsOff)
                                    {
                                        if (DateTime.UtcNow > offDeadline)
                                            throw new Exception(Properties.Resources.Error_Gpu_PowerOffTimeout);
                                        await Task.Delay(100);
                                    }
                                }
                                task.Description = Properties.Resources.Msg_Gpu_Off;
                            }
                            else
                            {
                                task.Description = Properties.Resources.Msg_Skip;
                            }
                            break;

                        case GpuTaskType.Optimization:
                            if (_needConfig)
                            {
                                bool optOk = await _vmGpuService.OptimizeVmForGpuAsync(SelectedVm.Name);
                                task.Description = optOk ? Properties.Resources.Msg_Gpu_MmioOk : Properties.Resources.Error_Gpu_OptFail;
                            }
                            else
                            {
                                task.Description = Properties.Resources.Msg_Skip;
                            }
                            break;

                        case GpuTaskType.Assign:
                            string targetPath = !string.IsNullOrEmpty(SelectedHostGpu.Pname)
                                                ? SelectedHostGpu.Pname
                                                : SelectedHostGpu.InstanceId;

                            var assignRes = await _vmGpuService.AssignGpuPartitionAsync(SelectedVm.Name, targetPath);
                            if (!assignRes.Success) throw new Exception(assignRes.Message);
                            task.Description = Properties.Resources.Msg_Gpu_AssignOk;
                            await Task.Delay(100);
                            var currentAdapters = await _vmGpuService.GetVmGpuAdaptersAsync(SelectedVm.Name);
                            // 记录下来，以便后续步骤（如驱动安装）失败时删除
                            _currentProcessingGpuAdapterId = currentAdapters.LastOrDefault().Id;
                            break;

                        case GpuTaskType.Driver:
                            {
                                task.Description = Properties.Resources.Msg_Gpu_Scanning;
                                AppendLog(task.Description);

                                // 获取所有硬盘的所有分区
                                var allPartitions = await _vmGpuService.GetPartitionsFromVmAsync(SelectedVm.Name);

                                if (allPartitions == null || allPartitions.Count == 0)
                                {
                                    throw new Exception(Properties.Resources.Error_Gpu_NoPartFound);
                                }

                                // 计算涉及到的物理磁盘数量
                                var distinctDisks = allPartitions.Select(p => p.DiskPath).Distinct().Count();
                                if (distinctDisks == 1 && allPartitions.Count == 1)
                                {
                                    var singlePart = allPartitions[0];

                                    if (singlePart.OsType == OperatingSystemType.Windows)
                                    {
                                        // 1. 如果是 Windows 且单一，执行原有自动注入逻辑
                                        task.Description = Properties.Resources.Msg_Gpu_DetectWin;
                                        var syncRes = await _vmGpuService.SyncWindowsDriversAsync(
                                            SelectedVm.Name,
                                            SelectedHostGpu.Pname,
                                            SelectedHostGpu.Manu,
                                            singlePart,
                                            msg => { task.Description = msg; AppendLog(msg); });

                                        if (!syncRes.Success) throw new Exception(syncRes.Message);
                                        task.Description = Properties.Resources.Msg_Gpu_DriverOk;
                                    }
                                    else if (singlePart.OsType == OperatingSystemType.Linux)
                                    {
                                        // 2. [新增] 如果是 Linux 且单一，直接触发 SelectPartition 流程（嗅探 IP 并显示 SSH 表单）
                                        task.Description = Properties.Resources.Msg_Gpu_LinuxDetected;
                                        AppendLog(Properties.Resources.Msg_Gpu_LinuxAutoPrep);

                                        // 异步启动 Linux 准备工作流（即你点击列表项时触发的逻辑）
                                        await SelectPartitionAndContinueAsync(singlePart);

                                        return; // 退出当前循环，由 SelectPartitionAndContinueAsync 接管后续逻辑
                                    }
                                }
                                else
                                {
                                    // 3. 多分区情况，保持现状，显示列表让用户选择
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        DetectedPartitions = new ObservableCollection<PartitionInfo>(allPartitions);
                                        ShowPartitionSelector = true;
                                        ShowSshForm = false;
                                    });
                                    task.Description = Properties.Resources.Msg_Gpu_ManualSelect;
                                    AppendLog(task.Description);
                                    return;
                                }
                            }
                            break;
                    }
                    task.Status = GpuTaskStatus.Success;
                    AppendLog(string.Format(Properties.Resources.Msg_Gpu_TaskOk, task.Name, task.Description));
                }
                catch (Exception ex)
                {
                    task.Status = GpuTaskStatus.Failed;
                    task.Description = string.Format(Properties.Resources.Error_Format_FailMsg, ex.Message);
                    AppendLog(string.Format(Properties.Resources.Error_Format_StageExc, task.Name, ex.Message));
                    if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId))
                    {
                        AppendLog(Properties.Resources.Error_Gpu_LinuxRollback);
                        await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                        _currentProcessingGpuAdapterId = null;
                        AppendLog(Properties.Resources.Msg_Gpu_PartitionRemoved);
                    }

                    ShowSnackbar(Properties.Resources.Error_Common_OpFail, string.Format(Properties.Resources.Error_Format_StageError, task.Name), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    return;
                }
            }

            await FinishWorkflowAsync();
        }

        [RelayCommand]
        private async Task SelectPartitionAndContinueAsync(PartitionInfo partition)
        {
            var driveTask = GpuTasks.FirstOrDefault(t => t.TaskType == GpuTaskType.Driver);
            if (driveTask == null) return;

            if (partition.OsType == OperatingSystemType.Windows)
            {
                ShowPartitionSelector = false;
                driveTask.Status = GpuTaskStatus.Running;
                driveTask.Description = string.Format(Properties.Resources.Msg_Gpu_SyncingPart, partition.PartitionNumber);
                AppendLog(driveTask.Description);

                var result = await _vmGpuService.SyncWindowsDriversAsync(
                    SelectedVm.Name,
                    SelectedHostGpu.Pname,
                    SelectedHostGpu.Manu,
                    partition,
                    msg => {
                        driveTask.Description = msg;
                        AppendLog(msg);
                    });

                if (result.Success)
                {
                    driveTask.Status = GpuTaskStatus.Success;
                    _currentProcessingGpuAdapterId = null;
                    await FinishWorkflowAsync();
                }
                else
                {
                    if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId))
                    {
                        AppendLog(string.Format(Properties.Resources.Error_Gpu_Rollback, result.Message));
                        await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                        _currentProcessingGpuAdapterId = null;
                    }

                    driveTask.Status = GpuTaskStatus.Failed;
                    driveTask.Description = result.Message;
                }
            }
            else if (partition.OsType == OperatingSystemType.Linux)
            {
#pragma warning disable MVVMTK0034 // 故意直接写字段:避免在 OnSelectedPartitionChanged 触发链中重入
                _selectedPartition = partition;
#pragma warning restore MVVMTK0034
                IsLoadingSettings = true;

                // UI 状态转换：保持卡片开启，但切换到 SSH 表单 Grid
                ShowPartitionSelector = true;
                ShowSshForm = true;

                driveTask.Description = Properties.Resources.Msg_Gpu_LinuxVm;
                AppendLog(string.Format(Properties.Resources.Msg_Gpu_LinuxRemoteInit, partition.DisplayName));
                try
                {
                    // --- 自动探测宿主代理 (不修改全局变量) ---
                    UseSshProxy = false; // 默认关闭开关
                    try
                    {
                        var systemProxy = System.Net.WebRequest.DefaultWebProxy;
                        var proxyUri = systemProxy.GetProxy(new Uri("https://github.com"));
                        if (proxyUri != null && !proxyUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                        {
                            SshProxyHost = proxyUri.Host;
                            SshProxyPort = proxyUri.Port.ToString();
                        }
                    }
                    catch { /* 静默失败 */ }

                    // 检查虚拟机电源状态
                    var status = await _queryService.IsVmPoweredOffAsync(SelectedVm.Name);
                    // 在 SelectPartitionAndContinueAsync 方法内部：
                    if (status.IsOff)
                    {
                        driveTask.Description = Properties.Resources.Msg_Gpu_IpSniff;
                        AppendLog(driveTask.Description);

                        // 1. 执行开机
                        await VmPowerService.ExecuteControlActionAsync(SelectedVm.Name, "Start");

                        // 2. 【新增】立刻强制同步一次 UI 状态，不等后台循环
                        await SyncSingleVmStateAsync(SelectedVm);

                        await Task.Delay(3000); // 给系统一点反应时间
                    }

                    driveTask.Description = Properties.Resources.Msg_Gpu_IpScanning;
                    AppendLog(driveTask.Description);

                    // 扫描 IP
                    string vmIp = await Task.Run(async () =>
                    {
                        var adapters = await VmNetworkService.GetNetworkAdaptersAsync(SelectedVm.Name);
                        string mac = adapters?.FirstOrDefault()?.MacAddress ?? string.Empty;
                        if (!string.IsNullOrEmpty(mac))
                        {
                            for (int i = 0; i < 3; i++)
                            {
                                var ip = await VmIpService.Lookup(SelectedVm.Name, mac);
                                if (!string.IsNullOrEmpty(ip)) return ip;
                                await Task.Delay(2000);
                            }
                        }
                        return string.Empty;
                    });

                    if (!string.IsNullOrEmpty(vmIp))
                    {
                        SshHost = Ipv4.SelectBest(vmIp);
                        AppendLog(string.Format(Properties.Resources.Msg_Gpu_IpOk, SshHost));
                    }
                    else
                    {
                        AppendLog(Properties.Resources.Error_Gpu_IpManual);
                    }

                    driveTask.Description = Properties.Resources.Msg_Gpu_SshConfirm;
                }
                catch (Exception ex)
                {
                    ShowSnackbar(Properties.Resources.Error_Gpu_EnvFail, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    AppendLog(string.Format(Properties.Resources.Warn_Gpu_EnvExc, ex.Message));
                }
                finally
                {
                    IsLoadingSettings = false;
                }
            }
        }
        // 开始 Linux 部署
        [RelayCommand]
        private async Task StartLinuxDeployAsync()
        {
            _gpuDeploymentCts?.Cancel();
            _gpuDeploymentCts = new CancellationTokenSource();
            var token = _gpuDeploymentCts.Token;

            // 1. 定位驱动安装任务项
            var driveTask = GpuTasks.FirstOrDefault(t => t.TaskType == GpuTaskType.Driver);
            if (driveTask == null) return;

            // 2. 验证
            if (SelectedLinuxScript == null || string.IsNullOrWhiteSpace(SshHost))
            {
                ShowSnackbar(Properties.Resources.Error_Common_Verify, Properties.Resources.VmPage_GpuCheckDeploy, ControlAppearance.Caution, SymbolRegular.Warning24);
                return;
            }

            // 3. 代理参数解析
            int? proxyPort = null;
            string proxyHost = string.Empty;
            if (UseSshProxy)
            {
                proxyHost = SshProxyHost?.Trim() ?? string.Empty;
                if (!int.TryParse(SshProxyPort, out int port) || string.IsNullOrWhiteSpace(proxyHost))
                {
                    ShowSnackbar(Properties.Resources.Error_Common_Verify, Properties.Resources.Validation_ProxyIpAndPortMismatch, ControlAppearance.Danger, SymbolRegular.Warning24);
                    return;
                }
                proxyPort = port;
            }

            // 4. UI 切换：隐藏卡片，显示控制台
            ShowPartitionSelector = false;
            ShowSshForm = false;
            ShowLogConsole = true;
            driveTask.Status = GpuTaskStatus.Running;

            AppendLog(Properties.Resources.Msg_Gpu_DeployStart);
            AppendLog($"[Info] Selected Script: {SelectedLinuxScript.Name}");
            if (UseSshProxy) AppendLog(string.Format(Properties.Resources.Msg_Gpu_UsingProxy, proxyHost, proxyPort));

            // 5. 组装凭据 (强制 KeepGlobalProxySetting 为 false)
            var creds = new SshCredentials
            {
                Host = SshHost,
                Port = SshPort,
                Username = SshUsername,
                Password = SshPassword,
                UseProxy = this.UseSshProxy,
                ProxyHost = this.UseSshProxy ? proxyHost : null,
                ProxyPort = this.UseSshProxy ? proxyPort : null,
                InstallGraphics = InstallGraphics
            };

            // 6. 执行部署
            string result = await _vmGpuService.ProvisionLinuxGpuAsync(
                SelectedVm.Name,
                SelectedLinuxScript,
                creds,
                msg => {
                    if (msg.Contains("[STEP:"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(msg, @"\[STEP:\s*(.*?)\]");
                        if (match.Success)
                        {
                            Application.Current.Dispatcher.Invoke(() => {
                                driveTask.Description = match.Groups[1].Value;
                            });
                        }
                    }
                    AppendLog(msg);
                },
                token
            );

            // 7. 流程结束判定
            if (result == "OK" || (result.Contains("successfully") && result.Contains("signing")))
            {
                driveTask.Status = GpuTaskStatus.Success;
                driveTask.Description = Properties.Resources.Msg_Gpu_LinuxDeployDone;
                _currentProcessingGpuAdapterId = null;
                AppendLog(Properties.Resources.Msg_Gpu_LinuxDeployDone);
                await FinishWorkflowAsync();
            }
            else
            {
                // 失败回滚
                if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId))
                {
                    AppendLog(Properties.Resources.Error_Gpu_LinuxRollback);
                    await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                    _currentProcessingGpuAdapterId = null;
                }

                driveTask.Status = GpuTaskStatus.Failed;
                driveTask.Description = result;
                AppendLog(string.Format(Properties.Resources.Error_Gpu_DeployFatal, result));
            }
        }
        // 返回分区选择列表
        [RelayCommand]
        private void GoBackToPartitionList()
        {
            ShowSshForm = false;
            var driveTask = GpuTasks.FirstOrDefault(t => t.Name == Properties.Resources.Task_Gpu_Driver);
            if (driveTask != null)
            {
                driveTask.Description = Properties.Resources.Msg_Gpu_SelectPart;
            }
        }

        // 完成 GPU 部署工作流
        private async Task FinishWorkflowAsync()
        {
            await Task.Delay(1000);
            // 确保在 UI 线程刷新
            await RefreshCurrentVmGpuAssignments();

            // --- 核心修复：非空安全获取显卡名称 ---
            string gpuName = "GPU";
            if (SelectedHostGpu != null)
            {
                gpuName = SelectedHostGpu.Name;
            }
            else if (SelectedVm?.AssignedGpus?.Count > 0)
            {
                // 如果 SelectedHostGpu 已经被重置，尝试从已分配列表里拿名字
                gpuName = SelectedVm.AssignedGpus.Last().Name;
            }

            CurrentViewType = VmDetailViewType.GpuSettings;

            ShowSnackbar(
                Properties.Resources.Msg_Common_ConfigSuccess,
                string.Format(Properties.Resources.Msg_Gpu_Ready, gpuName),
                ControlAppearance.Success,
                SymbolRegular.CheckmarkCircle24);
        }

        // 设备 ID 格式化辅助
        private string NormalizeDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return string.Empty;
            var normalizedId = deviceId.ToUpper();
            if (normalizedId.StartsWith(@"\\?\")) normalizedId = normalizedId.Substring(4);
            int suffixIndex = normalizedId.IndexOf("#{");
            if (suffixIndex != -1) normalizedId = normalizedId.Substring(0, suffixIndex);
            return normalizedId.Replace('\\', '#').Replace("#", "");
        }




        // ===== GPU 部署:日志 / 重置 helper（从 UI 辅助尾部归拢） =====
        // 追加日志到控制台
        private void AppendLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            Application.Current.Dispatcher.Invoke(() => {
                GpuDeploymentLog += $"[{timestamp}] {message}{Environment.NewLine}";
            });
        }

        // 复制日志
        [RelayCommand]
        private void CopyLog()
        {
            if (!string.IsNullOrEmpty(GpuDeploymentLog))
            {
                // 剪贴板可能被其它进程占用，SetText 会抛 COMException；成功才提示，失败不崩
                try
                {
                    Clipboard.SetText(GpuDeploymentLog);
                    ShowSnackbar(Properties.Resources.Msg_Common_CopyOk, Properties.Resources.Msg_Gpu_LogCopy, ControlAppearance.Success, SymbolRegular.Copy24);
                }
                catch { }
            }
        }
        // ✅ 增加重置/重试命令逻辑

        [RelayCommand]
        private async Task ResetGpuDeploymentAsync()
        {
            _gpuDeploymentCts?.Cancel();
            _gpuDeploymentCts = new CancellationTokenSource();
            IsLoadingSettings = false;

            if (SelectedPartition != null)
            {
                // --- 场景 1: 软重置 ---
                var driveTask = GpuTasks.FirstOrDefault(t => t.TaskType == GpuTaskType.Driver);
                if (driveTask != null)
                {
                    driveTask.Status = GpuTaskStatus.Pending;
                    driveTask.Description = SelectedPartition.OsType == OperatingSystemType.Linux
                        ? Properties.Resources.Msg_Gpu_SshConfirm
                        : Properties.Resources.Msg_Gpu_SelectPart;
                }

                if (SelectedPartition.OsType == OperatingSystemType.Linux)
                {
                    ShowPartitionSelector = true;
                    ShowSshForm = true;
                }
                else
                {
                    // Windows 流程重置
                    ShowPartitionSelector = true;
                    ShowSshForm = false;

                    // --- 关键改进：清空选中项，允许用户重新点击同一个分区 ---
                    SelectedPartition = null;
                }

                AppendLog($"--- {Properties.Resources.Label_Progress} ({Properties.Resources.VmPage_GpuResetDone}) ---");
                return;
            }
            // --- 场景 2: “硬重置”（彻底回滚，回到选显卡第一步） ---
            // 触发条件：还没有选定分区就挂了，或者用户在还没选分区时点击了重置

            // 1. 如果当前有正在处理的分区 ID（说明已经分配但未成功），执行物理回滚
            if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId))
            {
                AppendLog(Properties.Resources.VmPage_GpuUserRollback); // Properties.Resources.VmPage_MsgRollingBackGpu2
                try
                {
                    await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                    _currentProcessingGpuAdapterId = null;
                    AppendLog(Properties.Resources.VmPage_GpuUserRollbackDone); // Properties.Resources.VmPage_MsgRollbackComplete2
                }
                catch (Exception ex)
                {
                    AppendLog(string.Format(Properties.Resources.VmPage_GpuUserRollbackFail, ex.Message)); // Properties.Resources.VmPage_ErrRollbackFailed2
                }
            }

            // 2. 重置所有 UI 状态
            GpuTasks.Clear();
            GpuDeploymentLog = string.Empty;
            ShowPartitionSelector = false;
            ShowSshForm = false;
            ShowLogConsole = false;

            // 3. 彻底重来，重新初始化数据并跳转回选择界面
            await GoToAddGpuAsync();

            // 4. 弹出全局重置提示
            ShowSnackbar(
                Properties.Resources.VmPage_GpuResetDone, // Properties.Resources.VmPage_BtnReset2
                Properties.Resources.VmPage_GpuResetDesc, // Properties.Resources.VmPage_MsgProcessReset2
                Wpf.Ui.Controls.ControlAppearance.Info,
                Wpf.Ui.Controls.SymbolRegular.ArrowCounterclockwise24);
        }
    }
}
