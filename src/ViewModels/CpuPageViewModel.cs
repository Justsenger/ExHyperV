// 文件路径: ExHyperV/ViewModels/CpuPageViewModel.cs

using CommunityToolkit.Mvvm.ComponentModel;
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

namespace ExHyperV.ViewModels
{
    // CoreUpdateDto 保持不变
    internal class CoreUpdateDto
    {
        public string VmName { get; set; }
        public int CoreId { get; set; }
        public double Usage { get; set; }
        public PointCollection RenderedGraph { get; set; }
    }

    public partial class CpuPageViewModel : ObservableObject, IDisposable
    {
        private CpuMonitorService _cpuService;

        // --- 改动 1: 移除 volatile bool _isMonitoring, 使用 CancellationTokenSource 来控制循环 ---
        private CancellationTokenSource _monitoringCts;
        private Task _monitoringTask;

        private CancellationTokenSource _sleepTokenSource = new CancellationTokenSource();
        private const int MaxHistoryLength = 25;
        private readonly Dictionary<string, LinkedList<double>> _historyCache = new();
        public ObservableCollection<UiVmModel> VmList { get; } = new ObservableCollection<UiVmModel>();

        [ObservableProperty]
        private UiVmModel? _selectedVm;

        [ObservableProperty]
        private int _refreshInterval = 2000;

        [ObservableProperty]
        private bool _isLoading = true;

        private int _selectedSpeedIndex = 1;
        public int SelectedSpeedIndex
        {
            get => _selectedSpeedIndex;
            set { if (SetProperty(ref _selectedSpeedIndex, value)) { UpdateInterval(); WakeUpThread(); } }
        }

        // --- 改动 2: 构造函数不再启动监控 ---
        public CpuPageViewModel()
        {
            SelectedSpeedIndex = 1;
            // Task.Run(MonitorLoop); // 不再在这里启动
        }

        // --- 改动 3: 创建可重复调用的 Start 和 Stop 方法 ---

        /// <summary>
        /// 启动监控循环。如果已在运行，则什么也不做。
        /// </summary>
        public void StartMonitoring()
        {
            // 防止重复启动
            if (_monitoringTask != null && !_monitoringTask.IsCompleted)
            {
                return;
            }

            _monitoringCts = new CancellationTokenSource();
            _monitoringTask = Task.Run(() => MonitorLoop(_monitoringCts.Token));
        }

        /// <summary>
        /// 停止监控循环，并等待任务结束。
        /// </summary>
        public void StopMonitoring()
        {
            if (_monitoringCts != null && !_monitoringCts.IsCancellationRequested)
            {
                _monitoringCts.Cancel();

                // 唤醒可能正在长时间休眠的线程，以便它能快速响应取消
                WakeUpThread();

                // 等待后台任务完全退出，以避免资源竞争
                _monitoringTask?.Wait(TimeSpan.FromSeconds(1));

                _monitoringCts.Dispose();
                _monitoringCts = null;
            }
        }

        private void UpdateInterval()
        {
            RefreshInterval = SelectedSpeedIndex switch
            {
                0 => 1000,
                1 => 2000,
                2 => 5000,
                3 => -1,
                _ => 2000
            };
        }

        private void WakeUpThread()
        {
            _sleepTokenSource?.Cancel();
        }

        // --- 改动 4: MonitorLoop 接受一个 CancellationToken ---
        private async Task MonitorLoop(CancellationToken token)
        {
            try
            {
                // 在循环开始时创建服务实例
                _cpuService = new CpuMonitorService();
                Application.Current.Dispatcher.Invoke(() => IsLoading = false);
            }
            catch
            {
                Application.Current.Dispatcher.Invoke(() => IsLoading = false);
                return; // 如果服务创建失败，直接退出任务
            }

            // 使用 CancellationToken 来控制主循环
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (_sleepTokenSource.IsCancellationRequested)
                    {
                        _sleepTokenSource.Dispose();
                        _sleepTokenSource = new CancellationTokenSource();
                    }

                    if (RefreshInterval == -1)
                    {
                        // 使用链接的Token，这样外部的停止信号也能唤醒它
                        await Task.Delay(Timeout.Infinite, CancellationTokenSource.CreateLinkedTokenSource(token, _sleepTokenSource.Token).Token);
                        continue;
                    }

                    var startTime = DateTime.Now;
                    var rawData = _cpuService.GetCpuUsage();
                    var updates = ProcessData(rawData);

                    // 在应用更新前，再次检查是否已被请求取消
                    if (token.IsCancellationRequested) break;

                    Application.Current.Dispatcher.Invoke(() => ApplyUpdates(updates));

                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                    var delay = RefreshInterval - (int)elapsed;
                    if (delay < 100) delay = 100;

                    // 使用链接的Token进行延迟
                    await Task.Delay(delay, CancellationTokenSource.CreateLinkedTokenSource(token, _sleepTokenSource.Token).Token);
                }
                catch (TaskCanceledException)
                {
                    // 区分是哪个Token触发的取消
                    if (token.IsCancellationRequested)
                    {
                        // 这是正常的停止信号，退出循环
                        break;
                    }
                    // 否则，这只是一个WakeUp信号，循环继续
                }
                catch (Exception)
                {
                    if (token.IsCancellationRequested) break;
                    await Task.Delay(5000, token);
                }
            }

            // 循环结束后，释放服务资源
            // 注意: CpuMonitorService可能也需要实现IDisposable
            (_cpuService as IDisposable)?.Dispose();
            _cpuService = null;
        }

        // ProcessData, CalculatePoints, ApplyUpdates, CalculateOptimalColumns 方法保持不变...
        private List<CoreUpdateDto> ProcessData(List<CpuCoreMetric> rawData)
        {
            var updates = new List<CoreUpdateDto>();
            foreach (var metric in rawData)
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
                var points = CalculatePoints(history);
                points.Freeze();
                updates.Add(new CoreUpdateDto { VmName = metric.VmName, CoreId = metric.CoreId, Usage = metric.Usage, RenderedGraph = points });
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
                double x = i * step; double y = h - val;
                if (y < 0) y = 0; if (y > h) y = h;
                points.Add(new Point(x, y)); i++;
            }
            points.Add(new Point(w, h));
            return points;
        }

        private void ApplyUpdates(List<CoreUpdateDto> updates)
        {
            // 增加一个检查，确保在后台任务已经请求停止后，不再更新UI
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
                if (uiVm == null)
                {
                    uiVm = new UiVmModel { Name = vmName };
                    VmList.Add(uiVm);
                }

                uiVm.AverageUsage = group.Average(update => update.Usage);

                int coreCount = group.Count();
                int cols = CalculateOptimalColumns(coreCount);
                if (uiVm.Columns != cols) uiVm.Columns = cols;

                int rows = (int)Math.Ceiling((double)coreCount / cols);
                if (uiVm.Rows != rows) uiVm.Rows = rows;

                var existingCoreIds = uiVm.Cores.Select(c => c.CoreId).ToHashSet();
                var updatedCoreIds = group.Select(u => u.CoreId).ToHashSet();

                var coresToRemove = uiVm.Cores.Where(c => !updatedCoreIds.Contains(c.CoreId)).ToList();
                foreach (var core in coresToRemove) uiVm.Cores.Remove(core);

                foreach (var update in group)
                {
                    var uiCore = uiVm.Cores.FirstOrDefault(c => c.CoreId == update.CoreId);
                    if (uiCore == null)
                    {
                        uiCore = new UiCoreModel { CoreId = update.CoreId };
                        uiVm.Cores.Add(uiCore);
                    }
                    uiCore.Usage = update.Usage;
                    uiCore.HistoryPoints = update.RenderedGraph;
                }
            }

            if (SelectedVm == null && VmList.Any())
            {
                SelectedVm = VmList[0];
            }
        }

        private int CalculateOptimalColumns(int count)
        {
            if (count <= 1) return 1;
            if (count <= 3) return count;
            if (count == 4) return 2;
            if (count <= 6) return 3;
            if (count == 8) return 4;

            double sqrt = Math.Sqrt(count);
            if (sqrt == (int)sqrt) return (int)sqrt;

            int startingPoint = (int)sqrt;
            for (int i = startingPoint; i >= 2; i--)
            {
                if (count % i == 0)
                {
                    return count / i;
                }
            }
            return (int)Math.Ceiling(sqrt);
        }

        // --- 改动 5: Dispose 现在只调用 StopMonitoring ---
        public void Dispose()
        {
            StopMonitoring();
            _sleepTokenSource?.Dispose();
        }
    }
}