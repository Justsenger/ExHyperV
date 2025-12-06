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
        // 【修复1】去掉 readonly，否则无法在 MonitorLoop 中初始化
        private CpuMonitorService _cpuService;
        private volatile bool _isMonitoring = true;

        private CancellationTokenSource _sleepTokenSource = new CancellationTokenSource();

        private const int MaxHistoryLength = 25;
        private readonly Dictionary<string, LinkedList<double>> _historyCache = new();

        public ObservableCollection<UiVmModel> VmList { get; } = new ObservableCollection<UiVmModel>();

        [ObservableProperty]
        private int _refreshInterval = 2000;

        [ObservableProperty]
        private bool _isLoading = true;

        private int _selectedSpeedIndex = 1;
        public int SelectedSpeedIndex
        {
            get => _selectedSpeedIndex;
            set
            {
                if (SetProperty(ref _selectedSpeedIndex, value))
                {
                    UpdateInterval();
                    WakeUpThread();
                }
            }
        }

        public CpuPageViewModel()
        {
            // 构造函数保持干净，只启动任务
            Task.Run(MonitorLoop);
        }

        private void UpdateInterval()
        {
            RefreshInterval = SelectedSpeedIndex switch
            {
                0 => 1000,
                1 => 2000,
                2 => 5000,
                3 => -1, // 暂停
                _ => 2000
            };
        }

        private void WakeUpThread()
        {
            try { _sleepTokenSource?.Cancel(); } catch { }
        }

        private async Task MonitorLoop()
        {
            // 1. 在后台初始化 Service (解决进入页面卡顿)
            try
            {
                _cpuService = new CpuMonitorService();
            }
            catch
            {
                // 如果初始化失败，取消加载状态避免死锁，然后退出
                Application.Current.Dispatcher.Invoke(() => IsLoading = false);
                return;
            }

            while (_isMonitoring)
            {
                // 【修复2】删除了这里原先错误的代码块 (就是引发 CS0103 updates 不存在 和 CS0136 rawData 重复定义的代码)
                // 逻辑已经移入下方的 else 分支中

                if (_sleepTokenSource.IsCancellationRequested)
                {
                    _sleepTokenSource.Dispose();
                    _sleepTokenSource = new CancellationTokenSource();
                }

                try
                {
                    if (RefreshInterval == -1)
                    {
                        // 暂停状态
                        await Task.Delay(Timeout.Infinite, _sleepTokenSource.Token);
                    }
                    else
                    {
                        // 工作状态
                        var startTime = DateTime.Now;

                        var rawData = _cpuService.GetCpuUsage();
                        var updates = ProcessData(rawData);

                        // 更新 UI 并处理加载条状态
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            ApplyUpdates(updates);
                            // 【修复3】数据上屏后，关闭加载条 (逻辑移到这里)
                            if (IsLoading) IsLoading = false;
                        });

                        var elapsed = (DateTime.Now - startTime).TotalMilliseconds;
                        var delay = RefreshInterval - (int)elapsed;
                        if (delay < 100) delay = 100;

                        await Task.Delay(delay, _sleepTokenSource.Token);
                    }
                }
                catch (TaskCanceledException)
                {
                    // 忽略取消异常
                }
                catch (Exception)
                {
                    await Task.Delay(5000);
                }
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

                updates.Add(new CoreUpdateDto
                {
                    VmName = metric.VmName,
                    CoreId = metric.CoreId,
                    Usage = metric.Usage,
                    RenderedGraph = points
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
            foreach (var vm in vmsToRemove) VmList.Remove(vm);
            var grouped = updates.GroupBy(x => x.VmName);
            foreach (var group in grouped)
            {
                var vmName = group.Key;
                var uiVm = VmList.FirstOrDefault(v => v.Name == vmName);
                if (uiVm == null) { uiVm = new UiVmModel { Name = vmName }; VmList.Add(uiVm); }
                int cols = CalculateOptimalColumns(group.Count());
                if (uiVm.Columns != cols) uiVm.Columns = cols;
                foreach (var update in group)
                {
                    var uiCore = uiVm.Cores.FirstOrDefault(c => c.CoreId == update.CoreId);
                    if (uiCore == null) { uiCore = new UiCoreModel { CoreId = update.CoreId }; uiVm.Cores.Add(uiCore); }
                    uiCore.Usage = update.Usage; uiCore.HistoryPoints = update.RenderedGraph;
                }
            }
        }

        private int CalculateOptimalColumns(int count)
        {
            if (count <= 1) return 1;
            double sqrt = Math.Sqrt(count);
            int minCols = (int)Math.Ceiling(sqrt);
            int maxCols = 8;
            if (minCols > maxCols) maxCols = minCols + 2;
            for (int cols = minCols; cols <= maxCols; cols++) if (count % cols == 0) return cols;
            return minCols;
        }

        public void Dispose()
        {
            _isMonitoring = false;
            _sleepTokenSource?.Cancel();
            _sleepTokenSource?.Dispose();
        }
    }
}