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
                int cols = CalculateOptimalColumns(coreCount);
                if (uiVm.Columns != cols) uiVm.Columns = cols;
                int rows = (int)Math.Ceiling((double)coreCount / cols);
                if (uiVm.Rows != rows) uiVm.Rows = rows;
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
            if (SelectedVm == null && VmList.Any()) SelectedVm = VmList[0];
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
                if (vmId == Guid.Empty)
                {
                    MessageBox.Show($"无法找到虚拟机 '{SelectedVm.Name}' 的GUID。", "配置错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var hostVm = VmList.FirstOrDefault(vm => vm.Name.Equals("Host", StringComparison.OrdinalIgnoreCase));
                if (hostVm == null || hostVm.Cores.Count == 0)
                {
                    MessageBox.Show("未能找到宿主机核心信息，无法设置 CPU 绑定。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var dialogViewModel = new CpuAffinityDialogViewModel(SelectedVm.Name, SelectedVm.Cores.Count, hostVm.Cores);

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
                                                         .Where(c => c.IsSelected)
                                                         .Select(c => c.CoreId)
                                                         .ToList();

                    // =======================================================
                    // 终极业务逻辑: 先解绑，再绑定
                    // =======================================================

                    // 步骤 1: 无论如何，先执行解除绑定操作
                    Debug.WriteLine("------------------ APPLYING CHANGES ------------------");
                    Debug.WriteLine($"正在为 VM {vmId} 执行步骤 1: 解除旧的CPU组绑定...");
                    await Task.Run(() => HcsManager.SetVmCpuGroup(vmId, Guid.Empty));
                    Debug.WriteLine($"步骤 1: 解除绑定完成。");

                    // 步骤 2: 如果用户选择了核心，则查找/创建新组并绑定
                    if (selectedCoreIds.Any())
                    {
                        Debug.WriteLine("用户选择了非0个核心，继续执行 [绑定] 逻辑。");

                        Guid cpuGroupId = await _cpuAffinityService.FindOrCreateCpuGroupAsync(selectedCoreIds);

                        Debug.WriteLine($"正在为 VM {vmId} 执行步骤 2: 绑定新的CPU组 {cpuGroupId}...");
                        await Task.Run(() => HcsManager.SetVmCpuGroup(vmId, cpuGroupId));
                        Debug.WriteLine($"步骤 2: 新组绑定完成。");

                        MessageBox.Show($"已成功将CPU组 '{cpuGroupId}' 应用到虚拟机 '{SelectedVm.Name}'。", "操作成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        Debug.WriteLine("用户选择了0个核心，流程结束。");
                        MessageBox.Show($"已为虚拟机 '{SelectedVm.Name}' 解除CPU绑定。", "操作成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    Debug.WriteLine("------------------ CHANGES APPLIED -------------------");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"应用CPU组时发生错误：\n\n{ex.Message}", "严重错误", MessageBoxButton.OK, MessageBoxImage.Error);
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