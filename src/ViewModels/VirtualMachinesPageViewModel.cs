using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public enum VmDetailViewType { Dashboard, CpuSettings, CpuAffinity }

    public partial class VirtualMachinesPageViewModel : ObservableObject, IDisposable
    {
        private readonly VmQueryService _queryService;
        private readonly VmPowerService _powerService;
        private readonly IVmProcessorService _vmProcessorService;
        private readonly CpuAffinityService _cpuAffinityService;

        private CpuMonitorService _cpuService;
        private CancellationTokenSource _monitoringCts;
        private Task _cpuTask;
        private Task _stateTask;

        private const int MaxHistoryLength = 60;
        private readonly Dictionary<string, LinkedList<double>> _historyCache = new();
        private VmProcessorSettings _originalSettingsCache;
        private DispatcherTimer _uiTimer;

        [ObservableProperty] private bool _isLoading = true;
        [ObservableProperty] private bool _isLoadingSettings;
        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private ObservableCollection<VmInstanceInfo> _vmList = new();
        [ObservableProperty] private VmInstanceInfo _selectedVm;
        [ObservableProperty] private VmDetailViewType _currentViewType = VmDetailViewType.Dashboard;

        public ObservableCollection<int> PossibleVCpuCounts { get; private set; }

        public VirtualMachinesPageViewModel(VmQueryService queryService, VmPowerService powerService)
        {
            _queryService = queryService;
            _powerService = powerService;
            _vmProcessorService = new VmProcessorService();
            _cpuAffinityService = new CpuAffinityService();
            InitPossibleCpuCounts();

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uiTimer.Tick += (s, e) =>
            {
                foreach (var vm in VmList) vm.TickUptime();
            };
            _uiTimer.Start();

            Task.Run(async () => {
                await Task.Delay(300);
                Application.Current.Dispatcher.Invoke(() => LoadVmsCommand.Execute(null));
            });
        }

        private void InitPossibleCpuCounts()
        {
            var options = new HashSet<int>();
            int maxCores = Environment.ProcessorCount;
            int current = 1;
            while (current <= maxCores) { options.Add(current); current *= 2; }
            options.Add(maxCores);
            PossibleVCpuCounts = new ObservableCollection<int>(options.OrderBy(x => x));
        }

        private void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon)
        {
            Application.Current.Dispatcher.Invoke(() => {
                var presenter = Application.Current.MainWindow?.FindName("SnackbarPresenter") as SnackbarPresenter;
                if (presenter != null)
                {
                    var snack = new Snackbar(presenter) { Title = title, Content = message, Appearance = appearance, Icon = new SymbolIcon(icon), Timeout = TimeSpan.FromSeconds(3) };
                    snack.Show();
                }
            });
        }

        [RelayCommand]
        private async Task LoadVmsAsync()
        {
            IsLoading = true;
            VmList.Clear();
            try
            {
                var finalCollection = await Task.Run(async () => {
                    var vms = await _queryService.GetVmListAsync();
                    var sortedVms = vms.OrderBy(v => v.State == "已关机" ? 1 : 0).ThenBy(v => v.Name);
                    var list = new ObservableCollection<VmInstanceInfo>();
                    foreach (var vm in sortedVms)
                    {
                        string notes = vm.Notes ?? "";
                        string osType = notes.Contains("linux", StringComparison.OrdinalIgnoreCase) ? "linux" : "windows";

                        var instance = new VmInstanceInfo(vm.Id, vm.Name, vm.State, osType, vm.CpuCount, vm.MemoryGb, vm.DiskSize, vm.RawUptime)
                        {
                            Notes = vm.Notes,
                            Generation = vm.Generation
                        };

                        instance.ControlCommand = new AsyncRelayCommand<string>(async (action) => {
                            string optimisticText = GetOptimisticText(action);
                            instance.SetTransientState(optimisticText);

                            try
                            {
                                await _powerService.ExecuteControlActionAsync(instance.Name, action);
                                await SyncSingleVmStateAsync(instance);
                            }
                            catch (Exception ex)
                            {
                                Application.Current.Dispatcher.Invoke(() => instance.ClearTransientState());
                                ShowSnackbar("操作失败", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                                await SyncSingleVmStateAsync(instance);
                            }
                        });
                        list.Add(instance);
                    }
                    return list;
                });
                VmList = finalCollection;
                SelectedVm = VmList.FirstOrDefault();
                StartMonitoring();
            }
            catch (Exception ex) { ShowSnackbar("加载失败", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoading = false; }
        }

        private string GetOptimisticText(string action)
        {
            switch (action)
            {
                case "Start": return "正在启动";
                case "Restart": return "正在重启";
                case "Stop": return "正在关闭";
                case "TurnOff": return "已关机";
                case "Save": return "正在保存";
                case "Suspend": return "正在暂停";
                default: return "处理中...";
            }
        }

        private async Task SyncSingleVmStateAsync(VmInstanceInfo vm)
        {
            try
            {
                var allVms = await _queryService.GetVmListAsync();
                var freshData = allVms.FirstOrDefault(x => x.Name == vm.Name);
                if (freshData != null)
                {
                    Application.Current.Dispatcher.Invoke(() => {
                        vm.SyncBackendData(freshData.State, freshData.RawUptime);
                    });
                }
            }
            catch { }
        }

        [RelayCommand]
        private async Task GoToCpuSettings()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.CpuSettings;
            IsLoadingSettings = true;
            try
            {
                var settings = await Task.Run(() => _vmProcessorService.GetVmProcessorAsync(SelectedVm.Name));
                if (settings != null) { SelectedVm.Processor = settings; _originalSettingsCache = settings.Clone(); }
            }
            catch (Exception ex) { ShowSnackbar("加载失败", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            finally { IsLoadingSettings = false; }
        }



        // =========================================================
        // ↓↓↓↓↓↓ 补充的 CPU 亲和性相关属性与方法 ↓↓↓↓↓↓
        // =========================================================

        [ObservableProperty] private ObservableCollection<VmCoreModel> _affinityHostCores;
        [ObservableProperty] private int _affinityColumns = 8;
        [ObservableProperty] private int _affinityRows = 1;
        /// <summary>
        /// 进入 CPU 亲和性设置视图，加载数据
        /// </summary>
        [RelayCommand]
        private async Task GoToCpuAffinity()
        {
            if (SelectedVm == null) return;

            CurrentViewType = VmDetailViewType.CpuAffinity;
            IsLoadingSettings = true;

            try
            {
                int totalCores = Environment.ProcessorCount;

                var currentAffinity = await _cpuAffinityService.GetCpuAffinityAsync(SelectedVm.Id);

                var coresList = new List<VmCoreModel>();
                for (int i = 0; i < totalCores; i++)
                {
                    coresList.Add(new VmCoreModel
                    {
                        CoreId = i,
                        IsSelected = currentAffinity.Contains(i),
                        // 此时 CpuMonitorService 返回的就是 Models.CoreType，直接赋值即可
                        CoreType = CpuMonitorService.GetCoreType(i)
                    });
                }

                AffinityHostCores = new ObservableCollection<VmCoreModel>(coresList);
                AffinityColumns = 8;
                AffinityRows = (int)Math.Ceiling((double)totalCores / AffinityColumns);
            }
            catch (Exception ex)
            {
                ShowSnackbar("加载亲和性失败", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                GoToCpuSettings();
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }
        /// <summary>
        /// 保存 CPU 亲和性设置，并返回上一级
        /// </summary>
        [RelayCommand]
        private async Task SaveAffinity()
        {
            if (SelectedVm == null || AffinityHostCores == null) return;

            IsLoadingSettings = true;
            try
            {
                // 1. 获取所有被选中的核心 ID
                var selectedIndices = AffinityHostCores
                    .Where(c => c.IsSelected)
                    .Select(c => c.CoreId)
                    .ToList();

                // 验证：至少需要选择一个核心
                if (selectedIndices.Count == 0)
                {
                    ShowSnackbar("提示", "必须至少绑定一个 CPU 核心", ControlAppearance.Caution, SymbolRegular.Warning24);
                    return;
                }

                // 2. 调用 Service 保存 (现在 Service 已经有了这个方法)
                bool success = await _cpuAffinityService.SetCpuAffinityAsync(SelectedVm.Id, selectedIndices);

                if (success)
                {
                    ShowSnackbar("成功", "CPU 绑定设置已更新", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                    GoToCpuSettings();
                }
                else
                {
                    ShowSnackbar("保存失败", "无法应用亲和性设置，请检查 HCS 权限", ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar("错误", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }
        [RelayCommand]
        private async Task ApplyChangesAsync()
        {
            if (SelectedVm?.Processor == null) return;
            try
            {
                var result = await Task.Run(() => _vmProcessorService.SetVmProcessorAsync(SelectedVm.Name, SelectedVm.Processor));
                if (result.Success) { _originalSettingsCache = SelectedVm.Processor.Clone(); SelectedVm.CpuCount = SelectedVm.Processor.Count; }
                else { ShowSnackbar("保存失败", result.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
            }
            catch (Exception ex) { ShowSnackbar("异常", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24); }
        }

        [RelayCommand] private void GoBackToDashboard() => CurrentViewType = VmDetailViewType.Dashboard;
        partial void OnSelectedVmChanged(VmInstanceInfo value) { CurrentViewType = VmDetailViewType.Dashboard; _originalSettingsCache = null; }
        partial void OnSearchTextChanged(string value)
        {
            var view = CollectionViewSource.GetDefaultView(VmList);
            if (view != null) { view.Filter = item => (item is VmInstanceInfo vm) && (string.IsNullOrEmpty(value) || vm.Name.Contains(value, StringComparison.OrdinalIgnoreCase)); view.Refresh(); }
        }

        private void StartMonitoring()
        {
            if (_monitoringCts != null) return;
            _monitoringCts = new CancellationTokenSource();
            _cpuTask = Task.Run(() => MonitorCpuLoop(_monitoringCts.Token));
            _stateTask = Task.Run(() => MonitorStateLoop(_monitoringCts.Token));
        }

        private async Task MonitorCpuLoop(CancellationToken token)
        {
            try { _cpuService = new CpuMonitorService(); } catch { return; }
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var rawData = _cpuService.GetCpuUsage();
                    Application.Current.Dispatcher.Invoke(() => ProcessAndApplyCpuUpdates(rawData));
                    await Task.Delay(1000, token);
                }
                catch (TaskCanceledException) { break; }
                catch { await Task.Delay(5000, token); }
            }
            _cpuService?.Dispose();
        }

        private async Task MonitorStateLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var updates = await _queryService.GetVmListAsync();
                    Application.Current.Dispatcher.Invoke(() => {
                        foreach (var update in updates)
                        {
                            var vm = VmList.FirstOrDefault(v => v.Name == update.Name);
                            vm?.SyncBackendData(update.State, update.RawUptime);
                        }
                    });
                    await Task.Delay(3000, token);
                }
                catch (TaskCanceledException) { break; }
                catch { await Task.Delay(3000, token); }
            }
        }

        private void ProcessAndApplyCpuUpdates(List<CpuCoreMetric> rawData)
        {
            var grouped = rawData.GroupBy(x => x.VmName);
            foreach (var group in grouped)
            {
                var vm = VmList.FirstOrDefault(v => v.Name == group.Key);
                if (vm == null) continue;
                vm.AverageUsage = vm.IsRunning ? group.Average(x => x.Usage) : 0;
                UpdateVmCores(vm, group.ToList());
            }
        }

        private void UpdateVmCores(VmInstanceInfo vm, List<CpuCoreMetric> metrics)
        {
            var metricIds = metrics.Select(m => m.CoreId).ToHashSet();
            vm.Cores.Where(c => !metricIds.Contains(c.CoreId)).ToList().ForEach(r => vm.Cores.Remove(r));
            foreach (var metric in metrics)
            {
                var core = vm.Cores.FirstOrDefault(c => c.CoreId == metric.CoreId);
                if (core == null)
                {
                    core = new VmCoreModel { CoreId = metric.CoreId };
                    int idx = 0; while (idx < vm.Cores.Count && vm.Cores[idx].CoreId < metric.CoreId) idx++;
                    vm.Cores.Insert(idx, core);
                }
                core.Usage = metric.Usage; UpdateHistory(vm.Name, core);
            }
            vm.Columns = vm.Cores.Count <= 4 ? 2 : (int)Math.Ceiling(Math.Sqrt(vm.Cores.Count));
            vm.Rows = (vm.Cores.Count > 0) ? (int)Math.Ceiling((double)vm.Cores.Count / vm.Columns) : 1;
        }

        private void UpdateHistory(string vmName, VmCoreModel core)
        {
            string key = $"{vmName}_{core.CoreId}";
            if (!_historyCache.TryGetValue(key, out var history))
            {
                history = new LinkedList<double>(); for (int k = 0; k < MaxHistoryLength; k++) history.AddLast(0);
                _historyCache[key] = history;
            }
            history.AddLast(core.Usage); if (history.Count > MaxHistoryLength) history.RemoveFirst();
            core.HistoryPoints = CalculatePoints(history);
        }

        private PointCollection CalculatePoints(LinkedList<double> history)
        {
            double w = 100.0, h = 100.0, step = w / (MaxHistoryLength - 1);
            var points = new PointCollection(MaxHistoryLength + 2) { new Point(0, h) };
            int i = 0; foreach (var val in history) points.Add(new Point(i++ * step, h - (val * h / 100.0)));
            points.Add(new Point(w, h)); points.Freeze(); return points;
        }

        public void Dispose()
        {
            _monitoringCts?.Cancel();
            _cpuService?.Dispose();
            _uiTimer?.Stop();
        }
    }
}