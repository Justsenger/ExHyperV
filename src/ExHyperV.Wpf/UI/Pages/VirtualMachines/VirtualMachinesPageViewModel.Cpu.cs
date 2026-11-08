using ExHyperV.UI.Shared.Behaviors;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.UI.Pages.VirtualMachines
{
    public partial class VirtualMachinesPageViewModel
    {
        public ObservableCollection<int> PossibleVCpuCounts { get; private set; } = new();
        [ObservableProperty] private ObservableCollection<VmCoreItem> _affinityHostCores = new();
        [ObservableProperty] private int _affinityColumns = 8;
        [ObservableProperty] private int _affinityRows = 1;
        [ObservableProperty] private string? _affinityCpuModel;
        [ObservableProperty] private string _affinitySelectionDescription = string.Empty;
        private Guid? _affinityLoadedVmId;
        private long _affinityViewVersion;
        // 未确认的后台操作不会因切页或读回当前设置而结束；限制保留至本 ViewModel 释放。
        private readonly HashSet<Guid> _affinityUnknownVmIds = new();
        private readonly HashSet<Guid> _affinityObservedRunningVmIds = new();
        private readonly HashSet<Guid> _affinityRestoreInFlight = new();
        private readonly HashSet<Guid> _affinityRestorePending = new();
        private readonly List<Task> _affinityRestoreTasks = new();

        // 新增 CPU 字段的枚举下拉源（绑 ComboBox.ItemsSource）
        public Array SmtModeValues { get; } = Enum.GetValues(typeof(SmtMode));
        public Array MigrationCompatibilityModeValues { get; } = Enum.GetValues(typeof(VmMigrationCompatibilityMode));
        public Array ApicModeValues { get; } = Enum.GetValues(typeof(VmApicMode));
        public Array L3DistributionPolicyValues { get; } = Enum.GetValues(typeof(L3DistributionPolicy));
        public Array PageShatterModeValues { get; } = Enum.GetValues(typeof(PageShatterMode));
        public Array LpiModeValues { get; } = Enum.GetValues(typeof(LpiMode));
        // 能力门控标志（按主机硬件或 Hyper-V 属性支持情况置灰）
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowIntelPlatformFeatures))]
        private bool _isIntelHost;
        [ObservableProperty] private bool _isHwIsolationSupported;
        public bool IsArm64Host { get; } = VmProcessorService.IsArm64Host;
        public bool IsX64Host => !IsArm64Host;
        public bool ShowIntelPlatformFeatures => IsIntelHost;
        public bool ShowArm64PlatformFeatures => IsArm64Host;
        public bool ShowX64PlatformFeatures => IsX64Host;



        // 初始化可能的 vCPU 数量选项
        private void InitPossibleCpuCounts()
        {
            PossibleVCpuCounts = new ObservableCollection<int>(
                VmProcessorService.GetProcessorCountOptions(Environment.ProcessorCount));
            LoadHostPlatformAsync().SafeFireAndForget();
        }

        private async Task LoadHostPlatformAsync()
        {
            var capabilities = (await VmProcessorService.GetHostCapabilitiesAsync()).RequireData();
            IsIntelHost = capabilities.IsIntel;
            IsHwIsolationSupported = capabilities.HardwareIsolationSupported;
        }

        [RelayCommand]
        private async Task GoToCpuSettingsAsync()
        {
            if (SelectedVm == null) return;
            var vm = SelectedVm;
            CurrentViewType = VmDetailViewType.CpuSettings;
            IsLoadingSettings = true;
            try
            {
                await LoadHostPlatformAsync();
                var settings = (await VmProcessorService.GetVmProcessorAsync(vm.Id)).RequireData();
                if (SelectedVm == vm)
                {
                    vm.Processor = settings;
                }
            }
            catch (Exception ex) { ShowError($"{Properties.Resources.Error_Common_LoadFail}：{FriendlyError.CleanLines(ex.Message)}"); }
            finally
            {
                await Task.Delay(200);
                IsLoadingSettings = false;
            }
        }

        [RelayCommand]
        private async Task ApplyChangesAsync()
        {
            if (IsLoadingSettings || SelectedVm?.Processor == null) return;
            // 离开 CPU 设置页时，页内 ComboBox(EventToCommand SelectionChanged)/ToggleSwitch(Command Toggled)
            // 会在卸载瞬间被误触发并打到本命令；运行态下这会下发整个 Processor 而被 Hyper-V 拒("无法修改 Processor")。
            // 仅当仍停留在 CPU 设置页时才执行，挡掉一切导航离开后的卸载误触发。
            if (CurrentViewType != VmDetailViewType.CpuSettings) return;
            var vm = SelectedVm;
            var settings = vm.Processor;
            IsLoadingSettings = true;
            try
            {
                var result = await VmProcessorService.SetVmProcessorAsync(vm.Id, settings);
                if (!result.Success)
                {
                    ShowError($"{Properties.Resources.Error_Common_ApplyFail}：{FriendlyError.CleanLines(result.Error)}");
                    if (SelectedVm == vm) await GoToCpuSettingsAsync();
                }
            }
            catch (Exception ex)
            {
                ShowError(FriendlyError.CleanLines(ex.Message));
                if (SelectedVm == vm) await GoToCpuSettingsAsync();
            }
            finally { IsLoadingSettings = false; }
        }

        private void ResetCpuAffinityState()
        {
            _affinityViewVersion++;
            _affinityLoadedVmId = null;
            AffinityHostCores.Clear();
            AffinityCpuModel = null;
            AffinitySelectionDescription = Properties.Resources.Label_SelectCores;
            if (CurrentViewType == VmDetailViewType.CpuAffinity)
                IsLoadingSettings = false;
            SaveAffinityCommand.NotifyCanExecuteChanged();
        }

        partial void OnCurrentViewTypeChanging(VmDetailViewType value)
        {
            if (CurrentViewType == VmDetailViewType.CpuAffinity && value != VmDetailViewType.CpuAffinity)
                ResetCpuAffinityState();
        }

        private bool IsCurrentAffinityView(VmInstanceViewModel vm, long version) =>
            !_disposed && version == _affinityViewVersion && ReferenceEquals(SelectedVm, vm)
            && CurrentViewType == VmDetailViewType.CpuAffinity;

        private bool CanSaveAffinity() =>
            !_disposed && !IsLoadingSettings && CurrentViewType == VmDetailViewType.CpuAffinity
            && SelectedVm != null && _affinityLoadedVmId == SelectedVm.Id
            && !_affinityUnknownVmIds.Contains(SelectedVm.Id);

        private void BlockAffinitySaveForSession(Guid vmId)
        {
            _affinityUnknownVmIds.Add(vmId);
            SaveAffinityCommand.NotifyCanExecuteChanged();
        }

        [RelayCommand]
        private async Task GoToCpuAffinityAsync()
        {
            ResetCpuAffinityState();
            var vm = SelectedVm;
            if (vm == null) return;
            long version = _affinityViewVersion;
            CurrentViewType = VmDetailViewType.CpuAffinity;
            IsLoadingSettings = true;
            if (_affinityUnknownVmIds.Contains(vm.Id))
                ShowTip(Properties.Resources.Error_Cpu_AffinityOutcomeUnknown);

            try
            {
                var systemInfo = await HostSystemInfoService.GetSystemInfoAsync();
                if (!IsCurrentAffinityView(vm, version)) return;

                var available = (await CpuAffinityService.GetAvailableProcessorsAsync()).RequireData();
                int totalCores = available.Count;
                var affinity = (await CpuAffinityService.ReadStateAsync(vm.Id)).RequireData();
                var currentAffinity = affinity.AppliedProcessors ?? affinity.SavedProcessors ?? Array.Empty<int>();
                if (!IsCurrentAffinityView(vm, version)) return;

                var coresList = new List<VmCoreItem>();
                foreach (var processor in available)
                {
                    coresList.Add(new VmCoreItem
                    {
                        CoreId = processor.Index,
                        IsSelected = currentAffinity.Contains(processor.Index),
                        CoreType = processor.CoreType
                    });
                }
                int bestCols = 4;
                if (totalCores <= 4)
                {
                    bestCols = totalCores;
                }
                else
                {
                    double minPenalty = double.MaxValue;
                    for (int c = 4; c <= 10; c++)
                    {
                        int r = (int)Math.Ceiling((double)totalCores / c);
                        int remainder = (c - (totalCores % c)) % c;
                        double wasteScore = (double)remainder / c;
                        double aspect = (double)c / r;
                        double aspectScore = Math.Abs(aspect - 1.5);
                        double totalPenalty = (wasteScore * 2.0) + aspectScore;

                        if (totalPenalty < minPenalty)
                        {
                            minPenalty = totalPenalty;
                            bestCols = c;
                        }
                    }
                }

                AffinityCpuModel = systemInfo.CpuModel.Split(" @", 2, StringSplitOptions.None)[0];
                AffinitySelectionDescription = affinity.AppliedProcessors != null
                    ? Properties.Resources.Label_Cpu_AffinityAppliedSource
                    : Properties.Resources.Label_Cpu_AffinitySavedSource;
                AffinityHostCores = new ObservableCollection<VmCoreItem>(coresList);
                AffinityColumns = bestCols;
                AffinityRows = (int)Math.Ceiling((double)totalCores / AffinityColumns);
                _affinityLoadedVmId = vm.Id;
            }
            catch (Exception ex)
            {
                if (IsCurrentAffinityView(vm, version))
                    ShowError($"{Properties.Resources.Error_Cpu_AffinityFail}：{FriendlyError.CleanLines(ex.Message)}");
            }
            finally
            {
                if (IsCurrentAffinityView(vm, version))
                {
                    IsLoadingSettings = false;
                    SaveAffinityCommand.NotifyCanExecuteChanged();
                }
            }
        }

        [RelayCommand(CanExecute = nameof(CanSaveAffinity))]
        private async Task SaveAffinityAsync()
        {
            if (!CanSaveAffinity()) return;
            var vm = SelectedVm;
            long version = _affinityViewVersion;
            var selectedIndices = AffinityHostCores.Where(c => c.IsSelected).Select(c => c.CoreId).ToList();
            IsLoadingSettings = true;
            try
            {
                var result = await CpuAffinityService.SaveAsync(vm.Id, selectedIndices);
                bool outcomeUnknown = result.ErrorKind == ApiErrorKind.OutcomeUnknown
                    || result.CleanupErrorKind == ApiErrorKind.OutcomeUnknown;
                // 即使用户已切到另一页面或 VM，也必须记住原 VM 的未确认操作。
                if (outcomeUnknown)
                    BlockAffinitySaveForSession(vm.Id);
                if (!IsCurrentAffinityView(vm, version)) return;
                if (outcomeUnknown)
                {
                    ShowError(FormatAffinityError(Properties.Resources.Error_Cpu_AffinityOutcomeUnknown, result));
                    return;
                }
                if (result.Status != CpuAffinityService.SaveStatus.Failed && !result.MetadataSaved)
                {
                    ShowError(FormatAffinityError(Properties.Resources.Error_Common_SaveFail, result));
                    await GoToCpuAffinityAsync();
                    return;
                }

                if (result.Status == CpuAffinityService.SaveStatus.Applied)
                {
                    ShowSuccess(Properties.Resources.Msg_Cpu_AffinityApplied);
                    await GoToCpuSettingsAsync();
                }
                else
                {
                    if (result.Status == CpuAffinityService.SaveStatus.Queued)
                    {
                        ShowTip($"{Properties.Resources.Msg_Cpu_AffinityQueued}：{Properties.Resources.Msg_Cpu_RootNotice}");
                        await GoToCpuSettingsAsync();
                    }
                    else
                    {
                        ShowError(FormatAffinityError(Properties.Resources.Error_Cpu_ApplyFail, result));
                        await GoToCpuAffinityAsync();
                    }
                }
            }
            catch (CoreOperationException ex) when (ex.Result.ErrorKind == ApiErrorKind.OutcomeUnknown)
            {
                BlockAffinitySaveForSession(vm.Id);
                if (IsCurrentAffinityView(vm, version))
                    ShowError($"{Properties.Resources.Error_Cpu_AffinityOutcomeUnknown}{Environment.NewLine}{FriendlyError.CleanLines(ex.Message)}");
            }
            catch (Exception ex)
            {
                if (IsCurrentAffinityView(vm, version))
                    ShowError(FriendlyError.CleanLines(ex.Message));
            }
            finally
            {
                if (IsCurrentAffinityView(vm, version))
                    IsLoadingSettings = false;
            }
        }

        private static string FormatAffinityError(string title, CpuAffinityService.SaveResult result)
        {
            var lines = new List<string> { title };
            if (!string.IsNullOrWhiteSpace(result.Error)) lines.Add(result.Error);
            if (!string.IsNullOrWhiteSpace(result.CleanupError)) lines.Add(result.CleanupError);
            if (result.RetainedGroupId is Guid retained) lines.Add($"CPU Group: {retained:D}");
            return FriendlyError.CleanLines(string.Join(Environment.NewLine, lines));
        }


        private void ObserveCpuAffinityInventory(IReadOnlyList<VmInstance> snapshots)
        {
            var present = snapshots.Select(vm => vm.Id).ToHashSet();
            _affinityObservedRunningVmIds.RemoveWhere(id => !present.Contains(id));
            _affinityRestorePending.RemoveWhere(id => !present.Contains(id));
            foreach (var snapshot in snapshots)
                ObserveCpuAffinityRuntime(snapshot);
        }

        private void ObserveCpuAffinityRuntime(VmInstance snapshot)
        {
            if (_disposed) return;
            // 暂停和过渡态不结束一次运行周期；恢复同一进程时不重放保存的偏好。
            if (snapshot.State is VmState.PoweredOff or VmState.Saved or VmState.FastSaved or VmState.Hibernated)
            {
                _affinityObservedRunningVmIds.Remove(snapshot.Id);
                _affinityRestorePending.Remove(snapshot.Id);
                return;
            }
            if (snapshot.State != VmState.Running || !_affinityObservedRunningVmIds.Add(snapshot.Id)) return;
            ScheduleCpuAffinityRestore(snapshot.Id, snapshot.Name);
        }

        private void ScheduleCpuAffinityRestore(Guid vmId, string vmName)
        {
            if (_disposed || _monitoringToken.IsCancellationRequested || _affinityUnknownVmIds.Contains(vmId)) return;
            if (!_affinityRestoreInFlight.Add(vmId))
            {
                // 旧请求尚在结束时又观察到下一次启动，只保留一个后续请求。
                _affinityRestorePending.Add(vmId);
                return;
            }
            _affinityRestorePending.Remove(vmId);
            _affinityRestoreTasks.RemoveAll(task => task.IsCompleted);
            _affinityRestoreTasks.Add(Task.Run(() => RestoreCpuAffinityAfterStartAsync(vmId, vmName, _monitoringToken)));
        }

        private async Task RestoreCpuAffinityAfterStartAsync(Guid vmId, string vmName, CancellationToken token)
        {
            CpuAffinityService.StartupApplyStatus? status = null;
            Exception? failure = null;
            try
            {
                status = await CpuAffinityService.ApplySavedAffinityAfterStartAsync(vmId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) { failure = error; }

            try
            {
                await ApplyMonitorUpdateAsync(() =>
                {
                    _affinityRestoreInFlight.Remove(vmId);
                    if (failure is CoreOperationException operation && operation.Result.ErrorKind == ApiErrorKind.OutcomeUnknown)
                        BlockAffinitySaveForSession(vmId);

                    string? error = failure?.Message;
                    if (failure == null)
                    {
                        error = status switch
                        {
                            CpuAffinityService.StartupApplyStatus.Exhausted => Properties.Resources.Error_Cpu_AffinityRestoreExhausted,
                            CpuAffinityService.StartupApplyStatus.InvalidConfiguration => Properties.Resources.Error_Cpu_AffinityPreferenceInvalid,
                            CpuAffinityService.StartupApplyStatus.Applied
                                or CpuAffinityService.StartupApplyStatus.NotRequired
                                or CpuAffinityService.StartupApplyStatus.VmStopped
                                or CpuAffinityService.StartupApplyStatus.Superseded => null,
                            _ => status?.ToString()
                        };
                    }
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        if (_affinityUnknownVmIds.Contains(vmId))
                            error = $"{Properties.Resources.Error_Cpu_AffinityOutcomeUnknown}{Environment.NewLine}{error}";
                        ShowError(string.Format(Properties.Resources.Error_Cpu_AffinityRestoreFailedFormat,
                            vmName, FriendlyError.CleanLines(error)));
                    }

                    if (_affinityRestorePending.Remove(vmId) && _affinityObservedRunningVmIds.Contains(vmId))
                        ScheduleCpuAffinityRestore(vmId, vmName);
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }
}
