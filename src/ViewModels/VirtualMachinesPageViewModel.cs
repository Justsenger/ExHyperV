using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Interaction;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public enum VmDetailViewType
    {
        Dashboard, CpuSettings, CpuAffinity, MemorySettings, StorageSettings, AddStorage,
        GpuSettings,
        AddGpuSelect,
        AddGpuProgress, NetworkSettings, BootSettings, SpacetimeSettings
    }
    public partial class VirtualMachinesPageViewModel : ObservableObject, IDisposable
    {
        // ===== 私有服务字段与依赖注入 =====
        private readonly VmQueryService _queryService;
        private readonly VmGpuService _vmGpuService;


        // ===== 监控与后台任务字段 =====
        private CpuMonitorService _cpuService;
        private CancellationTokenSource _monitoringCts;
        private Task _cpuTask;
        private Task _stateTask;
        private DispatcherTimer _uiTimer;
        // 防止监控循环对同一网卡重复并发起 IP/ARP 查询（无界堆积）
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _ipLookupsInFlight = new();
        // PktMon 被动嗅探 vSwitch 上的 ARP，补无集成服务 VM（如国产 Linux）的 IP；进程级单例,与网络页/VmIpService 共用
        private readonly ArpSnoopService _ipSnoop = ArpSnoopService.Instance;

        private readonly Dictionary<Guid, (string NewName, DateTime Expiry)> _renameLockouts = new();


        // ===== 缓存与状态字段 =====
        private const int MaxHistoryLength = 60;
        private readonly Dictionary<string, LinkedList<double>> _historyCache = new();
        private VmProcessorSettings _originalSettingsCache;
        private VmMemorySettings _originalMemorySettingsCache;
        private bool _isInternalUpdating = false;
        private bool _isDiskPathManual = false; // 记录用户是否手动选择过磁盘路径


        // ===== 视图模型属性 - 页面状态 =====
        [ObservableProperty] private bool _isLoading = true;
        [ObservableProperty] private bool _isLoadingSettings;
        [ObservableProperty] private VmDetailViewType _currentViewType = VmDetailViewType.Dashboard;
        [ObservableProperty] private string _searchText = string.Empty;


        // ===== 视图模型属性 - 虚拟机列表与选择 =====
        [ObservableProperty] private ObservableCollection<VmInstanceViewModel> _vmList = new();
        [ObservableProperty] private VmInstanceViewModel _selectedVm;
        [ObservableProperty] private BitmapSource? _thumbnail;


        // ===== 视图模型属性 - GPU 管理 =====
        [ObservableProperty] private ObservableCollection<GpuInfo> _hostGpus = new();
        [ObservableProperty][NotifyCanExecuteChangedFor(nameof(ConfirmAddGpuCommand))] private GpuInfo _selectedHostGpu;
        [ObservableProperty] private bool _autoInstallDrivers = true;
        [ObservableProperty] private ObservableCollection<TaskItem> _gpuTasks = new();
        [ObservableProperty] private bool _showPartitionSelector = false;
        [ObservableProperty] private ObservableCollection<PartitionInfo> _detectedPartitions = new();
        [ObservableProperty] private PartitionInfo? _selectedPartition;
        [ObservableProperty] private bool _showSshForm = false;
        [ObservableProperty] private string? _currentProcessingGpuAdapterId;
        private bool _needConfig = false;

        // Linux SSH 凭据
        [ObservableProperty] private string _sshHost = "";
        [ObservableProperty] private string _sshUsername = "root";
        [ObservableProperty] private string _sshPassword = "";
        [ObservableProperty] private int _sshPort = 22;
        [ObservableProperty] private bool _installGraphics = true;
        [ObservableProperty] private bool _useSshProxy = false;
        [ObservableProperty] private string _sshProxyHost = "";
        [ObservableProperty] private string _sshProxyPort = "";
        private CancellationTokenSource? _gpuDeploymentCts;

        // 日志与控制台
        [ObservableProperty] private string _gpuDeploymentLog = string.Empty;
        [ObservableProperty] private bool _showLogConsole = false;


        // ===== 构造函数与资源释放 =====

        // Linux 部署字段

        [ObservableProperty] private ObservableCollection<LinuxScriptItem> _availableLinuxScripts = new();
        [ObservableProperty] private LinuxScriptItem _selectedLinuxScript;

        public VirtualMachinesPageViewModel(VmQueryService queryService)
        {
            _queryService = queryService;
            _vmGpuService = new VmGpuService(_queryService);

            InitPossibleCpuCounts();

            for (int i = 0; i < 64; i++)
            {
                AvailableLocations.Add(i);
            }

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uiTimer.Tick += (s, e) => { foreach (var vm in VmList) vm.TickUptime(); };
            _uiTimer.Start();

            Task.Run(async () => {
                await Task.Delay(300);
                Application.Current.Dispatcher.Invoke(() => LoadVmsCommand.Execute(null));
            });
            Task.Run(() => _ipSnoop.Start()); // 后台启动 PktMon 嗅探，不阻塞构造
        }

        public void Dispose()
        {
            _monitoringCts?.Cancel();
            _cpuService?.Dispose();
            _uiTimer?.Stop();
            // 不在此 Dispose 嗅探单例(全进程共用,退出时由其 ProcessExit 钩子清理)
        }


        // ===== 导航与页面状态控制 =====

        // 搜索框文本变化时的过滤逻辑
        partial void OnSearchTextChanged(string value)
        {
            var view = CollectionViewSource.GetDefaultView(VmList);
            if (view != null)
            {
                view.Filter = item => (item is VmInstanceViewModel vm) && (string.IsNullOrEmpty(value) || vm.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
                view.Refresh();
            }
        }

        // 返回仪表盘
        [RelayCommand]
        private void GoBackToDashboard() => CurrentViewType = VmDetailViewType.Dashboard;

        // 根据当前视图层级返回上一级
        [RelayCommand]
        private void GoBack()
        {
            switch (CurrentViewType)
            {
                case VmDetailViewType.AddStorage:
                    CurrentViewType = VmDetailViewType.StorageSettings;
                    break;
                case VmDetailViewType.BootSettings:
                case VmDetailViewType.GpuSettings:
                case VmDetailViewType.CpuSettings:
                case VmDetailViewType.CpuAffinity:
                case VmDetailViewType.MemorySettings:
                case VmDetailViewType.StorageSettings:
                case VmDetailViewType.NetworkSettings:
                case VmDetailViewType.SpacetimeSettings:
                    CurrentViewType = VmDetailViewType.Dashboard;
                    break;
                default:
                    CurrentViewType = VmDetailViewType.Dashboard;
                    break;
            }
        }


        // ===== 虚拟机列表管理与核心操作 =====

        [RelayCommand]
        private async Task OpenVmFolderAsync(VmInstanceViewModel vm)
        {
            if (vm == null) return;
            try
            {
                string? path = await _queryService.GetVmConfigRootAsync(vm.Name);

                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    System.Diagnostics.Process.Start("explorer.exe", path);
                }
                else
                {
                    ShowSnackbar(Properties.Resources.VmPage_SgxAccessDenied, Properties.Resources.VmPage_SgxReadOnly, ControlAppearance.Caution, SymbolRegular.Warning24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.VmPage_SgxAccessDenied, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }

        [RelayCommand]
        private async Task DeleteVmAsync(VmInstanceViewModel vm)
        {
            if (vm == null) return;
            IsLoading = true;

            try
            {
                var result = await VmDeleteService.DeleteVmAsync(vm.Name);
                if (result.Success)
                {
                    VmList.Remove(vm);
                    if (SelectedVm == vm) SelectedVm = VmList.FirstOrDefault();
                }
                else
                {
                    ShowSnackbar(Properties.Resources.VmPage_DeleteFail, FriendlyError.CleanLines(result.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.VmPage_DeleteFail, FriendlyError.CleanLines(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally { IsLoading = false; }
        }

        [RelayCommand]
        private async Task PurgeVmAsync(VmInstanceViewModel vm)
        {
            if (vm == null) return;

            // 二次确认弹窗
            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = Properties.Resources.VmPage_MsgOptimizeComplete,
                Content = string.Format(Properties.Resources.VmPage_MsgDiskReclaimOk, vm.Name),
                PrimaryButtonText = Properties.Resources.VmPage_ErrOptimizeFailed,
                CloseButtonText = Properties.Resources.VmPage_ErrSystemException,
            };

            var result = await dialog.ShowDialogAsync();
            if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

            IsLoading = true;
            try
            {
                var purge = await VmDeleteService.PurgeVmAsync(vm.Name, vm.Id);
                if (purge.Success)
                {
                    VmList.Remove(vm);
                    if (SelectedVm == vm) SelectedVm = VmList.FirstOrDefault();
                    ShowSnackbar(Properties.Resources.VmPage_LogStorageAddAction, string.Format(Properties.Resources.VmPage_LogStorageAutoAssign, vm.Name), ControlAppearance.Success, SymbolRegular.Delete24);
                }
                else
                {
                    ShowSnackbar(Properties.Resources.VmPage_LogUiSaveTriggered, FriendlyError.CleanLines(purge.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.VmPage_LogUiSaveTriggered, FriendlyError.CleanLines(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally { IsLoading = false; }
        }
        // 当选中的虚拟机发生变化时重置视图
        partial void OnSelectedVmChanged(VmInstanceViewModel value)
        {
            if (value != null)
            {
                IsCreatingVm = false;
                _ = RefreshBootOrderForSelectedVmAsync(value);
            }
            CurrentViewType = VmDetailViewType.Dashboard;
            _originalSettingsCache = null;
            _originalMemorySettingsCache = null;
            HostDisks.Clear();
        }


        // 把 Service 返回的 VmInstance(Model) 包成 live VM，并接上电源控制命令。
        // VmInstanceViewModel 构造函数已经从 Model 拷贝所有标量/集合（pass-through），无需重复 init。
        private VmInstanceViewModel CreateVmInstance(VmInstance snapshot)
        {
            var instance = new VmInstanceViewModel(snapshot);

            // 绑定电源控制命令 (必须绑定，否则新发现的 VM 按钮无效)
            instance.ControlCommand = new AsyncRelayCommand<string>(async (action) => {
                instance.SetTransientState(GetOptimisticText(action));
                try
                {
                    await VmPowerService.ExecuteControlActionAsync(instance.Name, action);
                    await SyncSingleVmStateAsync(instance);
                    if (action == "Start" || action == "Restart")
                    {
                        TryApplyAffinityForRootScheduler(instance);
                    }
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() => instance.ClearTransientState());
                    var realEx = ex;
                    while (realEx.InnerException != null) { realEx = realEx.InnerException; }
                    ShowSnackbar(Properties.Resources.Error_Common_OpFail, FriendlyError.CleanLines(realEx.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            });

            return instance;
        }

        public List<string> AvailableOsTypes => OsImages.SupportedTypes;

        // 加载虚拟机列表
        [RelayCommand]
        private async Task LoadVmsAsync()
        {
            if (IsLoading && VmList.Count > 0) return;
            IsLoading = true;
            try
            {
                var finalCollection = await Task.Run(async () => {
                    var vms = await _queryService.GetVmListAsync();
                    var list = new ObservableCollection<VmInstanceViewModel>();
                    foreach (var snapshot in vms)
                    {
                        if (string.IsNullOrWhiteSpace(snapshot.Name)) continue;
                        list.Add(CreateVmInstance(snapshot));
                    }
                    return list;
                });

                VmList = finalCollection;

                foreach (var vm in VmList.Where(v => v.IsRunning))
                {
                    TryApplyAffinityForRootScheduler(vm);
                }

                // 配置排序规则
                var view = CollectionViewSource.GetDefaultView(VmList);
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(nameof(VmInstanceViewModel.IsRunning), ListSortDirection.Descending));
                view.SortDescriptions.Add(new SortDescription(nameof(VmInstanceViewModel.Name), ListSortDirection.Ascending));

                // 开启实时排序
                if (view is System.ComponentModel.ICollectionViewLiveShaping liveView)
                {
                    liveView.IsLiveSorting = true;
                    liveView.LiveSortingProperties.Add(nameof(VmInstanceViewModel.IsRunning));
                }

                if (SelectedVm == null || !VmList.Any(x => x.Name == SelectedVm.Name))
                {
                    SelectedVm = VmList.FirstOrDefault();
                }

                StartMonitoring();
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Common_LoadFail, FriendlyError.CleanLines(ex.Message), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoading = false;
            }
            if (VmList.Count == 0)
            {
                SelectedVm = null;
            }
        }






        // 修改原本启动外部 vmconnect.exe 的逻辑
        [RelayCommand]
        private void OpenNativeConnect()
        {
            if (SelectedVm == null) return;

            try
            {
                // 打开当前选中虚拟机的沉浸式控制台窗口（现走新的 RdpClientHost）
                Navigation.OpenConsoleWindow(SelectedVm.Id.ToString(), SelectedVm.Name);

                // 4. (可选) 给个小反馈
                Debug.WriteLine(string.Format(Properties.Resources.VmPage_ErrOpenFailed, SelectedVm.Name));
            }
            catch (Exception ex)
            {
                ShowSnackbar(
                    Properties.Resources.Error_Vm_StartFail,
                    string.Format(Properties.Resources.VmPage_ErrConfigDirNotFound, ex.Message),
                    ControlAppearance.Danger,
                    SymbolRegular.ErrorCircle24);
            }
        }

        // 修改操作系统标签
        [RelayCommand]
        private async Task ChangeOsTypeAsync(string newType)
        {
            if (SelectedVm == null || SelectedVm.OsType == newType) return;
            string oldOsType = SelectedVm.OsType;
            string oldNotes = SelectedVm.Notes;
            SelectedVm.OsType = newType;
            SelectedVm.Notes = NotesTag.Update(SelectedVm.Notes, "OSType", newType);
            bool success = await _queryService.SetVmOsTypeAsync(SelectedVm.Name, newType);
            if (!success)
            {
                SelectedVm.OsType = oldOsType;
                SelectedVm.Notes = oldNotes;
                ShowSnackbar(Properties.Resources.Error_Common_ModFailShort, Properties.Resources.Error_Common_NoPermission, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }


        // ===== 后台监控循环与状态更新 =====

        // 启动后台监控线程
        private void StartMonitoring()
        {
            if (_monitoringCts != null) return;
            _monitoringCts = new CancellationTokenSource();
            _cpuTask = Task.Run(() => MonitorCpuLoop(_monitoringCts.Token));
            _stateTask = Task.Run(() => MonitorStateLoop(_monitoringCts.Token));
            // 新增：独立的缩略图任务，避免阻塞状态同步
            _ = Task.Run(() => MonitorThumbnailLoop(_monitoringCts.Token));
        }

        // CPU 使用率监控循环
        private async Task MonitorCpuLoop(CancellationToken token)
        {
            try { _cpuService = new CpuMonitorService(); } catch { return; }
            while (!token.IsCancellationRequested)
            {
                try { var rawData = _cpuService.GetCpuUsage(); Application.Current.Dispatcher.Invoke(() => ProcessAndApplyCpuUpdates(rawData)); await Task.Delay(1000, token); }
                catch (TaskCanceledException) { break; }
                catch { await Task.Delay(5000, token); }
            }
            _cpuService?.Dispose();
        }

        // 虚拟机状态与性能数据同步循环
        private async Task MonitorStateLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    // 1. 获取后端最新原始数据
                    var updates = await _queryService.GetVmListAsync();
                    var memoryMap = await _queryService.GetVmRuntimeMemoryDataAsync();

                    await _queryService.UpdateDiskPerformanceAsync(VmList.Select(v => v.Model));
                    var gpuUsageMap = await _queryService.GetGpuPerformanceAsync(VmList.Select(v => v.Model));

                    Application.Current.Dispatcher.Invoke(() => {
                        bool needsResort = false;

                        // --- A. 监测删除：移除本地列表中 已经不存在于后端 的 VM ---
                        var updateIds = updates.Select(u => u.Id).ToHashSet();
                        for (int i = VmList.Count - 1; i >= 0; i--)
                        {
                            if (!updateIds.Contains(VmList[i].Id))
                            {
                                if (SelectedVm == VmList[i]) SelectedVm = null;
                                VmList.RemoveAt(i);
                                needsResort = true;
                            }
                        }

                        // --- B. 监测新建：添加后端存在但 本地列表没有 的 VM ---
                        var currentIds = VmList.Select(v => v.Id).ToHashSet();
                        foreach (var update in updates)
                        {
                            if (!currentIds.Contains(update.Id))
                            {
                                var newVm = CreateVmInstance(update);
                                VmList.Add(newVm);
                                needsResort = true;
                            }
                        }

                        // --- C. 更新属性：原有逻辑 ---
                        foreach (var update in updates)
                        {
                            // 使用 Id 匹配比 Name 更可靠，因为 VM 可能会被改名
                            var vm = VmList.FirstOrDefault(v => v.Id == update.Id);
                            if (vm != null)
                            {
                                // --- [新增] 重命名锁定保护拦截逻辑 ---
                                bool skipNameUpdate = false;
                                lock (_renameLockouts)
                                {
                                    if (_renameLockouts.TryGetValue(vm.Id, out var lockout))
                                    {
                                        // 检查：1. 后端数据是否已经同步为新名字？ 2. 是否已经超过了 5 秒保护期？
                                        if (update.Name.Equals(lockout.NewName, StringComparison.OrdinalIgnoreCase) ||
                                            DateTime.Now > lockout.Expiry)
                                        {
                                            // 满足上述任一条件，解除锁定
                                            _renameLockouts.Remove(vm.Id);
                                        }
                                        else
                                        {
                                            // 后端传回的依然是旧名字且在保护期内，拦截本次更新
                                            skipNameUpdate = true;
                                        }
                                    }
                                }

                                // 把 fresh model 数据合入 vm（标量/transient state/网络适配器/磁盘/GPU 摘要）
                                bool wasRunning = vm.IsRunning;
                                bool skipNetworkAdapters = CurrentViewType == VmDetailViewType.NetworkSettings || IsLoadingSettings;
                                vm.Apply(update, skipNameUpdate, skipNetworkAdapters);
                                if (wasRunning != vm.IsRunning) needsResort = true;

                                // PageVM-only side effect 1：运行时收集 IP。
                                // 集成服务报的列表(含 IPv4+IPv6/多地址)最权威，绝不覆盖；嗅探/查询只补"没 IP 的空网卡"(如国产环境)。
                                if (vm.IsRunning)
                                {
                                    foreach (var adapter in vm.NetworkAdapters)
                                    {
                                        if (string.IsNullOrEmpty(adapter.MacAddress)) continue;
                                        if (adapter.IpAddresses != null && adapter.IpAddresses.Count > 0) continue; // 有 IP(集成服务,含 IPv6)不动

                                        // 空网卡:先查嗅探缓存(即时)；没有再异步回退集成/邻居查询(同一网卡已有在飞 Lookup 就跳过)
                                        if (_ipSnoop.TryGetIp(adapter.MacAddress, out var snoopIp))
                                        {
                                            adapter.IpAddresses = new List<string> { snoopIp };
                                            continue;
                                        }
                                        string lookupKey = $"{vm.Id}|{adapter.MacAddress}";
                                        if (!_ipLookupsInFlight.TryAdd(lookupKey, 0)) continue;
                                        _ = Task.Run(async () => {
                                            try
                                            {
                                                string arpIp = await VmIpService.Lookup(vm.Name, adapter.MacAddress);
                                                if (!string.IsNullOrEmpty(arpIp))
                                                    Application.Current.Dispatcher.Invoke(() => {
                                                        if (adapter.IpAddresses == null || adapter.IpAddresses.Count == 0)
                                                            adapter.IpAddresses = new List<string> { arpIp };
                                                        if (vm.IpAddress == "---" || string.IsNullOrWhiteSpace(vm.IpAddress)) vm.IpAddress = arpIp;
                                                    });
                                            }
                                            catch { }
                                            finally { _ipLookupsInFlight.TryRemove(lookupKey, out _); }
                                        });
                                    }

                                    // 主显示 IP = 网卡列表里第一个 IPv4(集成服务报的或嗅探补的都在里面)
                                    var primary = vm.NetworkAdapters.SelectMany(a => a.IpAddresses ?? new List<string>())
                                                    .FirstOrDefault(ip => !string.IsNullOrEmpty(ip) && !ip.Contains(":"));
                                    if (!string.IsNullOrEmpty(primary)) vm.IpAddress = primary;
                                }
                                // Apply 已处理 !IsRunning 时 vm.IpAddress = "---"

                                // PageVM-only side effect 2：从 memoryMap 应用动态内存数据
                                if (memoryMap.TryGetValue(vm.Id.ToString(), out var memData))
                                    vm.UpdateMemoryStatus(memData.AssignedMb, memData.AvailablePercent);
                                else if (memoryMap.TryGetValue(vm.Id.ToString().ToUpper(), out var memDataUpper))
                                    vm.UpdateMemoryStatus(memDataUpper.AssignedMb, memDataUpper.AvailablePercent);
                                else
                                    vm.UpdateMemoryStatus(0, 0);
                            }
                        }
                        foreach (var vm in VmList)
                        {
                            if (gpuUsageMap.TryGetValue(vm.Id, out var gpuData))
                                vm.UpdateGpuStats(gpuData);
                            else
                                vm.UpdateGpuStats(new VmQueryService.GpuUsageData());
                        }

                        if (needsResort)
                        {
                            CollectionViewSource.GetDefaultView(VmList)?.Refresh();
                        }
                    });

                    if (SelectedVm != null && SelectedVm.IsRunning)
                    {
                        await VmStorageService.RefreshVirtualDiskSizesAsync(SelectedVm.Model);
                    }

                    await Task.Delay(2000, token);
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MonitorLoop Error] {ex.Message}");
                    await Task.Delay(3000, token);
                }
            }
        }        // 同步单个虚拟机的最新状态
        private async Task SyncSingleVmStateAsync(VmInstanceViewModel vm)
        {
            try
            {
                var allVms = await _queryService.GetVmListAsync();
                var freshData = allVms.FirstOrDefault(x => x.Name == vm.Name);
                if (freshData != null)
                {
                    Application.Current.Dispatcher.Invoke(() => vm.Apply(freshData));
                }
            }
            catch { }
        }

        // 处理 CPU 更新数据
        private void ProcessAndApplyCpuUpdates(List<VmCoreMetric> rawData) { var grouped = rawData.GroupBy(x => x.VmName); foreach (var group in grouped) { var vm = VmList.FirstOrDefault(v => v.Name == group.Key); if (vm == null) continue; vm.AverageUsage = vm.IsRunning ? group.Average(x => x.Usage) : 0; UpdateVmCores(vm, group.ToList()); } }
        private void UpdateVmCores(VmInstanceViewModel vm, List<VmCoreMetric> metrics) { var metricIds = metrics.Select(m => m.CoreId).ToHashSet(); vm.Cores.Where(c => !metricIds.Contains(c.CoreId)).ToList().ForEach(r => vm.Cores.Remove(r)); foreach (var metric in metrics) { var core = vm.Cores.FirstOrDefault(c => c.CoreId == metric.CoreId); if (core == null) { core = new VmCoreItem { CoreId = metric.CoreId }; int idx = 0; while (idx < vm.Cores.Count && vm.Cores[idx].CoreId < metric.CoreId) idx++; vm.Cores.Insert(idx, core); } core.Usage = metric.Usage; UpdateHistory(vm.Name, core); } vm.Columns = GridLayoutMath.CalculateOptimalColumns(vm.Cores.Count); vm.Rows = (vm.Cores.Count > 0) ? (int)Math.Ceiling((double)vm.Cores.Count / vm.Columns) : 1; }
        private void UpdateHistory(string vmName, VmCoreItem core) { string key = $"{vmName}_{core.CoreId}"; if (!_historyCache.TryGetValue(key, out var history)) { history = new LinkedList<double>(); for (int k = 0; k < MaxHistoryLength; k++) history.AddLast(0); _historyCache[key] = history; } history.AddLast(core.Usage); if (history.Count > MaxHistoryLength) history.RemoveFirst(); core.HistoryPoints = CalculatePoints(history); }
        private PointCollection CalculatePoints(LinkedList<double> history) { double w = 100.0, h = 100.0, step = w / (MaxHistoryLength - 1); var points = new PointCollection(MaxHistoryLength + 2) { new Point(0, h) }; int i = 0; foreach (var val in history) points.Add(new Point(i++ * step, h - (val * h / 100.0))); points.Add(new Point(w, h)); points.Freeze(); return points; }


        // ===== GPU 管理模块 - 列表与基础操作 =====

        // 导航至 GPU 管理页面
        [RelayCommand]
        private async Task GoToGpuSettingsAsync()
        {
            if (SelectedVm == null) return;

            CurrentViewType = VmDetailViewType.GpuSettings;
            IsLoadingSettings = true;
            try
            {
                await RefreshCurrentVmGpuAssignments();
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Common_LoadFail, Properties.Resources.Error_Gpu_ReadInfo + ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 刷新当前虚拟机的显卡分配情况
        private async Task RefreshCurrentVmGpuAssignments()
        {
            if (SelectedVm == null) return;
            try
            {
                var vmAdapters = await _vmGpuService.GetVmGpuAdaptersAsync(SelectedVm.Name);
                var hostGpus = await _vmGpuService.GetHostGpusAsync();

                var tempList = new List<VmGpuAssignment>();

                foreach (var adapter in vmAdapters)
                {
                    var matchedHostGpu = hostGpus.FirstOrDefault(h =>
                        !string.IsNullOrEmpty(h.InstanceId) &&
                        !string.IsNullOrEmpty(adapter.InstancePath) &&
                        (adapter.InstancePath.Contains(h.InstanceId, StringComparison.OrdinalIgnoreCase) ||
                         NormalizeDeviceId(h.InstanceId) == NormalizeDeviceId(adapter.InstancePath)));

                    var assignment = new VmGpuAssignment { AdapterId = adapter.Id };

                    if (matchedHostGpu != null)
                    {
                        assignment.Name = matchedHostGpu.Name;
                        assignment.Manu = matchedHostGpu.Manu;
                        assignment.Vendor = matchedHostGpu.Vendor;
                        assignment.DriverVersion = matchedHostGpu.DriverVersion;
                        assignment.Ram = matchedHostGpu.Ram;
                        assignment.PName = matchedHostGpu.Pname;
                    }
                    else
                    {
                        assignment.Name = "Unknown Device";
                        assignment.Manu = "Default";
                    }
                    tempList.Add(assignment);
                }

                Application.Current.Dispatcher.Invoke(() => {
                    bool isHardwareSame = SelectedVm.AssignedGpus.Count == tempList.Count &&
                                         SelectedVm.AssignedGpus.Select(x => x.AdapterId)
                                                      .SequenceEqual(tempList.Select(x => x.AdapterId));

                    if (isHardwareSame)
                    {
                        for (int i = 0; i < tempList.Count; i++)
                        {
                            var target = SelectedVm.AssignedGpus[i];
                            var source = tempList[i];
                            target.Name = source.Name;
                            target.Manu = source.Manu;
                            target.Vendor = source.Vendor;
                            target.DriverVersion = source.DriverVersion;
                            target.Ram = source.Ram;
                            target.PName = source.PName;
                        }
                    }
                    else
                    {
                        SelectedVm.AssignedGpus.Clear();
                        foreach (var item in tempList) SelectedVm.AssignedGpus.Add(item);
                    }

                    SelectedVm.RefreshGpuSummary();
                });
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Gpu_RefreshFail, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
        }

        // 移除 GPU 分区
        [RelayCommand]
        private async Task RemoveGpuAsync(string adapterId)
        {
            if (SelectedVm == null || string.IsNullOrEmpty(adapterId)) return;

            var itemToRemove = SelectedVm.AssignedGpus.FirstOrDefault(x => x.AdapterId == adapterId);
            if (itemToRemove == null) return;

            IsLoadingSettings = true;
            try
            {
                bool success = await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, adapterId);

                if (success)
                {
                    Application.Current.Dispatcher.Invoke(() => {
                        SelectedVm.AssignedGpus.Remove(itemToRemove);
                        if (SelectedVm.AssignedGpus.Count == 0)
                        {
                            SelectedVm.GpuName = string.Empty;
                        }
                    });

                    ShowSnackbar(Properties.Resources.Common_Success, Properties.Resources.Msg_Gpu_PartitionRemoved, ControlAppearance.Success, SymbolRegular.Checkmark24);

                    await Task.Delay(2000);
                    await RefreshCurrentVmGpuAssignments();
                }
                else
                {
                    ShowSnackbar(Properties.Resources.Error_Storage_RemoveFail, Properties.Resources.Error_Gpu_RemoveFail, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                }
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Error_Common_OpException, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }


        // ===== GPU 管理模块 - 部署向导与自动化 =====

        // 导航至添加 GPU 向导
        [RelayCommand]
        private async Task GoToAddGpuAsync()
        {
            if (SelectedVm == null) return;

            IsLoadingSettings = true;
            try
            {
                // 1. 加载 GPU 列表
                var gpus = await _vmGpuService.GetHostGpusAsync();
                HostGpus = new ObservableCollection<GpuInfo>(gpus);
                SelectedHostGpu = null;

                // 2. 加载 Linux 脚本列表 (重写部分)
                var scripts = await _vmGpuService.GetAvailableScriptsAsync();
                AvailableLinuxScripts = new ObservableCollection<LinuxScriptItem>(scripts);
                SelectedLinuxScript = AvailableLinuxScripts.FirstOrDefault(); // 默认选中第一个（通常是本地脚本）

                CurrentViewType = VmDetailViewType.AddGpuSelect;
            }
            catch (Exception ex)
            {
                ShowSnackbar(Properties.Resources.Common_Error, "Failed to load GPU or Scripts: " + ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 取消添加 GPU
        [RelayCommand]
        private async Task CancelAddGpuAsync() // 【修改为 async Task】
        {
            // 【新增：处理中途取消的回滚】
            if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId) && SelectedVm != null)
            {
                try
                {
                    await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                    _currentProcessingGpuAdapterId = null;
                }
                catch { } // 静默清理
            }

            CurrentViewType = VmDetailViewType.GpuSettings;
            GpuTasks.Clear();
        }

        partial void OnSelectedPartitionChanged(PartitionInfo? value)
        {
            if (value == null) return;
            _ = SelectPartitionAndContinueCommand.ExecuteAsync(value);
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                _selectedPartition = null;
                OnPropertyChanged(nameof(SelectedPartition));
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // 检查是否可以确认添加
        private bool CanConfirmAddGpu() => SelectedHostGpu != null;

        // 确认添加 GPU 并开始流程
        [RelayCommand(CanExecute = nameof(CanConfirmAddGpu))]
        private async Task ConfirmAddGpu()
        {
            if (SelectedHostGpu == null) return;

            CurrentViewType = VmDetailViewType.AddGpuProgress;
            ShowPartitionSelector = false;

            GpuDeploymentLog = string.Empty;
            ShowLogConsole = true;

            AppendLog(string.Format(Properties.Resources.Msg_Gpu_WorkStart, SelectedVm.Name));
            AppendLog(string.Format(Properties.Resources.Msg_Gpu_Selected, SelectedHostGpu.Name));
            AppendLog(string.Format(Properties.Resources.Msg_Gpu_Path, SelectedHostGpu.Pname));

            GpuTasks.Clear();

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.Prepare,
                Name = Properties.Resources.Task_Gpu_Prepare,
                Description = Properties.Resources.Msg_Gpu_PreparingHost,
                Status = GpuTaskStatus.Pending
            });

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.ConfigCheck,
                Name = Properties.Resources.Task_Gpu_Config,
                Description = Properties.Resources.Msg_Gpu_CheckingConfig,
                Status = GpuTaskStatus.Pending
            });

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.PowerCheck,
                Name = Properties.Resources.Task_Gpu_Power,
                Description = Properties.Resources.Msg_Gpu_CheckingPower,
                Status = GpuTaskStatus.Pending
            });

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.Optimization,
                Name = Properties.Resources.Task_Gpu_Opt,
                Description = Properties.Resources.Msg_Gpu_Mmio,
                Status = GpuTaskStatus.Pending
            });

            GpuTasks.Add(new TaskItem
            {
                TaskType = GpuTaskType.Assign,
                Name = Properties.Resources.Task_Gpu_Assign,
                Description = Properties.Resources.Msg_Gpu_Creating,
                Status = GpuTaskStatus.Pending
            });
            if (AutoInstallDrivers)
            {
                GpuTasks.Add(new TaskItem
                {
                    TaskType = GpuTaskType.Driver,
                    Name = Properties.Resources.Task_Gpu_Driver,
                    Description = Properties.Resources.Msg_Gpu_WaitingScan,
                    Status = GpuTaskStatus.Pending
                });
            }

            await RunRealGpuWorkflowAsync(0);
        }

        // 执行 GPU 部署工作流
        private async Task RunRealGpuWorkflowAsync(int startIndex)
        {
            var tasks = GpuTasks;
            _currentProcessingGpuAdapterId = null;

            for (int i = startIndex; i < tasks.Count; i++)
            {
                if (CurrentViewType != VmDetailViewType.AddGpuProgress || SelectedHostGpu == null)
                {
                    Debug.WriteLine("GPU Workflow aborted: UI state or SelectedHostGpu has been reset.");
                    return;
                }

                var task = tasks[i];
                task.Status = GpuTaskStatus.Running;
                AppendLog(string.Format(Properties.Resources.Msg_Gpu_ExecTask, task.Name));
                try
                {
                    switch (task.TaskType)
                    {
                        case GpuTaskType.Prepare:
                            await _vmGpuService.PrepareHostEnvironmentAsync();
                            task.Description = Properties.Resources.Msg_Gpu_Policy;
                            break;

                        case GpuTaskType.ConfigCheck:
                            _needConfig = !(await _vmGpuService.CheckVmForGpuAsync(SelectedVm.Name));
                            task.Description = _needConfig ? Properties.Resources.Msg_Gpu_ConfigNeeded : Properties.Resources.Msg_Gpu_ConfigOk;
                            break;

                        case GpuTaskType.PowerCheck:
                            if (_needConfig || AutoInstallDrivers)
                            {
                                var (isOff, state) = await _queryService.IsVmPoweredOffAsync(SelectedVm.Name);
                                if (!isOff)
                                {
                                    task.Description = string.Format(Properties.Resources.Msg_Gpu_ForceOff, state);
                                    AppendLog(task.Description);
                                    await VmPowerService.ExecuteControlActionAsync(SelectedVm.Name, "TurnOff");
                                    var offDeadline = DateTime.UtcNow.AddSeconds(30);
                                    while (!(await _queryService.IsVmPoweredOffAsync(SelectedVm.Name)).IsOff)
                                    {
                                        if (DateTime.UtcNow > offDeadline)
                                            throw new Exception(Properties.Resources.Error_Gpu_PowerOffTimeout);
                                        await Task.Delay(100);
                                    }
                                }
                                task.Description = Properties.Resources.Msg_Gpu_Off;
                            }
                            else
                            {
                                task.Description = Properties.Resources.Msg_Skip;
                            }
                            break;

                        case GpuTaskType.Optimization:
                            if (_needConfig)
                            {
                                bool optOk = await _vmGpuService.OptimizeVmForGpuAsync(SelectedVm.Name);
                                task.Description = optOk ? Properties.Resources.Msg_Gpu_MmioOk : Properties.Resources.Error_Gpu_OptFail;
                            }
                            else
                            {
                                task.Description = Properties.Resources.Msg_Skip;
                            }
                            break;

                        case GpuTaskType.Assign:
                            string targetPath = !string.IsNullOrEmpty(SelectedHostGpu.Pname)
                                                ? SelectedHostGpu.Pname
                                                : SelectedHostGpu.InstanceId;

                            var assignRes = await _vmGpuService.AssignGpuPartitionAsync(SelectedVm.Name, targetPath);
                            if (!assignRes.Success) throw new Exception(assignRes.Message);
                            task.Description = Properties.Resources.Msg_Gpu_AssignOk;
                            await Task.Delay(100);
                            var currentAdapters = await _vmGpuService.GetVmGpuAdaptersAsync(SelectedVm.Name);
                            // 记录下来，以便后续步骤（如驱动安装）失败时删除
                            _currentProcessingGpuAdapterId = currentAdapters.LastOrDefault().Id;
                            break;

                        case GpuTaskType.Driver:
                            {
                                task.Description = Properties.Resources.Msg_Gpu_Scanning;
                                AppendLog(task.Description);

                                // 获取所有硬盘的所有分区
                                var allPartitions = await _vmGpuService.GetPartitionsFromVmAsync(SelectedVm.Name);

                                if (allPartitions == null || allPartitions.Count == 0)
                                {
                                    throw new Exception(Properties.Resources.Error_Gpu_NoPartFound);
                                }

                                // 计算涉及到的物理磁盘数量
                                var distinctDisks = allPartitions.Select(p => p.DiskPath).Distinct().Count();
                                if (distinctDisks == 1 && allPartitions.Count == 1)
                                {
                                    var singlePart = allPartitions[0];

                                    if (singlePart.OsType == OperatingSystemType.Windows)
                                    {
                                        // 1. 如果是 Windows 且单一，执行原有自动注入逻辑
                                        task.Description = Properties.Resources.Msg_Gpu_DetectWin;
                                        var syncRes = await _vmGpuService.SyncWindowsDriversAsync(
                                            SelectedVm.Name,
                                            SelectedHostGpu.Pname,
                                            SelectedHostGpu.Manu,
                                            singlePart,
                                            msg => { task.Description = msg; AppendLog(msg); });

                                        if (!syncRes.Success) throw new Exception(syncRes.Message);
                                        task.Description = Properties.Resources.Msg_Gpu_DriverOk;
                                    }
                                    else if (singlePart.OsType == OperatingSystemType.Linux)
                                    {
                                        // 2. [新增] 如果是 Linux 且单一，直接触发 SelectPartition 流程（嗅探 IP 并显示 SSH 表单）
                                        task.Description = Properties.Resources.Msg_Gpu_LinuxDetected;
                                        AppendLog(Properties.Resources.Msg_Gpu_LinuxAutoPrep);

                                        // 异步启动 Linux 准备工作流（即你点击列表项时触发的逻辑）
                                        await SelectPartitionAndContinueAsync(singlePart);

                                        return; // 退出当前循环，由 SelectPartitionAndContinueAsync 接管后续逻辑
                                    }
                                }
                                else
                                {
                                    // 3. 多分区情况，保持现状，显示列表让用户选择
                                    Application.Current.Dispatcher.Invoke(() =>
                                    {
                                        DetectedPartitions = new ObservableCollection<PartitionInfo>(allPartitions);
                                        ShowPartitionSelector = true;
                                        ShowSshForm = false;
                                    });
                                    task.Description = Properties.Resources.Msg_Gpu_ManualSelect;
                                    AppendLog(task.Description);
                                    return;
                                }
                            }
                            break;
                    }
                    task.Status = GpuTaskStatus.Success;
                    AppendLog(string.Format(Properties.Resources.Msg_Gpu_TaskOk, task.Name, task.Description));
                }
                catch (Exception ex)
                {
                    task.Status = GpuTaskStatus.Failed;
                    task.Description = string.Format(Properties.Resources.Error_Format_FailMsg, ex.Message);
                    AppendLog(string.Format(Properties.Resources.Error_Format_StageExc, task.Name, ex.Message));
                    if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId))
                    {
                        AppendLog(Properties.Resources.Error_Gpu_LinuxRollback);
                        await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                        _currentProcessingGpuAdapterId = null;
                        AppendLog(Properties.Resources.Msg_Gpu_PartitionRemoved);
                    }

                    ShowSnackbar(Properties.Resources.Error_Common_OpFail, string.Format(Properties.Resources.Error_Format_StageError, task.Name), ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    return;
                }
            }

            await FinishWorkflowAsync();
        }

        [RelayCommand]
        private async Task SelectPartitionAndContinueAsync(PartitionInfo partition)
        {
            var driveTask = GpuTasks.FirstOrDefault(t => t.TaskType == GpuTaskType.Driver);
            if (driveTask == null) return;

            if (partition.OsType == OperatingSystemType.Windows)
            {
                ShowPartitionSelector = false;
                driveTask.Status = GpuTaskStatus.Running;
                driveTask.Description = string.Format(Properties.Resources.Msg_Gpu_SyncingPart, partition.PartitionNumber);
                AppendLog(driveTask.Description);

                var result = await _vmGpuService.SyncWindowsDriversAsync(
                    SelectedVm.Name,
                    SelectedHostGpu.Pname,
                    SelectedHostGpu.Manu,
                    partition,
                    msg => {
                        driveTask.Description = msg;
                        AppendLog(msg);
                    });

                if (result.Success)
                {
                    driveTask.Status = GpuTaskStatus.Success;
                    _currentProcessingGpuAdapterId = null;
                    await FinishWorkflowAsync();
                }
                else
                {
                    if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId))
                    {
                        AppendLog(string.Format(Properties.Resources.Error_Gpu_Rollback, result.Message));
                        await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                        _currentProcessingGpuAdapterId = null;
                    }

                    driveTask.Status = GpuTaskStatus.Failed;
                    driveTask.Description = result.Message;
                }
            }
            else if (partition.OsType == OperatingSystemType.Linux)
            {
                _selectedPartition = partition;
                IsLoadingSettings = true;

                // UI 状态转换：保持卡片开启，但切换到 SSH 表单 Grid
                ShowPartitionSelector = true;
                ShowSshForm = true;

                driveTask.Description = Properties.Resources.Msg_Gpu_LinuxVm;
                AppendLog(string.Format(Properties.Resources.Msg_Gpu_LinuxRemoteInit, partition.DisplayName));
                try
                {
                    // --- 自动探测宿主代理 (不修改全局变量) ---
                    UseSshProxy = false; // 默认关闭开关
                    try
                    {
                        var systemProxy = System.Net.WebRequest.DefaultWebProxy;
                        var proxyUri = systemProxy.GetProxy(new Uri("https://github.com"));
                        if (proxyUri != null && !proxyUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
                        {
                            SshProxyHost = proxyUri.Host;
                            SshProxyPort = proxyUri.Port.ToString();
                        }
                    }
                    catch { /* 静默失败 */ }

                    // 检查虚拟机电源状态
                    var status = await _queryService.IsVmPoweredOffAsync(SelectedVm.Name);
                    // 在 SelectPartitionAndContinueAsync 方法内部：
                    if (status.IsOff)
                    {
                        driveTask.Description = Properties.Resources.Msg_Gpu_IpSniff;
                        AppendLog(driveTask.Description);

                        // 1. 执行开机
                        await VmPowerService.ExecuteControlActionAsync(SelectedVm.Name, "Start");

                        // 2. 【新增】立刻强制同步一次 UI 状态，不等后台循环
                        await SyncSingleVmStateAsync(SelectedVm);

                        await Task.Delay(3000); // 给系统一点反应时间
                    }

                    driveTask.Description = Properties.Resources.Msg_Gpu_IpScanning;
                    AppendLog(driveTask.Description);

                    // 扫描 IP
                    string vmIp = await Task.Run(async () =>
                    {
                        var adapters = await VmNetworkService.GetNetworkAdaptersAsync(SelectedVm.Name);
                        string mac = adapters?.FirstOrDefault()?.MacAddress ?? string.Empty;
                        if (!string.IsNullOrEmpty(mac))
                        {
                            for (int i = 0; i < 3; i++)
                            {
                                var ip = await VmIpService.Lookup(SelectedVm.Name, mac);
                                if (!string.IsNullOrEmpty(ip)) return ip;
                                await Task.Delay(2000);
                            }
                        }
                        return string.Empty;
                    });

                    if (!string.IsNullOrEmpty(vmIp))
                    {
                        SshHost = Ipv4.SelectBest(vmIp);
                        AppendLog(string.Format(Properties.Resources.Msg_Gpu_IpOk, SshHost));
                    }
                    else
                    {
                        AppendLog(Properties.Resources.Error_Gpu_IpManual);
                    }

                    driveTask.Description = Properties.Resources.Msg_Gpu_SshConfirm;
                }
                catch (Exception ex)
                {
                    ShowSnackbar(Properties.Resources.Error_Gpu_EnvFail, ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
                    AppendLog(string.Format(Properties.Resources.Warn_Gpu_EnvExc, ex.Message));
                }
                finally
                {
                    IsLoadingSettings = false;
                }
            }
        }
        // 开始 Linux 部署
        [RelayCommand]
        private async Task StartLinuxDeployAsync()
        {
            _gpuDeploymentCts?.Cancel();
            _gpuDeploymentCts = new CancellationTokenSource();
            var token = _gpuDeploymentCts.Token;

            // 1. 定位驱动安装任务项
            var driveTask = GpuTasks.FirstOrDefault(t => t.TaskType == GpuTaskType.Driver);
            if (driveTask == null) return;

            // 2. 验证
            if (SelectedLinuxScript == null || string.IsNullOrWhiteSpace(SshHost))
            {
                ShowSnackbar(Properties.Resources.Error_Common_Verify, Properties.Resources.VmPage_MemGranHugePage, ControlAppearance.Caution, SymbolRegular.Warning24);
                return;
            }

            // 3. 代理参数解析
            int? proxyPort = null;
            string proxyHost = string.Empty;
            if (UseSshProxy)
            {
                proxyHost = SshProxyHost?.Trim() ?? string.Empty;
                if (!int.TryParse(SshProxyPort, out int port) || string.IsNullOrWhiteSpace(proxyHost))
                {
                    ShowSnackbar(Properties.Resources.Error_Common_Verify, Properties.Resources.Validation_ProxyIpAndPortMismatch, ControlAppearance.Danger, SymbolRegular.Warning24);
                    return;
                }
                proxyPort = port;
            }

            // 4. UI 切换：隐藏卡片，显示控制台
            ShowPartitionSelector = false;
            ShowSshForm = false;
            ShowLogConsole = true;
            driveTask.Status = GpuTaskStatus.Running;

            AppendLog(Properties.Resources.Msg_Gpu_DeployStart);
            AppendLog($"[Info] Selected Script: {SelectedLinuxScript.Name}");
            if (UseSshProxy) AppendLog(string.Format(Properties.Resources.Msg_Gpu_UsingProxy, proxyHost, proxyPort));

            // 5. 组装凭据 (强制 KeepGlobalProxySetting 为 false)
            var creds = new SshCredentials
            {
                Host = SshHost,
                Port = SshPort,
                Username = SshUsername,
                Password = SshPassword,
                UseProxy = this.UseSshProxy,
                ProxyHost = this.UseSshProxy ? proxyHost : null,
                ProxyPort = this.UseSshProxy ? proxyPort : null,
                InstallGraphics = InstallGraphics
            };

            // 6. 执行部署
            string result = await _vmGpuService.ProvisionLinuxGpuAsync(
                SelectedVm.Name,
                SelectedLinuxScript,
                creds,
                msg => {
                    if (msg.Contains("[STEP:"))
                    {
                        var match = System.Text.RegularExpressions.Regex.Match(msg, @"\[STEP:\s*(.*?)\]");
                        if (match.Success)
                        {
                            Application.Current.Dispatcher.Invoke(() => {
                                driveTask.Description = match.Groups[1].Value;
                            });
                        }
                    }
                    AppendLog(msg);
                },
                token
            );

            // 7. 流程结束判定
            if (result == "OK" || (result.Contains("successfully") && result.Contains("signing")))
            {
                driveTask.Status = GpuTaskStatus.Success;
                driveTask.Description = Properties.Resources.Msg_Gpu_LinuxDeployDone;
                _currentProcessingGpuAdapterId = null;
                AppendLog(Properties.Resources.Msg_Gpu_LinuxDeployDone);
                await FinishWorkflowAsync();
            }
            else
            {
                // 失败回滚
                if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId))
                {
                    AppendLog(Properties.Resources.Error_Gpu_LinuxRollback);
                    await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                    _currentProcessingGpuAdapterId = null;
                }

                driveTask.Status = GpuTaskStatus.Failed;
                driveTask.Description = result;
                AppendLog(string.Format(Properties.Resources.Error_Gpu_DeployFatal, result));
            }
        }
        // 返回分区选择列表
        [RelayCommand]
        private void GoBackToPartitionList()
        {
            ShowSshForm = false;
            var driveTask = GpuTasks.FirstOrDefault(t => t.Name == Properties.Resources.Task_Gpu_Driver);
            if (driveTask != null)
            {
                driveTask.Description = Properties.Resources.Msg_Gpu_SelectPart;
            }
        }

        // 完成 GPU 部署工作流
        private async Task FinishWorkflowAsync()
        {
            await Task.Delay(1000);
            // 确保在 UI 线程刷新
            await RefreshCurrentVmGpuAssignments();

            // --- 核心修复：非空安全获取显卡名称 ---
            string gpuName = "GPU";
            if (SelectedHostGpu != null)
            {
                gpuName = SelectedHostGpu.Name;
            }
            else if (SelectedVm?.AssignedGpus?.Count > 0)
            {
                // 如果 SelectedHostGpu 已经被重置，尝试从已分配列表里拿名字
                gpuName = SelectedVm.AssignedGpus.Last().Name;
            }

            CurrentViewType = VmDetailViewType.GpuSettings;

            ShowSnackbar(
                Properties.Resources.Msg_Common_ConfigSuccess,
                string.Format(Properties.Resources.Msg_Gpu_Ready, gpuName),
                ControlAppearance.Success,
                SymbolRegular.CheckmarkCircle24);
        }

        // 设备 ID 格式化辅助
        private string NormalizeDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return string.Empty;
            var normalizedId = deviceId.ToUpper();
            if (normalizedId.StartsWith(@"\\?\")) normalizedId = normalizedId.Substring(4);
            int suffixIndex = normalizedId.IndexOf("#{");
            if (suffixIndex != -1) normalizedId = normalizedId.Substring(0, suffixIndex);
            return normalizedId.Replace('\\', '#').Replace("#", "");
        }



        // ===== UI 辅助方法 =====

        // 显示 Snackbar 通知
        private void ShowSnackbar(string title, string message, ControlAppearance appearance, SymbolRegular icon)
            => Notifications.ShowSnackbar(title, message, appearance, icon);
        private string GetOptimisticText(string action) => action switch { "Start" => Properties.Resources.Status_Starting, "Restart" => Properties.Resources.Status_Restarting, "Stop" => Properties.Resources.Status_StoppingPresent, "TurnOff" => Properties.Resources.Status_Off, "Save" => Properties.Resources.Status_Saving, "Suspend" => Properties.Resources.Status_Suspending, _ => Properties.Resources.Status_Processing };

        // 追加日志到控制台
        private void AppendLog(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            Application.Current.Dispatcher.Invoke(() => {
                GpuDeploymentLog += $"[{timestamp}] {message}{Environment.NewLine}";
            });
        }

        // 复制日志
        [RelayCommand]
        private void CopyLog()
        {
            if (!string.IsNullOrEmpty(GpuDeploymentLog))
            {
                Clipboard.SetText(GpuDeploymentLog);
                ShowSnackbar(Properties.Resources.Msg_Common_CopyOk, Properties.Resources.Msg_Gpu_LogCopy, ControlAppearance.Success, SymbolRegular.Copy24);
            }
        }

        // 复制文本到剪贴板
        [RelayCommand]
        private void CopyToClipboard(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text == "---" || text == "00-00-00-00-00-00") return;
            Clipboard.SetText(text);
        }
        // ✅ 增加重置/重试命令逻辑

        [RelayCommand]
        private async Task ResetGpuDeploymentAsync()
        {
            _gpuDeploymentCts?.Cancel();
            _gpuDeploymentCts = new CancellationTokenSource();
            IsLoadingSettings = false;

            if (SelectedPartition != null)
            {
                // --- 场景 1: 软重置 ---
                var driveTask = GpuTasks.FirstOrDefault(t => t.TaskType == GpuTaskType.Driver);
                if (driveTask != null)
                {
                    driveTask.Status = GpuTaskStatus.Pending;
                    driveTask.Description = SelectedPartition.OsType == OperatingSystemType.Linux
                        ? Properties.Resources.Msg_Gpu_SshConfirm
                        : Properties.Resources.Msg_Gpu_SelectPart;
                }

                if (SelectedPartition.OsType == OperatingSystemType.Linux)
                {
                    ShowPartitionSelector = true;
                    ShowSshForm = true;
                }
                else
                {
                    // Windows 流程重置
                    ShowPartitionSelector = true;
                    ShowSshForm = false;

                    // --- 关键改进：清空选中项，允许用户重新点击同一个分区 ---
                    SelectedPartition = null;
                }

                AppendLog($"--- {Properties.Resources.Label_Progress} ({Properties.Resources.VmPage_MemGranHugePage2}) ---");
                return;
            }
            // --- 场景 2: “硬重置”（彻底回滚，回到选显卡第一步） ---
            // 触发条件：还没有选定分区就挂了，或者用户在还没选分区时点击了重置

            // 1. 如果当前有正在处理的分区 ID（说明已经分配但未成功），执行物理回滚
            if (!string.IsNullOrEmpty(_currentProcessingGpuAdapterId))
            {
                AppendLog(Properties.Resources.VmPage_MemGranAutoAssign2); // Properties.Resources.VmPage_MsgRollingBackGpu2
                try
                {
                    await _vmGpuService.RemoveGpuPartitionAsync(SelectedVm.Name, _currentProcessingGpuAdapterId);
                    _currentProcessingGpuAdapterId = null;
                    AppendLog(Properties.Resources.VmPage_MemGranStandard2); // Properties.Resources.VmPage_MsgRollbackComplete2
                }
                catch (Exception ex)
                {
                    AppendLog(string.Format(Properties.Resources.VmPage_MemGranLargePage2, ex.Message)); // Properties.Resources.VmPage_ErrRollbackFailed2
                }
            }

            // 2. 重置所有 UI 状态
            GpuTasks.Clear();
            GpuDeploymentLog = string.Empty;
            ShowPartitionSelector = false;
            ShowSshForm = false;
            ShowLogConsole = false;

            // 3. 彻底重来，重新初始化数据并跳转回选择界面
            await GoToAddGpuAsync();

            // 4. 弹出全局重置提示
            ShowSnackbar(
                Properties.Resources.VmPage_MemGranHugePage2, // Properties.Resources.VmPage_BtnReset2
                Properties.Resources.VmPage_MemTrackDisable, // Properties.Resources.VmPage_MsgProcessReset2
                Wpf.Ui.Controls.ControlAppearance.Info,
                Wpf.Ui.Controls.SymbolRegular.ArrowCounterclockwise24);
        }
        private async Task MonitorThumbnailLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // 只有当选中且运行时才更新
                if (SelectedVm != null && SelectedVm.IsRunning)
                {
                    var img = await VmScreenshotService.CaptureAsync(SelectedVm.Name, 320, 240);
                    if (img != null)
                    {
                        Application.Current.Dispatcher.Invoke(() => SelectedVm.Thumbnail = img);
                    }
                }
                else if (SelectedVm != null && !SelectedVm.IsRunning && SelectedVm.Thumbnail != null)
                {
                    Application.Current.Dispatcher.Invoke(() => SelectedVm.Thumbnail = null);
                }

                // 缩略图不需要太高的刷新率，1.5秒或2秒一次即可，避免占用过多WMI资源
                await Task.Delay(1500, token);
            }
        }
        // 获取目录，用于 InitialDirectory
        private string GetDir(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                return Path.GetDirectoryName(path) ?? string.Empty;
            }
            catch { return string.Empty; }
        }

        // 获取文件名，用于 SaveFileDialog 的 FileName
        private string GetFileName(string? path, string defaultNameWithExt)
        {
            if (string.IsNullOrWhiteSpace(path)) return defaultNameWithExt;
            try
            {
                return Path.GetFileName(path) ?? defaultNameWithExt;
            }
            catch { return defaultNameWithExt; }
        }

    }
}
