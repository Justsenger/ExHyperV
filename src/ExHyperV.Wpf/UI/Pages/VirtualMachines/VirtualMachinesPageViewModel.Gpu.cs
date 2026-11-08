using ExHyperV.Application;
using ExHyperV.UI.Shared.Interaction;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using Wpf.Ui.Controls;

namespace ExHyperV.UI.Pages.VirtualMachines
{
    public partial class VirtualMachinesPageViewModel
    {
        [ObservableProperty] private ObservableCollection<HostGpuInfo> _hostGpus = new();
        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ConfirmAddGpuCommand))] private HostGpuInfo _selectedHostGpu;
        [ObservableProperty] private bool _autoInstallDrivers = true;
        [ObservableProperty] private ObservableCollection<TaskItem> _gpuTasks = new();
        [ObservableProperty] private bool _showPartitionSelector = false;
        [ObservableProperty] private ObservableCollection<GpuDeploymentPartition> _detectedPartitions = new();
        [ObservableProperty] private GpuDeploymentPartition? _selectedPartition;
        [ObservableProperty] private bool _showSshForm = false;
        private GpuDeploymentSession? _gpuWorkflow;
        private Guid _gpuWorkflowVmId;
        private long _gpuSnapshotRevision;
        private GpuDecisionRequest? _gpuDecision;


        // Linux SSH 凭据
        [ObservableProperty] private string _sshHost = "";
        [ObservableProperty] private string _sshUsername = "root";
        [ObservableProperty] private string _sshPassword = "";
        [ObservableProperty] private int _sshPort = 22;
        [ObservableProperty] private bool _installGraphics = true;
        [ObservableProperty] private bool _useSshProxy = false;
        [ObservableProperty] private string _sshProxyHost = "";
        [ObservableProperty] private string _sshProxyPort = "";

        // 日志与控制台
        [ObservableProperty] private string _gpuDeploymentLog = string.Empty;
        [ObservableProperty] private bool _showLogConsole = false;



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
                ShowError(Properties.Resources.Error_Gpu_ReadInfo + ex.Message);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        private async Task RefreshCurrentVmGpuAssignments(VmInstanceViewModel? targetVm = null)
        {
            var vm = targetVm ?? SelectedVm;
            if (vm == null) return;
            try
            {
                var assignments = (await _vmGpuService.GetVmGpuAssignmentsAsync(vm.Id)).RequireData();
                vm.AssignedGpus.Clear();
                vm.Model.AssignedGpus.Clear();
                foreach (var assignment in assignments)
                {
                    vm.AssignedGpus.Add(assignment);
                    vm.Model.AssignedGpus.Add(assignment);
                }
                vm.RefreshGpuSummary();
            }
            catch (Exception ex) { ShowError($"{Properties.Resources.Error_Gpu_RefreshFail}：{ex.Message}"); }
        }

        [RelayCommand]
        private async Task RemoveGpuAsync(VmGpuAssignment assignment)
        {
            var vm = SelectedVm;
            if (vm == null || assignment == null || !vm.AssignedGpus.Contains(assignment)) return;
            IsLoadingSettings = true;
            try
            {
                var result = await _vmGpuService.RemoveGpuPartitionAsync(vm.Id, assignment);
                if (result.Success)
                {
                    vm.AssignedGpus.Remove(assignment);
                    vm.Model.AssignedGpus.Remove(assignment);
                    vm.RefreshGpuSummary();
                    ShowSuccess(Properties.Resources.Msg_Gpu_PartitionRemoved);
                    await Task.Delay(2000);
                    await RefreshCurrentVmGpuAssignments(vm);
                }
                else ShowError($"{Properties.Resources.Error_Gpu_RemoveFail}：{result.Error}");
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { IsLoadingSettings = false; }
        }

        private bool CanUpdateGpuDrivers(VmGpuAssignment assignment) =>
            SelectedVm != null && assignment != null && !string.IsNullOrWhiteSpace(assignment.HostDeviceInstanceId);

        [RelayCommand(CanExecute = nameof(CanUpdateGpuDrivers))]
        private async Task UpdateGpuDriversAsync(VmGpuAssignment assignment)
        {
            var vm = SelectedVm;
            if (!CanUpdateGpuDrivers(assignment) || vm == null || !await StopGpuDeploymentAsync()) return;
            var targetId = vm.Id;
            IsLoadingSettings = true;
            try
            {
                await LoadGpuScriptsAsync();
                if (SelectedVm?.Id != targetId) return;
                var hostGpus = (await _vmGpuService.GetHostGpusAsync()).RequireData();
                if (SelectedVm != vm) return;
                SelectedHostGpu = hostGpus.FirstOrDefault(gpu => string.Equals(gpu.InstanceId,
                    assignment.HostDeviceInstanceId, StringComparison.OrdinalIgnoreCase));
                if (SelectedHostGpu == null)
                {
                    ShowError(Properties.Resources.Error_Gpu_ReadInfo);
                    return;
                }
                await StartGpuDeploymentAsync(GpuDeploymentMode.UpdateDrivers);
            }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { IsLoadingSettings = false; }
        }

        private async Task LoadGpuScriptsAsync()
        {
            AvailableLinuxScripts = new ObservableCollection<LinuxGpuDeploymentScript>(await _vmGpuService.GetAvailableScriptsAsync());
            SelectedLinuxScript = AvailableLinuxScripts.FirstOrDefault();
        }

        [RelayCommand]
        private async Task GoToAddGpuAsync()
        {
            var vm = SelectedVm;
            if (vm == null || !await StopGpuDeploymentAsync()) return;
            var targetId = vm.Id;
            IsLoadingSettings = true;
            try
            {
                HostGpus = new ObservableCollection<HostGpuInfo>((await _vmGpuService.GetHostGpusAsync()).RequireData());
                await LoadGpuScriptsAsync();
                if (SelectedVm?.Id != targetId) return;
                SelectedHostGpu = null;
                CurrentViewType = VmDetailViewType.AddGpuSelect;
            }
            catch (Exception ex) { ShowError(Properties.Resources.Error_Common_LoadFail + "：" + ex.Message); }
            finally { IsLoadingSettings = false; }
        }

        [RelayCommand]
        private async Task CancelAddGpuAsync()
        {
            if (!await StopGpuDeploymentAsync()) return;
            CurrentViewType = VmDetailViewType.GpuSettings;
            GpuTasks.Clear();
        }

        partial void OnSelectedPartitionChanged(GpuDeploymentPartition? value)
        {
            if (value == null) return;
            _ = SelectPartitionAndContinueCommand.ExecuteAsync(value);
            WpfApplication.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
#pragma warning disable MVVMTK0034
                _selectedPartition = null;
#pragma warning restore MVVMTK0034
                OnPropertyChanged(nameof(SelectedPartition));
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private bool CanConfirmAddGpu() => SelectedHostGpu != null;

        [RelayCommand(CanExecute = nameof(CanConfirmAddGpu))]
        private async Task ConfirmAddGpu()
        {
            var vm = SelectedVm;
            var gpu = SelectedHostGpu;
            var mode = AutoInstallDrivers ? GpuDeploymentMode.AddWithDrivers : GpuDeploymentMode.AddWithoutDrivers;
            if (vm == null || gpu == null || !await StopGpuDeploymentAsync()) return;
            if (SelectedVm != vm || SelectedHostGpu != gpu) return;
            await StartGpuDeploymentAsync(mode);
        }

        private bool IsCurrentGpuSession(GpuDeploymentSession session) =>
            ReferenceEquals(_gpuWorkflow, session) && SelectedVm?.Id == _gpuWorkflowVmId
            && CurrentViewType == VmDetailViewType.AddGpuProgress;

        private async Task StartGpuDeploymentAsync(GpuDeploymentMode mode)
        {
            if (SelectedVm == null || SelectedHostGpu == null) return;
            var session = new GpuDeploymentSession(SelectedVm.Id, SelectedVm.Name, SelectedHostGpu, _vmGpuService, _queryService, mode);
            _gpuWorkflow = session;
            _gpuWorkflowVmId = SelectedVm.Id;
            _gpuSnapshotRevision = 0;
            _gpuDecision = null;
            GpuDeploymentLog = string.Empty;
            ShowLogConsole = true;
            ShowPartitionSelector = ShowSshForm = false;
            GpuTasks.Clear();
            CurrentViewType = VmDetailViewType.AddGpuProgress;
            AppendLog(string.Format(Properties.Resources.Msg_Gpu_WorkStart, SelectedVm.Name));
            AppendLog(string.Format(Properties.Resources.Msg_Gpu_Selected, SelectedHostGpu.Name));
            var status = await session.StartAsync(value => WpfApplication.Current.Dispatcher.Invoke(() =>
            {
                if (!IsCurrentGpuSession(session)) { session.RequestCancellation(); return; }
                ApplyGpuStatus(value);
            }));
            await HandleGpuStatusAsync(session, status);
        }

        // This adapter only maps Core state to existing controls; it does not choose deployment steps.
        private void ApplyGpuStatus(GpuDeploymentSnapshot status)
        {
            if (status.Revision <= _gpuSnapshotRevision) return;
            _gpuSnapshotRevision = status.Revision;
            foreach (var step in status.Steps)
            {
                var item = GpuTasks.FirstOrDefault(t => t.TaskType == step.Type);
                if (item == null) { item = new TaskItem { TaskType = step.Type }; GpuTasks.Add(item); }
                item.Name = step.Name;
                item.Description = step.Description;
                item.Status = step.Status;
            }
            _gpuDecision = status.State == GpuDeploymentState.WaitingForDecision ? status.PendingDecision : null;
            ShowPartitionSelector = _gpuDecision?.Input is GpuDecisionInput.Partition or GpuDecisionInput.LinuxCredentials;
            ShowSshForm = _gpuDecision?.Input == GpuDecisionInput.LinuxCredentials;
            if (status.LastLog != null) AppendLog(status.LastLog);
        }

        private async Task HandleGpuStatusAsync(GpuDeploymentSession session, GpuDeploymentSnapshot status)
        {
            while (IsCurrentGpuSession(session))
            {
                ApplyGpuStatus(status);
                if (status.State == GpuDeploymentState.Completed) { await FinishWorkflowAsync(session); return; }
                if (status.State is GpuDeploymentState.Failed or GpuDeploymentState.CleanupRequired)
                {
                    ShowError(string.Join(Environment.NewLine, new[] { status.Error, status.CleanupError }.Where(s => !string.IsNullOrWhiteSpace(s))));
                    return;
                }
                if (status.State == GpuDeploymentState.Cancelled)
                {
                    CurrentViewType = VmDetailViewType.GpuSettings;
                    return;
                }
                var request = status.PendingDecision;
                if (status.State != GpuDeploymentState.WaitingForDecision || request == null) return;
                switch (request.Input)
                {
                    case GpuDecisionInput.Confirmation:
                        bool accepted = await Dialogs.ShowConfirmAsync(request.Title, request.Message, isDanger: request.IsDestructive);
                        if (!IsCurrentGpuSession(session) || session.GetStatus().PendingDecision?.Id != request.Id) return;
                        status = await session.SubmitDecisionAsync(new GpuConfirmation(request.Id, accepted));
                        break;
                    case GpuDecisionInput.Partition:
                        DetectedPartitions = new ObservableCollection<GpuDeploymentPartition>(request.Partitions);
                        return;
                    case GpuDecisionInput.LinuxCredentials:
                        SshHost = request.SuggestedHost;
                        UseSshProxy = false;
                        try
                        {
                            var proxy = System.Net.WebRequest.DefaultWebProxy?.GetProxy(new Uri("https://github.com"));
                            if (proxy != null && !proxy.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                            {
                                SshProxyHost = proxy.Host;
                                SshProxyPort = proxy.Port.ToString();
                            }
                        }
                        catch { /* An optional form default must not interrupt deployment. */ }
                        return;
                }
            }
        }

        private async Task SubmitGpuDecisionAsync(GpuDeploymentDecision decision)
        {
            var session = _gpuWorkflow;
            if (session == null || !IsCurrentGpuSession(session)) return;
            try { await HandleGpuStatusAsync(session, await session.SubmitDecisionAsync(decision)); }
            catch (ArgumentException ex) { if (IsCurrentGpuSession(session)) ShowTip(ex.Message); }
            catch (InvalidOperationException ex) { if (IsCurrentGpuSession(session)) ShowTip(ex.Message); }
        }

        [RelayCommand]
        private async Task SelectPartitionAndContinueAsync(GpuDeploymentPartition partition)
        {
            if (_gpuDecision != null)
                await SubmitGpuDecisionAsync(new GpuPartitionSelection(_gpuDecision.Id, partition.Id));
        }

        [RelayCommand]
        private async Task StartLinuxDeployAsync()
        {
            if (_gpuDecision == null) return;
            int? proxyPort = int.TryParse(SshProxyPort, out int port) ? port : null;
            await SubmitGpuDecisionAsync(new GpuLinuxInput(_gpuDecision.Id, SelectedLinuxScript, new SshCredentials
            {
                Host = SshHost, Port = SshPort, Username = SshUsername, Password = SshPassword,
                UseProxy = UseSshProxy, ProxyHost = SshProxyHost?.Trim(), ProxyPort = proxyPort,
                InstallGraphics = InstallGraphics
            }));
        }

        [RelayCommand]
        private async Task GoBackToPartitionListAsync()
        {
            if (_gpuDecision != null) await SubmitGpuDecisionAsync(new GpuBackToPartitions(_gpuDecision.Id));
        }

        private async Task FinishWorkflowAsync(GpuDeploymentSession session)
        {
            await Task.Delay(1000);
            if (!IsCurrentGpuSession(session)) return;
            await RefreshCurrentVmGpuAssignments();
            if (!IsCurrentGpuSession(session)) return;
            var gpuName = SelectedHostGpu?.Name ?? "GPU";
            CurrentViewType = VmDetailViewType.GpuSettings;
            ShowSuccess(string.Format(Properties.Resources.Msg_Gpu_Ready, gpuName));
        }

        private async Task<bool> StopGpuDeploymentAsync()
        {
            var session = _gpuWorkflow;
            if (session == null) return true;
            var status = session.GetStatus().AllowedActions.Contains(GpuDeploymentAction.RetryCleanup)
                ? await session.RetryCleanupAsync() : await session.CancelAsync();
            if (status.State == GpuDeploymentState.CleanupRequired)
            {
                ShowError(status.CleanupError ?? Properties.Resources.Gpu_CleanupRequired);
                return false;
            }
            if (ReferenceEquals(_gpuWorkflow, session)) { _gpuWorkflow = null; _gpuDecision = null; }
            return true;
        }

        private async Task CancelGpuSessionOnNavigationAsync()
        {
            var session = _gpuWorkflow;
            if (session == null) return;
            var status = await session.CancelAsync();
            if (status.State == GpuDeploymentState.CleanupRequired) ShowError(status.CleanupError!);
        }




        // 追加日志到控制台
        private void AppendLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            WpfApplication.Current.Dispatcher.Invoke(() => {
                GpuDeploymentLog += $"[{timestamp}] {message}{Environment.NewLine}";
            });
        }

        // 复制日志
        [RelayCommand]
        private void CopyLog()
        {
            // 复制走 Shell 门面（内部吞 COMException）；成功才提示
            if (!string.IsNullOrEmpty(GpuDeploymentLog) && Shell.CopyToClipboard(GpuDeploymentLog))
                ShowSuccess(Properties.Resources.Msg_Gpu_LogCopy);
        }

        [RelayCommand]
        private async Task ResetGpuDeploymentAsync()
        {
            if (!await StopGpuDeploymentAsync()) return;
            IsLoadingSettings = false;
            GpuTasks.Clear();
            GpuDeploymentLog = string.Empty;
            ShowPartitionSelector = ShowSshForm = ShowLogConsole = false;
            SelectedPartition = null;
            await GoToAddGpuAsync();
            ShowTip(Properties.Resources.VmPage_GpuResetDesc);
        }
    }
}
