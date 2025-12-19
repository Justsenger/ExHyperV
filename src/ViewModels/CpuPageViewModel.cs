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
using ExHyperV.Views;
using ExHyperV.Views.Dialogs;
using Wpf.Ui.Controls;
using Wpf.Ui;

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
        public static CpuPageViewModel Instance { get; } = new CpuPageViewModel();

        private CpuMonitorService _cpuService;
        private CancellationTokenSource _monitoringCts;
        private Task _monitoringTask;
        private CancellationTokenSource _sleepTokenSource = new CancellationTokenSource();
        private const int MaxHistoryLength = 25;
        private readonly Dictionary<string, LinkedList<double>> _historyCache = new();
        private readonly IVmProcessorService _vmProcessorService;
        private readonly CpuAffinityService _cpuAffinityService;
        private VMProcessorViewModel _originalProcessorConfig;

        private string? _lockedTopologyVmName;
        private DateTime _lockedTopologyUntil;

        public List<int> PossibleVCpuCounts { get; }

        public ObservableCollection<UiVmModel> VmList { get; } = new ObservableCollection<UiVmModel>();

        [ObservableProperty]
        private UiVmModel? _selectedVm;

        [ObservableProperty]
        private bool _isRootSchedulerActive;

        async partial void OnSelectedVmChanged(UiVmModel? value)
        {
            if (value != null && value.Name != "Host")
            {
                await LoadVmProcessorSettingsAsync(value);
                _originalProcessorConfig = value.Processor.CreateCopy();
                value.Processor.InstantApplyAction = (propertyName) => HandleInstantApply(propertyName);
            }
            else
            {
                _originalProcessorConfig = null;
            }
        }

        [ObservableProperty]
        private int _refreshInterval = 1000;

        [ObservableProperty]
        private bool _isLoading = true;

        private int _selectedSpeedIndex = 1;
        public int SelectedSpeedIndex
        {
            get => _selectedSpeedIndex;
            set { if (SetProperty(ref _selectedSpeedIndex, value)) { UpdateInterval(); WakeUpThread(); } }
        }

        private bool _systemInfoCached = false;
        private HyperVSchedulerType _cachedSchedulerType = HyperVSchedulerType.Unknown;
        private Dictionary<int, int> _cachedCpuSiblingMap = new Dictionary<int, int>();

        [ObservableProperty]
        private bool _isNumaSpanningEnabled = true;

        private bool _suppressNumaChange = false;

        async partial void OnIsNumaSpanningEnabledChanged(bool value)
        {
            if (_suppressNumaChange) return;
            if (SelectedVm == null || SelectedVm.Name != "Host") return;

            var (success, message) = await HyperVNUMAService.SetNumaSpanningEnabledAsync(value);

            if (success)
            {
                ShowSnackbar("设置已保存", "NUMA 设置已更新，重启计算机后生效。", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24, 3);
            }
            else
            {
                _suppressNumaChange = true;
                IsNumaSpanningEnabled = !value;
                _suppressNumaChange = false;
                ShowSnackbar("设置失败", message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }

        public CpuPageViewModel()
        {
            SelectedSpeedIndex = 1;
            _cpuAffinityService = new CpuAffinityService();
            _vmProcessorService = new VmProcessorService();

            var options = new HashSet<int>();
            int maxCores = Environment.ProcessorCount;
            int current = 1;

            while (current <= maxCores)
            {
                options.Add(current);
                current *= 2;
            }
            options.Add(maxCores);

            PossibleVCpuCounts = options.OrderBy(x => x).ToList();
            StartMonitoring();
        }

        private int _selectedSchedulerIndex = -1;
        public int SelectedSchedulerIndex
        {
            get => _selectedSchedulerIndex;
            set
            {
                if (value == _selectedSchedulerIndex || !_systemInfoCached || value < 0)
                {
                    SetProperty(ref _selectedSchedulerIndex, value);
                    return;
                }

                SetProperty(ref _selectedSchedulerIndex, value);
                IsRootSchedulerActive = (value == 2);

                var newType = value switch
                {
                    0 => HyperVSchedulerType.Classic,
                    1 => HyperVSchedulerType.Core,
                    2 => HyperVSchedulerType.Root,
                    _ => HyperVSchedulerType.Unknown
                };

                if (newType != HyperVSchedulerType.Unknown)
                {
                    _ = Task.Run(async () =>
                    {
                        if (await HyperVSchedulerService.SetSchedulerTypeAsync(newType))
                        {
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                ShowSnackbar("操作成功", "调度器类型已更改，需要重启系统才能生效。", ControlAppearance.Success, SymbolRegular.CheckmarkCircle24);
                            });
                        }
                    });
                }
            }
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

        private async void HandleInstantApply(string propertyName)
        {
            if (SelectedVm == null || SelectedVm.Name == "Host" || IsLoading) return;

            try
            {
                Debug.WriteLine($"即时应用: {propertyName} 属性已更改。");
                var (success, message) = await _vmProcessorService.SetVmProcessorAsync(SelectedVm.Name, SelectedVm.Processor);

                if (success)
                {
                    _originalProcessorConfig = SelectedVm.Processor.CreateCopy();
                }
                else
                {
                    ShowSnackbar("设置失败", message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    if (_originalProcessorConfig != null)
                    {
                        SelectedVm.Processor.Restore(_originalProcessorConfig);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar("错误", $"发生未预期的错误: {ex.Message}", ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                if (_originalProcessorConfig != null)
                {
                    SelectedVm.Processor.Restore(_originalProcessorConfig);
                }
            }
        }

        private void UpdateInterval()
        {
            RefreshInterval = SelectedSpeedIndex switch
            {
                0 => 500,
                1 => 1000,
                2 => 2000,
                3 => -1,
                _ => 1000
            };
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
            _cpuService?.Dispose();
            _cpuService = null;
        }

        private List<CoreUpdateDto> ProcessData(List<CpuCoreMetric> rawData)
        {
            var updates = new List<CoreUpdateDto>();
            foreach (var metric in rawData)
            {
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
                    var points = CalculatePoints(history);
                    points.Freeze();
                    updates.Add(new CoreUpdateDto { VmName = metric.VmName, CoreId = metric.CoreId, Usage = metric.Usage, RenderedGraph = points, IsRunning = true });
                }
                else
                {
                    updates.Add(new CoreUpdateDto { VmName = metric.VmName, CoreId = metric.CoreId, IsRunning = false });
                }
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
                double y = h - (val * h / 100.0);
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
            foreach (var vm in vmsToRemove) VmList.Remove(vm);

            var groupedByVm = updates.GroupBy(x => x.VmName);
            foreach (var group in groupedByVm)
            {
                var vmName = group.Key;
                var uiVm = VmList.FirstOrDefault(v => v.Name == vmName);
                if (uiVm == null)
                {
                    uiVm = new UiVmModel { Name = vmName };
                    if (vmName != "Host")
                    {
                        uiVm.Processor.Count = group.Count();
                        uiVm.Processor.RelativeWeight = 100;
                        uiVm.Processor.Reserve = 0;
                        uiVm.Processor.Maximum = 100;
                        uiVm.Processor.SmtMode = SmtMode.Inherit;
                        uiVm.Processor.EnableHostResourceProtection = false;
                    }
                    VmList.Add(uiVm);
                }

                uiVm.IsRunning = group.Any(u => u.IsRunning);
                uiVm.AverageUsage = uiVm.IsRunning ? group.Average(u => u.Usage) : 0;

                bool isTopologyLocked = (vmName == _lockedTopologyVmName && DateTime.Now < _lockedTopologyUntil);

                if (!isTopologyLocked)
                {
                    var updatedCoreIds = group.Select(u => u.CoreId).ToHashSet();
                    var coresToRemove = uiVm.Cores.Where(c => !updatedCoreIds.Contains(c.CoreId)).ToList();
                    foreach (var core in coresToRemove) uiVm.Cores.Remove(core);
                }

                foreach (var update in group)
                {
                    var uiCore = uiVm.Cores.FirstOrDefault(c => c.CoreId == update.CoreId);
                    if (uiCore == null)
                    {
                        if (isTopologyLocked) continue;

                        var serviceCoreType = vmName.Equals("Host", StringComparison.OrdinalIgnoreCase)
                            ? CpuMonitorService.GetCoreType(update.CoreId)
                            : Services.CoreType.Unknown;

                        Models.CoreType modelCoreType = serviceCoreType switch
                        {
                            Services.CoreType.Performance => Models.CoreType.Performance,
                            Services.CoreType.Efficient => Models.CoreType.Efficient,
                            _ => Models.CoreType.Unknown
                        };

                        uiCore = new UiCoreModel { CoreId = update.CoreId, CoreType = modelCoreType };
                        uiVm.Cores.Add(uiCore);
                    }

                    if (uiCore != null)
                    {
                        uiCore.Usage = update.Usage;
                        uiCore.HistoryPoints = update.RenderedGraph;
                    }
                }

                if (!isTopologyLocked)
                {
                    var sortedCores = uiVm.Cores.OrderBy(c => c.CoreId).ToList();
                    for (int i = 0; i < sortedCores.Count; i++)
                    {
                        var desiredCore = sortedCores[i];
                        int currentIndex = uiVm.Cores.IndexOf(desiredCore);
                        if (currentIndex != i)
                        {
                            uiVm.Cores.Move(currentIndex, i);
                        }
                    }

                    uiVm.Columns = CalculateOptimalColumns(uiVm.Cores.Count);
                    uiVm.Rows = (uiVm.Cores.Count > 0) ? (int)Math.Ceiling((double)uiVm.Cores.Count / uiVm.Columns) : 0;
                }
            }

            var hostVm = VmList.FirstOrDefault(vm => vm.Name.Equals("Host", StringComparison.OrdinalIgnoreCase));
            if (!_systemInfoCached && hostVm != null && hostVm.Cores.Any())
            {
                _cachedSchedulerType = HyperVSchedulerService.GetSchedulerType();
                _cachedCpuSiblingMap = CpuTopologyService.GetCpuSiblingMap();

                Task.Run(async () =>
                {
                    var numaEnabled = await HyperVNUMAService.GetNumaSpanningEnabledAsync();
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        _suppressNumaChange = true;
                        IsNumaSpanningEnabled = numaEnabled;
                        _suppressNumaChange = false;
                    });
                });

                _systemInfoCached = true;

                var initialIndex = _cachedSchedulerType switch
                {
                    HyperVSchedulerType.Classic => 0,
                    HyperVSchedulerType.Core => 1,
                    HyperVSchedulerType.Root => 2,
                    _ => -1
                };
                _selectedSchedulerIndex = initialIndex;
                IsRootSchedulerActive = (initialIndex == 2);
                OnPropertyChanged(nameof(SelectedSchedulerIndex));
            }

            if (SelectedVm == null)
            {
                SelectedVm = VmList.FirstOrDefault(vm => vm.Name == "Host") ?? VmList.FirstOrDefault();
            }

            var sortedVms = VmList
                .OrderBy(vm => vm.Name != "Host")
                .ThenBy(vm => !vm.IsRunning)
                .ThenBy(vm => vm.Name)
                .ToList();
            for (int i = 0; i < sortedVms.Count; i++)
            {
                var desiredVm = sortedVms[i];
                if (VmList.IndexOf(desiredVm) != i) VmList.Move(VmList.IndexOf(desiredVm), i);
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

        private async Task LoadVmProcessorSettingsAsync(UiVmModel vm)
        {
            try
            {
                var processorSettings = await _vmProcessorService.GetVmProcessorAsync(vm.Name);
                if (processorSettings != null)
                {
                    vm.Processor.Count = processorSettings.Count;
                    vm.Processor.Reserve = processorSettings.Reserve;
                    vm.Processor.Maximum = processorSettings.Maximum;
                    vm.Processor.RelativeWeight = processorSettings.RelativeWeight;
                    vm.Processor.ExposeVirtualizationExtensions = processorSettings.ExposeVirtualizationExtensions;
                    vm.Processor.EnableHostResourceProtection = processorSettings.EnableHostResourceProtection;
                    vm.Processor.CompatibilityForMigrationEnabled = processorSettings.CompatibilityForMigrationEnabled;
                    vm.Processor.CompatibilityForOlderOperatingSystemsEnabled = processorSettings.CompatibilityForOlderOperatingSystemsEnabled;
                    vm.Processor.SmtMode = processorSettings.SmtMode;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"加载虚拟机 {vm.Name} 的处理器配置失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task OpenVmCpuAffinityAsync()
        {
            if (SelectedVm == null || SelectedVm.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)) return;
            var hostVm = VmList.FirstOrDefault(vm => vm.Name.Equals("Host", StringComparison.OrdinalIgnoreCase));
            if (hostVm == null || hostVm.Cores.Count == 0) return;

            if (_cachedSchedulerType == HyperVSchedulerType.Root)
            {
                if (!SelectedVm.IsRunning)
                {
                    ShowSnackbar("操作不可用", "根调度器模式下，虚拟机未运行时，无法设置 CPU 绑定。", ControlAppearance.Caution, SymbolRegular.Warning24);
                    return;
                }

                IsLoading = true;
                try
                {
                    var (vmId, currentAffinity) = await Task.Run(() =>
                    {
                        var id = GetVmGuidByName(SelectedVm.Name);
                        var affinity = (id != Guid.Empty) ? ProcessAffinityManager.GetVmProcessAffinity(id) : new List<int>();
                        return (id, affinity);
                    });

                    if (vmId == Guid.Empty)
                    {
                        ShowSnackbar("错误", "无法找到虚拟机进程，请确保虚拟机正在运行。", ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                        return;
                    }

                    var dialogViewModel = new CpuAffinityDialogViewModel(SelectedVm.Name, SelectedVm.Cores.Count, hostVm.Cores, _cachedSchedulerType, _cachedCpuSiblingMap);
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
                        var selectedCoreIds = dialogViewModel.Cores.Where(c => c.IsSelected).Select(c => c.CoreId).ToList();
                        await Task.Run(() => ProcessAffinityManager.SetVmProcessAffinity(vmId, selectedCoreIds));
                    }
                }
                finally
                {
                    IsLoading = false;
                }
            }
            else
            {
                var vmId = GetVmGuidByName(SelectedVm.Name);
                if (vmId == Guid.Empty) return;

                var dialogViewModel = new CpuAffinityDialogViewModel(SelectedVm.Name, SelectedVm.Cores.Count, hostVm.Cores, _cachedSchedulerType, _cachedCpuSiblingMap);

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
                    var selectedCoreIds = dialogViewModel.Cores.Where(c => c.IsSelected).Select(c => c.CoreId).ToList();
                    await Task.Run(() => HcsManager.SetVmCpuGroup(vmId, Guid.Empty));
                    if (selectedCoreIds.Any())
                    {
                        Guid cpuGroupId = await _cpuAffinityService.FindOrCreateCpuGroupAsync(selectedCoreIds);
                        await Task.Run(() => HcsManager.SetVmCpuGroup(vmId, cpuGroupId));
                    }
                }
            }
        }


        [RelayCommand]
        private async Task ApplyChangesAsync()
        {
            if (SelectedVm == null || SelectedVm.Name == "Host" || IsLoading) return;

            var (success, message) = await _vmProcessorService.SetVmProcessorAsync(SelectedVm.Name, SelectedVm.Processor);

            if (success)
            {
                _originalProcessorConfig = SelectedVm.Processor.CreateCopy();
                UpdateCoresImmediately();
                _lockedTopologyVmName = SelectedVm.Name;
                _lockedTopologyUntil = DateTime.Now.AddSeconds(5);
                ShowSnackbar("操作成功", message, ControlAppearance.Success, SymbolRegular.CheckmarkCircle24, 1.5);
            }
            else
            {
                ShowSnackbar("操作失败", message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }

        private void UpdateCoresImmediately()
        {
            if (SelectedVm == null) return;

            int targetCount = (int)SelectedVm.Processor.Count;
            var currentCores = SelectedVm.Cores;

            while (currentCores.Count > targetCount)
            {
                currentCores.RemoveAt(currentCores.Count - 1);
            }

            while (currentCores.Count < targetCount)
            {
                int newId = currentCores.Count;
                currentCores.Add(new UiCoreModel
                {
                    CoreId = newId,
                    CoreType = Models.CoreType.Unknown,
                    Usage = 0,
                    HistoryPoints = null
                });
            }

            SelectedVm.Columns = CalculateOptimalColumns(currentCores.Count);
            SelectedVm.Rows = (currentCores.Count > 0) ? (int)Math.Ceiling((double)currentCores.Count / SelectedVm.Columns) : 0;
        }

        [RelayCommand]
        private void OpenNumaSettings()
        {
        }

        private void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon, double seconds = 2)
        {
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                var snackbar = new Snackbar(mainWindow.SnackbarPresenter)
                {
                    Title = title,
                    Content = message,
                    Appearance = appearance,
                    Icon = new SymbolIcon(icon) { FontSize = 32 },
                    Timeout = TimeSpan.FromSeconds(seconds),
                    Padding = new Thickness(12, 4, 12, 4)
                };

                snackbar.Show();
            }
        }
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
            _cpuService?.Dispose();
        }
    }
}