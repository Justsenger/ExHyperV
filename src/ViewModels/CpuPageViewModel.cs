using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using ExHyperV.Models;
using ExHyperV.Services;
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
                    try
                    {
                        await Task.WhenAny(_monitoringTask, Task.Delay(1000));
                    }
                    catch { }
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

                updates.Add(new CoreUpdateDto
                {
                    VmName = metric.VmName,
                    CoreId = metric.CoreId,
                    Usage = metric.Usage,
                    RenderedGraph = points,
                    IsRunning = metric.IsRunning
                });
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
                        uiCore = new UiCoreModel
                        {
                            CoreId = update.CoreId,
                            CoreType = vmName.Equals("Host", StringComparison.OrdinalIgnoreCase)
                                        ? CpuMonitorService.GetCoreType(update.CoreId)
                                        : CoreType.Unknown
                        };
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
        private void OpenVmCpuAffinity()
        {
            try
            {
                if (SelectedVm == null || SelectedVm.Name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var hostVm = VmList.FirstOrDefault(vm => vm.Name.Equals("Host", StringComparison.OrdinalIgnoreCase));
                if (hostVm == null || hostVm.Cores.Count == 0)
                {
                    MessageBox.Show("未能找到宿主机核心信息，无法设置 CPU 绑定。", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // =======================================================
                // 修正点: 传递了正确的参数 SelectedVm.Cores.Count (int)
                // =======================================================
                var dialogViewModel = new CpuAffinityDialogViewModel(SelectedVm.Name, SelectedVm.Cores.Count, hostVm.Cores);

                var dialog = new CpuAffinityDialog
                {
                    DataContext = dialogViewModel,
                    Owner = Application.Current.MainWindow
                };

                if (dialog.ShowDialog() == true)
                {
                    var selectedCoreIds = dialogViewModel.Cores
                                                         .Where(c => c.IsSelected)
                                                         .Select(c => c.CoreId)
                                                         .ToList();

                    string selectedIdsText = selectedCoreIds.Any() ? string.Join(", ", selectedCoreIds) : "无";
                    MessageBox.Show($"已为虚拟机 '{SelectedVm.Name}' 选择的核心: {selectedIdsText}", "设置成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"程序在执行“CPU绑定”操作时发生了一个未处理的异常：\n\n" +
                                $"类型: {ex.GetType().Name}\n" +
                                $"信息: {ex.Message}\n\n" +
                                $"堆栈跟踪:\n{ex.StackTrace}",
                                "严重运行时错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        public async void Dispose()
        {
            await StopMonitoringAsync();
            _sleepTokenSource?.Dispose();
        }
    }
}