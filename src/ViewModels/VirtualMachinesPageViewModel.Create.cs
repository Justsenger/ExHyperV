using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ExHyperV.Interaction;
using ExHyperV.Models;
using ExHyperV.Services;
using ExHyperV.Tools;
using Wpf.Ui.Controls;

namespace ExHyperV.ViewModels
{
    public partial class VirtualMachinesPageViewModel
    {
        // ===== 视图模型属性 - 创建虚拟机表单 =====

        // 控制右侧界面切换
        [ObservableProperty] private bool _isCreatingVm = false;
        [ObservableProperty] private string _creatingStatusText = string.Empty;

        // 当名称变化时，自动更新磁盘路径
        partial void OnNewVmNameChanged(string value)
        {
            UpdateDiskPath();
        }

        // 当基础路径变化时，自动更新磁盘路径
        partial void OnNewVmStoragePathChanged(string value)
        {
            UpdateDiskPath();
        }


        // 重命名

        // 1. 触发重命名模式
        [RelayCommand]
        private void RenameVm(VmInstanceViewModel vm)
        {
            if (vm == null) return;
            vm.StartEditing();
        }

        // 2. 取消重命名
        [RelayCommand]
        private void CancelRename(VmInstanceViewModel vm)
        {
            if (vm == null) return;
            vm.IsEditing = false;
        }

        [RelayCommand]
        private async Task CommitRenameAsync(VmInstanceViewModel vm)
        {
            if (vm == null || !vm.IsEditing) return;
            vm.IsEditing = false;

            if (string.IsNullOrWhiteSpace(vm.EditedName) || vm.EditedName == vm.Name) return;

            string oldName = vm.Name;
            string newName = vm.EditedName;
            Guid vmId = vm.Id; // 使用唯一 ID

            IsLoading = true;
            try
            {
                // 传 vmId 而非 oldName（VM 可能已改名）
                var result = await VmEditService.RenameVmAsync(vmId, newName);

                if (result.Success)
                {
                    lock (_renameLockouts)
                    {
                        _renameLockouts[vmId] = (newName, DateTime.Now.AddSeconds(5));
                    }
                    vm.Name = newName;
                }
                else
                {
                    ShowError($"{Properties.Resources.VmPage_RenameFail}：{result.Message}");
                }
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                IsLoading = false;
            }
        }


        // --- 1. 常规设置 ---
        [ObservableProperty] private string _newVmName = "NewVM";
        [ObservableProperty] private string _newVmStoragePath = string.Empty;

        // 批量创建数量：>1 时名称按 base-NN 生成、各台并行建；=1 与原单建完全一致。
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSingleCreate))]
        private string _newVmQuantity = "1";

        // 单台创建（数量≤1）。绑定给"现有磁盘"选项的 IsEnabled：批量不支持现有磁盘（多台共用一块盘会互踩）。
        public bool IsSingleCreate => !(int.TryParse(NewVmQuantity, out var q) && q > 1);

        partial void OnNewVmQuantityChanged(string value)
        {
            if (!IsSingleCreate && NewVmDiskMode == 1) NewVmDiskMode = 0;
        }
        [ObservableProperty] private ObservableCollection<string> _supportedVersions = new() { "12.0", "11.0", "10.0", "9.0", "8.0" };
        [ObservableProperty] private string _selectedVersion = "8.0";

        // --- 2. 计算资源 ---
        [ObservableProperty] private string _newVmProcessorCount = "4"; // ComboBox IsEditable="True" 绑定 string
        [ObservableProperty] private string _newVmMemoryMb = "4096";    // ComboBox IsEditable="True" 绑定 string
        [ObservableProperty] private bool _newVmDynamicMemory = false;

        // 安全特性 (仅第 2 代)
        [ObservableProperty] private bool _newVmEnableSecureBoot = true;
        [ObservableProperty] private bool _newVmEnableTpm = true;
        [ObservableProperty] private string _newVmIsolationType = "Disabled"; // Disabled, TrustedLaunch, VBS, SNP, TDX

        // --- 3. 存储资源 ---
        [ObservableProperty] private int _newVmDiskMode = 0; // 0:新建磁盘, 1:现有磁盘, 2:稍后附加
        [ObservableProperty] private string _newVmDiskSizeGb = "128";
        [ObservableProperty] private string _newVmNewDiskPath = string.Empty;      // 模式0使用
        [ObservableProperty] private string _newVmExistingDiskPath = string.Empty; // 模式1使用

        // 安装介质 (ISO)
        [ObservableProperty] private string _newVmIsoPath = string.Empty;

        // --- 4. 网络与全局动作 ---
        [ObservableProperty] private string _newVmSelectedSwitch = string.Empty;
        [ObservableProperty] private bool _startVmAfterCreation = true;

        // 1. 探测结果：系统是否支持
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEnableIsolation))] // 当此值改变，通知 UI 刷新 CanEnableIsolation
        private bool _isIsolationSupported = false;

        // 2. 找到你原有的 NewVmGeneration 属性，添加通知
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanEnableIsolation))] // 当代际改变，通知 UI 刷新 CanEnableIsolation
        private int _newVmGeneration = 2;

        // 3. 这是一个只读的计算属性，用于 UI 绑定
        public bool CanEnableIsolation => IsIsolationSupported && NewVmGeneration == 2;

        // ARM64 的 Hyper-V 不提供 IDE 控制器，无法承载第 1 代虚拟机（建机会卡在加盘步 Storage_Error_ControllerNotFound），
        // 据此禁用第 1 代选项。OS 架构运行期不变，故为只读计算属性、无需变更通知。
        public bool CanUseGen1 => RuntimeInformation.OSArchitecture != Architecture.Arm64;

        // 存储探测到的类型列表
        [ObservableProperty]
        private ObservableCollection<string> _supportedIsolationTypes = new() { "Disabled" };
        private bool _isDiskPathManual = false; // 用户是否手动选过磁盘路径（手动后不再自动联动）；仅本模块使用（原误置于核心 .cs）



        // ===== 创建虚拟机模块 =====

        // 1. 点击左侧 "+" 按钮：进入创建模式
        private void UpdateDiskPath()
        {
            if (string.IsNullOrWhiteSpace(NewVmName) || _isDiskPathManual) return; // 如果手动选过，就不再自动更新

            string root = string.IsNullOrWhiteSpace(NewVmStoragePath) ? @"C:\ProgramData\Microsoft\Windows\Hyper-V" : NewVmStoragePath;
            try
            {
                NewVmNewDiskPath = Path.Combine(root, NewVmName);   // 只存文件夹，vhdx 文件名由服务按最终 VM 名派生
            }
            catch { }
        }

        [RelayCommand]
        private async Task CreateVmAsync()
        {
            // --- 1. UI 状态与标志位重置 ---
            IsLoadingSettings = true;
            IsCreatingVm = true;
            SelectedVm = null;
            _isDiskPathManual = false;     // 重置用户手动选择磁盘路径的标记

            // --- 2. 基础配置默认值初始化 ---
            NewVmGeneration = 2;
            NewVmMemoryMb = "4096";
            int hostCores = Environment.ProcessorCount;
            NewVmProcessorCount = (hostCores >= 4 ? 4 : hostCores).ToString();

            NewVmDiskMode = 0;
            NewVmDiskSizeGb = "128";
            NewVmQuantity = "1";
            NewVmDynamicMemory = false;
            NewVmEnableSecureBoot = true;
            NewVmEnableTpm = true;
            StartVmAfterCreation = true;
            NewVmIsoPath = string.Empty;
            NewVmExistingDiskPath = string.Empty;

            try
            {
                // 3. 动态探测宿主机默认路径（不硬编码）
                // 调用 Service 通过 (Get-VMHost).VirtualMachinePath 获取真实路径
                var hostPaths = await VmCreateService.GetHostDefaultPathsAsync();

                // 设置 UI 显示的根路径 (例如 C:\ProgramData\Microsoft\Windows\Hyper-V)
                NewVmStoragePath = hostPaths.DefaultVmPath;

                // --- 4. 初始化名称并触发路径联动 ---
                // 获取当前系统中不冲突的名称 (如 NewVM, NewVM (2))
                NewVmName = GetNextAvailableName("NewVM");

                // 执行路径联动逻辑，确保 NewVmNewDiskPath 此时已经指向：
                // [默认路径]\[NewVM]\[NewVM].vhdx
                UpdateDiskPath();

                // --- 5. 探测系统支持的配置版本 ---
                var allVersions = await VmCreateService.GetSupportedVersionsAsync();
                SupportedVersions = new ObservableCollection<string>(allVersions);

                // 在已降序的列表里取第一个小于 200 的稳定版本作默认值
                var defaultStable = allVersions.FirstOrDefault(v =>
                    double.TryParse(v, out double verNum) && verNum < 200);

                // 如果找到稳定版则选中，否则选列表第一个
                SelectedVersion = defaultStable ?? SupportedVersions.FirstOrDefault();

                // --- 6. 探测机密计算 (Isolation) 支持情况 ---
                var (supported, types) = await VmCreateService.GetIsolationSupportAsync();
                IsIsolationSupported = supported;
                SupportedIsolationTypes = new ObservableCollection<string>(types);

                // 初始状态默认为 Disabled
                NewVmIsolationType = "Disabled";

                // --- 7. 加载虚拟交换机列表 ---
                var switches = await VmNetworkService.GetAvailableSwitchesAsync();

                // 创建一个临时的列表，第一项放“未连接”
                string noneText = Properties.Resources.Common_None; // “未连接”的文本
                var switchList = new List<string> { noneText };
                if (switches != null) switchList.AddRange(switches);

                AvailableSwitchNames = new ObservableCollection<string>(switchList);

                // --- 改进后的自动选择逻辑 ---

                // 1. 尝试寻找包含 "Default" 的交换机
                var defaultSwitch = AvailableSwitchNames.FirstOrDefault(s =>
                    s.Contains("Default", StringComparison.OrdinalIgnoreCase) ||
                    s.Contains(Properties.Resources.VmPage_Default, StringComparison.OrdinalIgnoreCase));

                if (defaultSwitch != null)
                {
                    NewVmSelectedSwitch = defaultSwitch;
                }
                else
                {
                    // 2. 如果没找到 Default，尝试寻找第一个“非未连接”的真实交换机
                    var firstRealSwitch = AvailableSwitchNames.FirstOrDefault(s => s != noneText);

                    // 3. 如果找到了真实交换机就选它，否则（即列表里只有“未连接”）才选“未连接”
                    NewVmSelectedSwitch = firstRealSwitch ?? noneText;
                }

            }
            catch (Exception ex)
            {
                // 探测失败（如宿主没装 Hyper-V 网络组件）：保证至少有“未连接”可选，并提示用户部分选项可能不准确
                AvailableSwitchNames = new ObservableCollection<string> { Properties.Resources.Common_None };
                NewVmSelectedSwitch = AvailableSwitchNames[0];
                ShowError($"{Properties.Resources.VmPage_CreateOptionsLoadFail}：{FriendlyError.CleanLines(ex.Message)}");
                Debug.WriteLine($"[CREATE-VM-NET-ERROR] {ex.Message}");
            }

            finally
            {
                // 延迟一小会儿关闭加载状态，确保 UI 绑定完成
                await Task.Delay(100);
                IsLoadingSettings = false;
            }
        }
        // 2. 点击 Properties.Resources.Button_Cancel 按钮：退出创建模式
        [RelayCommand]
        private void CancelCreate()
        {
            IsCreatingVm = false;
            // 恢复选中列表项提升体验
            if (SelectedVm == null && VmList.Count > 0)
            {
                SelectedVm = VmList.First();
            }
        }

        // --- 浏览文件系统相关命令 ---

        [RelayCommand]
        private void BrowseNewVmPath()
        {
            var picked = Dialogs.PickFolder(Properties.Resources.VmPage_SelectConfigDir,
                string.IsNullOrWhiteSpace(NewVmStoragePath) ? null : NewVmStoragePath);
            if (picked != null) NewVmStoragePath = picked;
        }


        [RelayCommand]
        private void BrowseNewDiskLocation()
        {
            var picked = Dialogs.PickFolder(Properties.Resources.VmPage_SelectNewVhdPath,
                string.IsNullOrWhiteSpace(NewVmNewDiskPath) ? null : NewVmNewDiskPath);
            if (picked != null)
            {
                NewVmNewDiskPath = picked;   // 文件夹；vhdx 文件名由服务按 VM 名派生
                _isDiskPathManual = true; // 标记用户已手动选择
            }
        }

        [RelayCommand]
        private void BrowseExistingDisk()
        {
            var picked = Dialogs.PickOpenFile(Properties.Resources.VmPage_SelectExistVhd, Properties.Resources.VmPage_VhdFilterBoth, GetDir(NewVmExistingDiskPath));
            if (picked != null) NewVmExistingDiskPath = picked;
        }

        [RelayCommand]
        private void BrowseIsoImage()
        {
            var picked = Dialogs.PickOpenFile(Properties.Resources.VmPage_SelectIso, Properties.Resources.VmPage_IsoFilter, GetDir(NewVmIsoPath));
            if (picked != null) NewVmIsoPath = picked;
        }

        [RelayCommand]
        private async Task ConfirmCreateAsync()
        {
            // --- 1. 基础验证：名称 ---
            if (string.IsNullOrWhiteSpace(NewVmName))
            {
                ShowTip(Properties.Resources.VmPage_NameEmpty);
                return;
            }

            // --- 2. 磁盘模式深度验证 ---
            if (NewVmDiskMode == 0) // 新建磁盘
            {
                if (string.IsNullOrWhiteSpace(NewVmNewDiskPath))
                {
                    ShowTip(Properties.Resources.VmPage_SelectVhdSave);
                    return;
                }
            }
            else if (NewVmDiskMode == 1) // 现有磁盘
            {
                if (string.IsNullOrWhiteSpace(NewVmExistingDiskPath))
                {
                    ShowTip(Properties.Resources.VmPage_SelectExistVhdPath);
                    return;
                }

                if (!File.Exists(NewVmExistingDiskPath))
                {
                    ShowTip(Properties.Resources.VmPage_ExistVhdNotFound);
                    return;
                }
            }

            // --- 4. ISO 镜像验证 (如果有输入) ---
            if (!string.IsNullOrWhiteSpace(NewVmIsoPath) && !File.Exists(NewVmIsoPath))
            {
                ShowTip(Properties.Resources.VmPage_IsoNotFound);
                return;
            }

            // --- 5. 计算资源数值校验（IsEditable 下拉可输入任意串，挡掉非法/0/负数，避免静默回退默认值或建机失败）---
            if (!int.TryParse(NewVmProcessorCount, out var cpuCount) || cpuCount < 1)
            {
                ShowTip(Properties.Resources.VmPage_InvalidCpuCount);
                return;
            }
            if (!long.TryParse(NewVmMemoryMb, out var memoryMb) || memoryMb < 32)
            {
                ShowTip(Properties.Resources.VmPage_InvalidMemory);
                return;
            }
            if (NewVmDiskMode == 0 && (!long.TryParse(NewVmDiskSizeGb, out var diskSize) || diskSize < 1))
            {
                ShowTip(Properties.Resources.VmPage_InvalidDiskSize);
                return;
            }

            // --- 数量校验 ---
            int quantity = int.TryParse(NewVmQuantity, out var qv) && qv >= 1 ? qv : 0;
            if (quantity < 1)
            {
                ShowTip(Properties.Resources.VmPage_InvalidQuantity);
                return;
            }
            if (quantity > 1 && NewVmDiskMode != 0)
            {
                ShowTip(Properties.Resources.VmPage_BatchNewDiskOnly);
                return;
            }

            // 组装参数（单台/批量共用；数值已校验，直接用解析结果）
            VmCreationParams Build(string name) => new VmCreationParams
            {
                Name = name,
                Path = NewVmStoragePath,
                Version = SelectedVersion,
                Generation = NewVmGeneration,
                ProcessorCount = cpuCount,
                MemoryMb = memoryMb,
                EnableDynamicMemory = NewVmDynamicMemory,
                EnableSecureBoot = NewVmEnableSecureBoot,
                EnableTpm = NewVmEnableTpm,
                IsolationType = NewVmIsolationType,
                DiskMode = NewVmDiskMode,
                DiskSizeGb = long.TryParse(NewVmDiskSizeGb, out var ds) ? ds : 128,
                VhdPath = NewVmDiskMode == 0 ? NewVmNewDiskPath : NewVmExistingDiskPath,
                IsDiskPathManual = _isDiskPathManual,
                IsoPath = NewVmIsoPath,
                SwitchName = NewVmSelectedSwitch,
                StartAfterCreation = StartVmAfterCreation
            };

            IsLoadingSettings = true;
            try
            {
                if (quantity <= 1)
                {
                    CreatingStatusText = Properties.Resources.VmPage_CreatingVm;
                    var result = await VmCreateService.CreateVirtualMachineAsync(Build(NewVmName));
                    if (result.Success)
                    {
                        string actualCreatedName = result.Message;
                        ShowSuccess(string.Format(Properties.Resources.VmPage_VmCreated, actualCreatedName));
                        IsCreatingVm = false;
                        await LoadVmsCommand.ExecuteAsync(null);
                        var newVm = VmList.FirstOrDefault(v => v.Name.Equals(actualCreatedName, StringComparison.OrdinalIgnoreCase));
                        if (newVm != null) SelectedVm = newVm;
                        // 启动放此处：成功后单独启动并检查引擎返回，失败(如内存不足)弹原因而非静默吞掉。
                        if (StartVmAfterCreation)
                        {
                            CreatingStatusText = Properties.Resources.VmPage_StartingVm;
                            var startResult = await VmPowerService.ExecuteControlActionAsync(actualCreatedName, "Start");
                            if (!startResult.Success)
                                ShowError($"{Properties.Resources.VmPage_StartFail}：{FriendlyError.CleanLines(startResult.Error)}");
                        }
                    }
                    else
                    {
                        ShowError($"{Properties.Resources.VmPage_CreateFail}：{result.Message}");
                    }
                }
                else
                {
                    // 批量：base-NN 命名 → 各台并行建 → 聚合汇报 → 只重载一次。资源够不够交给用户。
                    CreatingStatusText = string.Format(Properties.Resources.VmPage_CreatingBatch, quantity);
                    var names = await VmCreateService.BuildBatchNamesAsync(NewVmName, NewVmStoragePath, quantity);
                    var results = await Task.WhenAll(names.Select(n => VmCreateService.CreateVirtualMachineAsync(Build(n))));
                    int okCount = results.Count(r => r.Success);

                    int startFail = 0;
                    if (StartVmAfterCreation)
                    {
                        var okNames = names.Where((n, i) => results[i].Success).ToList();
                        var starts = await Task.WhenAll(okNames.Select(n => VmPowerService.ExecuteControlActionAsync(n, "Start")));
                        startFail = starts.Count(s => !s.Success);
                    }

                    IsCreatingVm = false;
                    await LoadVmsCommand.ExecuteAsync(null);

                    string tail = startFail > 0 ? string.Format(Properties.Resources.VmPage_BatchStartFail, startFail) : string.Empty;
                    if (okCount == quantity)
                        ShowSuccess(string.Format(Properties.Resources.VmPage_BatchAllOk, okCount) + tail);
                    else
                        ShowError(string.Format(Properties.Resources.VmPage_BatchPartial, okCount, quantity - okCount) + tail);
                }
            }
            catch (Exception ex)
            {
                ShowError(ex.Message);
            }
            finally
            {
                IsLoadingSettings = false;
                CreatingStatusText = string.Empty;
            }
        }
        // --- 辅助私有方法 ---

        private string GetNextAvailableName(string baseName)
        {
            if (!VmList.Any(v => v.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase)))
                return baseName;

            int i = 2;
            while (VmList.Any(v => v.Name.Equals($"{baseName} ({i})", StringComparison.OrdinalIgnoreCase)))
            {
                i++;
            }
            return $"{baseName} ({i})";
        }




    }
}
