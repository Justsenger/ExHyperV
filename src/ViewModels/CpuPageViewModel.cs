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
        private volatile bool _isMonitoring = true;
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

        public CpuPageViewModel()
        {
            SelectedSpeedIndex = 1;
            Task.Run(MonitorLoop);
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
            try { _sleepTokenSource?.Cancel(); } catch { }
        }

        private async Task MonitorLoop()
        {
            try { _cpuService = new CpuMonitorService(); }
            catch { Application.Current.Dispatcher.Invoke(() => IsLoading = false); return; }

            Application.Current.Dispatcher.Invoke(() => IsLoading = false);

            while (_isMonitoring)
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
                        await Task.Delay(Timeout.Infinite, _sleepTokenSource.Token);
                        continue;
                    }
                    var startTime = DateTime.Now;
                    var rawData = _cpuService.GetCpuUsage();
                    var updates = ProcessData(rawData);
                    Application.Current.Dispatcher.Invoke(() => ApplyUpdates(updates));
                    var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                    var delay = RefreshInterval - (int)elapsed;
                    if (delay < 100) delay = 100;
                    await Task.Delay(delay, _sleepTokenSource.Token);
                }
                catch (TaskCanceledException) { }
                catch (Exception) { await Task.Delay(5000); }
            }
        }

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
            if (!_isMonitoring) return;

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
            // 1. 优先处理常见的小数量，确保最佳美学效果
            if (count <= 1) return 1;
            if (count <= 3) return count; // 2和3个核心，单行显示
            if (count == 4) return 2;     // 2x2
            if (count <= 6) return 3;     // 5和6个核心，3x2布局
            if (count == 8) return 4;     // 8个核心，4x2布局，远优于3x3

            double sqrt = Math.Sqrt(count);

            // 2. 如果是完美的正方形，直接返回
            if (sqrt == (int)sqrt)
            {
                return (int)sqrt;
            }

            // 3. 核心逻辑：寻找最接近平方根的因数，以形成最饱满的矩形
            // 从平方根的整数部分向下搜索
            int startingPoint = (int)sqrt;
            for (int i = startingPoint; i >= 2; i--)
            {
                if (count % i == 0)
                {
                    // 找到了一个因数对 (i, count / i)
                    // 返回两者中较大的那个作为列数，以优先形成更宽的“横向”矩形
                    return count / i;
                }
            }

            // 4. 如果找不到因数 (数字是素数), 则回退到原始的“最接近正方形”的方法
            return (int)Math.Ceiling(sqrt);
        }
        public void Dispose()
        {
            _isMonitoring = false;
            _sleepTokenSource?.Cancel();
            _sleepTokenSource?.Dispose();
        }
    }
}