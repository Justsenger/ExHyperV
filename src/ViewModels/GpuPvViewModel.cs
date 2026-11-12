using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Interaction;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.Views;

namespace ExHyperV.ViewModels
{
    /// <summary>
    /// GPU-PV 独立页面：主机可分区 GPU + 各虚拟机的分区占用。
    /// 分区增删复用 <see cref="VmGpuService"/>（其底层提交走 <see cref="ResourceAssignmentService"/>，与 PCIe 直通页共用）。
    /// </summary>
    public partial class GpuPvViewModel : PageViewModelBase
    {
        private readonly VmQueryService _queryService;
        private readonly VmGpuService _gpuService;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private bool _isUiEnabled = true;

        [ObservableProperty]
        private bool _showNoPartitionableGpu;

        [ObservableProperty]
        private bool _isDriverBusy;

        [ObservableProperty]
        private bool _driverCanClose;

        [ObservableProperty]
        private string _driverTitle = string.Empty;

        [ObservableProperty]
        private string _driverStatus = string.Empty;

        [ObservableProperty]
        private string _driverLog = string.Empty;

        public ObservableCollection<GpuPvHostGpu> HostGpus { get; } = new();
        public ObservableCollection<GpuPvAssignment> Assignments { get; } = new();

        /// <summary>供 XAML 直接实例化。</summary>
        public GpuPvViewModel() : this(new VmQueryService())
        {
        }

        public GpuPvViewModel(VmQueryService queryService)
        {
            _queryService = queryService;
            _gpuService = new VmGpuService(queryService);
            LoadCommand.Execute(null);
        }

        [RelayCommand]
        private async Task LoadAsync()
        {
            if (DesignerProperties.GetIsInDesignMode(new DependencyObject()))
            {
                IsLoading = false;
                IsUiEnabled = true;
                return;
            }

            IsUiEnabled = false;
            IsLoading = true;
            try
            {
                var gpus = await _gpuService.GetHostGpusAsync();
                var vms = await _queryService.GetVmListAsync();

                var assignments = new List<GpuPvAssignment>();
                var vmsWithAssignments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var vm in vms)
                {
                    List<(string Id, string InstancePath)> adapters;
                    try
                    {
                        adapters = await _gpuService.GetVmGpuAdaptersAsync(vm.Name);
                    }
                    catch
                    {
                        // 单台虚拟机读取失败不应让整页不可用
                        continue;
                    }

                    foreach (var adapter in adapters)
                    {
                        var matched = MatchHostGpu(gpus, adapter.InstancePath);
                        var assignment = new GpuPvAssignment
                        {
                            VmName = vm.Name,
                            AdapterId = adapter.Id,
                            InstancePath = adapter.InstancePath,
                            GpuName = matched?.Name ?? Properties.Resources.GpuPv_UnknownDevice,
                            Manu = matched?.Manu ?? string.Empty,
                            DriverVersion = matched?.DriverVersion ?? string.Empty,
                            MemoryBytes = matched?.MemoryBytes ?? 0,
                            HostGpu = matched,
                        };
                        assignment.ShowVmMaintenance = vmsWithAssignments.Add(vm.Name);
                        assignments.Add(assignment);
                    }
                }

                HostGpus.Clear();
                foreach (var gpu in gpus)
                {
                    int count = assignments.Count(a =>
                        string.Equals(a.InstancePath, gpu.PartitionableGpuPath, StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrEmpty(gpu.InstanceId) &&
                         a.InstancePath.Contains(gpu.InstanceId, StringComparison.OrdinalIgnoreCase)));
                    HostGpus.Add(new GpuPvHostGpu(gpu, count));
                }

                Assignments.Clear();
                foreach (var assignment in assignments.OrderBy(a => a.VmName, StringComparer.OrdinalIgnoreCase))
                    Assignments.Add(assignment);

                ShowNoPartitionableGpu = !gpus.Any(g => !string.IsNullOrEmpty(g.PartitionableGpuPath));
            }
            catch (Exception ex)
            {
                ShowError(string.Format(Properties.Resources.GpuPv_Err_Load, ex.Message));
            }
            finally
            {
                IsLoading = false;
                IsUiEnabled = true;
            }
        }

        [RelayCommand]
        private async Task AssignAsync(GpuPvHostGpu? item)
        {
            if (item == null) return;
            if (!item.IsPartitionable)
            {
                ShowTip(Properties.Resources.GpuPv_Tip_NotPartitionable);
                return;
            }

            List<string> poweredOffVms;
            try
            {
                poweredOffVms = (await _queryService.GetVmListAsync())
                    .Where(vm => !vm.IsRunning)
                    .Select(vm => vm.Name)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                ShowError(string.Format(Properties.Resources.GpuPv_Err_ReadVmList, ex.Message));
                return;
            }

            var dialog = new GpuAssignDialog(item.Source, poweredOffVms);
            bool confirmed = await Dialogs.ShowContentDialogAsync(
                Properties.Resources.GpuPv_Dlg_Assign_Title, dialog, Properties.Resources.GpuPv_Dlg_Assign_Primary);
            if (!confirmed) return;
            string? vmName = dialog.SelectedVmName;
            if (string.IsNullOrWhiteSpace(vmName))
            {
                // 分配对话框不预选虚拟机：用户未选择时明确拒绝，绝不回退到列表第一项。
                ShowError(Properties.Resources.GpuPv_Err_NoVmSelected);
                return;
            }

            IsUiEnabled = false;
            try
            {
                var (success, message) = await _gpuService.AssignGpuPartitionAsync(
                    vmName, item.PartitionableGpuPath, dialog.SelectedSharePercent);
                if (success)
                {
                    ShowSuccess(string.Format(Properties.Resources.GpuPv_Msg_Assigned,
                        vmName, item.Name, dialog.SelectedSharePercent));
                    await LoadAsync();
                }
                else
                {
                    ShowError(string.IsNullOrWhiteSpace(message) ? Properties.Resources.GpuPv_Err_AssignFailed : message);
                }
            }
            finally
            {
                IsUiEnabled = true;
            }
        }

        [RelayCommand]
        private async Task RemoveAsync(GpuPvAssignment? item)
        {
            if (item == null) return;

            bool confirmed = await Dialogs.ShowConfirmAsync(
                Properties.Resources.GpuPv_Dlg_Remove_Title,
                string.Format(Properties.Resources.GpuPv_Dlg_Remove_Confirm, item.VmName, item.GpuName),
                Properties.Resources.GpuPv_Dlg_Remove_Primary,
                Properties.Resources.Btn_Cancel,
                isDanger: true);
            if (!confirmed) return;

            IsUiEnabled = false;
            try
            {
                bool success = await _gpuService.RemoveGpuPartitionAsync(item.VmName, item.AdapterId);
                if (success)
                {
                    ShowSuccess(string.Format(Properties.Resources.GpuPv_Msg_Removed, item.VmName, item.GpuName));
                    await LoadAsync();
                }
                else
                {
                    ShowError(Properties.Resources.GpuPv_Err_RemoveFailed);
                }
            }
            finally
            {
                IsUiEnabled = true;
            }
        }

        [RelayCommand]
        private async Task SyncDriverAsync(GpuPvAssignment? item)
        {
            if (item == null) return;
            if (item.HostGpu == null || string.IsNullOrEmpty(item.HostGpu.PartitionableGpuPath))
            {
                ShowError(Properties.Resources.GpuPv_Err_HostGpuNotFound);
                return;
            }
            if (!await EnsureVmPoweredOffAsync(item.VmName)) return;

            var partition = await PromptPartitionAsync(
                item.VmName, Properties.Resources.GpuPv_Dlg_Sync_Title, Properties.Resources.GpuPv_Btn_StartCopy);
            if (partition == null) return;

            await RunDriverTaskAsync(
                string.Format(Properties.Resources.GpuPv_Overlay_SyncTitle, item.VmName, item.GpuName),
                log => _gpuService.SyncWindowsDriversAsync(
                    item.VmName,
                    item.HostGpu.PartitionableGpuPath,
                    item.HostGpu.Manu,
                    partition,
                    log),
                string.Format(Properties.Resources.GpuPv_Msg_SyncDone, item.VmName));
        }

        [RelayCommand]
        private async Task CleanupDriverAsync(GpuPvAssignment? item)
        {
            if (item == null) return;
            if (!await EnsureVmPoweredOffAsync(item.VmName)) return;

            bool confirmed = await Dialogs.ShowConfirmAsync(
                Properties.Resources.GpuPv_Dlg_Cleanup_Title,
                string.Format(Properties.Resources.GpuPv_Dlg_Cleanup_Confirm, item.VmName),
                Properties.Resources.GpuPv_Btn_StartCleanup,
                Properties.Resources.Btn_Cancel,
                isDanger: true);
            if (!confirmed) return;

            var partition = await PromptPartitionAsync(
                item.VmName, Properties.Resources.GpuPv_Dlg_Cleanup_Title, Properties.Resources.GpuPv_Btn_StartCleanup);
            if (partition == null) return;

            await RunDriverTaskAsync(
                string.Format(Properties.Resources.GpuPv_Overlay_CleanupTitle, item.VmName),
                log => _gpuService.CleanupWindowsDriversAsync(item.VmName, partition, log),
                string.Format(Properties.Resources.GpuPv_Msg_CleanupDone, item.VmName));
        }

        [RelayCommand]
        private void CloseDriverOverlay() => IsDriverBusy = false;

        [RelayCommand]
        private async Task MmioCheckAsync(GpuPvAssignment? item)
        {
            if (item == null) return;
            if (!await EnsureVmPoweredOffAsync(item.VmName)) return;

            VmMmioSettings? current;
            try
            {
                current = await VmMmioService.GetSettingsAsync(item.VmName);
            }
            catch (Exception ex)
            {
                ShowError(string.Format(Properties.Resources.GpuPv_Err_MmioRead, ex.Message));
                return;
            }

            if (current == null)
            {
                ShowError(Properties.Resources.GpuPv_Err_MmioNoConfig);
                return;
            }

            var cachedPlan = VmMmioService.ComputeMmioPlan();
            bool ceilingKnown = cachedPlan.HasValue;
            var target = cachedPlan ?? new VmMmioService.MmioPlan(
                current.HighBaseMb ?? 0,
                VmMmioService.DefaultHighSizeMb,
                current.LowSizeMb ?? 3584);

            bool alreadyOptimal = ceilingKnown
                ? current.HighSizeMb == target.HighSizeMb &&
                  current.HighBaseMb == target.BaseMb &&
                  current.LowSizeMb == target.LowSizeMb
                : current.HighSizeMb == target.HighSizeMb;
            if (alreadyOptimal)
            {
                ShowSuccess(string.Format(Properties.Resources.GpuPv_Msg_MmioOptimal, item.VmName));
                return;
            }

            var dialog = new GpuMmioDialog(item.VmName, current, target, ceilingKnown);
            if (!await Dialogs.ShowContentDialogAsync(
                Properties.Resources.GpuPv_Dlg_Mmio_Title, dialog, Properties.Resources.GpuPv_Btn_ApplyFix)) return;

            IsUiEnabled = false;
            try
            {
                ApiResponse response;
                if (!ceilingKnown && current.HighBaseMb == null)
                {
                    var settings = current.Clone();
                    settings.HighSizeMb = target.HighSizeMb;
                    response = await VmMmioService.SetSettingAsync(
                        item.VmName, settings, nameof(VmMmioSettings.HighSizeMb));
                }
                else
                {
                    response = await VmMmioService.ApplyAsync(item.VmName, target);
                }

                if (response.Success)
                {
                    ShowSuccess(string.Format(Properties.Resources.GpuPv_Msg_MmioWritten,
                        target.BaseMb, target.HighSizeMb, target.LowSizeMb));
                }
                else
                {
                    ShowError(string.IsNullOrWhiteSpace(response.Error) ? Properties.Resources.GpuPv_Err_MmioWriteFailed : response.Error);
                }
            }
            finally
            {
                IsUiEnabled = true;
            }
        }

        [RelayCommand]
        private async Task RepairAsync(GpuPvAssignment? item)
        {
            if (item == null) return;
            if (!await EnsureVmPoweredOffAsync(item.VmName)) return;

            List<VmGpuRepairService.StaleGpuPartition> stale;
            try
            {
                stale = await VmGpuRepairService.FindStaleGpuPartitionsAsync(item.VmName);
            }
            catch (Exception ex)
            {
                ShowError(string.Format(Properties.Resources.GpuPv_Err_RepairDetect, ex.Message));
                return;
            }

            if (stale.Count == 0)
            {
                ShowSuccess(string.Format(Properties.Resources.GpuPv_Msg_NoStale, item.VmName));
                return;
            }

            var dialog = new GpuRepairDialog(item.VmName, stale);
            if (!await Dialogs.ShowContentDialogAsync(
                Properties.Resources.GpuPv_Btn_Repair, dialog, Properties.Resources.GpuPv_Btn_BackupRepair)) return;

            IsUiEnabled = false;
            try
            {
                var (success, message, rebound, removed) = await VmGpuRepairService.RepairAsync(item.VmName, stale);
                if (success)
                {
                    ShowSuccess(string.Format(Properties.Resources.GpuPv_Msg_Repaired,
                        item.VmName, rebound, removed, message));
                    await LoadAsync();
                }
                else
                {
                    ShowError(message);
                }
            }
            finally
            {
                IsUiEnabled = true;
            }
        }

        private async Task<PartitionInfo?> PromptPartitionAsync(string vmName, string title, string primaryText)
        {
            List<PartitionInfo> partitions;
            try
            {
                partitions = await _gpuService.GetPartitionsFromVmAsync(vmName);
            }
            catch (Exception ex)
            {
                ShowError(string.Format(Properties.Resources.GpuPv_Err_ReadPartitions, ex.Message));
                return null;
            }

            if (partitions.Count == 0)
            {
                ShowError(Properties.Resources.GpuPv_Err_NoPartitions);
                return null;
            }

            var dialog = new GpuDriverPartitionDialog(vmName, partitions, title);
            bool confirmed = await Dialogs.ShowContentDialogAsync(title, dialog, primaryText);
            var partition = dialog.SelectedPartition;
            if (!confirmed || partition == null) return null;

            if (partition.OsType != OperatingSystemType.Windows)
            {
                ShowError(Properties.Resources.GpuPv_Err_WindowsOnly);
                return null;
            }

            return partition;
        }

        private async Task<bool> EnsureVmPoweredOffAsync(string vmName)
        {
            try
            {
                var vm = (await _queryService.GetVmListAsync())
                    .FirstOrDefault(v => string.Equals(v.Name, vmName, StringComparison.OrdinalIgnoreCase));
                if (vm == null)
                {
                    ShowError(string.Format(Properties.Resources.GpuPv_Err_VmNotFound, vmName));
                    return false;
                }
                if (vm.IsRunning)
                {
                    ShowError(string.Format(Properties.Resources.GpuPv_Err_VmRunning, vmName));
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                ShowError(string.Format(Properties.Resources.GpuPv_Err_ReadVmState, ex.Message));
                return false;
            }
        }

        private async Task RunDriverTaskAsync(
            string title,
            Func<Action<string>, Task<(bool Success, string Message)>> operation,
            string successText)
        {
            DriverTitle = title;
            DriverStatus = Properties.Resources.GpuPv_Overlay_Preparing;
            DriverLog = string.Empty;
            DriverCanClose = false;
            IsDriverBusy = true;
            IsUiEnabled = false;

            void Append(string line)
            {
                DriverStatus = line;
                DriverLog += line + Environment.NewLine;
            }

            try
            {
                var result = await operation(Append);
                if (result.Success)
                {
                    DriverStatus = successText;
                    DriverLog += Environment.NewLine + successText + Environment.NewLine;
                    ShowSuccess(successText);
                    await LoadAsync();
                }
                else
                {
                    DriverStatus = string.Format(Properties.Resources.GpuPv_Overlay_FailedPrefix, result.Message);
                    DriverLog += Environment.NewLine + DriverStatus + Environment.NewLine;
                    ShowError(result.Message);
                }
            }
            catch (Exception ex)
            {
                DriverStatus = string.Format(Properties.Resources.GpuPv_Overlay_ExceptionPrefix, ex.Message);
                DriverLog += Environment.NewLine + ex + Environment.NewLine;
                ShowError(string.Format(Properties.Resources.GpuPv_Err_DriverOperation, ex.Message));
            }
            finally
            {
                DriverCanClose = true;
                IsUiEnabled = true;
            }
        }

        private static GpuInfo? MatchHostGpu(IEnumerable<GpuInfo> gpus, string instancePath)
        {
            if (string.IsNullOrWhiteSpace(instancePath)) return null;
            return gpus.FirstOrDefault(gpu =>
                (!string.IsNullOrEmpty(gpu.InstanceId) &&
                 instancePath.Contains(gpu.InstanceId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(gpu.PartitionableGpuPath) &&
                 string.Equals(instancePath, gpu.PartitionableGpuPath, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
