using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public partial class VirtualMachinesPageViewModel
    {
        // ===== 视图模型属性 - CPU 设置 =====
        public ObservableCollection<int> PossibleVCpuCounts { get; private set; } = new();
        [ObservableProperty] private ObservableCollection<VmCoreItem> _affinityHostCores = new();
        [ObservableProperty] private int _affinityColumns = 8;
        [ObservableProperty] private int _affinityRows = 1;
        [ObservableProperty] private string? _affinityCpuModel;

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
        public bool IsArm64Host { get; } = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        public bool IsX64Host => !IsArm64Host;
        public bool ShowIntelPlatformFeatures => IsIntelHost;
        public bool ShowArm64PlatformFeatures => IsArm64Host;
        public bool ShowX64PlatformFeatures => IsX64Host;
        private bool _cpuCapsInit;


        // ===== CPU 设置与亲和性模块 =====

        // 初始化可能的 vCPU 数量选项
        private void InitPossibleCpuCounts()
        {
            var options = new HashSet<int>();
            int maxCores = Environment.ProcessorCount;
            int current = 1;
            while (current <= maxCores) { options.Add(current); current *= 2; }
            options.Add(maxCores);
            PossibleVCpuCounts = new ObservableCollection<int>(options.OrderBy(x => x));
            LoadHostPlatformAsync().SafeFireAndForget();
        }

        private async Task LoadHostPlatformAsync()
        {
            IsIntelHost = await HostPlatformService.GetNativeHostPlatformAsync() == HostPlatform.Intel;
        }

        // 导航至 CPU 设置页面
        [RelayCommand]
        private async Task GoToCpuSettingsAsync()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.CpuSettings;
            IsLoadingSettings = true;
            try
            {
                await LoadHostPlatformAsync();
                if (!_cpuCapsInit)
                {
                    _cpuCapsInit = true;
                    try
                    {
                        var iso = await VmCreateService.GetIsolationSupportAsync();
                        IsHwIsolationSupported = iso.Supported && (iso.Types.Contains("SNP") || iso.Types.Contains("TDX"));
                    }
                    catch { }
                }
                var settings = await VmProcessorService.GetVmProcessorAsync(SelectedVm.Name);
                if (settings != null)
                {
                    SelectedVm.Processor = settings;
                }
            }
            catch (Exception ex) { ShowError($"{Properties.Resources.Error_Common_LoadFail}：{FriendlyError.CleanLines(ex.Message)}"); }
            finally
            {
                await Task.Delay(200);
                IsLoadingSettings = false;
            }
        }

        // 应用 CPU 设置更改
        [RelayCommand]
        private async Task ApplyChangesAsync()
        {
            if (IsLoadingSettings || SelectedVm?.Processor == null) return;
            // 离开 CPU 设置页时，页内 ComboBox(EventToCommand SelectionChanged)/ToggleSwitch(Command Toggled)
            // 会在卸载瞬间被误触发并打到本命令；运行态下这会下发整个 Processor 而被 Hyper-V 拒("无法修改 Processor")。
            // 仅当仍停留在 CPU 设置页时才执行，挡掉一切导航离开后的卸载误触发。
            if (CurrentViewType != VmDetailViewType.CpuSettings) return;
            IsLoadingSettings = true;
            try
            {
                var result = await Task.Run(() => VmProcessorService.SetVmProcessorAsync(SelectedVm.Name, SelectedVm.Processor));
                if (!result.Success)
                {
                    ShowError($"{Properties.Resources.Error_Common_ApplyFail}：{FriendlyError.CleanLines(result.Message)}");
                    await GoToCpuSettingsAsync();
                }
            }
            catch (Exception ex)
            {
                ShowError(FriendlyError.CleanLines(ex.Message));
                await GoToCpuSettingsAsync();
            }
            finally { IsLoadingSettings = false; }
        }

        // 导航至 CPU 亲和性页面
        [RelayCommand]
        private async Task GoToCpuAffinityAsync()
        {
            if (SelectedVm == null) return;
            CurrentViewType = VmDetailViewType.CpuAffinity;
            IsLoadingSettings = true;

            try
            {
                var systemInfo = await SystemInfoService.GetSystemInfoAsync();
                AffinityCpuModel = systemInfo.CpuModel.Split(" @", 2, StringSplitOptions.None)[0];

                int totalCores = Environment.ProcessorCount;
                var currentAffinity = await CpuAffinityService.GetCpuAffinityAsync(SelectedVm.Id, SelectedVm.Notes);

                var coresList = new List<VmCoreItem>();
                for (int i = 0; i < totalCores; i++)
                {
                    coresList.Add(new VmCoreItem
                    {
                        CoreId = i,
                        IsSelected = currentAffinity.Contains(i),
                        CoreType = CpuMonitorService.GetCoreType(i)
                    });
                }
                AffinityHostCores = new ObservableCollection<VmCoreItem>(coresList);

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

                AffinityColumns = bestCols;
                AffinityRows = (int)Math.Ceiling((double)totalCores / AffinityColumns);
            }
            catch (Exception ex)
            {
                ShowError($"{Properties.Resources.Error_Cpu_AffinityFail}：{FriendlyError.CleanLines(ex.Message)}");
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 保存亲和性设置
        [RelayCommand]
        private async Task SaveAffinityAsync()
        {
            if (SelectedVm == null || AffinityHostCores == null) return;
            IsLoadingSettings = true;
            try
            {
                // 1. 获取用户选中的核心索引列表
                var selectedIndices = AffinityHostCores.Where(c => c.IsSelected).Select(c => c.CoreId).ToList();

                // 2. 调用服务应用设置 (内部会自动判断调度器类型)
                bool success = await CpuAffinityService.SetCpuAffinityAsync(SelectedVm.Id, selectedIndices, SelectedVm.IsRunning);

                // Root 调度器在虚拟机未运行时无法立即设置 vmmem 进程亲和性，
                // 此时将选择保存为待执行配置，供下次启动后自动应用。
                // 其余失败不覆盖 Notes，避免界面显示一组实际未生效的绑定。
                var scheduler = HyperVSchedulerService.GetSchedulerType();
                bool queueForRootStartup = scheduler == HyperVSchedulerType.Root && !SelectedVm.IsRunning;
                if (success || queueForRootStartup)
                {
                    string affinityStr = selectedIndices.Count > 0 ? string.Join(",", selectedIndices) : "";
                    SelectedVm.Notes = NotesTag.Update(SelectedVm.Notes, "Affinity", affinityStr);
                    await _queryService.SetVmNotesAsync(SelectedVm.Name, SelectedVm.Notes);
                }

                if (success)
                {
                    ShowSuccess(Properties.Resources.Msg_Cpu_AffinityApplied);
                    await GoToCpuSettingsAsync();
                }
                else
                {
                    // 如果是因为 Root 模式未开机导致无法实时应用
                    if (queueForRootStartup)
                    {
                        ShowTip($"{Properties.Resources.Msg_Cpu_AffinityQueued}：{Properties.Resources.Msg_Cpu_RootNotice}");
                        await GoToCpuSettingsAsync();
                    }
                    else
                    {
                        ShowError(Properties.Resources.Error_Cpu_ApplyFail);
                    }
                }
            }
            catch (Exception ex)
            {
                ShowError(FriendlyError.CleanLines(ex.Message));
            }
            finally
            {
                IsLoadingSettings = false;
            }
        }

        // 自动应用亲和性

        private void TryApplyAffinityForRootScheduler(VmInstanceViewModel vm)
        {
            // 仅针对 Root 调度器且虚拟机正在运行的情况
            if (HyperVSchedulerService.GetSchedulerType() != HyperVSchedulerType.Root || !vm.IsRunning)
                return;

            string savedAffinity = NotesTag.Get(vm.Notes, "Affinity");
            if (string.IsNullOrEmpty(savedAffinity))
                return;

            // 异步执行，避免阻塞 UI
            _ = Task.Run(async () =>
            {
                try
                {
                    var coreIds = savedAffinity.Split(',')
                                             .Select(s => int.Parse(s.Trim()))
                                             .ToList();

                    // 尝试多次，因为 vmmem 进程可能启动较慢，或者为了确保应用成功
                    // 如果是软件刚启动检测到虚拟机已运行，通常一次就能成功，但保留重试机制更稳健
                    for (int i = 0; i < 5; i++)
                    {
                        // 如果是刚启动 VM，进程可能还没出来，等待一下；如果是已运行，这个等待不影响
                        if (i == 0) await Task.Delay(1000);
                        else await Task.Delay(2000);

                        // 再次检查是否还在运行，防止中途关机
                        if (!vm.IsRunning) break;

                        // 应用亲和性到 vmmem 进程
                        bool success = CpuAffinityService.TrySetVmmemAffinity(vm.Id, coreIds);
                        if (success)
                        {
                            Debug.WriteLine(string.Format(Properties.Resources.VmPage_AffinityApplied, vm.Name));
                            break;
                        }
                        Debug.WriteLine(string.Format(Properties.Resources.VmPage_AffinityApplyFailed, i + 1, vm.Name));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine(string.Format(Properties.Resources.VmPage_AffinityApplyException, ex.Message));
                }
            });
        }


    }
}
