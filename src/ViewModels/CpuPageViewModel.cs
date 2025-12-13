using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using ExHyperV.ViewModels.Dialogs;
using ExHyperV.Views.Dialogs;

namespace ExHyperV.ViewModels
{
    internal class CoreUpdateDto
    {
        public string VmName { get; set; }
        public int CoreId { get; set; }
        public double Usage { get; set; }
        public PointCollection RenderedGraph { get; set; }
        public bool IsRunning { get; set; }
    }

    public partial class CpuPageViewModel : ObservableObject, IDisposable
    {
        private CpuMonitorService _cpuService;
        private CancellationTokenSource _monitoringCts;
        private Task _monitoringTask;
        private CancellationTokenSource _sleepTokenSource = new CancellationTokenSource();
        private const int MaxHistoryLength = 25;
        private readonly Dictionary<string, LinkedList<double>> _historyCache = new();
        private readonly CpuAffinityService _cpuAffinityService;

        public ObservableCollection<UiVmModel> VmList { get; } = new ObservableCollection<UiVmModel>();

        [ObservableProperty]
        private UiVmModel? _selectedVm;

        [ObservableProperty]
        private int _refreshInterval = 1000;

        [ObservableProperty]
        private bool _isLoading = true;

        private int _selectedSpeedIndex = 0;
        public int SelectedSpeedIndex
        {
            get => _selectedSpeedIndex;
            set { if (SetProperty(ref _selectedSpeedIndex, value)) { UpdateInterval(); WakeUpThread(); } }
        }

        private bool _systemInfoCached = false;
        private HyperVSchedulerType _cachedSchedulerType = HyperVSchedulerType.Unknown;
        private Dictionary<int, int> _cachedCpuSiblingMap = new Dictionary<int, int>();

        public CpuPageViewModel()
        {
            SelectedSpeedIndex = 0;
            _cpuAffinityService = new CpuAffinityService();
        }

        public void StartMonitoring()
        {
            if (_monitoringTask != null && !_monitoringTask.IsCompleted) return;
            _monitoringCts = new CancellationTokenSource();
            _monitoringTask = Task.Run(() => MonitorLoop(_monitoringCts.Token));
        }

        public async Task StopMonitoringAsync()
        {
            if (_monitoringCts != null && !_monitoringCts.IsCancellationRequested)
            {
                _monitoringCts.Cancel();
                WakeUpThread();
                if (_monitoringTask != null)
                {
                    try { await Task.WhenAny(_monitoringTask, Task.Delay(1000)); } catch { }
                }
                _monitoringCts.Dispose();
                _monitoringCts = null;
            }
        }

        private void UpdateInterval()
        {
            RefreshInterval = SelectedSpeedIndex switch { 0 => 1000, 1 => 2000, 2 => -1, _ => 1000 };
        }

        private void WakeUpThread()
        {
            _sleepTokenSource?.Cancel();
        }

        private async Task MonitorLoop(CancellationToken token)
        {
            try
            {
                _cpuService = new CpuMonitorService();
                Application.Current.Dispatcher.Invoke(() => IsLoading = false);
            }
            catch { Application.Current.Dispatcher.Invoke(() => IsLoading = false); return; }

            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_sleepTokenSource.IsCancellationRequested) { _sleepTokenSource.Dispose(); _sleepTokenSource = new CancellationTokenSource(); }
                    if (RefreshInterval == -1)
                    {
                        await Task.Delay(Timeout.Infinite, CancellationTokenSource.CreateLinkedTokenSource(token, _sleepTokenSource.Token).Token);
                        continue;
                    }
                    var startTime = DateTime.Now;
                    var rawData = _cpuService.GetCpuUsage();
                    var updates = ProcessData(rawData);
                    if (token.IsCancellationRequested) break;
                    Application.Current.Dispatcher.Invoke(() => ApplyUpdates(updates));
                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                    var delay = RefreshInterval - (int)elapsed;
                    if (delay < 100) delay = 100;
                    await Task.Delay(delay, CancellationTokenSource.CreateLinkedTokenSource(token, _sleepTokenSource.Token).Token);
                }
                catch (TaskCanceledException) { if (token.IsCancellationRequested) break; }
                catch (Exception) { if (token.IsCancellationRequested) break; await Task.Delay(5000, token); }
            }
            (_cpuService as IDisposable)?.Dispose();
            _cpuService = null;
        }

        private List<CoreUpdateDto> ProcessData(List<CpuCoreMetric> rawData)
        {
            var updates = new List<CoreUpdateDto>();
            foreach (var metric in rawData)
            {
                PointCollection points = null;
                if (metric.IsRunning)
                {
                    var key = $"{metric.VmName}_{metric.CoreId}";
                    if (!_historyCache.TryGetValue(key, out var history))
                    {
                        history = new LinkedList<double>();
                        for (int k = 0; k < MaxHistoryLength; k++) history.AddLast(0);
                        _historyCache[key] = history;
                    }
                    history.AddLast(metric.Usage);
                    if (history.Count > MaxHistoryLength) history.RemoveFirst();
                    points = CalculatePoints(history);
                    points.Freeze();
                }
                updates.Add(new CoreUpdateDto { VmName = metric.VmName, CoreId = metric.CoreId, Usage = metric.Usage, RenderedGraph = points, IsRunning = metric.IsRunning });
            }
            return updates;
        }

        private PointCollection CalculatePoints(LinkedList<double> history)
        {
            double w = 100.0; double h = 100.0; double step = w / (MaxHistoryLength - 1);
            var points = new PointCollection(MaxHistoryLength + 2);
            points.Add(new Point(0, h));
            int i = 0;
            foreach (var val in history)
            {
                double x = i * step;
                double y = h - val;
                if (y < 0) y = 0; if (y > h) y = h;
                points.Add(new Point(x, y));
                i++;
            }
            points.Add(new Point(w, h));
            return points;
        }

        private void ApplyUpdates(List<CoreUpdateDto> updates)
        {
            if (_monitoringCts == null || _monitoringCts.IsCancellationRequested) return;
            var activeVmNames = updates.Select(x => x.VmName).ToHashSet();
            var vmsToRemove = VmList.Where(vm => !activeVmNames.Contains(vm.Name)).ToList();
            bool selectionRemoved = vmsToRemove.Any(vm => vm == SelectedVm);
            foreach (var vm in vmsToRemove) VmList.Remove(vm);
            if (selectionRemoved) SelectedVm = null;
            var grouped = updates.GroupBy(x => x.VmName);
            foreach (var group in grouped)
            {
                var vmName = group.Key;
                var uiVm = VmList.FirstOrDefault(v => v.Name == vmName);
                if (uiVm == null) { uiVm = new UiVmModel { Name = vmName }; VmList.Add(uiVm); }
                bool isVmRunning = group.Any(x => x.IsRunning);
                uiVm.IsRunning = isVmRunning;
                int coreCount = group.Count();
                uiVm.Columns = CalculateOptimalColumns(coreCount);
                uiVm.Rows = (coreCount > 0) ? (int)Math.Ceiling((double)coreCount / uiVm.Columns) : 0;
                uiVm.AverageUsage = isVmRunning ? group.Average(update => update.Usage) : 0;
                var updatedCoreIds = group.Select(u => u.CoreId).ToHashSet();
                var coresToRemove = uiVm.Cores.Where(c => !updatedCoreIds.Contains(c.CoreId)).ToList();
                foreach (var core in coresToRemove) uiVm.Cores.Remove(core);
                foreach (var update in group)
                {
                    var uiCore = uiVm.Cores.FirstOrDefault(c => c.CoreId == update.CoreId);
                    if (uiCore == null)
                    {
                        uiCore = new UiCoreModel { CoreId = update.CoreId, CoreType = vmName.Equals("Host", StringComparison.OrdinalIgnoreCase) ? CpuMonitorService.GetCoreType(update.CoreId) : CoreType.Unknown };
                        uiVm.Cores.Add(uiCore);
                    }
                    uiCore.Usage = update.Usage;
                    uiCore.HistoryPoints = update.RenderedGraph;
                }
            }

            var hostVm = VmList.FirstOrDefault(vm => vm.Name.Equals("Host", StringComparison.OrdinalIgnoreCase));
            if (hostVm != null)
            {
                var totalCoreUsage = hostVm.Cores.ToDictionary(c => c.CoreId, c => c.Usage);
                foreach (var vm in VmList.Where(v => !v.Name.Equals("Host", StringComparison.OrdinalIgnoreCase) && v.IsRunning))
                {
                    foreach (var vmCore in vm.Cores)
                    {
                        if (totalCoreUsage.ContainsKey(vmCore.CoreId))
                        {
                            totalCoreUsage[vmCore.CoreId] += vmCore.Usage;
                        }
                        else
                        {
                            totalCoreUsage[vmCore.CoreId] = vmCore.Usage;
                        }
                    }
                }
                foreach (var hostCore in hostVm.Cores)
                {
                    var newTotalUsage = totalCoreUsage[hostCore.CoreId];
                    hostCore.Usage = Math.Min(100.0, newTotalUsage);
                    var historyKey = $"Host_{hostCore.CoreId}";
                    if (!_historyCache.TryGetValue(historyKey, out var history))
                    {
                        history = new LinkedList<double>();
                        for (int k = 0; k < MaxHistoryLength; k++) history.AddLast(0);
                        _historyCache[historyKey] = history;
                    }
                    history.AddLast(hostCore.Usage);
                    if (history.Count > MaxHistoryLength) history.RemoveFirst();
                    hostCore.HistoryPoints = CalculatePoints(history);
                }
                hostVm.AverageUsage = hostVm.Cores.Any() ? hostVm.Cores.Average(c => c.Usage) : 0;
            }

            if (!_systemInfoCached && hostVm != null && hostVm.Cores.Any())
            {
                _cachedSchedulerType = HyperVSchedulerService.GetSchedulerType();
                _cachedCpuSiblingMap = CpuTopologyService.GetCpuSiblingMap();
                _systemInfoCached = true;
            }
            if (SelectedVm == null && VmList.Any()) SelectedVm = VmList[0];

            // ===================================================================
            //            ★★★ 新增：三级排序逻辑 ★★★
            // ===================================================================
            // 1. 根据新的三级规则，创建一个期望的有序列表。
            var sortedVms = VmList
                // 规则1: Host 永远第一 (false 排在 true 前面)
                .OrderBy(vm => vm.Name != "Host")
                // 规则2: 运行中的排在前面 (IsRunning 是 true, !IsRunning 是 false, false 排在 true 前面)
                .ThenBy(vm => !vm.IsRunning)
                // 规则3: 在各自的分组内，按字母顺序排序
                .ThenBy(vm => vm.Name)
                .ToList();

            // 2. 高效地移动列表项到正确的位置，避免UI闪烁。
            for (int i = 0; i < sortedVms.Count; i++)
            {
                var desiredVm = sortedVms[i];
                var currentIndex = VmList.IndexOf(desiredVm);
                if (currentIndex != i)
                {
                    VmList.Move(currentIndex, i);
                }
            }
        }

        private int CalculateOptimalColumns(int count)
        {
            if (count <= 1) return 1; if (count <= 3) return count; if (count == 4) return 2; if (count <= 6) return 3; if (count == 8) return 4;
            double sqrt = Math.Sqrt(count);
            if (sqrt == (int)sqrt) return (int)sqrt;
            int startingPoint = (int)sqrt;
            for (int i = startingPoint; i >= 2; i--) { if (count % i == 0) return count / i; }
            return (int)Math.Ceiling(sqrt);
        }

        #region --- 控制面板命令 ---

        [RelayCommand]
        private async Task OpenVmCpuAffinityAsync()
        {
            try
            {
                if (SelectedVm == null || SelectedVm.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)) return;
                var vmId = GetVmGuidByName(SelectedVm.Name);
                if (vmId == Guid.Empty) return;
                var hostVm = VmList.FirstOrDefault(vm => vm.Name.Equals("Host", StringComparison.OrdinalIgnoreCase));
                if (hostVm == null || hostVm.Cores.Count == 0) return;

                if (_cachedSchedulerType == HyperVSchedulerType.Root)
                {
                    if (!SelectedVm.IsRunning) return;

                    var dialogViewModel = new CpuAffinityDialogViewModel(
                        SelectedVm.Name, SelectedVm.Cores.Count, hostVm.Cores,
                        _cachedSchedulerType, _cachedCpuSiblingMap
                    );

                    var currentAffinity = ProcessAffinityManager.GetVmProcessAffinity(vmId);
                    foreach (var coreVm in dialogViewModel.Cores)
                    {
                        if (currentAffinity.Contains(coreVm.CoreId))
                        {
                            coreVm.IsSelected = true;
                        }
                    }

                    var dialog = new CpuAffinityDialog { DataContext = dialogViewModel, Owner = Application.Current.MainWindow };
                    if (dialog.ShowDialog() == true)
                    {
                        var selectedCoreIds = dialogViewModel.Cores
                            .Where(c => c.IsSelected).Select(c => c.CoreId).ToList();
                        ProcessAffinityManager.SetVmProcessAffinity(vmId, selectedCoreIds);
                    }
                }
                else
                {
                    var dialogViewModel = new CpuAffinityDialogViewModel(
                        SelectedVm.Name, SelectedVm.Cores.Count, hostVm.Cores,
                        _cachedSchedulerType, _cachedCpuSiblingMap
                    );

                    try
                    {
                        string vmGroupJson = await Task.Run(() => HcsManager.GetVmCpuGroupAsJson(vmId));
                        if (!string.IsNullOrEmpty(vmGroupJson))
                        {
                            var vmGroupInfo = JsonSerializer.Deserialize<VmCpuGroupInfo>(vmGroupJson);
                            if (vmGroupInfo?.CpuGroupId != Guid.Empty)
                            {
                                var groupDetails = await _cpuAffinityService.GetCpuGroupDetailsAsync(vmGroupInfo.CpuGroupId);
                                if (groupDetails?.Affinity?.LogicalProcessors != null)
                                {
                                    foreach (var coreVM in dialogViewModel.Cores)
                                    {
                                        if (groupDetails.Affinity.LogicalProcessors.Contains((uint)coreVM.CoreId))
                                        {
                                            coreVM.IsSelected = true;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"查询虚拟机当前CPU组失败: {ex.Message}");
                    }

                    var dialog = new CpuAffinityDialog { DataContext = dialogViewModel, Owner = Application.Current.MainWindow };
                    if (dialog.ShowDialog() == true)
                    {
                        var selectedCoreIds = dialogViewModel.Cores
                            .Where(c => c.IsSelected).Select(c => c.CoreId).ToList();
                        await Task.Run(() => HcsManager.SetVmCpuGroup(vmId, Guid.Empty));
                        if (selectedCoreIds.Any())
                        {
                            Guid cpuGroupId = await _cpuAffinityService.FindOrCreateCpuGroupAsync(selectedCoreIds);
                            await Task.Run(() => HcsManager.SetVmCpuGroup(vmId, cpuGroupId));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"处理 CPU 绑定时发生错误: {ex.Message}");
            }
        }

        #endregion

        private Guid GetVmGuidByName(string vmName)
        {
            try
            {
                string query = $"SELECT * FROM Msvm_ComputerSystem WHERE ElementName = '{vmName}'";
                using (var searcher = new ManagementObjectSearcher("root\\virtualization\\v2", query))
                {
                    var vmObject = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (vmObject != null && Guid.TryParse((string)vmObject["Name"], out Guid vmId))
                    {
                        return vmId;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetVmGuidByName for '{vmName}' failed: {ex.Message}");
            }
            return Guid.Empty;
        }

        public async void Dispose()
        {
            await StopMonitoringAsync();
            _sleepTokenSource?.Dispose();
        }
    }
}