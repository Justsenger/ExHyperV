using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Interaction;
using ExHyperV.Models;
using ExHyperV.Services;
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
                        assignments.Add(new GpuPvAssignment
                        {
                            VmName = vm.Name,
                            AdapterId = adapter.Id,
                            InstancePath = adapter.InstancePath,
                            GpuName = matched?.Name ?? "Unknown Device",
                            Manu = matched?.Manu ?? string.Empty,
                            DriverVersion = matched?.DriverVersion ?? string.Empty,
                            MemoryBytes = matched?.MemoryBytes ?? 0,
                            HostGpu = matched,
                        });
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
                ShowError("读取 GPU 分区信息失败：" + ex.Message);
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
                ShowTip("该显卡没有可分区接口。请先安装显卡驱动，再启用 Hyper-V（顺序反了只能重装驱动）。");
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
                ShowError("读取虚拟机列表失败：" + ex.Message);
                return;
            }

            var dialog = new GpuAssignDialog(item.Source, poweredOffVms);
            bool confirmed = await Dialogs.ShowContentDialogAsync("分配 GPU 分区", dialog, "分配");
            string? vmName = dialog.SelectedVmName;
            if (!confirmed || string.IsNullOrWhiteSpace(vmName)) return;

            IsUiEnabled = false;
            try
            {
                var (success, message) = await _gpuService.AssignGpuPartitionAsync(vmName, item.PartitionableGpuPath);
                if (success)
                {
                    ShowSuccess($"已为「{vmName}」分配 {item.Name} 分区。驱动需要在来宾内单独复制。");
                    await LoadAsync();
                }
                else
                {
                    ShowError(string.IsNullOrWhiteSpace(message) ? "分配失败。" : message);
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
                "移除 GPU 分区",
                $"确定从「{item.VmName}」移除 {item.GpuName} 分区？\r\n移除后该虚拟机将失去 GPU 加速，配置修改要求虚拟机处于关机状态。",
                "移除",
                "取消",
                isDanger: true);
            if (!confirmed) return;

            IsUiEnabled = false;
            try
            {
                bool success = await _gpuService.RemoveGpuPartitionAsync(item.VmName, item.AdapterId);
                if (success)
                {
                    ShowSuccess($"已从「{item.VmName}」移除 {item.GpuName} 分区。");
                    await LoadAsync();
                }
                else
                {
                    ShowError("移除失败：未能删除分区设置，请确认虚拟机已关机后重试。");
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
                ShowError("无法定位该分区对应的宿主显卡，驱动复制已中止。");
                return;
            }
            if (!await EnsureVmPoweredOffAsync(item.VmName)) return;

            var partition = await PromptPartitionAsync(item.VmName, "复制/更新显卡驱动", "开始复制");
            if (partition == null) return;

            await RunDriverTaskAsync(
                $"正在向「{item.VmName}」复制 {item.GpuName} 驱动（离线挂载）",
                log => _gpuService.SyncWindowsDriversAsync(
                    item.VmName,
                    item.HostGpu.PartitionableGpuPath,
                    item.HostGpu.Manu,
                    partition,
                    log),
                $"驱动已复制到「{item.VmName}」。启动虚拟机后由来宾系统完成驱动安装。");
        }

        [RelayCommand]
        private async Task CleanupDriverAsync(GpuPvAssignment? item)
        {
            if (item == null) return;
            if (!await EnsureVmPoweredOffAsync(item.VmName)) return;

            bool confirmed = await Dialogs.ShowConfirmAsync(
                "清理显卡驱动",
                $"将从「{item.VmName}」清理本工具复制进去的显卡驱动文件。\r\n" +
                "只删除带有本工具注入标记的内容；没有标记时只列出候选位置，不会自动删除。",
                "开始清理",
                "取消",
                isDanger: true);
            if (!confirmed) return;

            var partition = await PromptPartitionAsync(item.VmName, "清理显卡驱动", "开始清理");
            if (partition == null) return;

            await RunDriverTaskAsync(
                $"正在清理「{item.VmName}」的显卡驱动（离线挂载）",
                log => _gpuService.CleanupWindowsDriversAsync(item.VmName, partition, log),
                $"已清理「{item.VmName}」中本工具复制的显卡驱动。");
        }

        [RelayCommand]
        private void CloseDriverOverlay() => IsDriverBusy = false;

        private async Task<PartitionInfo?> PromptPartitionAsync(string vmName, string title, string primaryText)
        {
            List<PartitionInfo> partitions;
            try
            {
                partitions = await _gpuService.GetPartitionsFromVmAsync(vmName);
            }
            catch (Exception ex)
            {
                ShowError("读取虚拟机分区失败：" + ex.Message);
                return null;
            }

            if (partitions.Count == 0)
            {
                ShowError("虚拟机内没有可用的磁盘分区，或磁盘尚未初始化，无法进行离线驱动操作。");
                return null;
            }

            var dialog = new GpuDriverPartitionDialog(vmName, partitions, title);
            bool confirmed = await Dialogs.ShowContentDialogAsync(title, dialog, primaryText);
            var partition = dialog.SelectedPartition;
            if (!confirmed || partition == null) return null;

            if (partition.OsType != OperatingSystemType.Windows)
            {
                ShowError("本期只支持 Windows 来宾的驱动复制与清理；Linux 来宾请先只分配分区。");
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
                    ShowError($"找不到虚拟机「{vmName}」。");
                    return false;
                }
                if (vm.IsRunning)
                {
                    ShowError($"「{vmName}」正在运行。驱动复制/清理需要离线挂载磁盘，请先关机再操作。");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                ShowError("读取虚拟机状态失败：" + ex.Message);
                return false;
            }
        }

        private async Task RunDriverTaskAsync(
            string title,
            Func<Action<string>, Task<(bool Success, string Message)>> operation,
            string successText)
        {
            DriverTitle = title;
            DriverStatus = "准备中…";
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
                    DriverStatus = "失败：" + result.Message;
                    DriverLog += Environment.NewLine + "失败：" + result.Message + Environment.NewLine;
                    ShowError(result.Message);
                }
            }
            catch (Exception ex)
            {
                DriverStatus = "异常：" + ex.Message;
                DriverLog += Environment.NewLine + ex + Environment.NewLine;
                ShowError("驱动操作异常：" + ex.Message);
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
