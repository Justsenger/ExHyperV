using ExHyperV.Application;
using ExHyperV.UI.Pages.VirtualMachines.Imaging;
using ExHyperV.UI.Shared;
using ExHyperV.UI.Shared.Controls;
using ExHyperV.UI.Shared.Interaction;
using ExHyperV.UI.Windows.Console;
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
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.UI.Pages.VirtualMachines
{
    public enum VmDetailViewType
    {
        Dashboard, CpuSettings, CpuAffinity, MemorySettings, StorageSettings, AddStorage,
        GpuSettings,
        AddGpuSelect,
        AddGpuProgress, NetworkSettings, BootSettings, SpacetimeSettings, Advanced, Security, PcieSettings,
        Export
    }
    public partial class VirtualMachinesPageViewModel : PageViewModelBase, IDisposable
    {
        private readonly VmQueryService _queryService;
        private readonly VmGpuService _vmGpuService;


        private readonly CancellationTokenSource _monitoringCts = new();
        private readonly CancellationToken _monitoringToken;
        private Task[] _monitoringTasks = [];
        private Task? _stopMonitoringTask;
        private readonly Dispatcher _dispatcher;
        private DispatcherTimer _uiTimer;
        // 防止监控循环对同一网卡重复并发起 IP/ARP 查询（无界堆积）
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _ipLookupsInFlight = new();
        private readonly List<Task> _ipLookupTasks = [];

        private readonly Dictionary<Guid, (string NewName, DateTime Expiry)> _renameLockouts = new();


        private const int MaxHistoryLength = 60;
        private readonly Dictionary<string, LinkedList<double>> _historyCache = new();
        // 程序性赋值抑制统一改用基类 SuppressApply()/IsApplySuppressed（原 _isInternalUpdating）。
        // _originalMemorySettingsCache 归 Memory.cs、_isDiskPathManual 归 Create.cs（功能私有，不再堆在核心）。


        [ObservableProperty] private bool _isLoading = true;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveAffinityCommand))]
        private bool _isLoadingSettings;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsVmListEnabled))]
        [NotifyCanExecuteChangedFor(nameof(SaveAffinityCommand))]
        private VmDetailViewType _currentViewType = VmDetailViewType.Dashboard;

        // 进行中的向导/部署视图(选卡、GPU-PV 部署、加存储)绑死某台 VM，期间禁用左侧列表：
        // 防止切走后工作流后续步骤读到的 SelectedVm 变成别的 VM，把关机/挂卡等操作打到错的机器上。
        public bool IsVmListEnabled => !IsChangingGroups && !IsExporting && !IsPreparingVmImport && !IsExecutingVmImport && CurrentViewType is not
            (VmDetailViewType.AddGpuSelect or VmDetailViewType.AddGpuProgress or VmDetailViewType.AddStorage);
        [ObservableProperty] private string _searchText = string.Empty;


        [ObservableProperty] private ObservableCollection<VmInstanceViewModel> _vmList = new();
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(SaveAffinityCommand))]
        private VmInstanceViewModel _selectedVm;
        [ObservableProperty] private BitmapSource? _thumbnail;



        // Linux 部署字段

        [ObservableProperty] private ObservableCollection<LinuxGpuDeploymentScript> _availableLinuxScripts = new();
        [ObservableProperty] private LinuxGpuDeploymentScript _selectedLinuxScript;

        public VirtualMachinesPageViewModel(VmQueryService queryService)
        {
            _dispatcher = WpfApplication.Current.Dispatcher;
            _monitoringToken = _monitoringCts.Token;
            _queryService = queryService;
            _vmGpuService = new VmGpuService(_queryService);

            InitPossibleCpuCounts();

            for (int i = 0; i < 64; i++)
            {
                AvailableLocations.Add(i);
            }

            _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uiTimer.Tick += (s, e) => {
                foreach (var vm in VmList) vm.TickUptime();
                if (!IsLoading && IsVmListEnabled) RefreshNavigation();
                if (!IsLoading && IsVmListEnabled && DateTime.UtcNow >= _nextGroupRefresh) _ = RefreshGroupsAsync();
            };
            _uiTimer.Start();

            if (WpfApplication.Current is App app) app.StopDesktopMonitoring += StopMonitoringAsync;
            _ = Task.Run(async () => {
                try
                {
                    await Task.Delay(300, _monitoringToken);
                    await ApplyMonitorUpdateAsync(() => LoadVmsCommand.Execute(null), _monitoringToken);
                }
                catch (OperationCanceledException) when (_monitoringToken.IsCancellationRequested) { }
            });
        }

        public void Dispose()
        {
            StopMonitoringAsync().GetAwaiter().GetResult();
            foreach (var vm in VmList) vm.Dispose();
        }

        // Called on the desktop dispatcher, including App.OnExit. Only monitoring
        // and idle import previews are stopped here; running operations retain their exit policy.
        internal Task StopMonitoringAsync()
        {
            _dispatcher.VerifyAccess();
            if (_stopMonitoringTask != null) return _stopMonitoringTask;
            _disposed = true;
            _uiTimer.Stop();
            _monitoringCts.Cancel();
            if (WpfApplication.Current is App app) app.StopDesktopMonitoring -= StopMonitoringAsync;
            var idleImportCleanup = DisposeIdleImportForShutdownAsync();
            return _stopMonitoringTask = CompleteMonitoringStopAsync(
                _monitoringTasks.Concat(_ipLookupTasks).Concat(_affinityRestoreTasks).Append(idleImportCleanup).ToArray());
        }

        private async Task CompleteMonitoringStopAsync(Task[] tasks)
        {
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_monitoringToken.IsCancellationRequested) { }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[ExHyperV] Monitor task failed: {ex.Message}"); }
            finally
            {
                try { await _queryService.DisposeAsync().ConfigureAwait(false); }
                finally { _monitoringCts.Dispose(); }
            }
        }

        private Task ApplyMonitorUpdateAsync(Action update, CancellationToken token) =>
            _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed) update();
            }, DispatcherPriority.Normal, token).Task;



        partial void OnSearchTextChanged(string value)
        {
            RefreshNavigation();
        }

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
                case VmDetailViewType.PcieSettings:
                    CurrentViewType = VmDetailViewType.Dashboard;
                    break;
                default:
                    CurrentViewType = VmDetailViewType.Dashboard;
                    break;
            }
        }



        [RelayCommand]
        private async Task OpenVmFolderAsync(VmInstanceViewModel vm)
        {
            if (vm == null) return;
            try
            {
                string? path = (await _queryService.GetVmConfigRootAsync(vm.Id)).RequireData();

                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                {
                    // 资源管理器会复用普通权限的桌面进程，因此打开默认 Hyper-V 配置目录前需补充当前用户的只读权限。
                    var access = ExplorerFolderAccess.EnsureExplorerCanRead(path);
                    if (!access.Success)
                    {
                        ShowError($"{Properties.Resources.VmPage_OpenFail}：{access.Error}");
                        return;
                    }

                    var reveal = await Shell.RevealAsync(path);
                    if (!reveal.Success)
                        ShowError($"{Properties.Resources.VmPage_OpenFail}：{reveal.Error}");
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
        private readonly HashSet<Guid> _batchPowerBusy = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsMultiSelect))]
        [NotifyPropertyChangedFor(nameof(IsSingleOrNoneSelect))]
        [NotifyPropertyChangedFor(nameof(MultiPowerToggleText))]
        private int _selectedVmCount;

        public bool IsMultiSelect => SelectedVmCount > 1;
        public bool IsSingleOrNoneSelect => SelectedVmCount <= 1;

        // 多选电源按钮：全部在运行→关机(把运行中的全关)，否则→启动(把未运行的都拉起，已运行的不动)。
        public string MultiPowerToggleText => VmBatchControlService.GetPowerPlan(_selectedVms.Select(ToBatchTarget)).Action == VmPowerAction.Stop
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
            var byId = targets.ToDictionary(vm => vm.Id);
            var plan = VmBatchControlService.GetPowerPlan(targets.Select(ToBatchTarget));
            foreach (var target in plan.Targets)
            {
                _batchPowerBusy.Add(target.Id);
                byId[target.Id].ControlCommand?.NotifyCanExecuteChanged();
            }
            try
            {
                await VmBatchControlService.ExecutePowerAsync(targets.Select(ToBatchTarget),
                    (_, confirmation) => ConfirmPowerRepairAsync(confirmation),
                    (target, operation) => PresentPowerOperation(byId[target.Id], operation),
                    async result =>
                    {
                        await CompletePowerPresentationAsync(byId[result.Target.Id], result.Operation, false);
                        _batchPowerBusy.Remove(result.Target.Id);
                        byId[result.Target.Id].ControlCommand?.NotifyCanExecuteChanged();
                    });
            }
            finally
            {
                foreach (var target in plan.Targets)
                {
                    _batchPowerBusy.Remove(target.Id);
                    byId[target.Id].ControlCommand?.NotifyCanExecuteChanged();
                }
                OnPropertyChanged(nameof(MultiPowerToggleText));
            }
        }

        private static VmBatchTarget ToBatchTarget(VmInstanceViewModel vm) => new(vm.Id, vm.Name, vm.IsRunning);

        [RelayCommand]
        private async Task DeleteVmAsync(VmInstanceViewModel vm)
        {
            if (IsMultiSelect) { await DeleteMultipleAsync(_selectedVms.ToList()); return; }
            if (vm == null) return;
            IsLoading = true;

            try
            {
                var result = await VmDeleteService.DeleteVmAsync(vm.Name, vm.Id);
                if (result.RegistrationState == VmRegistrationState.Unregistered)
                {
                    VmList.Remove(vm);
                    vm.Dispose();
                    if (SelectedVm == vm) SelectedVm = VmList.FirstOrDefault();
                }
                if (!result.Success)
                {
                    ShowError($"{Properties.Resources.VmPage_DeleteFail}：{DescribeDeletionResult(result)}");
                }
            }
            catch (Exception ex)
            {
                ShowError($"{Properties.Resources.VmPage_DeleteFail}：{FriendlyError.CleanLines(ex.Message)}");
            }
            finally { IsLoading = false; }
        }

        // 批量删除（保留完整配置与磁盘）：确认 → 逐台删 → 聚合汇报 → 收拾选中项。
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
                var result = await VmBatchControlService.DeleteAsync(targets.Select(ToBatchTarget), false,
                    item => { if (item.Unregistered) { var vm = targets.First(vm => vm.Id == item.Target.Id); VmList.Remove(vm); vm.Dispose(); } });
                if (SelectedVm != null && !VmList.Contains(SelectedVm)) SelectedVm = VmList.FirstOrDefault();
                if (result.Failed == 0) ShowSuccess(string.Format(Properties.Resources.VmPage_MultiDeleteDone, result.Succeeded));
                else ShowError(string.Format(Properties.Resources.VmPage_MultiDeleteFail, result.Succeeded, result.Failed)
                    + Environment.NewLine + DescribeBatchDeletion(result));
            }
            finally { IsLoading = false; }
        }

        // 批量彻底删除仅展示名称清单，避免文件预览过长。
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
                var result = await VmBatchControlService.DeleteAsync(targets.Select(ToBatchTarget), true,
                    item => { if (item.Unregistered) { var vm = targets.First(vm => vm.Id == item.Target.Id); VmList.Remove(vm); vm.Dispose(); } });
                if (SelectedVm != null && !VmList.Contains(SelectedVm)) SelectedVm = VmList.FirstOrDefault();
                if (result.Failed == 0) ShowSuccess(string.Format(Properties.Resources.VmPage_MultiPurgeDone, result.Succeeded));
                else ShowError(string.Format(Properties.Resources.VmPage_MultiPurgeFail, result.Succeeded, result.Failed)
                    + Environment.NewLine + Environment.NewLine + DescribeBatchDeletion(result));
            }
            finally { IsLoading = false; }
        }

        [RelayCommand]
        private async Task PurgeVmAsync(VmInstanceViewModel vm)
        {
            if (IsMultiSelect) { await PurgeMultipleAsync(_selectedVms.ToList()); return; }
            if (vm == null) return;

            // 二次确认弹窗：预先算出"将删除的目录与文件"清单直接展示——替代口头提醒用户自己去查目录里有没有其他文件。
            var previewResult = await VmDeleteService.PreviewPurgeAsync(vm.Id);
            if (!previewResult.Success || !previewResult.HasData)
            {
                ShowError(FriendlyError.CleanLines(previewResult.Error ?? Properties.Resources.Error_Common_LoadFail));
                return;
            }
            var preview = previewResult.Data!;
            var listText = new System.Windows.Controls.TextBlock
            {
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
            };

            static bool IsHighlightedImage(string path)
            {
                string extension = System.IO.Path.GetExtension(path);
                return extension.Equals(".iso", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".vhd", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".vhdx", StringComparison.OrdinalIgnoreCase);
            }

            void AppendPreviewLine(string text, string? filePath = null)
            {
                var run = new System.Windows.Documents.Run(text);
                if (!string.IsNullOrEmpty(filePath) && IsHighlightedImage(filePath))
                    run.Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(232, 71, 86));
                listText.Inlines.Add(run);
                listText.Inlines.Add(new System.Windows.Documents.LineBreak());
            }
            var purgeFiles = preview.ConfigFiles
                .Concat(preview.DiskFiles)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (preview.DeletesConfigDirectory && !string.IsNullOrEmpty(preview.ConfigDirectory))
            {
                AppendPreviewLine("· " + preview.ConfigDirectory);
            }
            int shown = 0;
            foreach (string file in purgeFiles)
            {
                if (shown++ >= 40)
                {
                    AppendPreviewLine($"· … (+{purgeFiles.Count - 40})");
                    break;
                }

                bool nested = preview.DeletesConfigDirectory
                              && !string.IsNullOrEmpty(preview.ConfigDirectory)
                              && IsPathInside(file, preview.ConfigDirectory);
                string displayPath = nested
                    ? System.IO.Path.GetRelativePath(preview.ConfigDirectory!, file)
                    : file;
                AppendPreviewLine((nested ? "     · " : "· ") + displayPath, file);
            }
            if (listText.Inlines.Count == 0)
                AppendPreviewLine(vm.Name);

            if (preview.RetainedDiskFiles.Count > 0)
            {
                AppendPreviewLine(string.Empty);
                AppendPreviewLine(Properties.Resources.VmOperation_RetainedPaths);
                foreach (string file in preview.RetainedDiskFiles.Take(40))
                    AppendPreviewLine("· " + file);
                if (preview.RetainedDiskFiles.Count > 40)
                    AppendPreviewLine($"· … (+{preview.RetainedDiskFiles.Count - 40})");
            }
            foreach (string issue in preview.DiskDependencyIssues)
                AppendPreviewLine(FriendlyError.CleanLines(issue));

            static bool IsPathInside(string path, string root)
            {
                string fullPath = System.IO.Path.GetFullPath(path);
                string fullRoot = System.IO.Path.GetFullPath(root)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                return fullPath.StartsWith(
                    fullRoot + System.IO.Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }

            // 文件清单使用可滚动区域，避免长路径或大量文件扩大对话框。
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
                Padding = new System.Windows.Thickness(0, 0, 0, 8),
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Content = listText,
            });

            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = Properties.Resources.VmPage_PurgeTitle,
                Content = body,
                PrimaryButtonText = Properties.Resources.VmPage_PurgeBtn,
                PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Danger,   // 左侧确认按钮红色（危险操作）；右侧取消保持默认
                CloseButtonText = Properties.Resources.Button_Cancel,
            };
            ExHyperV.UI.Shared.Interaction.Dialogs.ForceDangerButtonWhiteForeground(dialog);   // Danger 主按钮亮色主题下红底黑字，强制刷白

            var result = await dialog.ShowDialogAsync();
            if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;

            IsLoading = true;
            try
            {
                var purge = await VmDeleteService.PurgeVmAsync(vm.Name, vm.Id);
                if (purge.RegistrationState == VmRegistrationState.Unregistered)
                {
                    VmList.Remove(vm);
                    vm.Dispose();
                    if (SelectedVm == vm) SelectedVm = VmList.FirstOrDefault();
                }
                if (purge.Success)
                {
                    ShowSuccess(string.Format(Properties.Resources.VmPage_PurgeDoneDesc, vm.Name));
                }
                else
                {
                    ShowError($"{Properties.Resources.VmPage_DeleteFail}：{DescribeDeletionResult(purge)}");
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
            ResetCpuAffinityState();
            SyncNavigationSelection();
            _originalMemorySettingsCache = null;
            _originalMmioSettingsCache = null;
            HostDisks.Clear();
            if (value == null) { CurrentViewType = VmDetailViewType.Dashboard; return; }
            IsCreatingVm = false;
            if (!IsExecutingVmImport) IsVmImportViewVisible = false;

            // 切 VM 时保留当前的无状态详情子页：重跑对应 GoTo 加载新 VM 的数据、停在同一子页(去 B 的对应详情页)；
            // 概览及其它一律回概览。进行中向导(AddGpu*/AddStorage)期间左侧列表已禁用，不会走到这里。
            switch (CurrentViewType)
            {
                case VmDetailViewType.CpuSettings: _ = GoToCpuSettingsCommand.ExecuteAsync(null); break;
                case VmDetailViewType.CpuAffinity: _ = GoToCpuAffinityCommand.ExecuteAsync(null); break;
                case VmDetailViewType.MemorySettings: _ = GoToMemorySettingsCommand.ExecuteAsync(null); break;
                case VmDetailViewType.StorageSettings: _ = GoToStorageSettingsCommand.ExecuteAsync(null); break;
                case VmDetailViewType.NetworkSettings: _ = GoToNetworkSettingsCommand.ExecuteAsync(null); break;
                case VmDetailViewType.BootSettings: _ = GoToBootSettingsCommand.ExecuteAsync(null); break;
                case VmDetailViewType.SpacetimeSettings: _ = GoToSpacetimeSettingsCommand.ExecuteAsync(null); break;
                case VmDetailViewType.Advanced: _ = GoToAdvancedSettingsCommand.ExecuteAsync(null); break;
                case VmDetailViewType.Security: _ = GoToSecuritySettingsCommand.ExecuteAsync(null); break;
                case VmDetailViewType.GpuSettings: _ = GoToGpuSettingsCommand.ExecuteAsync(null); break;
                case VmDetailViewType.PcieSettings: _ = GoToPcieSettingsCommand.ExecuteAsync(null); break;
                default:
                    CurrentViewType = VmDetailViewType.Dashboard;
                    _ = RefreshBootOrderForSelectedVmAsync(value);
                    break;
            }
        }


        // 把 Service 返回的 VmInstance(Model) 包成 live VM，并接上电源控制命令。
        // VmInstanceViewModel 构造函数已经从 Model 拷贝所有标量/集合（pass-through），无需重复 init。
        private VmInstanceViewModel CreateVmInstance(VmInstance snapshot)
        {
            var instance = new VmInstanceViewModel(snapshot);

            instance.ControlCommand = new AsyncRelayCommand<string>(async action =>
            {
                bool openConsoleOnStart = action == "Start" && !IsMultiSelect && SettingsService.GetOpenConsoleOnStart();
                try
                {
                    var operation = new VmPowerOperationSession(instance.Id, Enum.Parse<VmPowerAction>(action!), instance.Name);
                    await operation.RunAsync(ConfirmPowerRepairAsync, state => PresentPowerOperation(instance, state));
                    await CompletePowerPresentationAsync(instance, operation, openConsoleOnStart);
                }
                catch (Exception error)
                {
                    instance.ClearTransientState();
                    ShowError(FriendlyError.CleanLines(error.GetBaseException().Message));
                }
            }, _ => !_batchPowerBusy.Contains(instance.Id));
            return instance;
        }

        private Task<bool> ConfirmPowerRepairAsync(VmPowerRepairConfirmation confirmation) =>
            Dialogs.ShowConfirmAsync(confirmation.Title, confirmation.Message, confirmation.ConfirmText,
                Properties.Resources.Btn_Cancel, isDanger: true, showIcon: false,
                maxWidth: confirmation.Kind == VmPowerRepairKind.Gpu ? 340 : 360);

        private void PresentPowerOperation(VmInstanceViewModel instance, VmPowerOperationSession operation)
        {
            switch (operation.Stage)
            {
                case VmPowerOperationStage.ReadyToRetry:
                    ShowSuccess(operation.RepairNotice);
                    instance.SetTransientState(GetOptimisticText(operation.Action.ToString()));
                    break;
                case VmPowerOperationStage.Created:
                    instance.SetTransientState(GetOptimisticText(operation.Action.ToString()));
                    break;
                case VmPowerOperationStage.AwaitingConfirmation:
                case VmPowerOperationStage.Cancelled:
                    instance.ClearTransientState();
                    break;
                case VmPowerOperationStage.Failed:
                    instance.ClearTransientState();
                    ShowError(operation.RepairFailed ? operation.Error : FriendlyError.CleanLines(operation.Error));
                    break;
            }
        }

        private async Task CompletePowerPresentationAsync(VmInstanceViewModel instance,
            VmPowerOperationSession operation, bool openConsoleOnStart)
        {
            if (operation.Stage != VmPowerOperationStage.Completed) return;
            try
            {
                await SyncSingleVmStateAsync(instance);
                OnPowerActionSucceeded(instance, operation.Action.ToString(), openConsoleOnStart);
            }
            catch (Exception error)
            {
                instance.ClearTransientState();
                ShowError(FriendlyError.CleanLines(error.GetBaseException().Message));
            }
        }

        public IReadOnlyList<string> AvailableOsTypes => OsImages.SupportedTypes;

        private static string DescribeOperationResult(string? error, string? cleanupError,
            IEnumerable<string> retainedPaths, bool stateUnknown = false, string? recoveryPath = null)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(error)) lines.Add(FriendlyError.CleanLines(error));
            if (stateUnknown) lines.Add(Properties.Resources.VmOperation_StateUnknown);
            if (!string.IsNullOrWhiteSpace(cleanupError))
                lines.Add(string.Format(Properties.Resources.VmOperation_CleanupErrorFormat, FriendlyError.CleanLines(cleanupError)));
            if (!string.IsNullOrWhiteSpace(recoveryPath))
                lines.Add(string.Format(Properties.Resources.VmOperation_RecoveryPathFormat, recoveryPath));
            string[] paths = retainedPaths.Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Length > 0)
            {
                lines.Add(Properties.Resources.VmOperation_RetainedPaths);
                lines.AddRange(paths);
            }
            return string.Join(Environment.NewLine, lines);
        }

        private static string DescribeCreationResult(VmCreationResult result) =>
            DescribeOperationResult(result.Error, result.CleanupError, result.RetainedPaths,
                result.OutcomeUnknown || result.RegistrationState == VmRegistrationState.Unknown);

        private static string DescribeDeletionResult(VmDeletionResult result) =>
            DescribeOperationResult(result.Error, result.CleanupError, result.RetainedPaths,
                result.RegistrationState == VmRegistrationState.Unknown, result.RecoveryPath);

        private static string DescribeBatchDeletion(VmBatchDeleteResult result) =>
            string.Join(Environment.NewLine + Environment.NewLine, result.Items.Where(item => !item.Success)
                .Select(item => $"{item.Target.Name} ({item.Target.Id:D}){Environment.NewLine}{DescribeDeletionResult(item.Result)}"));

        [ObservableProperty] private string _inventoryWarning = string.Empty;

        private void ReportInventoryIssues(VmInventorySnapshot inventory)
        {
            InventoryWarning = inventory.IsComplete ? string.Empty
                : string.Format(Properties.Resources.VmInventory_IncompleteFormat,
                    string.Join(Environment.NewLine, inventory.Issues.Select(issue => issue.Error).Distinct()));
        }

        private static void PreserveUnavailableInventoryData(VmInstance fresh, VmInstanceViewModel previous,
            IReadOnlyList<VmQueryIssue> issues)
        {
            var unavailable = issues.Select(issue => issue.Component).ToHashSet(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(fresh.GpuName) &&
                (unavailable.Contains("GpuAssignments") || unavailable.Contains("PartitionableGpus") || unavailable.Contains("VideoControllerNames")))
            {
                fresh.GpuName = previous.GpuName;
            }
            if (unavailable.Contains("GuestIpAddresses"))
            {
                fresh.IpAddress = previous.IpAddress;
                foreach (var adapter in fresh.NetworkAdapters)
                {
                    var old = previous.NetworkAdapters.FirstOrDefault(item => item.DeviceId == adapter.DeviceId);
                    if (old != null) adapter.IpAddresses = old.IpAddresses?.ToList() ?? [];
                }
            }
            if (unavailable.Contains("SwitchNames"))
                foreach (var adapter in fresh.NetworkAdapters)
                {
                    var old = previous.NetworkAdapters.FirstOrDefault(item => item.DeviceId == adapter.DeviceId);
                    if (old != null && adapter.SwitchIdentity == null)
                        adapter.SwitchIdentity = old.SwitchIdentity;
                }
        }

        private static bool HasUnavailableGpuInventory(IReadOnlyList<VmQueryIssue> issues)
            => issues.Any(issue => issue.Component is "GpuAssignments" or "PartitionableGpus" or "VideoControllerNames");

        [RelayCommand]
        private async Task LoadVmsAsync()
        {
            if (_disposed) return;
            if (IsLoading && VmList.Count > 0) return;
            IsLoading = true;
            try
            {
                var inventory = (await _queryService.GetVmListAsync()).RequireData();
                if (_disposed) return;
                ReportInventoryIssues(inventory);
                Guid? selectedId = SelectedVm?.Id;
                var existing = VmList.ToDictionary(vm => vm.Id);
                var finalCollection = new ObservableCollection<VmInstanceViewModel>();
                foreach (var snapshot in inventory.VirtualMachines)
                {
                    if (existing.TryGetValue(snapshot.Id, out var vm))
                    {
                        PreserveUnavailableInventoryData(snapshot, vm, inventory.Issues);
                        vm.Apply(snapshot, skipNetworkAdapters: CurrentViewType == VmDetailViewType.NetworkSettings || IsLoadingSettings);
                        finalCollection.Add(vm);
                    }
                    else finalCollection.Add(CreateVmInstance(snapshot));
                }
                foreach (var removed in existing.Values.Where(vm => !finalCollection.Contains(vm))) removed.Dispose();
                VmList = finalCollection;

                ObserveCpuAffinityInventory(inventory.VirtualMachines);

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

                SelectedVm = VmList.FirstOrDefault(vm => vm.Id == selectedId) ?? VmList.FirstOrDefault();

                await RefreshGroupsAsync();
                StartMonitoring();
            }
            catch (Exception ex)
            {
                if (!_disposed) ShowError($"{Properties.Resources.Error_Common_LoadFail}：{FriendlyError.CleanLines(ex.Message)}");
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
            var vm = SelectedVm;
            try
            {
                if (!(await VmConsoleSupportService.IsConsoleSupportEnabledAsync(vm.Id)).RequireData())
                {
                    ShowTip(Properties.Resources.VmAdvanced_ConsoleDisabledHint);
                    return;
                }
                // 打开当前选中虚拟机的沉浸式控制台窗口（现走新的 RdpClientHost）
                Navigation.OpenConsoleWindow(vm.Id.ToString(), vm.Name);
            }
            catch (Exception ex)
            {
                ShowError(string.Format(Properties.Resources.VmPage_ErrConfigDirNotFound, ex.Message));
            }
        }

        // 修改操作系统分类
        [RelayCommand]
        private async Task ChangeOsTypeAsync(string newType)
        {
            if (SelectedVm == null || SelectedVm.OsType == newType) return;
            var vm = SelectedVm;
            string oldOsType = vm.OsType;
            vm.OsType = newType;
            var result = await VmEditService.SetVmOsTypeAsync(vm.Id, newType);
            if (!result.Success)
            {
                vm.OsType = oldOsType;
                ShowError($"{Properties.Resources.Error_Common_ModFailShort}：{FriendlyError.CleanLines(result.Error)}");
            }
        }



        private void StartMonitoring()
        {
            if (_disposed || _monitoringTasks.Length != 0) return;
            // 缩略图刷新独立运行，避免阻塞状态同步。
            _monitoringTasks = [
                Task.Run(() => MonitorCpuLoop(_monitoringToken)),
                Task.Run(() => MonitorStateLoop(_monitoringToken)),
                Task.Run(() => MonitorThumbnailLoop(_monitoringToken))
            ];
        }

        private async Task MonitorCpuLoop(CancellationToken token)
        {
            if (token.IsCancellationRequested) return;
            HyperVCpuMonitorService cpuService;
            try { cpuService = new HyperVCpuMonitorService(); } catch { return; }
            await using var ownedCpuService = cpuService;
            while (!token.IsCancellationRequested)
            {
                try { var rawData = cpuService.GetCpuUsage(); await ApplyMonitorUpdateAsync(() => ProcessAndApplyCpuUpdates(rawData), token); await Task.Delay(1000, token); }
                catch (TaskCanceledException) { break; }
                catch { await Task.Delay(5000, token); }
            }
        }

        // 虚拟机状态与性能数据同步循环
        private async Task MonitorStateLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var inventory = (await _queryService.GetVmListAsync()).RequireData();
                    var updates = inventory.VirtualMachines;
                    token.ThrowIfCancellationRequested();
                    await _queryService.UpdateNetworkPerformanceAsync(updates);
                    var memoryResult = await _queryService.GetVmRuntimeMemoryDataAsync();
                    var memoryMap = memoryResult.Success && memoryResult.HasData ? memoryResult.Data : null;

                    var snapshots = updates.ToArray();
                    await _queryService.UpdateDiskPerformanceAsync(snapshots);
                    token.ThrowIfCancellationRequested();
                    // 分配信息不完整时无法判断“没有 GPU”；本轮不据此清除旧采样。
                    var gpuUsageMap = HasUnavailableGpuInventory(inventory.Issues)
                        ? new Dictionary<Guid, VmQueryService.GpuUsageData>()
                        : await _queryService.GetGpuPerformanceAsync(snapshots);

                    await ApplyMonitorUpdateAsync(() => {
                        ReportInventoryIssues(inventory);
                        bool needsResort = false;

                        var updateIds = updates.Select(u => u.Id).ToHashSet();
                        for (int i = VmList.Count - 1; i >= 0; i--)
                        {
                            if (!updateIds.Contains(VmList[i].Id))
                            {
                                if (SelectedVm == VmList[i]) SelectedVm = null;
                                VmList[i].Dispose();
                                VmList.RemoveAt(i);
                                needsResort = true;
                            }
                        }

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
                                PreserveUnavailableInventoryData(update, vm, inventory.Issues);
                                vm.Apply(update, skipNameUpdate, skipNetworkAdapters);
                                if (wasRunning != vm.IsRunning) needsResort = true;

                                // Core decides address priority; the page only schedules refresh and applies results.
                                if (vm.IsRunning)
                                {
                                    foreach (var adapter in vm.NetworkAdapters)
                                    {
                                        var resolution = VmIpService.ResolveAdapterAddresses(adapter.MacAddress, adapter.IpAddresses);
                                        if (adapter.IpAddresses == null || !adapter.IpAddresses.SequenceEqual(resolution.Addresses))
                                            adapter.IpAddresses = resolution.Addresses;
                                        if (!resolution.RequiresLookup) continue;
                                        string lookupKey = $"{vm.Id}|{adapter.MacAddress}";
                                        if (!_ipLookupsInFlight.TryAdd(lookupKey, 0)) continue;
                                        _ipLookupTasks.RemoveAll(task => task.IsCompleted);
                                        _ipLookupTasks.Add(Task.Run(async () => {
                                            try
                                            {
                                                var ipResult = await VmIpService.LookupAsync(vm.Id, adapter.MacAddress);
                                                string arpIp = ipResult.Success && ipResult.HasData ? ipResult.Data : string.Empty;
                                                if (!string.IsNullOrEmpty(arpIp))
                                                    await ApplyMonitorUpdateAsync(() => {
                                                        adapter.IpAddresses = VmIpService.ResolveAdapterAddresses(
                                                            adapter.MacAddress, adapter.IpAddresses, arpIp).Addresses;
                                                        vm.IpAddress = VmIpService.GetPrimaryAddress(vm.NetworkAdapters, vm.IpAddress);
                                                    }, token);
                                            }
                                            catch { }
                                            finally { _ipLookupsInFlight.TryRemove(lookupKey, out _); }
                                        }));
                                    }

                                    vm.IpAddress = VmIpService.GetPrimaryAddress(vm.NetworkAdapters, vm.IpAddress);
                                }
                                // Apply 已处理 !IsRunning 时 vm.IpAddress = "---"

                                // PageVM-only side effect 2：从 memoryMap 应用动态内存数据
                                if (memoryMap != null && memoryMap.TryGetValue(vm.Id, out var memData))
                                    vm.UpdateMemoryStatus(memData);
                            }
                        }
                        foreach (var vm in VmList)
                        {
                            if (gpuUsageMap.TryGetValue(vm.Id, out var gpuData))
                                vm.UpdateGpuStats(gpuData);
                        }

                        if (needsResort)
                        {
                            CollectionViewSource.GetDefaultView(VmList)?.Refresh();
                        }
                        ObserveCpuAffinityInventory(updates);
                    }, token);

                    // 后台线程：先快照 SelectedVm 再用，避免与 UI 线程改选中项竞态导致 NRE
                    var selForDisk = SelectedVm;
                    if (selForDisk != null && selForDisk.IsRunning)
                    {
                        await VmStoragePresentation.RefreshVirtualDiskSizesAsync(selForDisk, token);
                    }

                    await Task.Delay(2000, token);
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MonitorLoop Error] {ex.Message}");
                    await ApplyMonitorUpdateAsync(() => InventoryWarning =
                        $"{Properties.Resources.Error_Common_LoadFail}：{FriendlyError.CleanLines(ex.Message)}", token);
                    await Task.Delay(3000, token);
                }
            }
        }        // 同步单个虚拟机的最新状态
        private async Task SyncSingleVmStateAsync(VmInstanceViewModel vm)
        {
            var inventory = (await _queryService.GetVmListAsync()).RequireData();
            var freshData = inventory.VirtualMachines.FirstOrDefault(x => x.Id == vm.Id);
            if (freshData != null)
            {
                WpfApplication.Current.Dispatcher.Invoke(() => {
                    ReportInventoryIssues(inventory);
                    PreserveUnavailableInventoryData(freshData, vm, inventory.Issues);
                    vm.Apply(freshData);
                    ObserveCpuAffinityRuntime(freshData);
                });
            }
        }

        private void ProcessAndApplyCpuUpdates(List<VmCpuCoreMetric> rawData)
        {
            foreach (var group in rawData.Where(metric => !metric.IsHost && metric.VmId.HasValue)
                         .GroupBy(metric => metric.VmId!.Value))
            {
                var vm = VmList.FirstOrDefault(item => item.Id == group.Key);
                if (vm == null || !vm.IsRunning || vm.CpuCount <= 0) continue;
                var metrics = group.OrderBy(metric => metric.CoreId).ToList();
                // 不以不完整的一组核心样本重算平均值，也不把缺测画成新采样。
                if (metrics.Count != vm.CpuCount
                    || !metrics.Select(metric => metric.CoreId).SequenceEqual(Enumerable.Range(0, vm.CpuCount))
                    || metrics.Any(metric => metric.Sample?.IsValid != true || !float.IsFinite(metric.Usage))) continue;
                vm.AverageUsage = metrics.Average(metric => metric.Usage);
                UpdateVmCores(vm, metrics);
            }
        }
        private void UpdateVmCores(VmInstanceViewModel vm, List<VmCpuCoreMetric> metrics) { var metricIds = metrics.Select(m => m.CoreId).ToHashSet(); vm.Cores.Where(c => !metricIds.Contains(c.CoreId)).ToList().ForEach(r => vm.Cores.Remove(r)); foreach (var metric in metrics) { var core = vm.Cores.FirstOrDefault(c => c.CoreId == metric.CoreId); if (core == null) { core = new VmCoreItem { CoreId = metric.CoreId }; int idx = 0; while (idx < vm.Cores.Count && vm.Cores[idx].CoreId < metric.CoreId) idx++; vm.Cores.Insert(idx, core); } core.Usage = metric.Usage; UpdateHistory(vm.Id, core); } vm.Columns = GridLayoutMath.CalculateOptimalColumns(vm.Cores.Count); vm.Rows = (vm.Cores.Count > 0) ? (int)Math.Ceiling((double)vm.Cores.Count / vm.Columns) : 1; }
        private void UpdateHistory(Guid vmId, VmCoreItem core) { string key = $"{vmId:D}_{core.CoreId}"; if (!_historyCache.TryGetValue(key, out var history)) { history = new LinkedList<double>(); for (int k = 0; k < MaxHistoryLength; k++) history.AddLast(0); _historyCache[key] = history; } history.AddLast(core.Usage); if (history.Count > MaxHistoryLength) history.RemoveFirst(); core.HistoryPoints = CalculatePoints(history); }
        private PointCollection CalculatePoints(LinkedList<double> history) { double w = 100.0, h = 100.0, step = w / (MaxHistoryLength - 1); var points = new PointCollection(MaxHistoryLength + 2) { new Point(0, h) }; int i = 0; foreach (var val in history) points.Add(new Point(i++ * step, h - (val * h / 100.0))); points.Add(new Point(w, h)); points.Freeze(); return points; }



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
                    var img = await VmScreenshotService.CaptureAsync(sel.Id, 320, 240);
                    if (img != null)
                    {
                        await ApplyMonitorUpdateAsync(() => sel.Thumbnail = img, token);
                    }
                }
                else if (sel != null && !sel.IsRunning && sel.Thumbnail != null)
                {
                    await ApplyMonitorUpdateAsync(() => sel.Thumbnail = null, token);
                }

                // 降低缩略图刷新频率以减少 WMI 开销。
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
