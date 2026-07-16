using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
        AddGpuProgress, NetworkSettings, BootSettings, SpacetimeSettings, Advanced, Security
    }
    public partial class VirtualMachinesPageViewModel : PageViewModelBase, IDisposable
    {
        // ===== 私有服务字段与依赖注入 =====
        private readonly VmQueryService _queryService;
        private readonly VmGpuService _vmGpuService;


        // ===== 监控与后台任务字段 =====
        private CpuMonitorService _cpuService = null!;
        private CancellationTokenSource? _monitoringCts;
        private DispatcherTimer _uiTimer;
        // 防止监控循环对同一网卡重复并发起 IP/ARP 查询（无界堆积）
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _ipLookupsInFlight = new();
        // PktMon 被动嗅探 vSwitch 上的 ARP，补无集成服务 VM（如国产 Linux）的 IP；进程级单例,与网络页/VmIpService 共用
        private readonly ArpSnoopService _ipSnoop = ArpSnoopService.Instance;

        private readonly Dictionary<Guid, (string NewName, DateTime Expiry)> _renameLockouts = new();


        // ===== 缓存与状态字段 =====
        private const int MaxHistoryLength = 60;
        private readonly Dictionary<string, LinkedList<double>> _historyCache = new();
        // 程序性赋值抑制统一改用基类 SuppressApply()/IsApplySuppressed（原 _isInternalUpdating）。
        // _originalMemorySettingsCache 归 Memory.cs、_isDiskPathManual 归 Create.cs（功能私有，不再堆在核心）。


        // ===== 视图模型属性 - 页面状态 =====
        [ObservableProperty] private bool _isLoading = true;
        [ObservableProperty] private bool _isLoadingSettings;
        [ObservableProperty] private VmDetailViewType _currentViewType = VmDetailViewType.Dashboard;
        [ObservableProperty] private string _searchText = string.Empty;


        // ===== 视图模型属性 - 虚拟机列表与选择 =====
        [ObservableProperty] private ObservableCollection<VmInstanceViewModel> _vmList = new();
        [ObservableProperty] private VmInstanceViewModel _selectedVm;
        [ObservableProperty] private BitmapSource? _thumbnail;


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


        // ===== 虚拟机列表与操作 =====

        [RelayCommand]
        private async Task OpenVmFolderAsync(VmInstanceViewModel vm)
        {
            if (vm == null) return;
            try
            {
                string? path = await _queryService.GetVmConfigRootAsync(vm.Name);

                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    Shell.Reveal(path);
                }
                else
                {
                    ShowError($"{Properties.Resources.VmPage_OpenFail}：{Properties.Resources.VmPage_ConfigDirNotFound}");
                }
            }
            catch (Exception ex)
            {
                ShowError($"{Properties.Resources.VmPage_OpenFail}：{ex.Message}");
            }
        }

        // 多选状态：code-behind 的 ListView.SelectionChanged 推进来。>1 时右键菜单只留删除/彻底删除，且按整批操作。
        private List<VmInstanceViewModel> _selectedVms = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsMultiSelect))]
        [NotifyPropertyChangedFor(nameof(IsSingleOrNoneSelect))]
        [NotifyPropertyChangedFor(nameof(MultiPowerToggleText))]
        private int _selectedVmCount;

        public bool IsMultiSelect => SelectedVmCount > 1;
        public bool IsSingleOrNoneSelect => SelectedVmCount <= 1;

        // 多选电源按钮：全部在运行→关机(把运行中的全关)，否则→启动(把未运行的都拉起，已运行的不动)。
        public string MultiPowerToggleText => _selectedVms.Count > 0 && _selectedVms.All(v => v.IsRunning)
            ? Properties.Resources.Button_ShutDown
            : Properties.Resources.Button_Start;

        public void UpdateSelection(System.Collections.IList items)
        {
            _selectedVms = items?.Cast<VmInstanceViewModel>().ToList() ?? new List<VmInstanceViewModel>();
            SelectedVmCount = _selectedVms.Count;
        }

        [RelayCommand]
        private async Task MultiPowerAsync()
        {
            var targets = _selectedVms.ToList();
            if (targets.Count == 0) return;
            bool allRunning = targets.All(v => v.IsRunning);
            string action = allRunning ? "Stop" : "Start";                    // 与单机右键/主按钮一致：Stop=优雅失败则硬关
            var toAct = (allRunning ? targets.Where(v => v.IsRunning) : targets.Where(v => !v.IsRunning)).ToList();
            // 复用每台自己的 ControlCommand：连带 transient 态、状态回同步、开机失败的反应式修复都照走。
            await Task.WhenAll(toAct.Select(v => v.ControlCommand?.ExecuteAsync(action) ?? Task.CompletedTask));
            OnPropertyChanged(nameof(MultiPowerToggleText));
        }

        [RelayCommand]
        private async Task DeleteVmAsync(VmInstanceViewModel vm)
        {
            if (IsMultiSelect) { await DeleteMultipleAsync(_selectedVms.ToList()); return; }
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
                    ShowError($"{Properties.Resources.VmPage_DeleteFail}：{FriendlyError.CleanLines(result.Message)}");
                }
            }
            catch (Exception ex)
            {
                ShowError($"{Properties.Resources.VmPage_DeleteFail}：{FriendlyError.CleanLines(ex.Message)}");
            }
            finally { IsLoading = false; }
        }

        // 批量删除（保留磁盘）：确认 → 逐台删 → 聚合汇报 → 收拾选中项。
        private async Task DeleteMultipleAsync(List<VmInstanceViewModel> targets)
        {
            if (targets.Count == 0) return;
            bool ok = await Dialogs.ShowConfirmAsync(
                Properties.Resources.VmPage_MultiDeleteTitle,
                string.Format(Properties.Resources.VmPage_MultiDeleteConfirm, targets.Count),
                Properties.Resources.Xaml_Delete, Properties.Resources.Button_Cancel, isDanger: true);
            if (!ok) return;

            IsLoading = true;
            try
            {
                int okCount = 0;
                foreach (var t in targets)
                {
                    var r = await VmDeleteService.DeleteVmAsync(t.Name);
                    if (r.Success) { VmList.Remove(t); okCount++; }
                }
                if (SelectedVm != null && !VmList.Contains(SelectedVm)) SelectedVm = VmList.FirstOrDefault();
                int fail = targets.Count - okCount;
                if (fail == 0) ShowSuccess(string.Format(Properties.Resources.VmPage_MultiDeleteDone, okCount));
                else ShowError(string.Format(Properties.Resources.VmPage_MultiDeleteFail, okCount, fail));
            }
            finally { IsLoading = false; }
        }

        // 批量彻底删除：不逐台展开文件预览（台数多会撑爆），改用名称清单确认 → 逐台彻底删 → 聚合汇报。
        private async Task PurgeMultipleAsync(List<VmInstanceViewModel> targets)
        {
            if (targets.Count == 0) return;
            string list = string.Join("\n", targets.Select(t => "· " + t.Name));
            bool ok = await Dialogs.ShowConfirmAsync(
                Properties.Resources.VmPage_PurgeTitle,
                string.Format(Properties.Resources.VmPage_MultiPurgeConfirm, targets.Count) + "\n\n" + list,
                Properties.Resources.VmPage_PurgeBtn, Properties.Resources.Button_Cancel, isDanger: true);
            if (!ok) return;

            IsLoading = true;
            try
            {
                int okCount = 0;
                foreach (var t in targets)
                {
                    var r = await VmDeleteService.PurgeVmAsync(t.Name, t.Id);
                    if (r.Success) { VmList.Remove(t); okCount++; }
                }
                if (SelectedVm != null && !VmList.Contains(SelectedVm)) SelectedVm = VmList.FirstOrDefault();
                int fail = targets.Count - okCount;
                if (fail == 0) ShowSuccess(string.Format(Properties.Resources.VmPage_MultiPurgeDone, okCount));
                else ShowError(string.Format(Properties.Resources.VmPage_MultiPurgeFail, okCount, fail));
            }
            finally { IsLoading = false; }
        }

        [RelayCommand]
        private async Task PurgeVmAsync(VmInstanceViewModel vm)
        {
            if (IsMultiSelect) { await PurgeMultipleAsync(_selectedVms.ToList()); return; }
            if (vm == null) return;

            // 二次确认弹窗：预先算出"将删除的目录与文件"清单直接展示——替代口头提醒用户自己去查目录里有没有其他文件。
            var preview = await VmDeleteService.PreviewPurgeAsync(vm.Id);
            var list = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(preview.ConfigDir))
            {
                list.AppendLine("· " + preview.ConfigDir);
                int shown = 0;
                foreach (var f in preview.ConfigDirFiles)
                {
                    if (shown++ >= 40) { list.AppendLine($"     · … (+{preview.ConfigDirFiles.Count - 40})"); break; }
                    list.AppendLine("     · " + System.IO.Path.GetFileName(f));
                }
            }
            foreach (var d in preview.ExternalDiskFiles)
                list.AppendLine("· " + d);
            if (list.Length == 0) list.Append(vm.Name);

            // 正文用原生控件：上方告警文字（自动换行）+ 下方等宽、可滚动的清单（路径长/文件多都不撑爆弹窗）。
            var body = new System.Windows.Controls.StackPanel();
            body.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = Properties.Resources.VmPage_PurgeConfirm,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 8),
            });
            body.Children.Add(new System.Windows.Controls.ScrollViewer
            {
                MaxHeight = 220,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = list.ToString().TrimEnd(),
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12,
                },
            });

            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = Properties.Resources.VmPage_PurgeTitle,
                Content = body,
                PrimaryButtonText = Properties.Resources.VmPage_PurgeBtn,
                PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,   // 左侧确认按钮红色（危险操作）；右侧取消保持默认
                CloseButtonText = Properties.Resources.Button_Cancel,
            };
            Interaction.Dialogs.ForceDangerButtonWhiteForeground(dialog);   // Danger 主按钮亮色主题下红底黑字，强制刷白

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
                    ShowSuccess(string.Format(Properties.Resources.VmPage_PurgeDoneDesc, vm.Name));
                }
                else
                {
                    ShowError($"{Properties.Resources.VmPage_DeleteFail}：{FriendlyError.CleanLines(purge.Message)}");
                }
            }
            catch (Exception ex)
            {
                ShowError($"{Properties.Resources.VmPage_DeleteFail}：{FriendlyError.CleanLines(ex.Message)}");
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
                    var result = await VmPowerService.ExecuteControlActionAsync(instance.Name, action);
                    if (!result.Success)
                    {
                        // 引擎拒绝了操作(配置错误/资源不足/GPU 分区不可用等)——清乐观态
                        Application.Current.Dispatcher.Invoke(() => instance.ClearTransientState());
                        // 反应式修复:开机失败且该 VM 存在悬空 GPU-PV(钉死的物理 GPU 已不在主机)时,弹确认→清除→重试。
                        // 这类记录在 WMI 层完全隐形、官方 cmdlet 删不掉,只能走 .vmcx 引擎(见 VmGpuRepairService)。
                        if ((action == "Start" || action == "Restart")
                            && await TryRepairStaleGpuPvAndRetryAsync(instance, action, result.Error))
                        {
                            return; // 已介入处理,不再弹通用报错
                        }
                        // 同款反应式修复:开机失败且挂着悬空直通物理盘 → 弹确认 → 移除 → 重试。
                        if ((action == "Start" || action == "Restart")
                            && await TryRemoveStalePassthroughDiskAndRetryAsync(instance, action, result.Error))
                        {
                            return;
                        }
                        ShowError(FriendlyError.CleanLines(result.Error));
                        return;
                    }
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
                    ShowError(FriendlyError.CleanLines(realEx.Message));
                }
            });

            return instance;
        }

        // 开机失败 → 若该 VM 存在悬空 GPU-PV(钉死的物理 GPU 已不在主机),弹确认 → 引擎清除 → 重试开机。
        // 返回 true = 已介入处理(调用方不再弹通用报错);false = 无悬空 GPU-PV / 检测失败,走通用报错。
        private async Task<bool> TryRepairStaleGpuPvAndRetryAsync(VmInstanceViewModel instance, string action, string startError)
        {
            List<VmGpuRepairService.StaleGpuPartition> stale;
            try { stale = await VmGpuRepairService.FindStaleGpuPartitionsAsync(instance.Name); }
            catch { return false; }
            if (stale.Count == 0) return false;

            // 仅当本次开机失败确实由 GPU 分区引起时才提示修复,避免把内存不足等其它原因误报成 GPU 问题。
            // 判据(本地化无关):失败错误文本含设备名 "GPU Partition"(本地化消息里仍为英文),
            // 或含某个失效分区的实例 GUID。两者皆无 → 本次失败另有其因(如 0x8007000E 内存不足)→ 交回通用报错。
            string err = startError ?? string.Empty;
            bool gpuImplicated = err.IndexOf("GPU Partition", StringComparison.OrdinalIgnoreCase) >= 0
                || stale.Any(s => err.IndexOf(s.Instance, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!gpuImplicated) return false;

            // 区分两种失配:同一张卡仍在主机但路径变了(可重指,保住 GPU)vs 卡已不在(只能清除)
            bool allRebind = stale.All(s => !string.IsNullOrEmpty(s.RebindPath));
            string title, message, confirmText;
            if (allRebind)
            {
                title = Properties.Resources.Gpu_StalePathTitle;
                message = string.Format(Properties.Resources.Gpu_StalePathMessage, instance.Name);
                confirmText = Properties.Resources.Gpu_StaleRebindConfirm;
            }
            else
            {
                title = Properties.Resources.Gpu_StaleTitle;
                message = string.Format(Properties.Resources.Gpu_StaleMessage, instance.Name);
                confirmText = Properties.Resources.Gpu_StaleRemoveConfirm;
            }
            bool ok = await Dialogs.ShowConfirmAsync(
                title, message, confirmText, Properties.Resources.Btn_Cancel,
                isDanger: true, showIcon: false, maxWidth: 340);
            if (!ok) return true; // 用户取消:已介入,不再弹通用报错

            var (success, repairMsg, rebound, removed) = await VmGpuRepairService.RepairAsync(instance.Name, stale);
            if (!success)
            {
                ShowError(string.IsNullOrEmpty(repairMsg) ? Properties.Resources.Gpu_StaleRepairFail : repairMsg);
                return true;
            }
            ShowSuccess(
                (rebound > 0 && removed == 0) ? string.Format(Properties.Resources.Gpu_StaleRebound, rebound) :
                (removed > 0 && rebound == 0) ? string.Format(Properties.Resources.Gpu_StaleRemovedMsg, removed) :
                Properties.Resources.Gpu_StaleRepaired);

            // 重试开机(引擎就地改 .vmcx 即生效,无需停 vmms)
            instance.SetTransientState(GetOptimisticText(action));
            var retry = await VmPowerService.ExecuteControlActionAsync(instance.Name, action);
            if (!retry.Success)
            {
                Application.Current.Dispatcher.Invoke(() => instance.ClearTransientState());
                ShowError(FriendlyError.CleanLines(retry.Error));
            }
            else
            {
                await SyncSingleVmStateAsync(instance);
                if (action == "Start" || action == "Restart") TryApplyAffinityForRootScheduler(instance);
            }
            return true;
        }

        // 开机失败且挂着悬空直通物理盘(HostResource 钉的盘已从可直通池消失——被拔出/联机) → 弹确认移除该盘再重试。
        // 返回 true=已介入(不再弹通用报错);false=无悬空盘/失败另有其因,交回通用报错。与 GPU-PV 悬空处理同款。
        private async Task<bool> TryRemoveStalePassthroughDiskAndRetryAsync(VmInstanceViewModel instance, string action, string startError)
        {
            List<VmStorageItem> stale;
            try { stale = await VmStorageService.FindStalePassthroughDisksAsync(instance.Name); }
            catch { return false; }
            if (stale.Count == 0) return false;

            // 确认失败确由物理盘附件打不开引起(否则内存不足等被误报)。判据本地化无关:错误码 0x80070103 或英文 "failed to open"。
            string err = startError ?? string.Empty;
            bool diskImplicated = err.IndexOf("0x80070103", StringComparison.OrdinalIgnoreCase) >= 0
                || err.IndexOf("failed to open", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!diskImplicated) return false;

            string names = string.Join(", ", stale.Select(DescribeStalePassthroughDisk));
            bool ok = await Dialogs.ShowConfirmAsync(
                Properties.Resources.Storage_StaleDiskTitle,
                string.Format(Properties.Resources.Storage_StaleDiskMessage, instance.Name, names),
                Properties.Resources.Storage_StaleDiskConfirm, Properties.Resources.Btn_Cancel,
                isDanger: true, showIcon: false, maxWidth: 360);
            if (!ok) return true; // 用户取消:已介入,不再弹通用报错

            int removed = 0;
            foreach (var d in stale)
            {
                var r = await VmStorageService.RemoveDriveAsync(instance.Name, d);
                if (r.Success) removed++;
            }
            if (removed == 0)
            {
                ShowError(Properties.Resources.Storage_StaleDiskRemoveFail);
                return true;
            }
            ShowSuccess(string.Format(Properties.Resources.Storage_StaleDiskRemoved, removed));

            instance.SetTransientState(GetOptimisticText(action));
            var retry = await VmPowerService.ExecuteControlActionAsync(instance.Name, action);
            if (!retry.Success)
            {
                Application.Current.Dispatcher.Invoke(() => instance.ClearTransientState());
                ShowError(FriendlyError.CleanLines(retry.Error));
            }
            else
            {
                await SyncSingleVmStateAsync(instance);
                if (action == "Start" || action == "Restart") TryApplyAffinityForRootScheduler(instance);
            }
            return true;
        }

        private static string DescribeStalePassthroughDisk(VmStorageItem d)
            => !string.IsNullOrEmpty(d.DiskModel) ? d.DiskModel
             : d.DiskNumber >= 0 ? string.Format(Properties.Resources.Storage_PhysicalDiskNumbered, d.DiskNumber)
             : Properties.Resources.Storage_PhysicalDisk;

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
                ShowError($"{Properties.Resources.Error_Common_LoadFail}：{FriendlyError.CleanLines(ex.Message)}");
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






        // 打开沉浸式控制台窗口（取代外部 vmconnect.exe）
        [RelayCommand]
        private async Task OpenNativeConnectAsync()
        {
            if (SelectedVm == null) return;

            // 已禁用控制台支持(无合成显示)的 VM：打开控制台只会黑屏/连不上，明确提示而非打开
            if (!await VmConsoleService.IsConsoleSupportEnabledAsync(SelectedVm.Name))
            {
                ShowTip(Properties.Resources.VmAdvanced_ConsoleDisabledHint);
                return;
            }

            try
            {
                // 打开当前选中虚拟机的沉浸式控制台窗口（现走新的 RdpClientHost）
                Navigation.OpenConsoleWindow(SelectedVm.Id.ToString(), SelectedVm.Name);
            }
            catch (Exception ex)
            {
                ShowError(string.Format(Properties.Resources.VmPage_ErrConfigDirNotFound, ex.Message));
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
                ShowError($"{Properties.Resources.Error_Common_ModFailShort}：{Properties.Resources.Error_Common_NoPermission}");
            }
        }


        // ===== 后台监控循环与状态更新 =====

        // 启动后台监控线程
        private void StartMonitoring()
        {
            if (_monitoringCts != null) return;
            _monitoringCts = new CancellationTokenSource();
            _ = Task.Run(() => MonitorCpuLoop(_monitoringCts.Token));
            _ = Task.Run(() => MonitorStateLoop(_monitoringCts.Token));
            // 独立的缩略图任务，避免阻塞状态同步
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
                                // 重命名锁定保护拦截
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

                    // 后台线程：先快照 SelectedVm 再用，避免与 UI 线程改选中项竞态导致 NRE
                    var selForDisk = SelectedVm;
                    if (selForDisk != null && selForDisk.IsRunning)
                    {
                        await VmStorageService.RefreshVirtualDiskSizesAsync(selForDisk.Model);
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


        // ===== UI 辅助方法 =====

        private string GetOptimisticText(string action) => action switch { "Start" => Properties.Resources.Status_Starting, "Restart" => Properties.Resources.Status_Restarting, "Stop" => Properties.Resources.Status_StoppingPresent, "TurnOff" => Properties.Resources.Status_StoppingPresent, "Save" => Properties.Resources.Status_Saving, "Suspend" => Properties.Resources.Status_Suspending, _ => Properties.Resources.Status_Processing };


        // 复制文本到剪贴板
        [RelayCommand]
        private void CopyToClipboard(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text == "---" || text == "00-00-00-00-00-00") return;
            Shell.CopyToClipboard(text);
        }
        private async Task MonitorThumbnailLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // 后台线程：先快照 SelectedVm 再用，避免与 UI 线程改选中项竞态导致 NRE
                var sel = SelectedVm;
                // 只有当选中且运行时才更新
                if (sel != null && sel.IsRunning)
                {
                    var img = await VmScreenshotService.CaptureAsync(sel.Name, 320, 240);
                    if (img != null)
                    {
                        Application.Current.Dispatcher.Invoke(() => sel.Thumbnail = img);
                    }
                }
                else if (sel != null && !sel.IsRunning && sel.Thumbnail != null)
                {
                    Application.Current.Dispatcher.Invoke(() => sel.Thumbnail = null);
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
